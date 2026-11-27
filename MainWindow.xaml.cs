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
    private readonly MediaService _media = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _edgeHide = new();
    private readonly DispatcherTimer _quickHide = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _idleFade = new() { Interval = TimeSpan.FromSeconds(3) };
    private TrayService? _tray;
    private HotkeyService? _hotkey;
    private AppSlotHotkeyService? _appSlotHotkeys;
    private IntPtr _handle;
    private Rect _monitor;
    private bool _ready, _updating, _pinned, _userHidden, _closed;
    private bool _edgeTriggerArmed = true, _pointerInEdgeEngagementZone, _hoverRequiresReentry;
    private int _edgeTransitionVersion;
    private EdgePhase _edgePhase = EdgePhase.Visible;
    private FrameworkElement? _presentedModePanel;
    private DateTimeOffset _edgeRevealGraceUntil;
    private BatterySnapshot? _battery;
    private string? _notice;
    private DateTimeOffset _noticeUntil;
    private int _ticks;
    private Activity _activity = Activity.Music;
    private bool _activityChosen, _updatingActivityTabs;
    private bool _idleMinimal, _swipeTracking, _swipeHandled;
    private Point _swipeStart;
    private string? _animatedNotice;
    private double _targetWidth = 140, _targetHeight = 40;

    public MainWindow()
    {
        _settings = SettingsStore.Load();
        ((App)Application.Current).ApplyVisualMode(_settings.VisualMode);
        InitializeComponent();
        ApplyRailVisibility();
        Island.SizeChanged += (_, _) =>
        {
            if (_ready && _settings.DockPosition != IslandDockPosition.TopCenter) PositionIsland();
        };
        _monitor = EnvironmentService.PrimaryScreenBounds();
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
            _hover.Tick += (_, _) =>
            {
                _hover.Stop();
                if (!_hoverRequiresReentry && _edgePhase == EdgePhase.Visible && Island.IsMouseOver
                    && _settings.HoverExpand && _state.Mode == IslandMode.Compact && _notice == null) Expand(false);
            };
            _leave.Tick += (_, _) => { _leave.Stop(); if (!IsPointerOverInteractiveChrome() && !_pinned && !_featuresModal && !_dropHover && !_shelfDragging) Collapse(); };
            _edgeHide.Tick += (_, _) => { _edgeHide.Stop(); HideAtScreenEdge(); };
            _quickHide.Tick += (_, _) =>
            {
                _quickHide.Stop();
            };
            _idleFade.Tick += (_, _) =>
            {
                _idleFade.Stop();
                if (_state.Mode == IslandMode.Compact && _notice == null && !_focus.IsRunning && !_focus.IsPaused
                    && !_media.Snapshot.HasSession && !Island.IsMouseOver)
                {
                    _idleMinimal = true;
                    UpdateCompact();
                }
            };
        // _waveMeter.Tick += (_, _) => UpdateWaveLevels();
        _focus.Completed += (_, _) =>
        {
                SavePreferences();
                Notify("Focus complete", "Take a little breather.", 12);
                ReactHearts();
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
            else
            {
                ScheduleEdgeHide(1400);
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _tick.Stop(); _hover.Stop(); _leave.Stop(); _edgeHide.Stop();
            _quickHide.Stop(); _idleFade.Stop();
            ShutdownEyes();
            SavePreferences();
            DisposeFeatures();
            _tray?.Dispose(); _hotkey?.Dispose(); _appSlotHotkeys?.Dispose(); _media.Dispose();
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
        bool shouldShow = !_userHidden && !fullscreen && _edgePhase != EdgePhase.Hidden;
        if (shouldShow && !IsVisible) Show();
        else if (!shouldShow && IsVisible && _edgePhase != EdgePhase.Retreating) Hide();
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
        DetectAudioChanges();
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
        UpdateArtworkAccent(media.Artwork);
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
        _updatingActivityTabs = true;
        try
        {
            foreach (var (kind, panel, button) in activities)
            {
                bool selected = _activity == kind;
                panel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                button.Tag = selected;
                button.Foreground = (Brush)FindResource(selected ? kind == Activity.Focus ? "TimerBrush" : "TextBrush" : "MutedBrush");
            }
        }
        finally { _updatingActivityTabs = false; }
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
        if (_activity == activity)
        {
            _activityChosen = true;
            UpdateActivity();
            return;
        }
        int direction = activity > _activity ? 16 : -16;
        _activity = activity;
        _activityChosen = true;
        UpdateActivity();
        var panel = activity switch { Activity.Focus => TimerActivity, Activity.Shelf => ShelfActivity, Activity.Calendar => CalendarActivity, Activity.Inbox => InboxActivity, Activity.Tools => ToolsActivity, _ => MusicActivity };
        UpdateExpandedSizing();
        if (_state.Mode == IslandMode.Expanded) AnimatePanelIn(panel, direction);
        ReactCurious();
    }
    private void MusicTab_Click(object sender, RoutedEventArgs e) { if (!_updatingActivityTabs) SetActivity(Activity.Music); }
    private void FocusTab_Click(object sender, RoutedEventArgs e) { if (!_updatingActivityTabs) SetActivity(Activity.Focus); }
    private void Satellite_Click(object sender, RoutedEventArgs e) { SetActivity(Activity.Music); Expand(true); }



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
            ReactHearts();
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
        // PlaybackBars.Visibility = !notice && !focus && mediaPresent ? Visibility.Visible : Visibility.Collapsed;
        // PlaybackBars.Opacity = _media.Snapshot.IsPlaying ? 1 : 0.45;
        // System.Windows.Automation.AutomationProperties.SetName(PlaybackBars, _media.Snapshot.IsPlaying ? "Playback is active" : "Media is paused");
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
        bool idle = !notice && !focus && !mediaPresent && _state.Mode == IslandMode.Compact
            && !Island.IsMouseOver;
        if (!idle) { _idleFade.Stop(); _idleMinimal = false; }
        else if (!_idleMinimal && !_idleFade.IsEnabled) _idleFade.Start();
        if (_state.Mode == IslandMode.Compact)
        {
            double width = notice ? live ? 300 : 320 : focus ? 184 : mediaPresent ? 176 : _idleMinimal ? 104 : _settings.ShowClock ? 140 : 124;
            CompactRow.Width = width - 20;
            ResizeIsland(width, notice ? live ? 52 : 42 : 40);
            ApplyAccessoryLayout(width);
        }
        SatelliteButton.Visibility = _state.Mode == IslandMode.Compact && focus && mediaPresent && !notice ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusLed();
        AnimateElementOpacity(CompactTitle, _idleMinimal ? 0 : 1);
        AnimateElementOpacity(CameraLens, _idleMinimal ? 0 : 1);
        if (_notice != _animatedNotice)
        {
            _animatedNotice = _notice;
            if (_notice != null && _state.Mode == IslandMode.Compact) AnimateNoticeIn();
        }
        UpdateActivity();
        FlushEyeEmotion();
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
        ResizeIsland(460, tools ? 365 : 260);
    }

    private void ResizeIsland(double width, double height)
    {
        bool widthChanged = _targetWidth != width;
        ApplyAccessoryLayout(width);
        if (widthChanged) { _targetWidth = width; Animate(Island, WidthProperty, width); }
        if (_targetHeight != height) { _targetHeight = height; Animate(Island, HeightProperty, height); }
        if (widthChanged && _ready && _settings.DockPosition != IslandDockPosition.TopCenter) PositionIsland();
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
        _edgeHide.Stop();
        if (_edgePhase != EdgePhase.Visible) ForceEdgeVisible();
        _leave.Stop(); _hover.Stop();
        _pinned = pinned;
        AnimateScale(1.0);
        AnimateElementOpacity(NotchSeam, 0.55);
        _state.Expand();
        ApplyMode();
    }
    private void Collapse()
    {
        _hover.Stop(); _leave.Stop();
        _pinned = false;
        _state.Collapse();
        ApplyMode();
        ScheduleEdgeHide(650);
    }
    private void OpenSettings()
    {
        _edgeHide.Stop();
        _userHidden = false;
        ForceEdgeVisible();
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
        FrameworkElement? desiredPanel = compact ? null : settings ? _catalogOpen ? AppCatalogPanel : SettingsPanel : ExpandedPanel;
        if (!compact) UpdateExpandedSizing();
        double bottomRadius = compact ? 20 : 32;
        Island.CornerRadius = new CornerRadius(0, 0, bottomRadius, bottomRadius);
        IslandSheen.CornerRadius = Island.CornerRadius;
        if (desiredPanel != null && !ReferenceEquals(_presentedModePanel, desiredPanel)) AnimatePanelIn(desiredPanel, 0);
        _presentedModePanel = desiredPanel;
        UpdateCompact();
    }

    private bool AnimationsEnabled => !_settings.ReducedMotion && SystemParameters.ClientAreaAnimation;

    private void AnimatePanelIn(FrameworkElement panel, double fromX)
    {
        panel.BeginAnimation(OpacityProperty, null);
        panel.SetValue(OpacityProperty, 1d);
        var offset = panel.RenderTransform as TranslateTransform ?? new TranslateTransform();
        panel.RenderTransform = offset;
        offset.BeginAnimation(TranslateTransform.XProperty, null);
        offset.X = 0;
        if (!AnimationsEnabled) return;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(190))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        panel.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        offset.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void Animate(FrameworkElement target, DependencyProperty property, double to)
    {
        double from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, to);
        if (!AnimationsEnabled || Math.Abs(from - to) < 0.01) return;
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(260))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateTransform(TranslateTransform target, DependencyProperty property, double to)
    {
        double from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, to);
        if (!AnimationsEnabled || Math.Abs(from - to) < 0.01) return;
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(220))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetMotionState()
    {
        Island.BeginAnimation(WidthProperty, null);
        Island.BeginAnimation(HeightProperty, null);
        Island.SetValue(WidthProperty, _targetWidth);
        Island.SetValue(HeightProperty, _targetHeight);
        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        IslandScale.ScaleX = IslandScale.ScaleY = 1;
        NotchSeam.BeginAnimation(OpacityProperty, null);
        NotchSeam.Opacity = 0.55;
        _quickHide.Stop();
        _idleFade.Stop();
        _idleMinimal = false;
        _animatedNotice = null;
        WindowChrome.BeginAnimation(OpacityProperty, null);
        WindowChrome.SetValue(OpacityProperty, 1d);
        WindowChromeOffset.BeginAnimation(TranslateTransform.YProperty, null);
        WindowChromeOffset.Y = 0;
        if (_edgePhase == EdgePhase.Retreating)
        {
            _edgePhase = EdgePhase.Hidden;
            Hide();
        }
        else if (_edgePhase == EdgePhase.Revealing) _edgePhase = EdgePhase.Visible;
        ApplyAccessoryLayout(_targetWidth);
        ResetEyeMotion();
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
        ApplyEyeStyle();
        LoadDesignPreferences();
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

    private void ScheduleEdgeHide(int delayMilliseconds)
    {
        if (!_ready || _closed || _userHidden || _edgePhase != EdgePhase.Visible || _state.Mode != IslandMode.Compact) return;
        int graceRemaining = (int)Math.Ceiling((_edgeRevealGraceUntil - DateTimeOffset.UtcNow).TotalMilliseconds);
        _edgeHide.Interval = TimeSpan.FromMilliseconds(Math.Max(100, Math.Max(delayMilliseconds, graceRemaining)));
        _edgeHide.Stop();
        _edgeHide.Start();
    }

    private void HideAtScreenEdge(bool force = false)
    {
        if (!_ready || _closed || _userHidden || _edgePhase != EdgePhase.Visible || _state.Mode != IslandMode.Compact) return;
        if (!force && (_pointerInEdgeEngagementZone || IsPointerOverInteractiveChrome() || _featuresModal || _dropHover || _shelfDragging))
        {
            ScheduleEdgeHide(500);
            return;
        }
        if (!force && DateTimeOffset.UtcNow < _edgeRevealGraceUntil)
        {
            ScheduleEdgeHide(200);
            return;
        }

        _edgeHide.Stop();
        _hover.Stop();
        _leave.Stop();
        _quickHide.Stop();
        _idleFade.Stop();
        if (EyesOnScreen()) PlaySleepy();
        _edgePhase = EdgePhase.Retreating;
        int version = ++_edgeTransitionVersion;
        void CompleteRetreat()
        {
            if (_closed || version != _edgeTransitionVersion || _edgePhase != EdgePhase.Retreating) return;
            _edgePhase = EdgePhase.Hidden;
            Hide();
            WindowChrome.BeginAnimation(OpacityProperty, null);
            WindowChrome.SetValue(OpacityProperty, 1d);
            WindowChromeOffset.BeginAnimation(TranslateTransform.YProperty, null);
            WindowChromeOffset.Y = 0;
        }
        if (!AnimationsEnabled)
        {
            CompleteRetreat();
            return;
        }

        double retreatOffset = -Math.Max(24, _targetHeight);
        double fromOpacity = WindowChrome.Opacity;
        double fromY = WindowChromeOffset.Y;
        WindowChrome.BeginAnimation(OpacityProperty, null);
        WindowChrome.SetValue(OpacityProperty, 0d);
        WindowChromeOffset.BeginAnimation(TranslateTransform.YProperty, null);
        WindowChromeOffset.Y = retreatOffset;
        var fade = new DoubleAnimation(fromOpacity, 0, TimeSpan.FromMilliseconds(120))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var slide = new DoubleAnimation(fromY, retreatOffset, TimeSpan.FromMilliseconds(120))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => CompleteRetreat();
        WindowChrome.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        WindowChromeOffset.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void CheckScreenEdgeReveal(int pointerX, int pointerY)
    {
        if (_userHidden || _closed) return;
        double scale = _handle == IntPtr.Zero ? 1 : Math.Max(96, GetDpiForWindow(_handle)) / 96d;
        double centerX = CalculateRevealCenter(scale);
        double dx = Math.Abs(pointerX - centerX);
        bool inActivationZone = pointerY >= _monitor.Top && pointerY <= _monitor.Top + Math.Max(3, 4 * scale)
            && dx <= Math.Max(120, _targetWidth / 2 + 8) * scale;
        bool inResetZone = pointerY >= _monitor.Top && pointerY <= _monitor.Top + Math.Max(10, 12 * scale)
            && dx <= Math.Max(140, _targetWidth / 2 + 18) * scale;
        _pointerInEdgeEngagementZone = inResetZone;
        if (!inResetZone) _edgeTriggerArmed = true;
        if (_edgePhase != EdgePhase.Hidden || !_edgeTriggerArmed || !inActivationZone) return;
        if (_settings.HideInFullscreen && EnvironmentService.IsFullscreenAppActive(_handle, _monitor)) return;
        _edgeTriggerArmed = false;
        RevealFromScreenEdge();
    }

    private void RevealFromScreenEdge()
    {
        if (_edgePhase != EdgePhase.Hidden) return;
        int version = ++_edgeTransitionVersion;
        _edgePhase = EdgePhase.Revealing;
        _edgeRevealGraceUntil = DateTimeOffset.UtcNow.AddMilliseconds(1300);
        Show();
        PositionIsland();
        WindowChrome.BeginAnimation(OpacityProperty, null);
        WindowChrome.SetValue(OpacityProperty, 1d);
        WindowChromeOffset.BeginAnimation(TranslateTransform.YProperty, null);
        WindowChromeOffset.Y = 0;
        void CompleteReveal()
        {
            if (_closed || version != _edgeTransitionVersion || _edgePhase != EdgePhase.Revealing) return;
            _edgePhase = EdgePhase.Visible;
            ReactSurprised();
            ScheduleEdgeHide(1300);
        }
        if (!AnimationsEnabled)
        {
            CompleteReveal();
            return;
        }
        double revealOffset = -Math.Max(24, _targetHeight);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(revealOffset, 0, TimeSpan.FromMilliseconds(160))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) => CompleteReveal();
        WindowChrome.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        WindowChromeOffset.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void ForceEdgeVisible()
    {
        ++_edgeTransitionVersion;
        _edgeHide.Stop();
        _edgePhase = EdgePhase.Visible;
        WindowChrome.BeginAnimation(OpacityProperty, null);
        WindowChrome.SetValue(OpacityProperty, 1d);
        WindowChromeOffset.BeginAnimation(TranslateTransform.YProperty, null);
        WindowChromeOffset.Y = 0;
        if (!IsVisible) Show();
        PositionIsland();
    }

    private void PositionIsland()
    {
        if (_handle == IntPtr.Zero) return;
        double scale = Math.Max(96, GetDpiForWindow(_handle)) / 96d;
        int left = CalculateWindowLeft(scale);
        int top = (int)Math.Round(_monitor.Top + _settings.TopOffset * scale);
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
        ForceEdgeVisible();
        if (_state.Mode == IslandMode.Compact) { Expand(true); Activate(); }
        else { _hoverRequiresReentry = true; Collapse(); }
    }
    private void ToggleVisibility()
    {
        bool reveal = _userHidden || _edgePhase == EdgePhase.Hidden;
        if (reveal)
        {
            _userHidden = false;
            _edgeRevealGraceUntil = DateTimeOffset.UtcNow.AddMilliseconds(1300);
            ForceEdgeVisible();
            Collapse();
            PositionIsland();
        }
        else
        {
            _userHidden = true;
            ++_edgeTransitionVersion;
            _edgeHide.Stop();
            Hide();
        }
    }
    private bool IsPointerOverInteractiveChrome() => Island.IsMouseOver || SideRail.IsMouseOver || SatelliteButton.IsMouseOver;
    private void InteractiveChrome_MouseEnter(object sender, MouseEventArgs e)
    {
        _edgeHide.Stop();
        _leave.Stop();
    }
    private void InteractiveChrome_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_pinned && _state.Mode == IslandMode.Expanded) { _leave.Stop(); _leave.Start(); }
        else if (_state.Mode == IslandMode.Compact) ScheduleEdgeHide(500);
    }
    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _edgeHide.Stop();
        _leave.Stop();
        _quickHide.Stop();
        if (_state.Mode == IslandMode.Compact)
        {
            if (_idleMinimal) { _idleMinimal = false; UpdateCompact(); }
            AnimateScale(1.03);
            AnimateElementOpacity(NotchSeam, 0.85);
        }
        if (_settings.HoverExpand && _state.Mode == IslandMode.Compact) { _hover.Stop(); _hover.Start(); }
    }
    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _hover.Stop();
        _hoverRequiresReentry = false;
        AnimateScale(1.0);
        AnimateElementOpacity(NotchSeam, 0.55);
        _quickHide.Stop();
        _quickHide.Start();
        if (!_pinned && _state.Mode == IslandMode.Expanded) { _leave.Stop(); _leave.Start(); }
        else if (_state.Mode == IslandMode.Compact) ScheduleEdgeHide(500);
    }
    private void Island_PreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_state.Mode != IslandMode.Compact) return;
        _swipeStart = e.GetPosition(this);
        _swipeTracking = true;
        _swipeHandled = false;
    }
    private void Island_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_swipeTracking || e.LeftButton != MouseButtonState.Pressed) return;
        double dy = e.GetPosition(this).Y - _swipeStart.Y;
        if (Math.Abs(dy) < 16) return;
        _swipeTracking = false;
        _swipeHandled = true;
        if (dy > 0) { _hoverRequiresReentry = false; Expand(true); }
        else { _hoverRequiresReentry = true; HideAtScreenEdge(true); }
    }
    private void Island_PreviewLeftUp(object sender, MouseButtonEventArgs e) => _swipeTracking = false;
    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        if (_swipeHandled) { _swipeHandled = false; return; }
        Expand(true);
    }
