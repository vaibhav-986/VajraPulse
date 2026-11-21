using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace VajraPulse.Services;

public sealed record StartupRegistration(bool IsAvailable, bool IsEnabled, string Status);

/// <summary>
/// Manages the current user's ordinary Windows Run entry. It never changes machine-wide
/// settings and writes only after the user changes the Start with Windows option.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VajraPulse";

    public static StartupRegistration Read()
    {
        string? command = CurrentCommand();
        if (command == null)
            return new(false, false, "Startup is unavailable for this app build.");

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? stored = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            bool enabled = string.Equals(stored, command, StringComparison.OrdinalIgnoreCase);
            return new(true, enabled, enabled ? "Starts with Windows." : "Does not start with Windows.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new(false, false, "Windows startup settings could not be read.");
        }
    }

    public static StartupRegistration SetEnabled(bool enabled)
    {
        string? command = CurrentCommand();
        if (command == null)
            return new(false, false, "Startup is unavailable for this app build.");

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
                key.SetValue(ValueName, command, RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return new(true, enabled, enabled ? "Vajra Pulse will start with Windows." : "Vajra Pulse will not start with Windows.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new(false, false, "Windows did not allow the startup setting to change.");
        }
    }

    private static string? CurrentCommand()
    {
        if (NotificationListenerService.HasPackageIdentity) return null;
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return '"' + path + '"';
    }
}
