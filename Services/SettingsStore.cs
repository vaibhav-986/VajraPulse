using System;
using System.IO;
using System.Text.Json;
using System.Linq;

namespace VajraPulse.Services;

public sealed class PulseSettings
{
    public int TopOffset { get; set; } = 10;
    public bool HoverExpand { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public bool ShowClock { get; set; } = true;
    public int FocusMinutes { get; set; } = 25;
    public int FocusSessionSeconds { get; set; }
    public int FocusRemainingSeconds { get; set; }
    public bool FocusTimerRunning { get; set; }
    public bool FocusTimerPaused { get; set; }
    public DateTimeOffset? FocusDeadlineUtc { get; set; }
    public bool HasSeenWelcome { get; set; }
    public string[] ShelfPaths { get; set; } = Array.Empty<string>();
    public string CalendarPath { get; set; } = "";
    public int MeetingReminderMinutes { get; set; } = 15;
    public bool NotificationsEnabled { get; set; }
    public bool HideNotificationContent { get; set; } = true;
    public bool NotifyAllApps { get; set; }
    public string[] NotificationAppIds { get; set; } = Array.Empty<string>();
}

public static class SettingsStore
{
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VajraPulse", "settings.json");

    public static PulseSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<PulseSettings>(File.ReadAllText(SettingsPath)) ?? new PulseSettings();
            Normalize(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        { return new PulseSettings(); }
    }

    public static bool Save(PulseSettings settings)
    {
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Normalize(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, true);
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
        s.CalendarPath = s.CalendarPath is { Length: <= 4096 } ? s.CalendarPath : "";
        s.ShelfPaths = (s.ShelfPaths ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 4096).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
        s.NotificationAppIds = (s.NotificationAppIds ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 512).Distinct(StringComparer.Ordinal).Take(50).ToArray();
    }
}
