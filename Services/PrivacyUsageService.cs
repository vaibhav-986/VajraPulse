using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace VajraPulse.Services;

public enum PrivacyDeviceKind
{
    Microphone,
    Camera
}

public sealed record PrivacyDeviceUsage(
    PrivacyDeviceKind Kind,
    bool IsInUse,
    string? AppName,
    string? AppPath,
    DateTimeOffset? LastStart,
    DateTimeOffset? LastStop);

public sealed record PrivacyUsageSnapshot(
    bool IsAvailable,
    string Status,
    DateTimeOffset ObservedAt,
    IReadOnlyList<PrivacyDeviceUsage> Usages);

/// <summary>
/// Reads Windows' existing camera and microphone usage records. This service never opens a
/// capture device, requests permission, or writes to the registry.
/// </summary>
public sealed class PrivacyUsageService : IDisposable
{
    public const string MicrophoneSettingsUri = "ms-settings:privacy-microphone";
    public const string CameraSettingsUri = "ms-settings:privacy-webcam";
    public const int MaximumRegistryEntries = 500;

    private const string ConsentStorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const int MaximumTraversalDepth = 4;
    private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumUnclosedUsageAge = TimeSpan.FromHours(24);

    private bool _disposed;

    public static string GetSettingsUri(PrivacyDeviceKind kind) =>
        kind == PrivacyDeviceKind.Microphone ? MicrophoneSettingsUri : CameraSettingsUri;

