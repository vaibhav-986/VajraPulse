using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VajraPulse.Services;

namespace VajraPulse;

public partial class MainWindow
{
    private readonly SystemTelemetryService _systemTelemetry = new();
    private readonly WifiStatusService _wifiStatus = new();
    private readonly AppLauncherService _appLauncher = new();
    private readonly InstalledAppCatalogService _installedApps = new();
    private readonly AudioSessionService _audioSessions = new();
    private readonly ObservableCollection<LaunchSlotItem> _launchSlotItems = new();
    private readonly ObservableCollection<InstalledAppItem> _catalogItems = new();
    private readonly ObservableCollection<MixerItem> _mixerItems = new();
    private IReadOnlyList<InstalledAppCatalogEntry> _catalogEntries = Array.Empty<InstalledAppCatalogEntry>();
    private CancellationTokenSource? _catalogCancellation;
    private bool _toolsBusy, _mixerBusy, _mixerCommandBusy, _catalogOpen, _catalogBusy, _appLaunchBusy;
    private int _catalogSlotIndex = -1;
    private ToolSection _toolSection = ToolSection.System;

    private enum ToolSection { System, Apps, Mixer }

    private async Task InitializeToolsAsync()
    {
        LaunchSlotsList.ItemsSource = _launchSlotItems;
        SettingsLaunchSlotsList.ItemsSource = _launchSlotItems;
        AppCatalogResults.ItemsSource = _catalogItems;
        MixerList.ItemsSource = _mixerItems;
        RefreshLaunchSlotItems();
        RenderToolSection();
        await RefreshTelemetryAsync();
        if (_closed) return;
        await RefreshWifiAsync();
    }

    private async Task TickToolsAsync()
    {
        if (_toolsBusy || _closed) return;
        _toolsBusy = true;
        try
        {
            await RefreshTelemetryAsync();
            if (_closed) return;
            if (_ticks % 5 == 0) await RefreshWifiAsync();
            if (_closed) return;
            if (_activity == Activity.Tools && _toolSection == ToolSection.Mixer && _ticks % 2 == 0)
                await RefreshMixerAsync();
        }
        finally { _toolsBusy = false; }
    }

    private void DisposeTools()
    {
        _catalogCancellation?.Cancel();
        _catalogCancellation?.Dispose();
        _catalogCancellation = null;
        _catalogItems.Clear();
        _launchSlotItems.Clear();
        _mixerItems.Clear();
    }

    private async Task RefreshTelemetryAsync()
    {
        SystemTelemetrySnapshot snapshot = await Task.Run(_systemTelemetry.Sample);
        if (_closed) return;

        RenderMeter(CpuMeter, CpuValueText, snapshot.Cpu.Percent, snapshot.Cpu.Availability,
            snapshot.Cpu.Percent.HasValue ? $"{snapshot.Cpu.Percent.Value:0}%" : snapshot.Cpu.Availability == TelemetryAvailability.WarmingUp ? "Warming up" : "Unavailable",
            "CPU usage");

        string memoryText = snapshot.Memory.Percent.HasValue
            ? $"{FormatBytes(snapshot.Memory.UsedBytes)} / {FormatBytes(snapshot.Memory.TotalBytes)} · {snapshot.Memory.Percent.Value:0}%"
            : "Unavailable";
        RenderMeter(MemoryMeter, MemoryValueText, snapshot.Memory.Percent, snapshot.Memory.Availability, memoryText, "Memory usage");

        NetworkDownText.Text = snapshot.Network.BytesReceivedPerSecond.HasValue ? FormatRate(snapshot.Network.BytesReceivedPerSecond.Value) : "—";
        NetworkUpText.Text = snapshot.Network.BytesSentPerSecond.HasValue ? FormatRate(snapshot.Network.BytesSentPerSecond.Value) : "—";
        NetworkInterfaceText.Text = snapshot.Network.Availability switch
        {
            TelemetryAvailability.Available => snapshot.Network.ActiveInterfaceCount == 1 ? "1 active interface" : $"{snapshot.Network.ActiveInterfaceCount} active interfaces",
            TelemetryAvailability.WarmingUp => "Warming up",
            _ => "Unavailable"
        };
        SystemTelemetryStatus.Text = snapshot.Cpu.Availability == TelemetryAvailability.Unavailable
            || snapshot.Memory.Availability == TelemetryAvailability.Unavailable
            || snapshot.Network.Availability == TelemetryAvailability.Unavailable
            ? "Some local system counters are unavailable."
            : "Local aggregate counters · no history is stored";
    }