private void UpdateStatusLed()
    {
        bool compact = _state.Mode == IslandMode.Compact;
        StatusLed.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        StatusLed.Opacity = compact ? 0.95 : 0;
        string key = _desktopActivities.Current?.Kind == PulseActivityKind.Privacy ? "DangerBrush"
            : _focus.IsRunning || _focus.IsPaused ? "TimerBrush"
            : _media.Snapshot.IsPlaying ? "AccentBrush"
            : _battery?.IsCharging == true ? "MintBrush"
            : "MutedBrush";
        StatusLed.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
    }



    private void AnimateScale(double to)
    {
        double from = IslandScale.ScaleX;
        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        IslandScale.ScaleX = IslandScale.ScaleY = to;
        if (!AnimationsEnabled || Math.Abs(from - to) < 0.001) return;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(190))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
        };
        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation.Clone(), HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateElementOpacity(UIElement target, double to, Action? completed = null)
    {
        double from = target.Opacity;
        target.BeginAnimation(OpacityProperty, null);
        target.Opacity = to;
        if (!AnimationsEnabled || Math.Abs(from - to) < 0.01) { completed?.Invoke(); return; }
        var fade = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        if (completed != null) fade.Completed += (_, _) => completed();
        target.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateNoticeIn()
    {
        var slide = CompactRow.RenderTransform as TranslateTransform;
        if (slide == null) { slide = new TranslateTransform(); CompactRow.RenderTransform = slide; }
        slide.BeginAnimation(TranslateTransform.YProperty, null);
        slide.Y = 0;
        CompactRow.BeginAnimation(OpacityProperty, null);
        CompactRow.Opacity = 1;
        if (!AnimationsEnabled) return;
        var drop = new DoubleAnimation(-9, 0, TimeSpan.FromMilliseconds(230))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop };
        slide.BeginAnimation(TranslateTransform.YProperty, drop, HandoffBehavior.SnapshotAndReplace);
        CompactRow.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) { _hoverRequiresReentry = true; Collapse(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void Island_RightClick(object sender, MouseButtonEventArgs e) { OpenSettings(); e.Handled = true; }
    private void Back_Click(object sender, RoutedEventArgs e) { FocusMinutes_LostFocus(sender, e); Expand(true); }
    private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { if (_catalogOpen) CloseAppCatalog(); else { _hoverRequiresReentry = true; Collapse(); } e.Handled = true; } }
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
        _settings.UseArtworkAccent = ArtworkAccentCheck.IsChecked == true;
        ApplyRailVisibility();
        UpdateArtworkAccent(_media.Snapshot.Artwork);
        if (_settings.ReducedMotion) ResetMotionState();
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
    private enum EdgePhase { Visible, Retreating, Hidden, Revealing }
    private void Quit() => Close();

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
