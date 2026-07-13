using System.Globalization;
using System.Security.Cryptography;

namespace Codex.TaskbarStatus.Core;

public sealed record AgentSpinnerFrame(
    AgentSpinnerDefinition Definition,
    string Text,
    int FrameIndex);

/// <summary>
/// Selects one spinner per Codex request and derives its frame from elapsed time.
/// </summary>
public sealed class RequestSpinnerAnimator
{
    private readonly IReadOnlyList<AgentSpinnerDefinition> _catalog;
    private readonly Func<int, int> _nextIndex;
    private string? _requestKey;
    private AgentSpinnerDefinition? _definition;
    private DateTimeOffset _animationStartedAtUtc;
    private int _lastDefinitionIndex = -1;

    public RequestSpinnerAnimator(
        IReadOnlyList<AgentSpinnerDefinition>? catalog = null,
        Func<int, int>? nextIndex = null)
    {
        _catalog = catalog ?? AgentSpinnerCatalog.All;
        if (_catalog.Count == 0)
        {
            throw new ArgumentException("The spinner catalog cannot be empty.", nameof(catalog));
        }

        _nextIndex = nextIndex ?? RandomNumberGenerator.GetInt32;
    }

    public AgentSpinnerFrame? GetFrame(
        bool isActive,
        string? sessionId,
        string? turnId,
        DateTimeOffset? requestStartedAtUtc,
        DateTimeOffset nowUtc)
    {
        if (!isActive)
        {
            Reset();
            return null;
        }

        var requestKey = CreateRequestKey(sessionId, turnId, requestStartedAtUtc);
        if (_definition is null || !string.Equals(_requestKey, requestKey, StringComparison.Ordinal))
        {
            _requestKey = requestKey;
            _definition = SelectDefinition();
            _animationStartedAtUtc = requestStartedAtUtc is { } started && started <= nowUtc
                ? started
                : nowUtc;
        }

        var elapsed = nowUtc > _animationStartedAtUtc
            ? nowUtc - _animationStartedAtUtc
            : TimeSpan.Zero;
        var frameIndex = (int)((elapsed.Ticks / _definition.Interval.Ticks) % _definition.Frames.Count);
        return new AgentSpinnerFrame(_definition, _definition.Frames[frameIndex], frameIndex);
    }

    public void Reset()
    {
        _requestKey = null;
        _definition = null;
        _animationStartedAtUtc = default;
    }

    public static string CreateRequestKey(
        string? sessionId,
        string? turnId,
        DateTimeOffset? requestStartedAtUtc)
    {
        var requestIdentity = !string.IsNullOrWhiteSpace(turnId)
            ? $"turn:{turnId}"
            : $"started:{requestStartedAtUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? string.Empty}";
        return $"session:{sessionId ?? string.Empty}|{requestIdentity}";
    }

    private AgentSpinnerDefinition SelectDefinition()
    {
        var excludePrevious = _lastDefinitionIndex >= 0 && _catalog.Count > 1;
        var poolSize = excludePrevious ? _catalog.Count - 1 : _catalog.Count;
        var draw = _nextIndex(poolSize);
        if (draw < 0 || draw >= poolSize)
        {
            throw new InvalidOperationException("The spinner randomizer returned an out-of-range index.");
        }

        var selectedIndex = excludePrevious && draw >= _lastDefinitionIndex
            ? draw + 1
            : draw;
        _lastDefinitionIndex = selectedIndex;
        return _catalog[selectedIndex];
    }
}
