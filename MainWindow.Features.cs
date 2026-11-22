using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using VajraPulse.Core;
using VajraPulse.Services;

namespace VajraPulse;

public partial class MainWindow
{
    private readonly FileShelfService _shelf = new();
    private readonly CalendarService _calendar = new();
    private readonly NotificationListenerService _notifications = new();
    private readonly ActivityCoordinator _desktopActivities = new();
    private readonly PrivacyUsageService _privacyUsage = new();
    private readonly DownloadProgressService _downloads = new();
    private readonly BluetoothBatteryService _bluetoothBattery = new(TimeSpan.FromSeconds(4));
    private readonly Dictionary<string, BluetoothBatteryDevice> _bluetoothDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bluetoothLowNotified = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PulseNotification> _recentAlerts = new();
    private readonly HashSet<string> _remindedMeetings = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _brightnessDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _featuresInitialized, _featuresModal, _featureTickBusy, _brightnessBusy, _brightnessSending;
    private bool _seekPending, _seekSending, _brightnessPending, _dropHover, _shelfDragging, _privacyBusy, _bluetoothBusy, _bluetoothInitialized;
    private string? _lastDeviceId;
    private string? _trackedDownloadId, _displayedDownloadId, _renderedDesktopActivityId, _privacySettingsUri;
    private bool? _lastMuted;
    private bool _audioSnapshotTaken, _wasAudioAvailable;
    private BrightnessSnapshot? _brightness;
    private CalendarMeeting? _nextMeeting;
    private Point _shelfDragStart;
    private int _alertIndex, _animationCycle, _liveSerial, _liveRenderedSerial = -1;
    private LiveKind _liveKind;
    private string _liveTitle = "", _liveDetail = "";
    private double? _liveLevel;
    private enum LiveKind { Text, Charging, Volume, Brightness, Headphones, Calendar, Inbox, Privacy, Download, Bluetooth }

    private async Task InitializeFeaturesAsync()
    {
        _featuresInitialized = true;
        _shelf.Add(_settings.ShelfPaths);
        ShelfList.ItemsSource = _shelf.Files;
        UpdateShelf();
        if (!string.IsNullOrWhiteSpace(_settings.CalendarPath))
        {
            var result = await Task.Run(() => _calendar.LoadFromFile(_settings.CalendarPath));
            if (_closed) return;
            if (result.Error != null) MeetingStatus.Text = result.Error;
        }
        UpdateCalendar(false);
        _seekDebounce.Tick += async (_, _) => { if (!MediaSeekSlider.IsMouseCaptureWithin) await CommitSeek(); };
        _brightnessDebounce.Tick += async (_, _) => { if (!BrightnessSlider.IsMouseCaptureWithin) await CommitBrightness(); };
        LiveStatePanel.MouseLeftButtonUp += (_, _) =>
        {
            if (_liveKind is LiveKind.Privacy or LiveKind.Download or LiveKind.Bluetooth) OpenDesktopActivity();
            else if (_liveKind is LiveKind.Volume or LiveKind.Brightness or LiveKind.Headphones) OpenSettings();
            else { if (_liveKind == LiveKind.Calendar) SetActivity(Activity.Calendar); else if (_liveKind == LiveKind.Inbox) SetActivity(Activity.Inbox); Expand(true); }
        };
        await RefreshBrightnessAsync();
        if (_closed) return;
        await RefreshPrivacyActivityAsync();
        if (_closed) return;
        RefreshDownloadActivity();
        RenderDesktopActivity();
        await InitializeToolsAsync();
        if (_closed) return;
        if (_settings.NotificationsEnabled)
        {
            var access = await _notifications.EnableAsync(false);
            _settings.NotificationsEnabled = access.Allowed;
            if (access.Allowed) await SyncNotifications();
        }
        LoadFeaturePreferences();
    }

