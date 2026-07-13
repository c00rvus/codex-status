using System.Diagnostics;
using System.Text.Json;

namespace Codex.TaskbarStatus.ExtensionApp;

/// <summary>
/// Minimal JSONL client for the local Codex app-server. The process is kept
/// alive between reads so updating a taskbar indicator does not repeatedly
/// start Codex in the background.
/// </summary>
internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private Process? _process;
    private StreamWriter? _standardInput;
    private StreamReader? _standardOutput;
    private int _nextRequestId;

    public async Task<string> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var requestId = ++_nextRequestId;
        await WriteLineAsync(
            JsonSerializer.Serialize(new
            {
                method = "account/rateLimits/read",
                id = requestId,
                @params = new { },
            }),
            cancellationToken).ConfigureAwait(false);

        return await ReadResponseAsync(requestId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        StopProcess();
        return ValueTask.CompletedTask;
    }

    public void Reset() => StopProcess();

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } &&
            _standardInput is not null &&
            _standardOutput is not null)
        {
            return;
        }

        StopProcess();

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCodexExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The Codex app-server could not be started.");
        }
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();

        _process = process;
        _standardInput = process.StandardInput;
        _standardOutput = process.StandardOutput;
        _nextRequestId = 0;

        var initializeId = ++_nextRequestId;
        await WriteLineAsync(
            JsonSerializer.Serialize(new
            {
                method = "initialize",
                id = initializeId,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "codex-status-widget",
                        title = "Codex Status Widget",
                        version = "1.1.0",
                    },
                    capabilities = new { },
                },
            }),
            cancellationToken).ConfigureAwait(false);
        _ = await ReadResponseAsync(initializeId, cancellationToken).ConfigureAwait(false);

        await WriteLineAsync(
            JsonSerializer.Serialize(new
            {
                method = "initialized",
                @params = new { },
            }),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = _standardInput
            ?? throw new InvalidOperationException("The Codex app-server input is unavailable.");
        await input.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadResponseAsync(int requestId, CancellationToken cancellationToken)
    {
        var output = _standardOutput
            ?? throw new InvalidOperationException("The Codex app-server output is unavailable.");

        while (true)
        {
            var line = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new EndOfStreamException("The Codex app-server closed its output stream.");
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) ||
                    !id.TryGetInt32(out var responseId) ||
                    responseId != requestId)
                {
                    // Notifications such as account/rateLimits/updated may arrive
                    // between a request and its response. The next explicit read
                    // is authoritative, so those notifications can be skipped.
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException(
                        $"The Codex app-server returned an error: {error.GetRawText()}");
                }

                return line;
            }
            catch (JsonException)
            {
                // Ignore non-protocol output and keep waiting for this request id.
            }
        }
    }

    private static string ResolveCodexExecutable()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(userProfile, ".codex", "packages", "standalone", "current", "bin", "codex.exe"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? "codex.exe";
    }

    private void StopProcess()
    {
        _standardInput?.Dispose();
        _standardInput = null;
        _standardOutput?.Dispose();
        _standardOutput = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            _process.Dispose();
            _process = null;
        }
    }
}
