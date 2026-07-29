using Codex.TaskbarStatus.Standalone;

namespace Codex.TaskbarStatus.Tests;

public sealed class StartupLaunchTests
{
    [Theory]
    [InlineData("--startup")]
    [InlineData("--STARTUP")]
    public void IsStartupLaunchRecognizesStartupArgument(string argument)
    {
        Assert.True(StartupLaunch.IsStartupLaunch([argument]));
    }

    [Fact]
    public void IsStartupLaunchIgnoresNormalActivations()
    {
        Assert.False(StartupLaunch.IsStartupLaunch(["--open-settings"]));
    }
}
