using Codex.TaskbarStatus.Core;
using Codex.TaskbarStatus.Standalone.Hosting;
using Codex.TaskbarStatus.Standalone.Widget;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Diagnostics;

namespace Codex.TaskbarStatus.Standalone;

internal sealed class StandaloneHost
{
    private readonly StandaloneSettingsStore _settingsStore = new();
    private readonly CodexStatusWidget _widget = new();
    private StandaloneSettings _settings = new();
    private StandaloneWidgetContext? _context;
    private TaskbarPreviewWindow? _previewWindow;
    private TaskbarHostService? _taskbarHost;
    private FlyoutWindow? _flyoutWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIconService? _trayIcon;
    private DispatcherQueue? _dispatcherQueue;
    private bool _settingsWindowOpening;
    private bool _exiting;

    private readonly bool _openFlyoutOnStart;
    private readonly bool _openSettingsOnStart;

    internal StandaloneHost(bool openFlyoutOnStart, bool openSettingsOnStart)
    {
        _openFlyoutOnStart = openFlyoutOnStart;
        _openSettingsOnStart = openSettingsOnStart;
    }

    internal async Task StartAsync()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Program.ActivationRequested += OnActivationRequested;
        _settings = _settingsStore.Load();
        _context = new StandaloneWidgetContext(_settings.WidgetSettingsJson);
        _context.PreviewRefreshRequested += RefreshPreview;
        _context.OpenFlyoutRequested += ToggleFlyout;
        _context.FlyoutResizeRequested += ResizeFlyout;
        _context.OpenTaskRequested += OpenTask;
        _context.AttentionNotificationRequested += ShowAttentionNotification;

        await _widget.InitializeAsync(_context);
        var previewContent = _widget.CreatePreviewContent()
            ?? throw new InvalidOperationException("The widget did not provide preview content.");
        _previewWindow = new TaskbarPreviewWindow(
            previewContent,
            ToggleFlyout,
            OpenSettings,
            Exit);
        _taskbarHost = new TaskbarHostService(
            _previewWindow,
            () => _widget.PreviewLogicalWidth,
            () => _widget.IsPreviewVisible,
            () => _settings,
            RestartAfterExplorer);
        _taskbarHost.Start();
        try
        {
            _trayIcon = new TrayIconService(
                OpenSettings,
                ShowDetails,
                Restart,
                Exit);
        }
        catch (Exception exception)
        {
            StandaloneLog.Write("Notification-area icon startup failed", exception);
        }

        // RenderPreview measures the actual content and queues a refresh when
        // its calculated width differs from the initial metadata width.
        previewContent.DispatcherQueue.TryEnqueue(RefreshPreview);
        StandaloneLog.Write(
            $"Standalone host started. Settings={_settingsStore.SettingsPath}");

