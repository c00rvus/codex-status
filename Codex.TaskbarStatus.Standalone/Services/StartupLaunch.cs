namespace Codex.TaskbarStatus.Standalone;

internal static class StartupLaunch
{
    internal const string Argument = "--startup";

    internal static bool IsStartupLaunch(IEnumerable<string> arguments) =>
        arguments.Any(argument =>
            string.Equals(argument, Argument, StringComparison.OrdinalIgnoreCase));
}
