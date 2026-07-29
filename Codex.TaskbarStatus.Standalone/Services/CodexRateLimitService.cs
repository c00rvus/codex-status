using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Standalone.Widget;

/// <summary>
/// Keeps an account-wide rate-limit snapshot available to the UI without
/// blocking the dispatcher thread. The live app-server is authoritative; a
/// bounded rollout tail is used only while the live protocol is unavailable.
/// </summary>
internal sealed class CodexRateLimitService : IAsyncDisposable
{
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly CodexAppServerClient _appServer = new();
    private readonly RolloutRateLimitReader _rolloutReader = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CodexRateLimitSnapshot _snapshot;
    private string _source;
    private Task? _refreshTask;
    private long _nextRefreshUtcTicks;
    private int _refreshing;
    private int _disposed;

    public CodexRateLimitService()
    {
        var now = DateTimeOffset.UtcNow;
        _snapshot = RemoveStaleWindows(_rolloutReader.ReadLatest(), now);
        _source = _snapshot.HasKnownData ? "Local session (fallback)" : "Unavailable";
    }

    public CodexRateLimitSnapshot Current =>
        RemoveStaleWindows(Volatile.Read(ref _snapshot), DateTimeOffset.UtcNow);

    public string Source => Volatile.Read(ref _source);

    public void RequestRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        if (nowTicks < Interlocked.Read(ref _nextRefreshUtcTicks) ||
            Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        // Debounce dispatcher ticks immediately. RefreshCore moves this to the
        // normal success/retry interval once the attempt finishes.
        Interlocked.Exchange(
            ref _nextRefreshUtcTicks,
            DateTimeOffset.UtcNow.AddSeconds(2).UtcDateTime.Ticks);
        _refreshTask = Task.Run(RefreshCoreAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        var refreshTask = _refreshTask;
        if (refreshTask is not null)
        {
            try
            {
                await refreshTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await _appServer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _rolloutReader.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task RefreshCoreAsync()
    {
        var nextInterval = RetryInterval;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(RequestTimeout);

            var response = await _appServer
                .ReadRateLimitsAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!CodexRateLimitParser.TryParseAppServerResponse(response, out var liveSnapshot))
            {
                throw new InvalidDataException("Codex returned an unrecognized rate-limit response.");
            }

            Volatile.Write(
                ref _snapshot,
                RemoveStaleWindows(liveSnapshot, DateTimeOffset.UtcNow));
            Volatile.Write(ref _source, "Codex app-server");
            nextInterval = LiveRefreshInterval;
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidDataException or
            InvalidOperationException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or
            OperationCanceledException)
        {
            _appServer.Reset();
            if (!_shutdown.IsCancellationRequested)
            {
                var fallback = RemoveStaleWindows(
                    _rolloutReader.ReadLatest(),
                    DateTimeOffset.UtcNow);
                if (fallback.HasKnownData)
                {
                    Volatile.Write(ref _snapshot, fallback);
                    Volatile.Write(ref _source, "Local session (fallback)");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(
                ref _nextRefreshUtcTicks,
                DateTimeOffset.UtcNow.Add(nextInterval).UtcDateTime.Ticks);
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private static CodexRateLimitSnapshot RemoveStaleWindows(
        CodexRateLimitSnapshot snapshot,
        DateTimeOffset now)
    {
        var fiveHour = snapshot.FiveHour.IsStaleAt(
                now,
                snapshot.ObservedAtUtc,
                TimeSpan.FromHours(5))
            ? RateLimitWindowState.Unknown
            : snapshot.FiveHour;
        var weekly = snapshot.Weekly.IsStaleAt(
                now,
                snapshot.ObservedAtUtc,
                TimeSpan.FromDays(7))
            ? RateLimitWindowState.Unknown
            : snapshot.Weekly;

        // "Disabled" is meaningful only as part of a still-current successful
        // snapshot. Once its companion weekly observation is stale, do not keep
        // presenting a historical absence as current account configuration.
        if (fiveHour.Availability == RateLimitAvailability.Disabled &&
            weekly.Availability == RateLimitAvailability.Unknown)
        {
            fiveHour = RateLimitWindowState.Unknown;
        }

        return snapshot with { FiveHour = fiveHour, Weekly = weekly };
    }
}