    private static void RenderMeter(ProgressBar meter, TextBlock valueText, double? percent,
        TelemetryAvailability availability, string text, string automationLabel)
    {
        meter.IsIndeterminate = false;
        meter.Value = percent ?? 0;
        meter.Opacity = availability == TelemetryAvailability.Available ? 1 : 0.45;
        valueText.Text = text;
        AutomationProperties.SetName(meter, automationLabel);
        AutomationProperties.SetHelpText(meter, percent.HasValue
            ? $"{Math.Round(percent.Value)} percent"
            : text);
    }

    private async Task RefreshWifiAsync()
    {
        WifiStatusSnapshot snapshot = await Task.Run(_wifiStatus.Read);
        if (_closed) return;
        WifiRailButton.ToolTip = snapshot.Status + ". Open Wi-Fi settings.";
        AutomationProperties.SetHelpText(WifiRailButton, snapshot.Status);
        AutomationProperties.SetName(WifiRailButton, "Open Wi-Fi settings");
        WifiRailButton.Foreground = (Brush)FindResource(snapshot.State == WifiConnectionState.Connected ? "MintBrush" : "TextBrush");
        WifiRailButton.Opacity = snapshot.State == WifiConnectionState.Unavailable ? 0.5 : 1;
    }

    private void UpdateBluetoothRail(BluetoothBatterySnapshot snapshot)
    {
        int connected = snapshot.Devices.Count(device => device.IsConnected);
        string status = !snapshot.IsSupported ? "Bluetooth status unavailable"
            : connected == 0 ? "No Bluetooth devices connected"
            : connected == 1 ? "1 Bluetooth device connected"
            : $"{connected} Bluetooth devices connected";
        BluetoothRailButton.ToolTip = status + ". Open Bluetooth settings.";
        AutomationProperties.SetHelpText(BluetoothRailButton, status);
        AutomationProperties.SetName(BluetoothRailButton, "Open Bluetooth settings");
        BluetoothRailButton.Foreground = (Brush)FindResource(connected > 0 ? "MintBrush" : "TextBrush");
        BluetoothRailButton.Opacity = snapshot.IsSupported ? 1 : 0.5;
    }

    private void ToolsTab_Click(object sender, RoutedEventArgs e)
    {
        SetActivity(Activity.Tools);
        SetToolSection(_toolSection);
    }

    private void SystemRail_Click(object sender, RoutedEventArgs e)
    {
        SetActivity(Activity.Tools);
        SetToolSection(ToolSection.System);
        Expand(true);
    }

    private void BluetoothRail_Click(object sender, RoutedEventArgs e) => OpenWindowsSettings("ms-settings:bluetooth");
    private void WifiRail_Click(object sender, RoutedEventArgs e) => OpenWindowsSettings("ms-settings:network-wifi");
    private void SystemTool_Click(object sender, RoutedEventArgs e) => SetToolSection(ToolSection.System);
    private void AppsTool_Click(object sender, RoutedEventArgs e) => SetToolSection(ToolSection.Apps);
    private async void MixerTool_Click(object sender, RoutedEventArgs e)
    {
        SetToolSection(ToolSection.Mixer);
        await RefreshMixerAsync();
    }

    private void SetToolSection(ToolSection section)
    {
        _toolSection = section;
        RenderToolSection();
        UpdateActivity();
    }

    private void RenderToolSection()
    {
        SystemToolsPanel.Visibility = _toolSection == ToolSection.System ? Visibility.Visible : Visibility.Collapsed;
        AppsToolsPanel.Visibility = _toolSection == ToolSection.Apps ? Visibility.Visible : Visibility.Collapsed;
        MixerToolsPanel.Visibility = _toolSection == ToolSection.Mixer ? Visibility.Visible : Visibility.Collapsed;
        SystemToolSelector.Foreground = (Brush)FindResource(_toolSection == ToolSection.System ? "TextBrush" : "MutedBrush");
        AppsToolSelector.Foreground = (Brush)FindResource(_toolSection == ToolSection.Apps ? "TextBrush" : "MutedBrush");
        MixerToolSelector.Foreground = (Brush)FindResource(_toolSection == ToolSection.Mixer ? "TextBrush" : "MutedBrush");
        if (_toolSection == ToolSection.Apps) RefreshLaunchSlotItems();
    }

