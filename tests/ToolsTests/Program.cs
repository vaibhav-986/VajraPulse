using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using VajraPulse.Services;

var tests = new (string Name, Action Run)[]
{
    ("CPU delta calculation", TestCpuCalculation),
    ("CPU reset is warming up", TestCpuReset),
    ("Wi-Fi state aggregation", TestWifiAggregation),
    ("Settings migrate to five slots", TestSettingsMigration),
    ("Future settings are not overwritten", TestFutureSettings),
    ("Launcher validates local explicit targets", TestLauncherValidation),
    ("Launcher routes explicit target kinds", TestLauncherKinds),
    ("Installed app catalog merges and filters", TestCatalogMergeAndFilter),
    ("Audio sessions group by application", TestAudioGrouping),
    ("Version metadata is 1.3", TestVersionMetadata),
    ("Live telemetry is bounded", TestLiveTelemetry),
    ("Live Wi-Fi status is bounded", TestLiveWifi),
    ("Live audio enumeration is read-only", TestLiveAudio)
};

int failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}
return failures == 0 ? 0 : 1;

static void TestCpuCalculation()
{
    double? value = SystemTelemetryService.CalculateCpuPercent(
        new SystemTelemetryService.CpuCounters(100, 300, 100),
        new SystemTelemetryService.CpuCounters(150, 400, 200));
    Assert(value is > 74.9 and < 75.1, "Expected 75% aggregate CPU usage.");
}

static void TestCpuReset()
{
    double? value = SystemTelemetryService.CalculateCpuPercent(
        new SystemTelemetryService.CpuCounters(100, 300, 100),
        new SystemTelemetryService.CpuCounters(90, 400, 200));
    Assert(value == null, "Regressed counters must not fabricate utilization.");
}

static void TestWifiAggregation()
{
    Assert(WifiStatusService.AggregateState(4, 1) == WifiConnectionState.Connected, "A connected interface should win.");
    Assert(WifiStatusService.AggregateState(4, 5) == WifiConnectionState.Connecting, "Associating should report connecting.");
    Assert(WifiStatusService.AggregateState(4) == WifiConnectionState.Disconnected, "Disconnected should remain disconnected.");
}

static void TestSettingsMigration()
{
    string directory = MakeTempDirectory();
    try
    {
        string path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{\"FocusMinutes\":45,\"ShelfPaths\":[\"C:\\\\demo.txt\"]}");
        PulseSettings settings = SettingsStore.Load(path);
        Assert(settings.SchemaVersion == SettingsStore.CurrentSchemaVersion, "Legacy settings should migrate to the current schema.");
        Assert(settings.FocusMinutes == 45, "Legacy values should be preserved.");
        Assert(settings.LaunchSlots.Length == 5, "Migration should create exactly five slots.");
        Assert(settings.ShowSystemRailButton && settings.ShowBluetoothRailButton && settings.ShowWifiRailButton, "Legacy settings should show all rail buttons by default.");
        settings.ShowBluetoothRailButton = false;
        settings.LaunchSlots[2].DisplayName = "Editor";
        settings.LaunchSlots[2].TargetPath = @"C:\Apps\Editor.exe";
        settings.LaunchSlots[3] = new AppLaunchSlotSettings { DisplayName = "Calculator", TargetKind = AppLaunchTargetKind.AppUserModelId, AppUserModelId = "Microsoft.Calculator_123!App" };
        Assert(SettingsStore.Save(settings, path), "Migrated settings should save.");
        PulseSettings reloaded = SettingsStore.Load(path);
        Assert(reloaded.LaunchSlots.Length == 5 && reloaded.LaunchSlots[2].DisplayName == "Editor", "Slot order should round-trip.");
        Assert(!reloaded.ShowBluetoothRailButton && reloaded.ShowSystemRailButton && reloaded.ShowWifiRailButton, "Rail options should round-trip independently.");
        Assert(reloaded.LaunchSlots[3].TargetKind == AppLaunchTargetKind.AppUserModelId && reloaded.LaunchSlots[3].AppUserModelId.Contains("Calculator"), "Packaged app targets should round-trip.");
    }
    finally { Directory.Delete(directory, true); }
}