    private void DisposeFeatures()
    {
        _seekDebounce.Stop(); _brightnessDebounce.Stop();
        _notifications.Dispose(); _privacyUsage.Dispose(); _downloads.Dispose();
        DisposeTools();
        _recentAlerts.Clear();
    }
    private void CaptureFeaturePreferences() { if (_featuresInitialized) _settings.ShelfPaths = _shelf.SavedPaths(); }
    private void LoadFeaturePreferences()
    {
        NotificationsCheck.IsChecked = _settings.NotificationsEnabled && _notifications.Enabled;
        HideNotificationCheck.IsChecked = _settings.HideNotificationContent;
        AllNotificationsCheck.IsChecked = _settings.NotifyAllApps;
        NotificationStatus.Text = _notifications.Status;
    }
    private async Task TickFeaturesAsync()
    {
        if (_featureTickBusy || _closed) return;
        _featureTickBusy = true;
        try
        {
            if (_ticks % 4 == 0) await RefreshBrightnessAsync();
            if (_closed) return;
            if (_ticks % 4 == 0) await RefreshPrivacyActivityAsync();
            if (_closed) return;
            if (_ticks % 30 == 1) await RefreshBluetoothActivityAsync();
            if (_closed) return;
            RefreshDownloadActivity();
            _desktopActivities.Tick();
            RenderDesktopActivity();
            await TickToolsAsync();
            if (_closed) return;
            if (_ticks % 5 == 0 && _settings.NotificationsEnabled) await SyncNotifications();
            if (_closed) return;
            if (_ticks % 15 == 0) UpdateCalendar(true);
        }
        finally { _featureTickBusy = false; }
    }

    private async Task RefreshPrivacyActivityAsync()
    {
        if (_privacyBusy || _closed) return;
        _privacyBusy = true;
        try
        {
            PrivacyUsageSnapshot snapshot = await Task.Run(_privacyUsage.Read);
            if (_closed) return;

            PrivacyDeviceUsage[] active = snapshot.Usages.Where(usage => usage.IsInUse).ToArray();
            if (active.Length == 0)
            {
                _desktopActivities.Remove("privacy:active");
                _privacySettingsUri = null;
                return;
            }

            bool microphone = active.Any(usage => usage.Kind == PrivacyDeviceKind.Microphone);
            bool camera = active.Any(usage => usage.Kind == PrivacyDeviceKind.Camera);
            string title = microphone && camera
                ? "Camera and microphone in use"
                : camera ? "Camera in use" : "Microphone in use";
            string[] apps = active
                .Select(usage => usage.AppName ?? "Windows app")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            string detail = string.Join(" · ", apps);
            if (active.Select(usage => usage.AppName ?? "Windows app").Distinct(StringComparer.OrdinalIgnoreCase).Skip(2).Any())
                detail += " · more";

            _privacySettingsUri = camera && !microphone
                ? PrivacyUsageService.CameraSettingsUri
                : PrivacyUsageService.MicrophoneSettingsUri;
            _desktopActivities.Upsert(new PulseActivity(
                "privacy:active",
                PulseActivityKind.Privacy,
                title,
                BoundActivityText(detail),
                DateTimeOffset.UtcNow,
                isPersistent: true));
        }
        finally { _privacyBusy = false; }
    }