    public PrivacyUsageSnapshot Read()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_disposed)
            return new(false, "Privacy usage sensing is closed.", now, Array.Empty<PrivacyDeviceUsage>());
        if (!OperatingSystem.IsWindows())
            return new(false, "Windows privacy usage data is unavailable on this platform.", now, Array.Empty<PrivacyDeviceUsage>());

        var readings = new Dictionary<string, PrivacyDeviceUsage>(StringComparer.OrdinalIgnoreCase);
        var state = new ReadState();
        ReadKind(PrivacyDeviceKind.Microphone, "microphone", now, readings, state);
        ReadKind(PrivacyDeviceKind.Camera, "webcam", now, readings, state);

        var usages = new List<PrivacyDeviceUsage>(readings.Values);
        usages.Sort((left, right) =>
        {
            int current = right.IsInUse.CompareTo(left.IsInUse);
            if (current != 0) return current;
            DateTimeOffset leftTime = Latest(left) ?? DateTimeOffset.MinValue;
            DateTimeOffset rightTime = Latest(right) ?? DateTimeOffset.MinValue;
            int recency = rightTime.CompareTo(leftTime);
            return recency != 0 ? recency : left.Kind.CompareTo(right.Kind);
        });

        bool available = state.MicrophoneRootFound || state.CameraRootFound;
        string status;
        if (!available)
            status = state.AccessDenied
                ? "Windows privacy usage data could not be read because access was denied."
                : "Windows privacy usage data is unavailable.";
        else if (!state.MicrophoneRootFound || !state.CameraRootFound || state.AccessDenied || state.ReadError || state.Truncated)
            status = state.Truncated
                ? "Windows privacy usage data is partially available; the bounded registry scan reached its limit."
                : "Windows privacy usage data is partially available.";
        else
            status = "Windows privacy usage data is available.";

        return new(available, status, now, usages.ToArray());
    }

    public void Dispose() => _disposed = true;

    private static DateTimeOffset? Latest(PrivacyDeviceUsage usage)
    {
        if (!usage.LastStart.HasValue) return usage.LastStop;
        if (!usage.LastStop.HasValue) return usage.LastStart;
        return usage.LastStart > usage.LastStop ? usage.LastStart : usage.LastStop;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadKind(
        PrivacyDeviceKind kind,
        string capabilityName,
        DateTimeOffset now,
        Dictionary<string, PrivacyDeviceUsage> readings,
        ReadState state)
    {
        int remaining = MaximumRegistryEntries / 2;
        RegistryView[] views = Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
            : new[] { RegistryView.Default };
        RegistryHive[] hives = { RegistryHive.CurrentUser, RegistryHive.LocalMachine };

        foreach (RegistryHive hive in hives)
        {
            foreach (RegistryView view in views)
            {
                if (remaining <= 0)
                {
                    state.Truncated = true;
                    return;
                }

                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? root = baseKey.OpenSubKey(ConsentStorePath + "\\" + capabilityName, writable: false);
                    if (root == null) continue;
                    if (kind == PrivacyDeviceKind.Microphone) state.MicrophoneRootFound = true;
                    else state.CameraRootFound = true;
                    Traverse(root, kind, Array.Empty<string>(), 0, now, readings, state, ref remaining);
                }
                catch (UnauthorizedAccessException) { state.AccessDenied = true; }
                catch (SecurityException) { state.AccessDenied = true; }
                catch (Exception exception) when (IsRegistryReadException(exception)) { state.ReadError = true; }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Traverse(
        RegistryKey key,
        PrivacyDeviceKind kind,
        IReadOnlyList<string> relativeSegments,
        int depth,
        DateTimeOffset now,
        Dictionary<string, PrivacyDeviceUsage> readings,
        ReadState state,
        ref int remaining)
    {
        if (remaining-- <= 0)
        {
            state.Truncated = true;
            return;
        }

        try
        {
            FileTimeValue start = ReadFileTime(key, "LastUsedTimeStart", now);
            FileTimeValue stop = ReadFileTime(key, "LastUsedTimeStop", now);
            if (start.Timestamp.HasValue || stop.Timestamp.HasValue)
            {
                (string? appName, string? appPath) = ParseIdentity(relativeSegments);
                var usage = new PrivacyDeviceUsage(
                    kind,
                    IsCurrentUsage(start, stop, now),
                    appName,
                    appPath,
                    start.Timestamp,
                    stop.Timestamp);
                AddOrUpdate(readings, usage);
            }

            if (depth >= MaximumTraversalDepth) return;
            string[] subKeyNames = key.GetSubKeyNames();
            foreach (string subKeyName in subKeyNames)
            {
                if (remaining <= 0)
                {
                    state.Truncated = true;
                    return;
                }

                try
                {
                    using RegistryKey? child = key.OpenSubKey(subKeyName, writable: false);
                    if (child == null) continue;
                    var childSegments = new string[relativeSegments.Count + 1];
                    for (int index = 0; index < relativeSegments.Count; index++)
                        childSegments[index] = relativeSegments[index];
                    childSegments[^1] = subKeyName;
                    Traverse(child, kind, childSegments, depth + 1, now, readings, state, ref remaining);
                }
                catch (UnauthorizedAccessException) { state.AccessDenied = true; }
                catch (SecurityException) { state.AccessDenied = true; }
                catch (Exception exception) when (IsRegistryReadException(exception)) { state.ReadError = true; }
            }
        }
        catch (UnauthorizedAccessException) { state.AccessDenied = true; }
        catch (SecurityException) { state.AccessDenied = true; }
        catch (Exception exception) when (IsRegistryReadException(exception)) { state.ReadError = true; }
    }

    private static bool IsRegistryReadException(Exception exception) =>
        exception is IOException or ArgumentException or ObjectDisposedException or PlatformNotSupportedException;

    [SupportedOSPlatform("windows")]
    private static FileTimeValue ReadFileTime(RegistryKey key, string valueName, DateTimeOffset now)
    {
        object? raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        FileTimeValue parsed = ParseFileTimeValue(raw);
        if (parsed.Timestamp.HasValue && parsed.Timestamp.Value > now + FutureClockTolerance)
            return FileTimeValue.Invalid;
        return parsed;
    }

    internal static FileTimeValue ParseFileTimeValue(object? raw)
    {
        if (raw == null) return FileTimeValue.Missing;

        long fileTime;
        if (raw is long signed)
            fileTime = signed;
        else if (raw is ulong unsigned && unsigned <= long.MaxValue)
            fileTime = (long)unsigned;
        else if (raw is byte[] bytes && bytes.Length == sizeof(long))
            fileTime = BitConverter.ToInt64(bytes, 0);
        else
            return FileTimeValue.Invalid;

        if (fileTime == 0) return FileTimeValue.Zero;
        if (fileTime < 0) return FileTimeValue.Invalid;
        try
        {
            return new(true, false, new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero));
        }
        catch (ArgumentOutOfRangeException)
        {
            return FileTimeValue.Invalid;
        }
    }

    internal static bool IsCurrentUsage(FileTimeValue start, FileTimeValue stop, DateTimeOffset now)
    {
        if (!start.IsValid || !start.Timestamp.HasValue || !stop.IsValid) return false;
        DateTimeOffset started = start.Timestamp.Value;
        if (started > now + FutureClockTolerance || now - started > MaximumUnclosedUsageAge) return false;
        return stop.IsExplicitZero || (stop.Timestamp.HasValue && stop.Timestamp.Value < started);
    }

    internal static (string? AppName, string? AppPath) ParseIdentity(IReadOnlyList<string> relativeSegments)
    {
        if (relativeSegments.Count == 0) return (null, null);

        string first = relativeSegments[0];
        if (string.Equals(first, "NonPackaged", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "_NonPackaged", StringComparison.OrdinalIgnoreCase))
        {
            if (relativeSegments.Count < 2) return (null, null);
            string encodedPath = string.Join("\\", CopyAfterFirst(relativeSegments));
            string? path = SanitizePath(encodedPath.Replace('#', '\\'));
            return path == null ? (null, null) : (FileNameWithoutExtension(path), path);
        }

        return (SanitizeName(first), null);
    }

    private static string[] CopyAfterFirst(IReadOnlyList<string> values)
    {
        var result = new string[values.Count - 1];
        for (int index = 1; index < values.Count; index++) result[index - 1] = values[index];
        return result;
    }

    private static string? SanitizePath(string value)
    {
        string? clean = Sanitize(value, 1024);
        if (clean == null || clean.Length < 3 || !char.IsAsciiLetter(clean[0]) || clean[1] != ':' || clean[2] != '\\')
            return null;
        return clean;
    }

    internal static string? SanitizeName(string value) => Sanitize(value, 160);

    private static string? Sanitize(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var chars = new char[Math.Min(value.Length, maximumLength)];
        int length = 0;
        foreach (char character in value)
        {
            if (length == maximumLength) break;
            if (!char.IsControl(character)) chars[length++] = character;
        }
        string clean = new(chars, 0, length);
        clean = clean.Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static string? FileNameWithoutExtension(string path)
    {
        int separator = path.LastIndexOf('\\');
        string name = separator >= 0 ? path.Substring(separator + 1) : path;
        int extension = name.LastIndexOf('.');
        if (extension > 0) name = name.Substring(0, extension);
        return SanitizeName(name);
    }

    private static void AddOrUpdate(Dictionary<string, PrivacyDeviceUsage> readings, PrivacyDeviceUsage candidate)
    {
        string identity = candidate.AppPath != null
            ? "path:" + candidate.AppPath
            : candidate.AppName != null
                ? "name:" + candidate.AppName
                : "unknown:" + (candidate.LastStart?.UtcTicks.ToString() ?? "-") + ":" + (candidate.LastStop?.UtcTicks.ToString() ?? "-");
        string key = candidate.Kind + ":" + identity;
        if (!readings.TryGetValue(key, out PrivacyDeviceUsage? existing) || IsPreferred(candidate, existing))
            readings[key] = candidate;
    }

    private static bool IsPreferred(PrivacyDeviceUsage candidate, PrivacyDeviceUsage existing)
    {
        if (candidate.IsInUse != existing.IsInUse) return candidate.IsInUse;
        return (Latest(candidate) ?? DateTimeOffset.MinValue) > (Latest(existing) ?? DateTimeOffset.MinValue);
    }

    internal readonly record struct FileTimeValue(bool IsValid, bool IsExplicitZero, DateTimeOffset? Timestamp)
    {
        public static FileTimeValue Missing => new(false, false, null);
        public static FileTimeValue Invalid => new(false, false, null);
        public static FileTimeValue Zero => new(true, true, null);
    }

    private sealed class ReadState
    {
        public bool MicrophoneRootFound;
        public bool CameraRootFound;
        public bool AccessDenied;
        public bool ReadError;
        public bool Truncated;
    }
}