static void TestFutureSettings()
{
    string directory = MakeTempDirectory();
    try
    {
        string path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":99,\"FocusMinutes\":88}");
        PulseSettings settings = SettingsStore.Load(path);
        Assert(settings.IsFutureSchema, "A future schema should be marked read-only.");
        Assert(!SettingsStore.Save(settings, path), "A future schema must not be overwritten.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert(document.RootElement.GetProperty("SchemaVersion").GetInt32() == 99, "The future file should remain unchanged.");
    }
    finally { Directory.Delete(directory, true); }
}

static void TestLauncherValidation()
{
    string directory = MakeTempDirectory();
    try
    {
        string executable = Path.Combine(directory, "Example.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        var service = new AppLauncherService(_ => throw new InvalidOperationException("The test must not launch."));
        Assert(service.Validate(executable).IsSuccess, "An existing local .exe should validate.");
        Assert(service.Validate(Path.Combine(directory, "Missing.exe")).Code == AppLaunchResultCode.TargetMissing, "A missing target should be explicit.");
        string document = Path.Combine(directory, "note.txt");
        File.WriteAllText(document, "test");
        Assert(service.Validate(document).Code == AppLaunchResultCode.UnsupportedTarget, "Documents should not be launch slots.");
        Assert(service.Validate(@"\\server\share\app.exe").Code == AppLaunchResultCode.InvalidPath, "UNC targets should be rejected.");
    }
    finally { Directory.Delete(directory, true); }
}

static void TestLauncherKinds()
{
    string directory = MakeTempDirectory();
    try
    {
        string executable = Path.Combine(directory, "Editor.exe");
        string shortcut = Path.Combine(directory, "Editor.lnk");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllBytes(shortcut, Array.Empty<byte>());
        ProcessStartInfo? captured = null;
        var activator = new FakePackagedActivator();
        var service = new AppLauncherService(start => { captured = start; return null; }, activator);
        var local = new AppLaunchSlotSettings { DisplayName = "Editor", TargetKind = AppLaunchTargetKind.LocalPath, TargetPath = executable };
        AppLaunchResult localResult = service.LaunchAsync(local).GetAwaiter().GetResult();
        Assert(localResult.IsSuccess && captured?.FileName == executable, "A local app should use the injected process starter.");
        Assert(captured?.WorkingDirectory == directory && captured.UseShellExecute, "A direct executable should start in its containing directory through Shell.");
        var startMenu = new AppLaunchSlotSettings { DisplayName = "Editor", TargetKind = AppLaunchTargetKind.StartMenuShortcut, TargetPath = shortcut };
        Assert(service.LaunchAsync(startMenu).GetAwaiter().GetResult().IsSuccess && captured?.FileName == shortcut, "A Start Menu app should launch its original shortcut.");
        var packaged = new AppLaunchSlotSettings { DisplayName = "Calculator", TargetKind = AppLaunchTargetKind.AppUserModelId, AppUserModelId = "Microsoft.Calculator_123!App" };
        Assert(service.LaunchAsync(packaged).GetAwaiter().GetResult().IsSuccess && activator.Count == 1, "A packaged app should activate only after explicit launch.");
    }
    finally { Directory.Delete(directory, true); }
}

static void TestCatalogMergeAndFilter()
{
    var duplicateA = new InstalledAppCatalogEntry("shortcut:C:/Apps/Editor.lnk", "Editor", AppLaunchTargetKind.StartMenuShortcut, @"C:\Apps\Editor.lnk", "Editor code tools", "Desktop app");
    var duplicateB = duplicateA with { DisplayName = "Editor duplicate" };
    var packaged = new InstalledAppCatalogEntry("aumid:Example!App", "Editor", AppLaunchTargetKind.AppUserModelId, "Example!App", "Editor windows notes", "Windows app");
    var browser = new InstalledAppCatalogEntry("shortcut:C:/Apps/Browser.lnk", "Browser", AppLaunchTargetKind.StartMenuShortcut, @"C:\Apps\Browser.lnk", "Fast Browser web", "Desktop app");
    var service = new InstalledAppCatalogService(new IInstalledAppCatalogSource[]
    {
        new FakeCatalogSource(duplicateA, packaged),
        new FakeCatalogSource(duplicateB, browser)
    });
    InstalledAppCatalogResult result = service.LoadAsync().GetAwaiter().GetResult();
    Assert(result.Entries.Count == 3, "Catalog should deduplicate stable IDs but keep same-name distinct apps.");
    Assert(result.Entries[0].DisplayName == "Browser", "Catalog should be deterministically sorted.");
    IReadOnlyList<InstalledAppCatalogEntry> filtered = InstalledAppCatalogService.Filter(result.Entries, "editor notes");
    Assert(filtered.Count == 1 && filtered[0].TargetKind == AppLaunchTargetKind.AppUserModelId, "Search should require every term case-insensitively.");
}

static void TestAudioGrouping()
{
    AudioSessionSnapshot snapshot = AudioSessionService.CreateSnapshot(new[]
    {
        new AudioSessionCandidate("browser", "Browser", 10, "one", 0.2f, false),
        new AudioSessionCandidate("browser", "Browser", 10, "two", 0.8f, true),
        new AudioSessionCandidate("music", "Music", 20, "three", 0.5f, false)
    });
    Assert(snapshot.Applications.Count == 2, "Sessions should group by application identity.");
    AudioApplicationSessionSnapshot browser = snapshot.Applications.Single(item => item.Id == "browser");
    Assert(browser.SessionCount == 2 && browser.HasMixedVolume && browser.HasMixedMute, "Grouped mixed state should be reported.");
    Assert(browser.Volume is > 0.49f and < 0.51f, "Grouped volume should be averaged.");
}

static void TestVersionMetadata()
{
    string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    XDocument project = XDocument.Load(Path.Combine(root, "VajraPulse.csproj"));
    string? version = project.Descendants("Version").SingleOrDefault()?.Value;
    string? fileVersion = project.Descendants("FileVersion").SingleOrDefault()?.Value;
    Assert(version == "1.3.0" && fileVersion == "1.3.0.0", "Project versions should be 1.3.");
    XDocument package = XDocument.Load(Path.Combine(root, "packaging", "Package.appxmanifest"));
    XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    Assert(package.Root?.Element(ns + "Identity")?.Attribute("Version")?.Value == "1.3.0.0", "MSIX version should be 1.3.0.0.");
}

static void TestLiveTelemetry()
{
    var service = new SystemTelemetryService();
    SystemTelemetrySnapshot first = service.Sample();
    Thread.Sleep(20);
    SystemTelemetrySnapshot second = service.Sample();
    Assert(second.Cpu.Percent is null or >= 0 and <= 100, "CPU must be bounded or unavailable.");
    Assert(second.Memory.Percent is null or >= 0 and <= 100, "Memory must be bounded or unavailable.");
    Assert(second.Network.BytesReceivedPerSecond is null or >= 0, "Network receive rate must be non-negative.");
}

static void TestLiveWifi()
{
    WifiStatusSnapshot snapshot = new WifiStatusService().Read();
    Assert(snapshot.InterfaceCount is >= 0 and <= WifiStatusService.MaximumInterfaces, "Wi-Fi interfaces should be bounded.");
    Assert(!string.IsNullOrWhiteSpace(snapshot.Status), "Wi-Fi should provide a truthful status.");
}

static void TestLiveAudio()
{
    AudioSessionSnapshot snapshot = new AudioSessionService().Read();
    Assert(snapshot.Applications.Count <= AudioSessionService.MaximumApplications, "Audio applications should be bounded.");
    Assert(!string.IsNullOrWhiteSpace(snapshot.Status), "Audio enumeration should provide a status.");
    foreach (AudioApplicationSessionSnapshot app in snapshot.Applications)
        Assert(app.Volume is >= 0 and <= 1, "Audio volumes should be bounded.");
}

static string MakeTempDirectory()
{
    string path = Path.Combine(Path.GetTempPath(), "VajraPulse-ToolsTests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakePackagedActivator : IPackagedAppActivator
{
    public int Count { get; private set; }
    public Task<AppLaunchResult> LaunchAsync(string appUserModelId, CancellationToken cancellationToken)
    {
        Count++;
        return Task.FromResult(new AppLaunchResult(AppLaunchResultCode.Success, "Fake launch accepted.", appUserModelId));
    }
}

sealed class FakeCatalogSource : IInstalledAppCatalogSource
{
    private readonly IReadOnlyList<InstalledAppCatalogEntry> _entries;
    public FakeCatalogSource(params InstalledAppCatalogEntry[] entries) => _entries = entries;
    public Task<InstalledAppSourceResult> EnumerateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new InstalledAppSourceResult(_entries, Array.Empty<string>()));
}