    private async Task RefreshBluetoothActivityAsync()
    {
        if (_bluetoothBusy || _closed) return;
        _bluetoothBusy = true;
        try
        {
            BluetoothBatterySnapshot snapshot = await _bluetoothBattery.PollAsync();
            if (_closed) return;
            UpdateBluetoothRail(snapshot);
            if (!snapshot.IsSupported) return;

            BluetoothBatteryDevice[] connected = snapshot.Devices.Where(device => device.IsConnected).ToArray();
            foreach (BluetoothBatteryDevice device in connected)
            {
                _bluetoothDevices.TryGetValue(device.Id, out BluetoothBatteryDevice? previous);
                if (device.Percent is <= 20 && _bluetoothLowNotified.Add(device.Id))
                {
                    AddBluetoothActivity("Bluetooth battery low", $"{device.Name} · {device.Percent}%", 8);
                }
                else if (_bluetoothInitialized && previous?.IsConnected != true)
                {
                    string detail = device.Percent.HasValue ? $"{device.Name} · {device.Percent}%" : device.Name;
                    AddBluetoothActivity("Bluetooth connected", detail, 5);
                }

                if (device.Percent is > 25) _bluetoothLowNotified.Remove(device.Id);
            }

            if (_bluetoothInitialized)
            {
                foreach (BluetoothBatteryDevice previous in _bluetoothDevices.Values.Where(device => device.IsConnected))
                {
                    if (!connected.Any(device => string.Equals(device.Id, previous.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        _bluetoothLowNotified.Remove(previous.Id);
                        AddBluetoothActivity("Bluetooth disconnected", previous.Name, 5);
                    }
                }
            }

            _bluetoothDevices.Clear();
            foreach (BluetoothBatteryDevice device in snapshot.Devices) _bluetoothDevices[device.Id] = device;
            _bluetoothInitialized = true;
        }
        finally { _bluetoothBusy = false; }
    }

    private void AddBluetoothActivity(string title, string detail, int seconds)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _desktopActivities.Upsert(new PulseActivity(
            "bluetooth:event",
            PulseActivityKind.Bluetooth,
            title,
            BoundActivityText(detail),
            now,
            expiresAt: now.AddSeconds(seconds)));
    }

    private void RefreshDownloadActivity()
    {
        DownloadActivity? active = _downloads.Snapshot
            .Where(download => download.State is DownloadState.Active or DownloadState.Stalled)
            .OrderByDescending(download => download.StartedAt)
            .FirstOrDefault();

        if (active != null)
        {
            if (_trackedDownloadId != null && !string.Equals(_trackedDownloadId, active.Id, StringComparison.Ordinal))
                _desktopActivities.Remove("download:" + _trackedDownloadId);

            _trackedDownloadId = active.Id;
            _displayedDownloadId = active.Id;
            string title = active.State == DownloadState.Stalled ? "Download paused" : "Downloading";
            string detail = $"{active.FileName} · {FormatByteCount(active.BytesReceived)}";
            _desktopActivities.Upsert(new PulseActivity(
                "download:" + active.Id,
                PulseActivityKind.Download,
                title,
                BoundActivityText(detail),
                active.StartedAt,
                isPersistent: true,
                isIndeterminate: true));
            return;
        }

        if (_trackedDownloadId == null) return;

        string completedId = _trackedDownloadId;
        _desktopActivities.Remove("download:" + completedId);
        DownloadActivity? terminal = _downloads.Snapshot.FirstOrDefault(download => download.Id == completedId);
        _trackedDownloadId = null;
        if (terminal?.State is not (DownloadState.Completed or DownloadState.Failed)) return;

        _displayedDownloadId = terminal.Id;
        bool completed = terminal.State == DownloadState.Completed;
        _desktopActivities.Upsert(new PulseActivity(
            "download-result:" + terminal.Id,
            PulseActivityKind.Download,
            completed ? "Download complete" : "Download failed",
            BoundActivityText(terminal.FileName),
            terminal.StateChangedAt,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(6)));
    }

    private void RenderDesktopActivity()
    {
        PulseActivity? activity = _desktopActivities.Current;
        if (activity == null)
        {
            if (_liveKind is LiveKind.Privacy or LiveKind.Download or LiveKind.Bluetooth)
            {
                _notice = null;
                _liveKind = LiveKind.Text;
                _renderedDesktopActivityId = null;
                ApplyMode();
            }
            return;
        }

        LiveKind kind = activity.Kind switch
        {
            PulseActivityKind.Privacy => LiveKind.Privacy,
            PulseActivityKind.Bluetooth => LiveKind.Bluetooth,
            _ => LiveKind.Download
        };
        bool mustInterrupt = kind == LiveKind.Privacy;
        bool alreadyShowingDesktopActivity = _liveKind is LiveKind.Privacy or LiveKind.Download or LiveKind.Bluetooth;
        if (!mustInterrupt && _notice != null && !alreadyShowingDesktopActivity) return;

        if (string.Equals(_renderedDesktopActivityId, activity.Id, StringComparison.Ordinal) && _liveKind == kind)
        {
            _liveTitle = activity.Title;
            _liveDetail = activity.Detail;
            _liveLevel = activity.Progress;
            _notice = string.IsNullOrWhiteSpace(activity.Detail) ? activity.Title : activity.Title + " · " + activity.Detail;
            _noticeUntil = DateTimeOffset.UtcNow.AddSeconds(3);
            UpdateCompact();
            return;
        }

        _renderedDesktopActivityId = activity.Id;
        ShowLiveState(kind, activity.Title, activity.Detail, activity.Progress, 3);
    }

    private void OpenDesktopActivity()
    {
        try
        {
            if (_liveKind == LiveKind.Privacy && _privacySettingsUri != null)
            {
                Process.Start(new ProcessStartInfo(_privacySettingsUri) { UseShellExecute = true });
                return;
            }
            if (_liveKind == LiveKind.Bluetooth)
            {
                OpenWindowsSettings("ms-settings:bluetooth");
                return;
            }

            if (_liveKind != LiveKind.Download || _displayedDownloadId == null) return;
            DownloadRevealTarget? target = _downloads.RevealTarget(_displayedDownloadId);
            if (target == null) return;
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(target.ItemPath == null ? target.DirectoryPath : "/select," + target.ItemPath);
            Process.Start(start);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
    }

    private static string BoundActivityText(string value) =>
        value.Length <= PulseActivity.MaximumDetailLength
            ? value
            : value[..(PulseActivity.MaximumDetailLength - 1)] + "…";

    private static string FormatByteCount(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.#} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
    }

    private void ShowLiveState(LiveKind kind, string title, string detail, double? level, int seconds)
    {
        if (kind != LiveKind.Privacy && _desktopActivities.Current?.Kind == PulseActivityKind.Privacy) return;
        _liveKind = kind; _liveTitle = title; _liveDetail = detail;
        _liveLevel = level.HasValue ? Math.Clamp(level.Value, 0, 1) : null;
        _liveSerial++;
        _notice = string.IsNullOrWhiteSpace(detail) ? title : title + " · " + detail;
        _noticeUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        UpdateCompact();
    }
    private void UpdateLiveStateVisual()
    {
        if (_liveKind == LiveKind.Text || _notice == null || _liveRenderedSerial == _liveSerial) return;
        _liveRenderedSerial = _liveSerial;
        string color = _liveKind switch
        {
            LiveKind.Privacy => "DangerBrush",
            LiveKind.Charging or LiveKind.Download => "MintBrush",
            LiveKind.Calendar => "TimerBrush",
            _ => "TextBrush"
        };
        Brush brush = (Brush)FindResource(color);
        LiveStateTitle.Text = _liveTitle; LiveStateDetail.Text = _liveDetail;
        LiveStateIcon.Foreground = LiveStateValue.Foreground = LiveStateFill.Background = brush;
        LiveStateIcon.Text = _liveKind switch { LiveKind.Charging => "", LiveKind.Brightness => "", LiveKind.Headphones => "", LiveKind.Calendar => "", LiveKind.Inbox => "", LiveKind.Privacy => "", LiveKind.Download => "", LiveKind.Bluetooth => "", _ => _audio.IsMuted ? "" : "" };
        LiveStateTrack.Visibility = _liveLevel.HasValue ? Visibility.Visible : Visibility.Hidden;
        LiveStateValue.Text = _liveLevel.HasValue ? Math.Round(_liveLevel.Value * 100) + "%" : "";
        LiveStateFill.BeginAnimation(WidthProperty, null);
        double width = 82 * (_liveLevel ?? 0);
        LiveStateFill.Width = width;
        if (_liveLevel.HasValue && !_settings.ReducedMotion)
            LiveStateFill.BeginAnimation(WidthProperty, new DoubleAnimation(_liveKind == LiveKind.Charging ? 0 : Math.Max(0, width - 12), width, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void TestLiveState()
    {
        Collapse();
        switch (_animationCycle++ % 4)
        {
            case 0: ShowLiveState(LiveKind.Charging, "Charging animation test", "No power state changed", _battery?.Percent >= 0 ? _battery.Percent / 100d : null, 4); break;
            case 1: ShowLiveState(LiveKind.Volume, "Volume animation test", "No audio settings changed", _audio.IsAvailable ? _audio.Volume : null, 4); break;
            case 2: ShowLiveState(LiveKind.Brightness, "Brightness animation test", "No display settings changed", _brightness?.IsAvailable == true ? _brightness.Percent / 100d : null, 4); break;
            default: ShowLiveState(LiveKind.Headphones, "Headphone animation test", "No connection state changed", null, 4); break;
        }
    }
    private void DetectAudioChanges()
    {
        if (_audio.IsAvailable)
        {
            if ((_lastDeviceId != null && _lastDeviceId != _audio.DeviceId) || (_audioSnapshotTaken && !_wasAudioAvailable))
                ShowLiveState(_audio.IsHeadphones ? LiveKind.Headphones : LiveKind.Volume, _audio.IsHeadphones ? "Headphones active" : "Audio output changed", _audio.DeviceName, null, 4);
            if (_lastMuted.HasValue && _lastMuted.Value != _audio.IsMuted)
                ShowLiveState(LiveKind.Volume, _audio.IsMuted ? "Muted" : "Sound on", _audio.DeviceName, _audio.IsMuted ? 0 : _audio.Volume, 2);
            _lastDeviceId = _audio.DeviceId;
            _lastMuted = _audio.IsMuted;
        }
        _audioSnapshotTaken = true;
        _wasAudioAvailable = _audio.IsAvailable;
    }

    private async Task RefreshBrightnessAsync()
    {
        if (_brightnessBusy) return;
        _brightnessBusy = true;
        try
        {
            var brightness = await Task.Run(BrightnessService.Read);
            if (_closed) return;
            if (_brightness?.IsAvailable == true && brightness.IsAvailable && Math.Abs(brightness.Percent - _brightness.Percent) >= 1 && !BrightnessSlider.IsMouseCaptureWithin && !_brightnessPending && !_brightnessSending)
                ShowLiveState(LiveKind.Brightness, "Brightness", "Internal display", brightness.Percent / 100d, 2);
            _brightness = brightness;
            _updating = true;
            if (!_brightnessPending && !BrightnessSlider.IsMouseCaptureWithin) BrightnessSlider.Value = brightness.Percent;
            BrightnessSlider.IsEnabled = brightness.IsAvailable && !_brightnessSending;
            BrightnessValue.Text = brightness.IsAvailable ? brightness.Percent + "%" : "N/A";
            BrightnessStatus.Text = brightness.IsAvailable ? "Internal display. Changes appear briefly in the island." : "This display does not expose Windows brightness control.";
            _updating = false;
        }
        finally { _brightnessBusy = false; }
    }
    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating || _brightness?.IsAvailable != true) return;
        _brightnessPending = true;
        BrightnessValue.Text = Math.Round(e.NewValue) + "%";
        _brightnessDebounce.Stop(); _brightnessDebounce.Start();
    }
    private async Task CommitBrightness()
    {
        _brightnessDebounce.Stop();
        if (!_brightnessPending || _brightnessSending || _closed) return;
        _brightnessPending = false; _brightnessSending = true;
        try
        {
            int requested = (int)Math.Round(BrightnessSlider.Value);
            var changed = await Task.Run(() => BrightnessService.Set(requested));
            if (_closed) return;
            _brightness = changed;
            if (changed.IsAvailable) ShowLiveState(LiveKind.Brightness, "Brightness", "Internal display", changed.Percent / 100d, 2);
            else BrightnessStatus.Text = "Windows could not change brightness on this display.";
        }
        finally { _brightnessSending = false; }
        await RefreshBrightnessAsync();
    }
    private async void Brightness_MouseUp(object sender, MouseButtonEventArgs e) => await CommitBrightness();
    private async void Brightness_KeyUp(object sender, KeyEventArgs e) => await CommitBrightness();

    private void Seek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating || !_media.Snapshot.CanSeek) return;
        _seekPending = true;
        _seekDebounce.Stop(); _seekDebounce.Start();
        UpdateProgress();
    }
    private async Task CommitSeek()
    {
        _seekDebounce.Stop();
        if (!_seekPending || _seekSending || _closed) return;
        _seekPending = false; _seekSending = true;
        try
        {
            TimeSpan position = TimeSpan.FromSeconds(MediaSeekSlider.Value * _media.Snapshot.Duration.TotalSeconds);
            if (!await _media.SeekAsync(position) && !_closed) Notify("Seeking unavailable", "This player rejected the request", 3);
        }
        finally { _seekSending = false; }
        if (!_closed) UpdateProgress();
    }
    private async void Seek_MouseUp(object sender, MouseButtonEventArgs e) => await CommitSeek();
    private async void Seek_KeyUp(object sender, KeyEventArgs e) => await CommitSeek();

    private void FilesTab_Click(object sender, RoutedEventArgs e) { UpdateShelf(); SetActivity(Activity.Shelf); }
    private void CalendarTab_Click(object sender, RoutedEventArgs e) { UpdateCalendar(false); SetActivity(Activity.Calendar); }
    private void InboxTab_Click(object sender, RoutedEventArgs e) { UpdateInbox(); SetActivity(Activity.Inbox); }
    private void UpdateShelf()
    {
        ShelfEmptyText.Visibility = _shelf.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_activity == Activity.Shelf) UpdateActivity();
    }
    private void Shelf_Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Add to Vajra file shelf", Multiselect = true, CheckFileExists = true, Filter = "All files|*.*" };
        _featuresModal = true;
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            int added = _shelf.Add(dialog.FileNames);
            SavePreferences(); UpdateShelf(); SetActivity(Activity.Shelf); Expand(true);
            if (added == 0) Notify("No files added", "Duplicates, missing files, or shelf full", 3);
        }
        finally { _featuresModal = false; }
    }
    private void Shelf_Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ShelfFile file }) { _shelf.Remove(file); SavePreferences(); UpdateShelf(); }
        e.Handled = true;
    }
    private void Shelf_Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ShelfList.SelectedItem is not ShelfFile file) { Notify("Select a file", "Then choose Show folder", 3); return; }
        string? directory = file.IsDirectory ? file.Path : Path.GetDirectoryName(file.Path);
        if (directory == null || !Directory.Exists(directory)) { Notify("Folder unavailable", "The original file may have moved", 3); return; }
        try { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; start.ArgumentList.Add(directory); Process.Start(start); }
        catch (Win32Exception) { Notify("Could not open folder", "Try File Explorer", 3); }
    }
    private void Shelf_MouseDown(object sender, MouseButtonEventArgs e) => _shelfDragStart = e.GetPosition(ShelfList);
    private void Shelf_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _shelfDragging || ShelfList.SelectedItem is not ShelfFile file || !file.Exists) return;
        Point current = e.GetPosition(ShelfList);
        if (Math.Abs(current.X - _shelfDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - _shelfDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _shelfDragging = true;
        try { DragDrop.DoDragDrop(ShelfList, new DataObject(DataFormats.FileDrop, new[] { file.Path }), DragDropEffects.Copy | DragDropEffects.Link); }
        finally { _shelfDragging = false; _dropHover = false; if (!IsPointerOverInteractiveChrome()) Collapse(); }
    }
    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (_shelfDragging || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _dropHover = true; _leave.Stop(); SetActivity(Activity.Shelf); Expand(false);
        e.Effects = DragDropEffects.Copy; e.Handled = true;
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (_shelfDragging || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _dropHover = true; _leave.Stop();
        e.Effects = DragDropEffects.Copy; e.Handled = true;
    }
    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        Point point = e.GetPosition(Island);
        if (new Rect(0, 0, Island.ActualWidth, Island.ActualHeight).Contains(point)) return;
        _dropHover = false;
        if (!_pinned) _leave.Start();
    }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_shelfDragging || !e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        _dropHover = false;
        int added = _shelf.Add(paths);
        SavePreferences(); UpdateShelf(); SetActivity(Activity.Shelf); Expand(true);
        if (added == 0) Notify("No files added", "Duplicates, missing files, or shelf full", 3);
        e.Effects = DragDropEffects.Copy; e.Handled = true;
    }

    private void UpdateCalendar(bool remind)
    {
        var now = DateTimeOffset.Now;
        _nextMeeting = _calendar.GetUpcoming(now, 50).FirstOrDefault(m => !m.IsAllDay);
        if (_nextMeeting == null)
        {
            MeetingStatus.Text = string.IsNullOrWhiteSpace(_settings.CalendarPath) ? "No calendar connected" : "No timed meetings in the next 30 days";
            MeetingTitle.Text = string.IsNullOrWhiteSpace(_settings.CalendarPath) ? "Import a calendar .ics file" : "Your calendar is clear";
            MeetingWhen.Text = "Local import. No account access.";
            JoinMeetingButton.IsEnabled = false;
            return;
        }
        var meeting = _nextMeeting;
        double minutes = (meeting.Start - now).TotalMinutes;
        MeetingStatus.Text = minutes <= 0 ? "In progress" : $"Starts in {Math.Ceiling(minutes)} min";
        MeetingTitle.Text = string.IsNullOrWhiteSpace(meeting.Title) ? "Untitled calendar event" : meeting.Title;
        MeetingWhen.Text = meeting.Start.LocalDateTime.ToString("ddd, MMM d · HH:mm") + " – " + meeting.End.LocalDateTime.ToString("HH:mm");
        JoinMeetingButton.IsEnabled = meeting.JoinUrl != null;
        if (remind && minutes is > 0 && minutes <= _settings.MeetingReminderMinutes && _remindedMeetings.Add(meeting.Id))
            ShowLiveState(LiveKind.Calendar, "Meeting in " + Math.Ceiling(minutes) + " min", MeetingTitle.Text, null, 8);
    }
    private async void Calendar_Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import local calendar", Filter = "Calendar files|*.ics", CheckFileExists = true };
        _featuresModal = true;
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            var result = await Task.Run(() => _calendar.LoadFromFile(dialog.FileName));
            if (_closed) return;
            if (result.ImportedCount == 0) { Notify("Calendar was not imported", result.Error ?? $"{result.UnsupportedCount} unsupported events", 5); return; }
            _settings.CalendarPath = dialog.FileName;
            _remindedMeetings.Clear();
            SavePreferences(); UpdateCalendar(false); SetActivity(Activity.Calendar); Expand(true);
            if (result.UnsupportedCount > 0 || result.Error != null)
                Notify($"Imported {result.ImportedCount} events", $"{result.UnsupportedCount} unsupported. " + result.Error, 6);
        }
        finally { _featuresModal = false; }
    }
    private void Calendar_Clear_Click(object sender, RoutedEventArgs e)
    {
        _calendar.Clear(); _settings.CalendarPath = ""; _remindedMeetings.Clear();
        SavePreferences(); UpdateCalendar(false);
    }
    private void Calendar_Join_Click(object sender, RoutedEventArgs e)
    {
        if (_nextMeeting?.JoinUrl is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { Notify("Could not open meeting", "Check your browser", 3); }
    }

    private async void Notifications_Enable_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (NotificationsCheck.IsChecked != true)
        {
            _notifications.Disable(); _settings.NotificationsEnabled = false;
            _recentAlerts.Clear(); NotificationAppChoice.ItemsSource = null;
            if (_liveKind == LiveKind.Inbox) { _notice = null; _liveKind = LiveKind.Text; ApplyMode(); }
            SavePreferences(); UpdateInbox();
            NotificationStatus.Text = _notifications.Status;
            return;
        }
        _featuresModal = true;
        try
        {
            bool request = NotificationListenerService.HasPackageIdentity;
            if (request && MessageBox.Show(this, "Windows will ask whether Vajra Pulse may read notifications. Previews stay local, message content is hidden by default, and you choose which apps are allowed. Continue?", "Notification privacy", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            {
                _notifications.Disable(); _settings.NotificationsEnabled = false;
                _updating = true; NotificationsCheck.IsChecked = false; _updating = false;
                NotificationStatus.Text = "Notification setup cancelled.";
                SavePreferences();
                return;
            }
            var result = await _notifications.EnableAsync(request);
            if (_closed) return;
            _settings.NotificationsEnabled = result.Allowed;
            _updating = true; NotificationsCheck.IsChecked = result.Allowed; _updating = false;
            NotificationStatus.Text = result.Message;
            SavePreferences();
            if (result.Allowed) await SyncNotifications();
        }
        finally { _featuresModal = false; }
    }
    private void NotificationPrivacy_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _settings.HideNotificationContent = HideNotificationCheck.IsChecked == true;
        _settings.NotifyAllApps = AllNotificationsCheck.IsChecked == true;
        _recentAlerts.Clear();
        if (_liveKind == LiveKind.Inbox) { _notice = null; _liveKind = LiveKind.Text; ApplyMode(); }
        UpdateInbox(); SavePreferences();
    }
    private void NotificationApp_Add_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationAppChoice.SelectedItem is not NotificationApp app) return;
        _settings.NotificationAppIds = _settings.NotificationAppIds.Append(app.Id).Distinct(StringComparer.Ordinal).ToArray();
        SavePreferences(); NotificationStatus.Text = $"{_settings.NotificationAppIds.Length} selected app(s).";
    }
    private void NotificationApp_Remove_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationAppChoice.SelectedItem is not NotificationApp app) return;
        _settings.NotificationAppIds = _settings.NotificationAppIds.Where(id => id != app.Id).ToArray();
        _recentAlerts.RemoveAll(n => n.AppId == app.Id);
        SavePreferences(); UpdateInbox(); NotificationStatus.Text = $"{_settings.NotificationAppIds.Length} selected app(s).";
    }
    private async Task SyncNotifications()
    {
        var fresh = await _notifications.SyncAsync(_settings.NotificationAppIds, _settings.NotifyAllApps, _settings.HideNotificationContent);
        if (_closed) return;
        if (!_notifications.Enabled || !_settings.NotificationsEnabled)
        {
            _settings.NotificationsEnabled = false; _recentAlerts.Clear();
            _updating = true; NotificationsCheck.IsChecked = false; _updating = false;
            if (_liveKind == LiveKind.Inbox) { _notice = null; _liveKind = LiveKind.Text; ApplyMode(); }
            NotificationStatus.Text = _notifications.Status;
            SavePreferences(); UpdateInbox();
            return;
        }
        fresh = fresh.Where(n => _settings.NotifyAllApps || _settings.NotificationAppIds.Contains(n.AppId, StringComparer.Ordinal))
            .Select(n => _settings.HideNotificationContent ? n with { Title = "New notification", Body = "Content hidden" } : n).ToArray();
        string? selection = (NotificationAppChoice.SelectedItem as NotificationApp)?.Id;
        NotificationAppChoice.ItemsSource = _notifications.Apps;
        NotificationAppChoice.SelectedItem = _notifications.Apps.FirstOrDefault(a => a.Id == selection);
        NotificationStatus.Text = _notifications.Status;
        foreach (var notification in fresh)
        {
            _recentAlerts.Add(notification);
            if (_recentAlerts.Count > 20) _recentAlerts.RemoveAt(0);
        }
        if (fresh.Count > 0)
        {
            _alertIndex = _recentAlerts.Count - 1;
            var notification = fresh[^1];
            ShowLiveState(LiveKind.Inbox, notification.AppName, notification.Title, null, 6);
        }
        UpdateInbox();
    }
    private void UpdateInbox()
    {
        if (_recentAlerts.Count == 0)
        {
            InboxApp.Text = "Private by default";
            InboxTitle.Text = _settings.NotificationsEnabled ? "Waiting for selected apps" : "Notification previews are off";
            InboxBody.Text = _settings.NotificationsEnabled ? "Only new alerts from allowed apps will appear. Existing alerts are not replayed." : "Enable previews in Settings, then choose apps. No messages are being read.";
            return;
        }
        _alertIndex = Math.Clamp(_alertIndex, 0, _recentAlerts.Count - 1);
        var item = _recentAlerts[_alertIndex];
        InboxApp.Text = item.AppName + " · " + item.Created.LocalDateTime.ToString("HH:mm");
        InboxTitle.Text = item.Title; InboxBody.Text = item.Body;
    }
    private void Inbox_Next_Click(object sender, RoutedEventArgs e)
    {
        if (_recentAlerts.Count == 0) return;
        _alertIndex = (_alertIndex + 1) % _recentAlerts.Count;
        UpdateInbox();
    }
}
