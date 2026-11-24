using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VajraPulse.Core;
using VajraPulse.Services;

namespace VajraPulse;

public partial class MainWindow : Window
{
    private readonly PulseSettings _settings;
    private readonly IslandState _state = new();
    private readonly FocusTimer _focus = new();
    private readonly AudioService _audio = new();
    private readonly MediaService _media = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private TrayService? _tray;
    private HotkeyService? _hotkey;
    private AppSlotHotkeyService? _appSlotHotkeys;
    private IntPtr _handle;
    private Rect _monitor;
    private bool _ready, _updating, _pinned, _userHidden, _closed;
    private BatterySnapshot? _battery;
    private float? _lastVolume;
    private string? _notice;
    private DateTimeOffset _noticeUntil;
    private int _ticks;
    private Activity _activity = Activity.Music;
    private bool _activityChosen, _waveAnimating;
    private double _targetWidth = 140, _targetHeight = 36;

    public MainWindow()
    {
        _settings = SettingsStore.Load();
        InitializeComponent();
        ApplyRailVisibility();
        _monitor = EnvironmentService.PrimaryScreenBounds();
        IsVisibleChanged += (_, _) => UpdatePlaybackAnimation();
        _hotkey = new HotkeyService(this, ToggleFromShortcut);
        _appSlotHotkeys = new AppSlotHotkeyService(this, new Action<int>[]
        {
            i => LaunchSlotDirect(0), i => LaunchSlotDirect(1), i => LaunchSlotDirect(2),
            i => LaunchSlotDirect(3), i => LaunchSlotDirect(4)
        });
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_handle)?.AddHook(WindowHook);
        };
        Loaded += async (_, _) =>
        {
            _ready = true;
            PositionIsland();
            InitializeEyes();
            await InitializeFeaturesAsync();
            if (_closed) return;
            LoadPreferences();
            PositionIsland();
            _tray = new TrayService(() => Dispatcher.Invoke(ToggleVisibility),
                () => Dispatcher.Invoke(OpenSettings), () => Dispatcher.Invoke(Quit));
            ShortcutText.Text = _hotkey.IsRegistered ? "Ctrl + Alt + Space" : "Shortcut unavailable · use tray";
            _tick.Tick += Tick;
            _hover.Tick += (_, _) => { _hover.Stop(); if (Island.IsMouseOver && _state.Mode == IslandMode.Compact && _notice == null) Expand(false); };
            _leave.Tick += (_, _) => { _leave.Stop(); if (!IsPointerOverInteractiveChrome() && !_pinned && !_featuresModal && !_dropHover && !_shelfDragging) Collapse(); };
            _focus.Completed += (_, _) =>
            {
                SavePreferences();
                Notify("Focus complete", "Take a little breather.", 12);
            };
            RestoreFocusState();
            PollLocalState();
            await RefreshMedia();
            if (_closed) return;
            _tick.Start();
            if (!_settings.HasSeenWelcome)
            {
                _settings.HasSeenWelcome = true;
                SavePreferences();
                Expand(true);
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _tick.Stop(); _hover.Stop(); _leave.Stop();
            ShutdownEyes();
            SavePreferences();
            DisposeFeatures();
            _tray?.Dispose(); _hotkey?.Dispose(); _appSlotHotkeys?.Dispose(); _audio.Dispose(); _media.Dispose();
            HwndSource.FromHwnd(_handle)?.RemoveHook(WindowHook);
            Application.Current.Shutdown();
        };
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (_closed) return;
        _ticks++;
        _focus.Tick();
        if (_notice != null && DateTimeOffset.UtcNow >= _noticeUntil)
        {
            _notice = null;
            ApplyMode();
        }
        PollLocalState();
        UpdateTimer();
        UpdateCompact();
        UpdateProgress();
        await TickFeaturesAsync();
        if (_closed) return;
        bool fullscreen = _settings.HideInFullscreen && EnvironmentService.IsFullscreenAppActive(_handle, _monitor);
        bool shouldShow = !_userHidden && !fullscreen;
        if (shouldShow && !IsVisible) Show();
        else if (!shouldShow && IsVisible) Hide();
        if (_ticks % 5 == 0)
        {
            var monitors = EnvironmentService.AllScreenBounds();
            if (!monitors.Contains(_monitor)) { _monitor = EnvironmentService.PrimaryScreenBounds(); PositionIsland(); }
        }
        if (_ticks % 2 == 0) await RefreshMedia();
    }

    private void PollLocalState()
    {
        var battery = EnvironmentService.ReadBattery();
        if (_battery != null && battery.IsPluggedIn != _battery.IsPluggedIn)
            ShowLiveState(LiveKind.Charging, battery.IsPluggedIn ? "Power connected" : "On battery", battery.IsCharging ? "Charging" : battery.IsPluggedIn ? "Plugged in" : "Power disconnected", battery.Percent >= 0 ? battery.Percent / 100d : null, 4);
        _battery = battery;
        BatteryText.Text = !battery.HasBattery ? "Desktop" :
            (battery.IsCharging ? "↯ " : "") + (battery.Percent >= 0 ? $"{battery.Percent}%" : "Battery");
        BatteryText.ToolTip = battery.IsCharging ? "Charging" : battery.IsPluggedIn ? "Plugged in" : "On battery";
        BatteryText.Foreground = (Brush)FindResource(battery.HasBattery && battery.Percent is >= 0 and <= 20 && !battery.IsPluggedIn ? "TimerBrush" : "MutedBrush");
        _audio.Refresh();
        if (_audio.IsAvailable && _lastVolume.HasValue && Math.Abs(_lastVolume.Value - _audio.Volume) > 0.015f && !VolumeSlider.IsMouseCaptureWithin)
            ShowLiveState(LiveKind.Volume, "Volume", _audio.IsMuted ? "Muted" : "System audio", _audio.IsMuted ? 0 : _audio.Volume, 2);
        DetectAudioChanges();
        _lastVolume = _audio.IsAvailable ? _audio.Volume : null;
        UpdateAudio();
        UpdateTimer();
        UpdateCompact();
    }

    private async Task RefreshMedia()
    {
        await _media.RefreshAsync();
        if (_closed) return;
        var media = _media.Snapshot;
        TrackTitle.Text = media.Title;
        TrackArtist.Text = media.Artist;
        PlayGlyph.Visibility = media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.IsEnabled = media.CanToggle;
        PreviousButton.IsEnabled = media.CanPrevious;
        NextButton.IsEnabled = media.CanNext;
        ExpandedArtwork.Source = CompactArtwork.Source = SatelliteArtwork.Source = media.Artwork;
        ArtworkFallback.Visibility = SatelliteFallback.Visibility = media.Artwork == null ? Visibility.Visible : Visibility.Collapsed;
        if (!_activityChosen && !_focus.IsRunning && !_focus.IsPaused)
            _activity = media.HasSession ? Activity.Music : Activity.Focus;
        UpdateActivity();
        UpdateCompact();
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        var media = _media.Snapshot;
        bool timeline = media.Duration > TimeSpan.Zero;
        MediaSeekSlider.Visibility = timeline ? Visibility.Visible : Visibility.Hidden;
        MediaSeekSlider.IsEnabled = media.CanSeek && !_seekSending;
        MediaSeekSlider.ToolTip = media.CanSeek ? "Drag to seek" : "This player does not allow seeking";
        if (!_seekPending && !MediaSeekSlider.IsMouseCaptureWithin)
        {
            _updating = true;
            MediaSeekSlider.Value = timeline ? Math.Clamp(media.CurrentPosition.TotalSeconds / media.Duration.TotalSeconds, 0, 1) : 0;
            _updating = false;
        }
        var position = _seekPending && timeline ? TimeSpan.FromSeconds(MediaSeekSlider.Value * media.Duration.TotalSeconds) : media.CurrentPosition;
        MediaElapsed.Text = timeline ? FormatMediaTime(position) : "";
        MediaRemaining.Text = timeline ? "−" + FormatMediaTime(media.Duration - position) : "";
    }

    private static string FormatMediaTime(TimeSpan t)
    {
        int seconds = (int)Math.Max(0, t.TotalSeconds);
        return seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
    }

    private void UpdateActivity()
    {
        var activities = new[] { (Activity.Music, MusicActivity, MusicTab), (Activity.Focus, TimerActivity, FocusTab), (Activity.Shelf, ShelfActivity, FilesTab), (Activity.Calendar, CalendarActivity, CalendarTab), (Activity.Inbox, InboxActivity, InboxTab), (Activity.Tools, ToolsActivity, ToolsTab) };
        foreach (var (kind, panel, button) in activities)
        {
            panel.Visibility = _activity == kind ? Visibility.Visible : Visibility.Collapsed;
            button.Foreground = (Brush)FindResource(_activity == kind ? kind == Activity.Focus ? "TimerBrush" : "TextBrush" : "MutedBrush");
        }
        HeaderSubtitle.Text = _notice ?? (_activity switch
        {
            Activity.Focus => "Focus",
            Activity.Shelf => $"File shelf · {_shelf.Files.Count} shortcuts",
            Activity.Calendar => "Next meeting",
            Activity.Inbox => "Notification previews",
            Activity.Tools => _toolSection switch { ToolSection.Apps => "App shortcuts", ToolSection.Mixer => "App volume mixer", _ => "System activity" },
            _ => _media.Snapshot.HasSession ? _media.Snapshot.IsPlaying ? "Now playing" : "Paused" : "Music"
        });
    }

    private void SetActivity(Activity activity)
    {
        _activity = activity;
        _activityChosen = true;
        UpdateActivity();
        var panel = activity switch { Activity.Focus => TimerActivity, Activity.Shelf => ShelfActivity, Activity.Calendar => CalendarActivity, Activity.Inbox => InboxActivity, Activity.Tools => ToolsActivity, _ => MusicActivity };
        UpdateExpandedSizing();
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(_settings.ReducedMotion ? 0 : 160)));
    }
    private void MusicTab_Click(object sender, RoutedEventArgs e) => SetActivity(Activity.Music);
    private void FocusTab_Click(object sender, RoutedEventArgs e) => SetActivity(Activity.Focus);
    private void Satellite_Click(object sender, RoutedEventArgs e) { SetActivity(Activity.Music); Expand(true); }

    private void UpdateAudio()
    {
        _updating = true;
        if (!VolumeSlider.IsMouseCaptureWithin) VolumeSlider.Value = _audio.Volume * 100;
        VolumeSlider.IsEnabled = MuteButton.IsEnabled = _audio.IsAvailable;
        VolumeText.Text = _audio.IsAvailable ? _audio.IsMuted ? "Muted" : $"{Math.Round(_audio.Volume * 100)}%" : "N/A";
        MuteButton.Content = _audio.IsMuted ? "" : "";
        _updating = false;
    }

    private void UpdateTimer()
    {
        bool active = _focus.IsRunning || _focus.IsPaused;
        TimeSpan remaining = active || _focus.Remaining == TimeSpan.Zero ? _focus.Remaining : TimeSpan.FromMinutes(_settings.FocusMinutes);
        TimerText.Text = FormatTime(remaining);
        FocusLabel.Text = _focus.IsPaused ? "Paused" : _focus.IsRunning ? "Time to focus" : "Focus timer";
        TimerButton.Content = _focus.IsRunning ? "Pause" : _focus.IsPaused ? "Resume" : "Start focus";
        ResetButton.IsEnabled = active || _focus.Remaining == TimeSpan.Zero;
    }

    private void CaptureFocusState()
    {
        if (!_focus.IsRunning && !_focus.IsPaused)
        {
            _settings.FocusSessionSeconds = 0;
            _settings.FocusRemainingSeconds = 0;
            _settings.FocusTimerRunning = false;
            _settings.FocusTimerPaused = false;
            _settings.FocusDeadlineUtc = null;
            return;
        }

        int durationSeconds = (int)Math.Clamp(Math.Ceiling(_focus.Duration.TotalSeconds), 1, 86_400);
        int remainingSeconds = (int)Math.Clamp(Math.Ceiling(_focus.Remaining.TotalSeconds), 1, durationSeconds);
        _settings.FocusSessionSeconds = durationSeconds;
        _settings.FocusRemainingSeconds = remainingSeconds;
        _settings.FocusTimerRunning = _focus.IsRunning;
        _settings.FocusTimerPaused = _focus.IsPaused;
        _settings.FocusDeadlineUtc = _focus.IsRunning
            ? DateTimeOffset.UtcNow.AddSeconds(remainingSeconds)
            : null;
    }

    private void RestoreFocusState()
    {
        if (_settings.FocusSessionSeconds <= 0 || (!_settings.FocusTimerRunning && !_settings.FocusTimerPaused)) return;

        TimeSpan duration = TimeSpan.FromSeconds(_settings.FocusSessionSeconds);
        TimeSpan remaining = TimeSpan.FromSeconds(_settings.FocusRemainingSeconds);
        if (_settings.FocusTimerRunning && _settings.FocusDeadlineUtc is { } deadline)
            remaining = deadline - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            _settings.FocusSessionSeconds = 0;
            _settings.FocusRemainingSeconds = 0;
            _settings.FocusTimerRunning = false;
            _settings.FocusTimerPaused = false;
            _settings.FocusDeadlineUtc = null;
            SavePreferences();
            Notify("Focus complete", "The timer finished while Vajra Pulse was closed.", 8);
            return;
        }

        if (remaining > duration) remaining = duration;
        _focus.Restore(duration, remaining, _settings.FocusTimerRunning, _settings.FocusTimerPaused);
        UpdateTimer();
        UpdateCompact();
    }

    private static string FormatTime(TimeSpan t)
    {
        int seconds = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private void UpdateCompact()
    {
        bool focus = _focus.IsRunning || _focus.IsPaused;
        bool mediaPresent = _media.Snapshot.HasSession;
        bool notice = _notice != null;
        bool clock = !notice && !focus && !mediaPresent && _settings.ShowClock;
        CompactBadge.Visibility = notice || focus || mediaPresent ? Visibility.Visible : Visibility.Collapsed;
        CompactLeftColumn.Width = new GridLength(notice || focus || mediaPresent || clock ? 30 : 0);
        CompactBadge.Background = notice || focus ? Brushes.Transparent : (Brush)FindResource("PanelBrush");
        CompactArtwork.Visibility = !notice && !focus && mediaPresent && _media.Snapshot.Artwork != null ? Visibility.Visible : Visibility.Collapsed;
        CompactIcon.Visibility = CompactArtwork.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        CompactIcon.Foreground = CompactStatus.Foreground = (Brush)FindResource(focus && !notice ? "TimerBrush" : "TextBrush");
        CameraLens.Visibility = !notice && !focus && !mediaPresent ? Visibility.Visible : Visibility.Collapsed;
        PlaybackBars.Visibility = !notice && !focus && mediaPresent ? Visibility.Visible : Visibility.Collapsed;
        PlaybackBars.Opacity = _media.Snapshot.IsPlaying ? 1 : 0.45;
        System.Windows.Automation.AutomationProperties.SetName(PlaybackBars, _media.Snapshot.IsPlaying ? "Playback is active" : "Media is paused");
        CompactRightColumn.Width = new GridLength(focus ? 40 : 24);
        Grid.SetColumn(CompactTitle, notice ? 1 : clock ? 0 : 2);
        CompactTitle.HorizontalAlignment = notice ? HorizontalAlignment.Left : clock ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        CompactTitle.Text = notice ? _notice! : clock ? DateTime.Now.ToString("HH:mm") : "";
        CompactStatus.Text = !notice && focus ? FormatTime(_focus.Remaining) : "";
        EyeCluster.Visibility = notice ? Visibility.Collapsed : Visibility.Visible;
        CompactIcon.Text = notice ? "" : focus ? "" : "";
        bool live = notice && _liveKind != LiveKind.Text;
        CompactButton.Visibility = _state.Mode == IslandMode.Compact && !live ? Visibility.Visible : Visibility.Collapsed;
        LiveStatePanel.Visibility = _state.Mode == IslandMode.Compact && live ? Visibility.Visible : Visibility.Collapsed;
        UpdateLiveStateVisual();
        if (_state.Mode == IslandMode.Compact)
        {
            double width = notice ? live ? 300 : 320 : focus ? 184 : mediaPresent ? 176 : _settings.ShowClock ? 140 : 124;
            CompactRow.Width = width - 20;
            ResizeIsland(width, notice ? live ? 52 : 42 : 36);
            SatellitePosition.X = -(width / 2 + 22);
        }
        SatelliteButton.Visibility = _state.Mode == IslandMode.Compact && focus && mediaPresent && !notice ? Visibility.Visible : Visibility.Collapsed;
        UpdateActivity();
        UpdatePlaybackAnimation();
        FlushEyeEmotion();
    }

    private void UpdatePlaybackAnimation()
    {
        bool run = _media.Snapshot.IsPlaying && _state.Mode == IslandMode.Compact && !_focus.IsRunning && !_focus.IsPaused && _notice == null && !_settings.ReducedMotion && IsVisible;
        if (_waveAnimating == run) return;
        _waveAnimating = run;
        var bars = new[] { Wave1, Wave2, Wave3, Wave4 };
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i].BeginAnimation(HeightProperty, null);
            if (run)
            {
                var animation = new DoubleAnimation(4 + i % 2 * 2, 12 + i % 3 * 3, TimeSpan.FromMilliseconds(360 + i * 95))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Timeline.SetDesiredFrameRate(animation, 20);
                bars[i].BeginAnimation(HeightProperty, animation);
            }
        }
    }

    private void UpdateExpandedSizing()
    {
        if (_state.Mode == IslandMode.Settings)
        {
            ResizeIsland(366, 414);
            return;
        }
        bool tools = _activity == Activity.Tools;
        ExpandedContentRow.Height = new GridLength(tools ? 242 : 122);
        ResizeIsland(400, tools ? 430 : 310);
    }

    private void ResizeIsland(double width, double height)
    {
        SideRailPosition.X = width / 2 + 22;
        if (_targetWidth != width) { _targetWidth = width; Animate(Island, WidthProperty, width); }
        if (_targetHeight != height) { _targetHeight = height; Animate(Island, HeightProperty, height); }
    }

    private void Notify(string title, string detail, int seconds)
    {
        if (_desktopActivities.Current?.Kind == PulseActivityKind.Privacy) return;
        _liveKind = LiveKind.Text;
        _notice = string.IsNullOrWhiteSpace(detail) ? title : $"{title} · {detail}";
        _noticeUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        HeaderSubtitle.Text = _notice;
        UpdateCompact();
        if (_state.Mode == IslandMode.Compact) ApplyMode();
    }

    private void Expand(bool pinned)
    {
        _leave.Stop(); _hover.Stop();
        _pinned = pinned;
        _state.Expand();
        ApplyMode();
    }
    private void Collapse()
    {
        _hover.Stop(); _leave.Stop();
        _pinned = false;
        _state.Collapse();
        ApplyMode();
    }
    private void OpenSettings()
    {
        _userHidden = false;
        Show();
        _pinned = true;
        _catalogOpen = false;
        _state.OpenSettings();
        LoadPreferences();
        ApplyMode();
        Activate();
    }

    private void ApplyMode()
    {
        bool compact = _state.Mode == IslandMode.Compact;
        bool settings = _state.Mode == IslandMode.Settings;
        CompactButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ExpandedPanel.Visibility = !compact && !settings ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = settings && !_catalogOpen ? Visibility.Visible : Visibility.Collapsed;
        AppCatalogPanel.Visibility = settings && _catalogOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!compact) UpdateExpandedSizing();
        Island.CornerRadius = new CornerRadius(compact ? 24 : 32);
        if (!compact)
        {
            var panel = settings ? _catalogOpen ? AppCatalogPanel : SettingsPanel : ExpandedPanel;
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(_settings.ReducedMotion ? 0 : 180)) { BeginTime = TimeSpan.FromMilliseconds(_settings.ReducedMotion ? 0 : 90) });
        }
        UpdateCompact();
    }

    private void Animate(FrameworkElement target, DependencyProperty property, double to)
    {
        if (_settings.ReducedMotion)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }
        target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(440))
        { EasingFunction = new SpringEase { EasingMode = EasingMode.EaseIn } });
    }

    private void LoadPreferences()
    {
        _updating = true;
        HoverCheck.IsChecked = _settings.HoverExpand;
        FullscreenCheck.IsChecked = _settings.HideInFullscreen;
        MotionCheck.IsChecked = _settings.ReducedMotion;
        ClockCheck.IsChecked = _settings.ShowClock;
        SystemRailCheck.IsChecked = _settings.ShowSystemRailButton;
        BluetoothRailCheck.IsChecked = _settings.ShowBluetoothRailButton;
        WifiRailCheck.IsChecked = _settings.ShowWifiRailButton;
        ApplyRailVisibility();
        StartupRegistration startup = StartupService.Read();
        StartWithWindowsCheck.IsChecked = startup.IsEnabled;
        StartWithWindowsCheck.IsEnabled = startup.IsAvailable;
        StartWithWindowsCheck.ToolTip = startup.Status;
        GapSlider.Value = _settings.TopOffset;
        GapText.Text = $"{_settings.TopOffset} px";
        FocusMinutesBox.Text = _settings.FocusMinutes.ToString();
        LoadFeaturePreferences();
        _updating = false;
    }
    private bool SavePreferences()
    {
        CaptureFocusState();
        CaptureFeaturePreferences();
        bool saved = SettingsStore.Save(_settings);
        if (_ready) SettingsStatus.Text = _settings.IsFutureSchema
            ? "Settings were created by a newer Vajra Pulse version and were not overwritten."
            : saved ? "Saved locally. No account. No telemetry." : "Could not save preferences.";
        return saved;
    }
    private void PositionIsland()
    {
        if (_handle == IntPtr.Zero) return;
        double scale = Math.Max(96, GetDpiForWindow(_handle)) / 96d;
        int left = (int)Math.Round(_monitor.Left + (_monitor.Width - Width * scale) / 2);
        int top = (int)Math.Round(_monitor.Top + (_settings.TopOffset - 8) * scale);
        SetWindowPos(_handle, new IntPtr(-1), left, top, 0, 0, 0x11);
    }
    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x02E0 || msg == 0x007E)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!_closed) PositionIsland(); }));
        return IntPtr.Zero;
    }

    private void ToggleFromShortcut()
    {
        _userHidden = false;
        if (!IsVisible) { Show(); Expand(true); Activate(); }
        else if (_state.Mode == IslandMode.Compact) { Expand(true); Activate(); }
        else Collapse();
    }
    private void ToggleVisibility()
    {
        _userHidden = !_userHidden;
        if (_userHidden) Hide(); else { Show(); Collapse(); PositionIsland(); }
    }
    private bool IsPointerOverInteractiveChrome() => Island.IsMouseOver || SideRail.IsMouseOver || SatelliteButton.IsMouseOver;
    private void InteractiveChrome_MouseEnter(object sender, MouseEventArgs e) => _leave.Stop();
    private void InteractiveChrome_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_pinned && _state.Mode == IslandMode.Expanded) { _leave.Stop(); _leave.Start(); }
    }
    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _leave.Stop();
        if (_settings.HoverExpand && _state.Mode == IslandMode.Compact) { _hover.Stop(); _hover.Start(); }
    }
    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _hover.Stop();
        if (!_pinned && _state.Mode == IslandMode.Expanded) { _leave.Stop(); _leave.Start(); }
    }
    private void Compact_Click(object sender, RoutedEventArgs e) => Expand(true);
    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void Island_RightClick(object sender, MouseButtonEventArgs e) { OpenSettings(); e.Handled = true; }
    private void Back_Click(object sender, RoutedEventArgs e) { FocusMinutes_LostFocus(sender, e); Expand(true); }
    private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { if (_catalogOpen) CloseAppCatalog(); else Collapse(); e.Handled = true; } }
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_ready && _state.Mode != IslandMode.Compact) { _pinned = false; _leave.Start(); }
    }
    private async void MediaPlay_Click(object sender, RoutedEventArgs e) => await MediaControl("toggle");
    private async void MediaPrevious_Click(object sender, RoutedEventArgs e) => await MediaControl("previous");
    private async void MediaNext_Click(object sender, RoutedEventArgs e) => await MediaControl("next");
    private async Task MediaControl(string action)
    {
        if (!await _media.ControlAsync(action)) Notify("Media control unavailable", "Try your player", 3);
        await RefreshMedia();
    }
    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        if (_focus.IsRunning) _focus.Pause();
        else if (_focus.IsPaused) _focus.Resume();
        else _focus.Start(TimeSpan.FromMinutes(_settings.FocusMinutes));
        SetActivity(Activity.Focus);
        SavePreferences();
        UpdateTimer(); UpdateCompact();
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _focus.Reset();
        SavePreferences();
        UpdateTimer(); UpdateCompact();
    }
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int minutes))
        {
            if (_focus.IsRunning || _focus.IsPaused) { Notify("Timer is active", "Reset to change length", 3); return; }
            _settings.FocusMinutes = minutes;
            SavePreferences(); UpdateTimer();
        }
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating) return;
        _audio.SetVolume((float)(e.NewValue / 100));
        _lastVolume = _audio.Volume;
        VolumeText.Text = $"{Math.Round(e.NewValue)}%";
    }
    private void Mute_Click(object sender, RoutedEventArgs e) { _audio.ToggleMute(); UpdateAudio(); }
    private void ApplyRailVisibility()
    {
        SystemRailButton.Visibility = _settings.ShowSystemRailButton ? Visibility.Visible : Visibility.Collapsed;
        BluetoothRailButton.Visibility = _settings.ShowBluetoothRailButton ? Visibility.Visible : Visibility.Collapsed;
        WifiRailButton.Visibility = _settings.ShowWifiRailButton ? Visibility.Visible : Visibility.Collapsed;
        SideRail.Visibility = _settings.ShowSystemRailButton || _settings.ShowBluetoothRailButton || _settings.ShowWifiRailButton
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Preferences_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating) return;
        _settings.HoverExpand = HoverCheck.IsChecked == true;
        _settings.HideInFullscreen = FullscreenCheck.IsChecked == true;
        _settings.ReducedMotion = MotionCheck.IsChecked == true;
        _settings.ShowClock = ClockCheck.IsChecked == true;
        _settings.ShowSystemRailButton = SystemRailCheck.IsChecked == true;
        _settings.ShowBluetoothRailButton = BluetoothRailCheck.IsChecked == true;
        _settings.ShowWifiRailButton = WifiRailCheck.IsChecked == true;
        ApplyRailVisibility();
        SavePreferences(); ApplyMode();
    }
    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating) return;
        StartupRegistration result = StartupService.SetEnabled(StartWithWindowsCheck.IsChecked == true);
        _updating = true;
        StartWithWindowsCheck.IsChecked = result.IsEnabled;
        StartWithWindowsCheck.IsEnabled = result.IsAvailable;
        StartWithWindowsCheck.ToolTip = result.Status;
        _updating = false;
        SettingsStatus.Text = result.Status;
    }
    private void Gap_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating) return;
        _settings.TopOffset = (int)e.NewValue;
        GapText.Text = $"{_settings.TopOffset} px";
        PositionIsland(); SavePreferences();
    }
    private void FocusMinutes_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating) return;
        if (int.TryParse(FocusMinutesBox.Text, out int minutes)) _settings.FocusMinutes = Math.Clamp(minutes, 1, 180);
        FocusMinutesBox.Text = _settings.FocusMinutes.ToString();
        SavePreferences(); UpdateTimer();
    }
    private void Display_Click(object sender, RoutedEventArgs e)
    {
        var monitors = EnvironmentService.AllScreenBounds();
        int current = Array.IndexOf(monitors, _monitor);
        _monitor = monitors[(current + 1) % monitors.Length];
        PositionIsland();
        SettingsStatus.Text = monitors.Length > 1 ? "Moved to the next display." : "One display connected.";
    }
    private void Test_Click(object sender, RoutedEventArgs e) => TestLiveState();
    private void Quit_Click(object sender, RoutedEventArgs e) => Quit();
    private enum Activity { Music, Focus, Shelf, Calendar, Inbox, Tools }
    private void Quit() => Close();

    private void SetTheme(string theme)
    {
        ResourceDictionary newTheme = new ResourceDictionary { Source = new Uri($"UI/{theme}Theme.xaml", UriKind.Relative) };
        Application.Current.Resources.MergedDictionaries.Remove(Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source != null && d.Source.ToString().EndsWith("Theme.xaml")));
        Application.Current.Resources.MergedDictionaries.Add(newTheme);
    }
    
    private void DarkTheme_Click(object sender, RoutedEventArgs e) => SetTheme("Dark");
    private void LightTheme_Click(object sender, RoutedEventArgs e) => SetTheme("Light");
    private void GlassTheme_Click(object sender, RoutedEventArgs e) => SetTheme("Glass");
    
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

public sealed class SpringEase : EasingFunctionBase
{
    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return 1 - Math.Exp(-9 * t) * Math.Cos(9 * t);
    }
    protected override Freezable CreateInstanceCore() => new SpringEase();
}
