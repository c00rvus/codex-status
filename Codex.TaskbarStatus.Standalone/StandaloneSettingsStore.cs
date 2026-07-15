using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Standalone;

public sealed class StandaloneSettings
{
    public const int DefaultAnchorOffsetPx = 64;
    public const int DefaultMonitorIndex = 0;

    public string WidgetSettingsJson { get; set; } = new CodexWidgetSettings().ToJson();
    public int AnchorOffsetPx { get; set; } = DefaultAnchorOffsetPx;
    public int MonitorIndex { get; set; } = DefaultMonitorIndex;
    public string MonitorDeviceName { get; set; } = string.Empty;
    public TaskbarPlacementMode PlacementMode { get; set; } = TaskbarPlacementMode.Automatic;
}

public sealed class StandaloneSettingsStore
{
    private const string SettingsDirectoryName = "CodexTaskbarStatus";
    private const string SettingsFileName = "standalone-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    static StandaloneSettingsStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    private readonly object _syncRoot = new();

    public StandaloneSettingsStore()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    internal StandaloneSettingsStore(string localApplicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);

        SettingsPath = Path.Combine(
            localApplicationDataPath,
            SettingsDirectoryName,
            SettingsFileName);
    }

    public string SettingsPath { get; }

    public StandaloneSettings Load()
    {
        lock (_syncRoot)
        {
            if (File.Exists(SettingsPath))
            {
                return TryReadStandaloneSettings(SettingsPath) ?? CreateDefaultSettings();
            }

            var settings = CreateDefaultSettings();

            // A read-only profile should not prevent the app from starting with defaults.
            try
            {
                SaveCore(settings);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return Normalize(settings);
        }
    }

    public void Save(StandaloneSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_syncRoot)
        {
            SaveCore(settings);
        }
    }

    private void SaveCore(StandaloneSettings settings)
    {
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The standalone settings path has no directory.");

        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            ReplaceAtomically(temporaryPath, SettingsPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            try
            {
                File.Move(temporaryPath, destinationPath);
                return;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another process completed first-run initialization. Replace its complete
                // file below instead of ever exposing a partially written destination.
            }
        }

        try
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch (IOException)
        {
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
    }

    private static StandaloneSettings? TryReadStandaloneSettings(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            var settings = CreateDefaultSettings();
            // Files written by the first standalone prototype had only an
            // explicit pixel anchor. Preserve that exact position on upgrade.
            settings.PlacementMode = TaskbarPlacementMode.Manual;

            if (TryGetProperty(root, "widgetSettingsJson", out var widgetSettings))
            {
                settings.WidgetSettingsJson = widgetSettings.ValueKind switch
                {
                    JsonValueKind.String => widgetSettings.GetString() ?? string.Empty,
                    JsonValueKind.Object => widgetSettings.GetRawText(),
                    _ => settings.WidgetSettingsJson,
                };
            }

            settings.AnchorOffsetPx = ReadNonNegativeInt(
                root,
                "anchorOffsetPx",
                StandaloneSettings.DefaultAnchorOffsetPx);
            settings.MonitorIndex = ReadNonNegativeInt(
                root,
                "monitorIndex",
                StandaloneSettings.DefaultMonitorIndex);
            settings.MonitorDeviceName = ReadString(
                root,
                "monitorDeviceName",
                string.Empty);
            settings.PlacementMode = ReadPlacementMode(
                root,
                "placementMode",
                TaskbarPlacementMode.Manual);

            return Normalize(settings);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static StandaloneSettings Normalize(StandaloneSettings settings)
    {
        return new StandaloneSettings
        {
            WidgetSettingsJson = CodexWidgetSettings
                .FromJson(settings.WidgetSettingsJson)
                .ToJson(),
            AnchorOffsetPx = settings.AnchorOffsetPx >= 0
                ? settings.AnchorOffsetPx
                : StandaloneSettings.DefaultAnchorOffsetPx,
            MonitorIndex = settings.MonitorIndex >= 0
                ? settings.MonitorIndex
                : StandaloneSettings.DefaultMonitorIndex,
            MonitorDeviceName = settings.MonitorDeviceName?.Trim() ?? string.Empty,
            PlacementMode = Enum.IsDefined(settings.PlacementMode)
                ? settings.PlacementMode
                : TaskbarPlacementMode.Automatic,
        };
    }

    private static StandaloneSettings CreateDefaultSettings() => new()
    {
        WidgetSettingsJson = new CodexWidgetSettings().ToJson(),
    };

    private static int ReadNonNegativeInt(JsonElement root, string name, int fallback)
    {
        return TryGetProperty(root, name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var parsed) &&
               parsed >= 0
            ? parsed
            : fallback;
    }

    private static string ReadString(JsonElement root, string name, string fallback)
    {
        return TryGetProperty(root, name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? fallback
            : fallback;
    }

    private static TaskbarPlacementMode ReadPlacementMode(
        JsonElement root,
        string name,
        TaskbarPlacementMode fallback)
    {
        if (!TryGetProperty(root, name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<TaskbarPlacementMode>(
                value.GetString(),
                ignoreCase: true,
                out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(TaskbarPlacementMode), numeric))
        {
            return (TaskbarPlacementMode)numeric;
        }

        return fallback;
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
