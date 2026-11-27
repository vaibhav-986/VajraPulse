using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace VajraPulse.Services;

public enum AppLaunchTargetKind
{
    LocalPath,
    StartMenuShortcut,
    AppUserModelId
}

public enum IslandDockPosition
{
    TopCenter = 0,
    TopLeft = 1,
    TopRight = 2
}

public sealed class AppLaunchSlotSettings
{
    public string DisplayName { get; set; } = "";
    public AppLaunchTargetKind TargetKind { get; set; }
    public string TargetPath { get; set; } = "";
    public string AppUserModelId { get; set; } = "";
}

public sealed class PulseSettings
{
    public int SchemaVersion { get; set; } = SettingsStore.CurrentSchemaVersion;
    [JsonIgnore] public bool IsFutureSchema { get; set; }
    public int TopOffset { get; set; }
    public bool HoverExpand { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public bool ShowClock { get; set; } = true;
    public bool ShowSystemRailButton { get; set; } = true;
    public bool ShowBluetoothRailButton { get; set; } = true;
    public bool ShowWifiRailButton { get; set; } = true;
    public int FocusMinutes { get; set; } = 25;
    public int FocusSessionSeconds { get; set; }
    public int FocusRemainingSeconds { get; set; }
    public bool FocusTimerRunning { get; set; }
    public bool FocusTimerPaused { get; set; }
    public DateTimeOffset? FocusDeadlineUtc { get; set; }
    public bool PomodoroEnabled { get; set; }
    public int PomodoroWorkMinutes { get; set; } = 25;
    public int PomodoroBreakMinutes { get; set; } = 5;
    public int PomodoroLongBreakMinutes { get; set; } = 15;
    public int PomodoroCyclesCompleted { get; set; }
    public bool PomodoroIsBreak { get; set; }
    public bool HasSeenWelcome { get; set; }
    public string[] ShelfPaths { get; set; } = Array.Empty<string>();
    public string CalendarPath { get; set; } = "";
    public int MeetingReminderMinutes { get; set; } = 15;
    public bool NotificationsEnabled { get; set; }
    public bool HideNotificationContent { get; set; } = true;
    public bool NotifyAllApps { get; set; }
    public string[] NotificationAppIds { get; set; } = Array.Empty<string>();
    public string AccentColor { get; set; } = "";
    public string EyeStyle { get; set; } = "Googly";
    public string VisualMode { get; set; } = "Cute";
    public IslandDockPosition DockPosition { get; set; } = IslandDockPosition.TopCenter;
    public bool UseArtworkAccent { get; set; } = true;
    public bool WeatherEnabled { get; set; }
    public string WeatherCity { get; set; } = "";
    public bool WeatherFahrenheit { get; set; }
    public AppLaunchSlotSettings[] LaunchSlots { get; set; } = SettingsStore.CreateEmptyLaunchSlots();
    public Dictionary<string, string> CustomHotkeys { get; set; } = new Dictionary<string, string>();
}

public static class SettingsStore
{
    public const int CurrentSchemaVersion = 5;
    public const int LaunchSlotCount = 5;
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VajraPulse", "settings.json");

    public static AppLaunchSlotSettings[] CreateEmptyLaunchSlots() =>
        Enumerable.Range(0, LaunchSlotCount).Select(_ => new AppLaunchSlotSettings()).ToArray();

