using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;

namespace Codex.TaskbarStatus.Standalone;

internal interface IStartupRunKey
{
    string? Read(string valueName);

    byte[]? ReadApproval(string valueName);

    void Write(string valueName, string command);

    void Delete(string valueName);

    void DeleteApproval(string valueName);
}

internal sealed class WindowsStartupRegistration
{
    internal const string InstallerValueName = "Codex Status";
    internal const string SourceValueName = "Codex Status Source";
    internal const string DevelopmentValueName = "Codex Status Development";
    private const string InstallerRegistryKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1";

    private readonly string _executablePath;
    private readonly string _valueName;
    private readonly IStartupRunKey _runKey;

    internal WindowsStartupRegistration(
        string executablePath,
        string valueName,
        IStartupRunKey runKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentNullException.ThrowIfNull(runKey);

        _executablePath = Path.GetFullPath(executablePath);
        _valueName = valueName;
        _runKey = runKey;
    }

    [SupportedOSPlatform("windows")]
    internal static WindowsStartupRegistration CreateForCurrentProcess()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException(
                "The current executable path is unavailable.");
        }

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var runKey = new CurrentUserStartupRunKey();
        var valueName = ResolveValueName(
            executablePath,
            localApplicationData,
            ReadInstallerLocation());
        valueName = FindMatchingOwnedValueName(
            executablePath,
            runKey) ?? valueName;
        return new WindowsStartupRegistration(
            executablePath,
            valueName,
            runKey);
    }

    internal bool IsEnabled()
    {
        var registeredCommand = _runKey.Read(_valueName);
        var registeredExecutable = ExtractExecutablePath(registeredCommand);
        return registeredExecutable is not null &&
            TryGetFullPath(registeredExecutable, out var registeredPath) &&
            string.Equals(
                registeredPath,
                _executablePath,
                StringComparison.OrdinalIgnoreCase) &&
            !IsExplicitlyDisabled(_runKey.ReadApproval(_valueName));
    }

    internal void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _runKey.Write(
                _valueName,
                $"\"{_executablePath}\" {StartupLaunch.Argument}");
            _runKey.DeleteApproval(_valueName);
            return;
        }

        _runKey.Delete(_valueName);
        _runKey.DeleteApproval(_valueName);
    }

    internal static string ResolveValueName(
        string executablePath,
        string localApplicationDataPath,
        string? installerLocation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);

        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!string.IsNullOrWhiteSpace(installerLocation) &&
            IsWithinDirectory(fullExecutablePath, installerLocation))
        {
            return InstallerValueName;
        }

        var programsRoot = Path.Combine(
            Path.GetFullPath(localApplicationDataPath),
            "Programs");
        var sourceRoot = Path.Combine(programsRoot, "Codex Status Source");
        if (IsWithinDirectory(fullExecutablePath, sourceRoot))
        {
            return SourceValueName;
        }

        var installerRoot = Path.Combine(programsRoot, "Codex Status");
        return IsWithinDirectory(fullExecutablePath, installerRoot)
            ? InstallerValueName
            : DevelopmentValueName;
    }

    private static string? FindMatchingOwnedValueName(
        string executablePath,
        IStartupRunKey runKey)
    {
        foreach (var valueName in new[]
                 {
                     InstallerValueName,
                     SourceValueName,
                     DevelopmentValueName,
                 })
        {
            var registeredExecutable = ExtractExecutablePath(
                runKey.Read(valueName));
            if (registeredExecutable is not null &&
                TryGetFullPath(registeredExecutable, out var registeredPath) &&
                string.Equals(
                    registeredPath,
                    Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return valueName;
            }
        }

        return null;
    }

    internal static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var candidate = command.Trim();
        if (candidate[0] == '"')
        {
            var closingQuote = candidate.IndexOf('"', 1);
            return closingQuote > 1
                ? candidate[1..closingQuote]
                : null;
        }

        var executableEnd = candidate.IndexOf(
            ".exe",
            StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0
            ? candidate[..(executableEnd + 4)].Trim()
            : null;
    }

    internal static bool IsExplicitlyDisabled(byte[]? approval) =>
        approval is { Length: > 0 } && approval[0] == 0x03;

    private static bool IsWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = Path
            .GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(
            normalizedDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetFullPath(string path, out string? fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            fullPath = null;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadInstallerLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                InstallerRegistryKeyPath);
            return key?.GetValue(
                "InstallLocation",
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            SecurityException or
            IOException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class CurrentUserStartupRunKey : IStartupRunKey
    {
        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupApprovedRunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        public string? Read(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(
                valueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        public byte[]? ReadApproval(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                StartupApprovedRunKeyPath);
            return key?.GetValue(
                valueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
        }

        public void Write(string valueName, string command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                RunKeyPath,
                writable: true)
                ?? throw new InvalidOperationException(
                    "The Windows startup registry key could not be opened.");
            key.SetValue(valueName, command, RegistryValueKind.String);
        }

        public void Delete(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                RunKeyPath,
                writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }

        public void DeleteApproval(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                StartupApprovedRunKeyPath,
                writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
