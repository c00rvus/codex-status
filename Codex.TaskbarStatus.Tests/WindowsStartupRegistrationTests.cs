using Codex.TaskbarStatus.Standalone;

namespace Codex.TaskbarStatus.Tests;

public sealed class WindowsStartupRegistrationTests
{
    [Fact]
    public void IsEnabledReturnsFalseWhenValueIsMissing()
    {
        var executable = CreateExecutablePath("Codex Status");
        var registration = new WindowsStartupRegistration(
            executable,
            WindowsStartupRegistration.InstallerValueName,
            new FakeStartupRunKey());

        Assert.False(registration.IsEnabled());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsEnabledRecognizesLegacyAndCurrentCommands(bool includeStartupArgument)
    {
        var executable = CreateExecutablePath("Codex Status");
        var command = $"\"{executable}\"" +
            (includeStartupArgument ? " --startup" : string.Empty);
        var runKey = new FakeStartupRunKey
        {
            [WindowsStartupRegistration.InstallerValueName] = command,
        };
        var registration = new WindowsStartupRegistration(
            executable,
            WindowsStartupRegistration.InstallerValueName,
            runKey);

        Assert.True(registration.IsEnabled());
    }

    [Fact]
    public void IsEnabledRejectsAnotherExecutable()
    {
        var executable = CreateExecutablePath("Codex Status");
        var runKey = new FakeStartupRunKey
        {
            [WindowsStartupRegistration.InstallerValueName] =
                $"\"{CreateExecutablePath("Another App")}\" --startup",
        };
        var registration = new WindowsStartupRegistration(
            executable,
            WindowsStartupRegistration.InstallerValueName,
            runKey);

        Assert.False(registration.IsEnabled());
    }

    [Fact]
    public void IsEnabledRejectsMalformedCommand()
    {
        var executable = CreateExecutablePath("Codex Status");
        var runKey = new FakeStartupRunKey
        {
            [WindowsStartupRegistration.InstallerValueName] = "\"unterminated",
        };
        var registration = new WindowsStartupRegistration(
            executable,
            WindowsStartupRegistration.InstallerValueName,
            runKey);

        Assert.False(registration.IsEnabled());
    }

    [Fact]
    public void IsEnabledReturnsFalseWhenWindowsExplicitlyDisabledStartup()
    {
        var executable = CreateExecutablePath("Codex Status");
        var runKey = new FakeStartupRunKey
        {
            [WindowsStartupRegistration.InstallerValueName] =
                $"\"{executable}\" --startup",
        };
        runKey.SetApproval(
            WindowsStartupRegistration.InstallerValueName,
            [0x03, 0x00, 0x00, 0x00]);
        var registration = new WindowsStartupRegistration(
            executable,
            WindowsStartupRegistration.InstallerValueName,
            runKey);

        Assert.False(registration.IsEnabled());
    }

    [Fact]
    public void SetEnabledWritesQuotedStartupCommand()
    {
        var executable = CreateExecutablePath("Codex Status");
        var runKey = new FakeStartupRunKey();
        var registration = new WindowsStartupRegistration(
            executable,
            WindowsStartupRegistration.InstallerValueName,
            runKey);

        registration.SetEnabled(true);

        Assert.Equal(
            $"\"{Path.GetFullPath(executable)}\" --startup",
            runKey[WindowsStartupRegistration.InstallerValueName]);
    }

    [Fact]
    public void SetEnabledClearsWindowsDisabledApproval()
    {
        var executable = CreateExecutablePath("Codex Status");
        var runKey = new FakeStartupRunKey();
        runKey.SetApproval(
            WindowsStartupRegistration.InstallerValueName,
            [0x03, 0x00, 0x00, 0x00]);
        var registration = new WindowsStartupRegistration(
            executable,
            WindowsStartupRegistration.InstallerValueName,
            runKey);

        registration.SetEnabled(true);

        Assert.Null(
            runKey.ReadApproval(
                WindowsStartupRegistration.InstallerValueName));
        Assert.True(registration.IsEnabled());
    }

    [Fact]
    public void SetEnabledFalseDeletesOnlyOwnedValue()
    {
        var executable = CreateExecutablePath("Codex Status");
        var runKey = new FakeStartupRunKey
        {
            [WindowsStartupRegistration.InstallerValueName] =
                $"\"{executable}\" --startup",
            ["Another App"] = "\"C:\\Another\\App.exe\"",
        };
        runKey.SetApproval(
            WindowsStartupRegistration.InstallerValueName,
            [0x03, 0x00, 0x00, 0x00]);
        var registration = new WindowsStartupRegistration(
            executable,
            WindowsStartupRegistration.InstallerValueName,
            runKey);

        registration.SetEnabled(false);

        Assert.Null(runKey[WindowsStartupRegistration.InstallerValueName]);
        Assert.Null(
            runKey.ReadApproval(
                WindowsStartupRegistration.InstallerValueName));
        Assert.Equal("\"C:\\Another\\App.exe\"", runKey["Another App"]);
    }

    [Fact]
    public void ResolveValueNameSeparatesInstallerSourceAndDevelopmentBuilds()
    {
        var localApplicationData = Path.Combine(
            Path.GetTempPath(),
            "CodexStatusStartupTests",
            Guid.NewGuid().ToString("N"));

        Assert.Equal(
            WindowsStartupRegistration.InstallerValueName,
            WindowsStartupRegistration.ResolveValueName(
                Path.Combine(
                    localApplicationData,
                    "Programs",
                    "Codex Status",
                    "Codex.TaskbarStatus.Standalone.exe"),
                localApplicationData));
        Assert.Equal(
            WindowsStartupRegistration.SourceValueName,
            WindowsStartupRegistration.ResolveValueName(
                Path.Combine(
                    localApplicationData,
                    "Programs",
                    "Codex Status Source",
                    "Codex.TaskbarStatus.Standalone.exe"),
                localApplicationData));
        Assert.Equal(
            WindowsStartupRegistration.DevelopmentValueName,
            WindowsStartupRegistration.ResolveValueName(
                Path.Combine(
                    localApplicationData,
                    "artifacts",
                    "standalone-dev",
                    "Codex.TaskbarStatus.Standalone.exe"),
                localApplicationData));
    }

    [Fact]
    public void ResolveValueNameRecognizesCustomInstallerDirectory()
    {
        var localApplicationData = Path.Combine(
            Path.GetTempPath(),
            "CodexStatusStartupTests",
            Guid.NewGuid().ToString("N"));
        var customInstallLocation = Path.Combine(
            Path.GetTempPath(),
            "Custom Codex Status");
        var executable = Path.Combine(
            customInstallLocation,
            "Codex.TaskbarStatus.Standalone.exe");

        Assert.Equal(
            WindowsStartupRegistration.InstallerValueName,
            WindowsStartupRegistration.ResolveValueName(
                executable,
                localApplicationData,
                customInstallLocation));
    }

    private static string CreateExecutablePath(string directoryName) =>
        Path.Combine(
            Path.GetTempPath(),
            directoryName,
            "Codex.TaskbarStatus.Standalone.exe");

    private sealed class FakeStartupRunKey : IStartupRunKey
    {
        private readonly Dictionary<string, string> _values =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _approvals =
            new(StringComparer.Ordinal);

        internal string? this[string valueName]
        {
            get => Read(valueName);
            set
            {
                if (value is null)
                {
                    _values.Remove(valueName);
                }
                else
                {
                    _values[valueName] = value;
                }
            }
        }

        public string? Read(string valueName) =>
            _values.TryGetValue(valueName, out var value)
                ? value
                : null;

        public byte[]? ReadApproval(string valueName) =>
            _approvals.TryGetValue(valueName, out var value)
                ? value
                : null;

        public void Write(string valueName, string command) =>
            _values[valueName] = command;

        public void Delete(string valueName) => _values.Remove(valueName);

        public void DeleteApproval(string valueName) =>
            _approvals.Remove(valueName);

        internal void SetApproval(string valueName, byte[] value) =>
            _approvals[valueName] = value;
    }
}