    public static PulseSettings Load(string? path = null)
    {
        string settingsPath = path ?? SettingsPath;
        try
        {
            var settings = JsonSerializer.Deserialize<PulseSettings>(File.ReadAllText(settingsPath)) ?? new PulseSettings();
            if (settings.SchemaVersion > CurrentSchemaVersion)
            {
                return new PulseSettings { IsFutureSchema = true };
            }
            if (settings.SchemaVersion < 5 && string.Equals(settings.AccentColor, "#F5F5F7", StringComparison.OrdinalIgnoreCase))
            {
                settings.AccentColor = "";
            }
            settings.SchemaVersion = CurrentSchemaVersion;
            Normalize(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        { return new PulseSettings(); }
    }

    public static bool Save(PulseSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.IsFutureSchema) return false;
        string settingsPath = path ?? SettingsPath;
        string temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            settings.SchemaVersion = CurrentSchemaVersion;
            Normalize(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, settingsPath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Normalize(PulseSettings s)
    {
        s.TopOffset = Math.Clamp(s.TopOffset, 0, 80);
        s.FocusMinutes = Math.Clamp(s.FocusMinutes, 1, 180);
        s.PomodoroWorkMinutes = Math.Clamp(s.PomodoroWorkMinutes, 1, 180);
        s.PomodoroBreakMinutes = Math.Clamp(s.PomodoroBreakMinutes, 1, 60);
        s.PomodoroLongBreakMinutes = Math.Clamp(s.PomodoroLongBreakMinutes, 5, 60);
        s.PomodoroCyclesCompleted = Math.Clamp(s.PomodoroCyclesCompleted, 0, 1000);
        if (s.FocusSessionSeconds is < 1 or > 86_400)
        {
            s.FocusSessionSeconds = 0;
            s.FocusRemainingSeconds = 0;
            s.FocusTimerRunning = false;
            s.FocusTimerPaused = false;
            s.FocusDeadlineUtc = null;
        }
        else
        {
            s.FocusRemainingSeconds = Math.Clamp(s.FocusRemainingSeconds, 0, s.FocusSessionSeconds);
            if (s.FocusTimerRunning && s.FocusTimerPaused)
            {
                s.FocusTimerRunning = false;
                s.FocusTimerPaused = true;
            }
            if ((s.FocusTimerRunning || s.FocusTimerPaused) && s.FocusRemainingSeconds == 0)
            {
                s.FocusTimerRunning = false;
                s.FocusTimerPaused = false;
                s.FocusDeadlineUtc = null;
            }
            if (!s.FocusTimerRunning) s.FocusDeadlineUtc = null;
        }
        s.MeetingReminderMinutes = Math.Clamp(s.MeetingReminderMinutes, 1, 60);
        s.WeatherCity = (s.WeatherCity ?? "").Trim();
        if (s.WeatherCity.Length > 80) s.WeatherCity = s.WeatherCity[..80];
        s.EyeStyle = s.EyeStyle is "Googly" or "Kawaii" or "Starry" ? s.EyeStyle : "Googly";
        s.VisualMode = s.VisualMode is "Cute" or "Glass" or "Neon" ? s.VisualMode : "Cute";
        if (!string.IsNullOrWhiteSpace(s.AccentColor))
        {
            string value = s.AccentColor.Trim().TrimStart('#');
            bool valid = (value.Length is 6 or 8) && value.All(Uri.IsHexDigit);
            s.AccentColor = valid ? "#" + value.ToUpperInvariant() : "";
        }
        else s.AccentColor = "";
        if (!Enum.IsDefined(s.DockPosition)) s.DockPosition = IslandDockPosition.TopCenter;
        s.CalendarPath = s.CalendarPath is { Length: <= 4096 } ? s.CalendarPath : "";
        s.ShelfPaths = (s.ShelfPaths ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 4096).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
        s.NotificationAppIds = (s.NotificationAppIds ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 512).Distinct(StringComparer.Ordinal).Take(50).ToArray();
        var slots = (s.LaunchSlots ?? Array.Empty<AppLaunchSlotSettings>())
            .Take(LaunchSlotCount)
            .Select(NormalizeLaunchSlot)
            .ToList();
        while (slots.Count < LaunchSlotCount) slots.Add(new AppLaunchSlotSettings());
        s.LaunchSlots = slots.ToArray();
    }

    private static AppLaunchSlotSettings NormalizeLaunchSlot(AppLaunchSlotSettings? slot)
    {
        if (slot == null) return new AppLaunchSlotSettings();
        AppLaunchTargetKind kind = Enum.IsDefined(slot.TargetKind) ? slot.TargetKind : AppLaunchTargetKind.LocalPath;
        string name = BoundText(slot.DisplayName, 80);
        string path = BoundText(slot.TargetPath, 4096);
        string appId = BoundText(slot.AppUserModelId, 512);
        if (kind == AppLaunchTargetKind.AppUserModelId)
            return string.IsNullOrWhiteSpace(appId) ? new AppLaunchSlotSettings() : new AppLaunchSlotSettings { DisplayName = name, TargetKind = kind, AppUserModelId = appId };
        return string.IsNullOrWhiteSpace(path) ? new AppLaunchSlotSettings() : new AppLaunchSlotSettings { DisplayName = name, TargetKind = kind, TargetPath = path };
    }

    private static string BoundText(string? value, int maximumLength)
    {
        value = value?.Trim() ?? "";
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