        if (_openSettingsOnStart)
        {
            OpenSettings();
        }
        else if (_openFlyoutOnStart)
        {
            ToggleFlyout();
        }
    }

    private void RefreshPreview()
    {
        _taskbarHost?.Refresh();
    }

    private void OnActivationRequested(Program.ActivationRequest request)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_exiting)
            {
                return;
            }

            if (request == Program.ActivationRequest.OpenFlyout)
            {
                ToggleFlyout();
            }
            else
            {
                OpenSettings();
            }
        });
    }

    private void ToggleFlyout()
    {
        if (_exiting || _previewWindow is null)
        {
            return;
        }

        if (!EnsureFlyout())
        {
            return;
        }

        _flyoutWindow!.Toggle(
            _previewWindow.ScreenBounds,
            _previewWindow.Dpi);
    }

    private void ShowDetails()
    {
        if (_exiting ||
            _previewWindow is null ||
            !EnsureFlyout() ||
            _flyoutWindow is null)
        {
            return;
        }

        _flyoutWindow.Show(
            _previewWindow.ScreenBounds,
            _previewWindow.Dpi);
    }

    private void ResizeFlyout(int logicalHeight)
    {
        if (_exiting)
        {
            return;
        }

        _flyoutWindow?.UpdateLogicalHeight(logicalHeight);
    }

    private void OpenTask(string sessionId)
    {
        if (_exiting || !CodexTaskDeepLink.TryCreate(sessionId, out var uri) || uri is null)
        {
            return;
        }

        _flyoutWindow?.Hide();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
            StandaloneLog.Write($"Opened Codex task {sessionId}.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StandaloneLog.Write("Opening the Codex task failed", exception);
            _trayIcon?.ShowNotification(
                "Could not open Codex",
                "Open Codex manually and select the task from its history.",
                isError: true,
                ShowDetails);
        }
    }

    private void ShowAttentionNotification(WidgetAttentionNotification notification)
    {
        if (_exiting)
        {
            return;
        }

        _trayIcon?.ShowNotification(
            notification.IsError ? "Codex task failed" : "Codex needs attention",
            $"{notification.Title}\n{notification.Message}",
            notification.IsError,
            ShowDetails);
    }

    private bool EnsureFlyout()
    {
        if (_flyoutWindow is not null)
        {
            return true;
        }

        var previewWindow = _previewWindow;
        if (previewWindow is null)
        {
            return false;
        }

        var flyoutContent = _widget.CreateFlyoutContent();
        if (flyoutContent is null)
        {
            return false;
        }

        _flyoutWindow = new FlyoutWindow(
            flyoutContent,
            _widget.FlyoutWidth,
            _widget.FlyoutHeight,
            previewWindow.WindowHandle,
            OpenSettings,
            _widget.OnFlyoutVisibilityChanged);
        return true;
    }

    private void OpenSettings()
    {
        if (_exiting)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _flyoutWindow?.Hide();
            _settingsWindow.BringToFront();
            return;
        }

        if (_settingsWindowOpening)
        {
            return;
        }

        _settingsWindowOpening = true;
        SettingsWindow? createdWindow = null;
        try
        {
            _flyoutWindow?.Hide();
            var originalSettings = _context?.SettingsJson ?? _settings.WidgetSettingsJson;
            var originalStandaloneSettings = CloneSettings(_settings);
            var placementDraft = ToPlacementDraft(_settings);
            createdWindow = new SettingsWindow(
                _widget,
                originalSettings,
                placementDraft,
                GetMonitorOptions(),
                draftJson =>
                {
                    _widget.OnSettingsDraftChanged(draftJson);
                    RefreshPreview();
                },
                ApplyPlacementDraft,
                SaveSettings,
                () =>
                {
                    if (!ReferenceEquals(_settingsWindow, createdWindow))
                    {
                        return;
                    }

                    _settings = originalStandaloneSettings;
                    _widget.OnSettingsDraftChanged(originalSettings);
                    RefreshPreview();
                },
                () =>
                {
                    if (ReferenceEquals(_settingsWindow, createdWindow))
                    {
                        _settingsWindow = null;
                    }
                });
            _settingsWindow = createdWindow;
            createdWindow.ShowCentered();
        }
        catch
        {
            if (createdWindow is not null)
            {
                try
                {
                    createdWindow.Close();
                }
                catch (Exception closeException)
                {
                    StandaloneLog.Write(
                        "Closing the settings window after an activation failure failed",
                        closeException);
                }
            }

            if (ReferenceEquals(_settingsWindow, createdWindow))
            {
                _settingsWindow = null;
            }

            throw;
        }
        finally
        {
            _settingsWindowOpening = false;
        }
    }

    private void SaveSettings(
        string settingsJson,
        StandalonePlacementDraft placementDraft)
    {
        var normalized = CodexWidgetSettings.FromJson(settingsJson).ToJson();
        ApplyPlacementDraft(placementDraft);
        _settings.WidgetSettingsJson = normalized;
        _settingsStore.Save(_settings);
        _context?.ReplaceSettings(normalized);
        _widget.OnSettingsDraftChanged(normalized);
        RefreshPreview();
        StandaloneLog.Write("Standalone settings saved.");
    }

    private void ApplyPlacementDraft(StandalonePlacementDraft draft)
    {
        var normalized = draft.Normalize();
        var selectedTaskbar = NativeMethods.GetTaskbars().FirstOrDefault(taskbar =>
            !string.IsNullOrWhiteSpace(normalized.MonitorDeviceName)
                ? string.Equals(
                    taskbar.DeviceName,
                    normalized.MonitorDeviceName,
                    StringComparison.OrdinalIgnoreCase)
                : taskbar.Index == normalized.MonitorIndex);

        _settings.MonitorIndex = selectedTaskbar.Window != nint.Zero
            ? selectedTaskbar.Index
            : normalized.MonitorIndex;
        _settings.MonitorDeviceName = selectedTaskbar.Window != nint.Zero
            ? selectedTaskbar.DeviceName
            : normalized.MonitorDeviceName;
        _settings.PlacementMode = normalized.Mode;
        _settings.AnchorOffsetPx = normalized.ManualOffsetPx;
        RefreshPreview();
    }

    private static StandalonePlacementDraft ToPlacementDraft(StandaloneSettings settings) => new(
        settings.MonitorIndex,
        settings.MonitorDeviceName,
        settings.PlacementMode,
        settings.AnchorOffsetPx);

    private static StandaloneSettings CloneSettings(StandaloneSettings settings) => new()
    {
        WidgetSettingsJson = settings.WidgetSettingsJson,
        AnchorOffsetPx = settings.AnchorOffsetPx,
        MonitorIndex = settings.MonitorIndex,
        MonitorDeviceName = settings.MonitorDeviceName,
        PlacementMode = settings.PlacementMode,
    };

    private static IReadOnlyList<TaskbarMonitorOption> GetMonitorOptions()
    {
        return NativeMethods.GetTaskbars()
            .Select(taskbar => new TaskbarMonitorOption(
                taskbar.Index,
                $"Monitor {taskbar.Index + 1} — " +
                $"{taskbar.MonitorBounds.Width} × {taskbar.MonitorBounds.Height}",
                taskbar.IsPrimary,
                IsAvailable: true,
                DeviceName: taskbar.DeviceName,
                WidthPx: taskbar.MonitorBounds.Width))
            .ToArray();
    }

    private async void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Program.ActivationRequested -= OnActivationRequested;
        try
        {
            _taskbarHost?.Stop();
            _trayIcon?.Dispose();
            _trayIcon = null;
            _previewWindow?.Hide();
            _flyoutWindow?.Dispose();
            _flyoutWindow = null;
            await _widget.DisposeAsync();
        }
        catch (Exception exception)
        {
            StandaloneLog.Write("Standalone shutdown failed", exception);
        }
        finally
        {
            Application.Current.Exit();
        }
    }

    private void RestartAfterExplorer() => Restart("Explorer taskbar recreation");

    private void Restart() => Restart("notification-area menu");

    private async void Restart(string reason)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Program.ActivationRequested -= OnActivationRequested;
        _taskbarHost?.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _flyoutWindow?.Dispose();
        _flyoutWindow = null;

        try
        {
            StandaloneLog.Write($"Relaunching after {reason}.");
            Program.Relaunch();
        }
        catch (Exception exception)
        {
            StandaloneLog.Write("Starting the Explorer recovery process failed", exception);
        }

        try
        {
            await _widget.DisposeAsync();
        }
        catch (Exception exception)
        {
            StandaloneLog.Write("Standalone restart cleanup failed", exception);
        }
        finally
        {
            Application.Current.Exit();
        }
    }
}