    private void OpenWindowsSettings(string uri)
    {
        if (uri is not ("ms-settings:bluetooth" or "ms-settings:network-wifi")) return;
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Notify("Could not open Windows Settings", "Try opening Settings manually", 4);
        }
    }

    private void RefreshLaunchSlotItems()
    {
        AppLaunchSlotSettings[] slots = _settings.LaunchSlots;
        _launchSlotItems.Clear();
        for (int index = 0; index < SettingsStore.LaunchSlotCount; index++)
        {
            AppLaunchSlotSettings slot = index < slots.Length ? slots[index] : new AppLaunchSlotSettings();
            AppLaunchResult validation = _appLauncher.Validate(slot);
            bool configured = slot.TargetKind == AppLaunchTargetKind.AppUserModelId
                ? !string.IsNullOrWhiteSpace(slot.AppUserModelId)
                : !string.IsNullOrWhiteSpace(slot.TargetPath);
            string name = configured
                ? string.IsNullOrWhiteSpace(slot.DisplayName)
                    ? slot.TargetKind == AppLaunchTargetKind.AppUserModelId ? "Windows app" : AppLauncherService.SuggestedDisplayName(slot.TargetPath)
                    : slot.DisplayName
                : "Not configured";
            string detail = validation.IsSuccess ? "Open" : validation.Message;
            _launchSlotItems.Add(new LaunchSlotItem(index, index + 1, name, detail, validation.IsSuccess, configured));
        }
    }

    private async void LaunchSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LaunchSlotItem item } || _appLaunchBusy) return;
        AppLaunchSlotSettings slot = _settings.LaunchSlots[item.Index];
        AppLaunchResult validation = _appLauncher.Validate(slot);
        if (!validation.IsSuccess)
        {
            await OpenAppCatalogAsync(item);
            return;
        }
        _appLaunchBusy = true;
        try
        {
            AppLaunchResult result = await _appLauncher.LaunchAsync(slot);
            if (!result.IsSuccess)
            {
                RefreshLaunchSlotItems();
                Notify("Application could not open", result.Message, 4);
            }
        }
        finally { _appLaunchBusy = false; }
    }

    private async void LaunchSlotChoose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LaunchSlotItem item }) await OpenAppCatalogAsync(item);
    }

    private void LaunchSlotClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LaunchSlotItem item }) return;
        AppLaunchSlotSettings previous = _settings.LaunchSlots[item.Index];
        _settings.LaunchSlots[item.Index] = new AppLaunchSlotSettings();
        if (!SavePreferences())
        {
            _settings.LaunchSlots[item.Index] = previous;
            RefreshLaunchSlotItems();
            Notify("App slot was not cleared", "The previous selection was kept.", 4);
            return;
        }
        RefreshLaunchSlotItems();
        SettingsStatus.Text = $"App slot {item.Number} cleared.";
    }

    private async Task OpenAppCatalogAsync(LaunchSlotItem item)
    {
        _catalogSlotIndex = item.Index;
        _catalogOpen = true;
        _pinned = true;
        _state.OpenSettings();
        AppCatalogTitle.Text = $"Choose an app for slot {item.Number}";
        AppCatalogAddButton.Content = $"Add to slot {item.Number}";
        AppCatalogSearch.Text = "";
        AppCatalogResults.SelectedItem = null;
        ApplyMode();
        AppCatalogSearch.Focus();
        if (_catalogEntries.Count == 0) await LoadAppCatalogAsync(false);
        else ApplyCatalogFilter();
    }

    private async Task LoadAppCatalogAsync(bool force)
    {
        if (_catalogBusy && !force) return;
        _catalogCancellation?.Cancel();
        _catalogCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _catalogCancellation = cancellation;
        _catalogBusy = true;
        AppCatalogStatus.Text = "Loading installed apps…";
        AppCatalogAddButton.IsEnabled = false;
        try
        {
            InstalledAppCatalogResult result = await _installedApps.LoadAsync(cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested || !ReferenceEquals(_catalogCancellation, cancellation)) return;
            _catalogEntries = result.Entries;
            ApplyCatalogFilter();
            if (result.Warnings.Count > 0)
                AppCatalogStatus.Text += " · Some app sources were unavailable";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_catalogCancellation, cancellation)) _catalogBusy = false;
        }
    }

    private void ApplyCatalogFilter()
    {
        IReadOnlyList<InstalledAppCatalogEntry> filtered = InstalledAppCatalogService.Filter(_catalogEntries, AppCatalogSearch.Text);
        _catalogItems.Clear();
        foreach (InstalledAppCatalogEntry entry in filtered) _catalogItems.Add(new InstalledAppItem(entry));
        AppCatalogStatus.Text = _catalogEntries.Count == 0
            ? "No launchable installed apps were found."
            : $"Showing {_catalogItems.Count} of {_catalogEntries.Count} installed apps";
        AppCatalogAddButton.IsEnabled = AppCatalogResults.SelectedItem is InstalledAppItem;
    }

    private void AppCatalogSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_catalogOpen && !_catalogBusy) ApplyCatalogFilter();
    }

    private void AppCatalogSelection_Changed(object sender, SelectionChangedEventArgs e) =>
        AppCatalogAddButton.IsEnabled = AppCatalogResults.SelectedItem is InstalledAppItem;

    private async void AppCatalog_Refresh_Click(object sender, RoutedEventArgs e) => await LoadAppCatalogAsync(true);
    private void AppCatalog_Back_Click(object sender, RoutedEventArgs e) => CloseAppCatalog();

    private void CloseAppCatalog()
    {
        _catalogCancellation?.Cancel();
        _catalogOpen = false;
        AppCatalogResults.SelectedItem = null;
        ApplyMode();
    }

    private void AppCatalog_Add_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogSlotIndex is < 0 or >= SettingsStore.LaunchSlotCount || AppCatalogResults.SelectedItem is not InstalledAppItem item) return;
        AppLaunchSlotSettings replacement = item.Entry.TargetKind == AppLaunchTargetKind.AppUserModelId
            ? new AppLaunchSlotSettings { DisplayName = item.Name, TargetKind = item.Entry.TargetKind, AppUserModelId = item.Entry.Target }
            : new AppLaunchSlotSettings { DisplayName = item.Name, TargetKind = item.Entry.TargetKind, TargetPath = item.Entry.Target };
        if (!TrySaveLaunchSlot(_catalogSlotIndex, replacement)) return;
        int number = _catalogSlotIndex + 1;
        CloseAppCatalog();
        SettingsStatus.Text = $"App slot {number} saved locally.";
    }

    private void AppCatalog_AdvancedBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogSlotIndex is < 0 or >= SettingsStore.LaunchSlotCount) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Choose application for slot {_catalogSlotIndex + 1}",
            Filter = "Applications and shortcuts|*.exe;*.lnk",
            CheckFileExists = true,
            DereferenceLinks = false,
            Multiselect = false
        };
        _featuresModal = true;
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            AppLaunchResult validation = _appLauncher.Validate(dialog.FileName);
            if (!validation.IsSuccess)
            {
                Notify("Application was not added", validation.Message, 5);
                return;
            }
            var replacement = new AppLaunchSlotSettings
            {
                DisplayName = AppLauncherService.SuggestedDisplayName(dialog.FileName),
                TargetKind = AppLaunchTargetKind.LocalPath,
                TargetPath = dialog.FileName
            };
            if (!TrySaveLaunchSlot(_catalogSlotIndex, replacement)) return;
            int number = _catalogSlotIndex + 1;
            CloseAppCatalog();
            SettingsStatus.Text = $"App slot {number} saved locally.";
        }
        finally { _featuresModal = false; }
    }

    private bool TrySaveLaunchSlot(int index, AppLaunchSlotSettings replacement)
    {
        AppLaunchSlotSettings previous = _settings.LaunchSlots[index];
        _settings.LaunchSlots[index] = replacement;
        if (SavePreferences())
        {
            RefreshLaunchSlotItems();
            return true;
        }
        _settings.LaunchSlots[index] = previous;
        RefreshLaunchSlotItems();
        Notify("App slot was not saved", "The previous selection was kept.", 4);
        return false;
    }

    private async Task RefreshMixerAsync()
    {
        if (_mixerBusy || _mixerCommandBusy || _closed) return;
        _mixerBusy = true;
        try
        {
            AudioSessionSnapshot snapshot = await Task.Run(_audioSessions.Read);
            if (_closed) return;
            _mixerItems.Clear();
            foreach (AudioApplicationSessionSnapshot application in snapshot.Applications)
                _mixerItems.Add(new MixerItem(application));
            MixerList.Visibility = _mixerItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MixerEmptyText.Visibility = _mixerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MixerEmptyText.Text = snapshot.Status;
        }
        finally { _mixerBusy = false; }
    }

    private async void MixerVolume_MouseUp(object sender, MouseButtonEventArgs e) => await CommitMixerVolume(sender as Slider);
    private async void MixerVolume_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            await CommitMixerVolume(sender as Slider);
    }

    private async Task CommitMixerVolume(Slider? slider)
    {
        if (slider?.Tag is not MixerItem item || _mixerCommandBusy) return;
        _mixerCommandBusy = true;
        float requestedVolume = (float)(slider.Value / 100);
        try
        {
            AudioSessionActionResult result = await Task.Run(() => _audioSessions.SetVolume(item.SessionIds, requestedVolume));
            if (!result.IsSuccess && !_closed) Notify("App volume unavailable", result.Message, 3);
        }
        finally { _mixerCommandBusy = false; }
        if (!_closed) await RefreshMixerAsync();
    }

    private async void MixerMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MixerItem item } || _mixerCommandBusy) return;
        _mixerCommandBusy = true;
        try
        {
            AudioSessionActionResult result = await Task.Run(() => _audioSessions.SetMute(item.SessionIds, !item.IsMuted));
            if (!result.IsSuccess && !_closed) Notify("App mute unavailable", result.Message, 3);
        }
        finally { _mixerCommandBusy = false; }
        if (!_closed) await RefreshMixerAsync();
    }

    private static string FormatRate(double bytesPerSecond) => FormatBytes((ulong)Math.Max(0, bytesPerSecond)) + "/s";
    private static string FormatBytes(ulong bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024UL * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024UL * 1024 * 1024) return $"{bytes / (1024d * 1024):0.#} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.#} GB";
    }

    private sealed class InstalledAppItem
    {
        public InstalledAppItem(InstalledAppCatalogEntry entry) => Entry = entry;
        public InstalledAppCatalogEntry Entry { get; }
        public string Name => Entry.DisplayName;
        public string Source => Entry.SourceLabel;
        public string Glyph => Entry.TargetKind == AppLaunchTargetKind.AppUserModelId ? "" : "";
    }

    private sealed class LaunchSlotItem
    {
        public LaunchSlotItem(int index, int number, string name, string detail, bool canLaunch, bool isConfigured)
        {
            Index = index; Number = number; Name = name; Detail = detail; CanLaunch = canLaunch; IsConfigured = isConfigured;
        }
        public int Index { get; }
        public int Number { get; }
        public string Name { get; }
        public string Detail { get; }
        public string VisibleDetail => CanLaunch ? "Open" : IsConfigured ? "Replace" : "Choose";
        public bool CanLaunch { get; }
        public bool IsConfigured { get; }
        public string ChooseLabel => IsConfigured ? "Replace" : "Choose";
        public string AutomationName => $"Application slot {Number}";
        public string AutomationHelpText => CanLaunch ? $"Launch {Name}" : $"{Name}. {Detail}";
        public string ChooseAutomationName => $"Choose or replace application slot {Number}";
        public string ClearAutomationName => $"Clear application slot {Number}";
    }

    private sealed class MixerItem
    {
        public MixerItem(AudioApplicationSessionSnapshot snapshot)
        {
            Name = snapshot.DisplayName;
            Volume = snapshot.Volume * 100;
            IsMuted = snapshot.IsMuted;
            HasMixedMute = snapshot.HasMixedMute;
            HasMixedVolume = snapshot.HasMixedVolume;
            SessionIds = snapshot.SessionInstanceIds;
        }
        public string Name { get; }
        public double Volume { get; }
        public bool IsMuted { get; }
        public bool HasMixedMute { get; }
        public bool HasMixedVolume { get; }
        public IReadOnlyList<string> SessionIds { get; }
        public string VolumeText => (HasMixedVolume ? "Mixed · " : "") + Math.Round(Volume) + "%";
        public string VolumeAutomationName => $"{Name} volume, {Math.Round(Volume)} percent";
        public string MuteGlyph => IsMuted ? "" : "";
        public string MuteAutomationName => IsMuted ? $"Unmute {Name}" : $"Mute {Name}";
    }
}
