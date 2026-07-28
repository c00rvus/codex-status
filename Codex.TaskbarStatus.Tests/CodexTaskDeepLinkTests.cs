using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class CodexTaskDeepLinkTests
{
    [Fact]
    public void TryCreate_UsesCanonicalCodexThreadLink()
    {
        var created = CodexTaskDeepLink.TryCreate(
            " 019faa53-5ea7-71f0-a427-9ce7b72a9fa7 ",
            out var uri);

        Assert.True(created);
        Assert.Equal(
            "codex://threads/019faa53-5ea7-71f0-a427-9ce7b72a9fa7",
            uri?.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_RejectsMissingSessionId(string? sessionId)
    {
        Assert.False(CodexTaskDeepLink.TryCreate(sessionId, out var uri));
        Assert.Null(uri);
    }
}
