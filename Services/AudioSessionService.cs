using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static VajraPulse.Services.CoreAudioInterop;

namespace VajraPulse.Services;

public enum AudioSessionAvailability
{
    Available,
    Unavailable
}

public enum AudioSessionActionCode
{
    Success,
    SessionNotFound,
    Unavailable,
    AccessDenied,
    Failed
}

public sealed record AudioApplicationSessionSnapshot(
    string Id,
    string DisplayName,
    uint? ProcessId,
    float Volume,
    bool IsMuted,
    bool HasMixedVolume,
    bool HasMixedMute,
    int SessionCount,
    IReadOnlyList<string> SessionInstanceIds);

public sealed record AudioSessionSnapshot(
    AudioSessionAvailability Availability,
    string Status,
    IReadOnlyList<AudioApplicationSessionSnapshot> Applications);

public sealed record AudioSessionActionResult(AudioSessionActionCode Code, string Message)
{
    public bool IsSuccess => Code == AudioSessionActionCode.Success;
}

public sealed class AudioSessionService
{
    public const int MaximumSessions = 128;
    public const int MaximumApplications = 32;
    private readonly object _gate = new();

    public AudioSessionSnapshot Read()
    {
        lock (_gate)
        {
            try
            {
                IReadOnlyList<AudioSessionCandidate> candidates = EnumerateCandidates();
                return CreateSnapshot(candidates);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return new(AudioSessionAvailability.Unavailable, "Application audio sessions are unavailable.", Array.Empty<AudioApplicationSessionSnapshot>());
            }
        }
    }

    public AudioSessionActionResult SetVolume(IReadOnlyList<string> sessionInstanceIds, float volume) =>
        Apply(sessionInstanceIds, simple =>
        {
            Guid context = Guid.Empty;
            Marshal.ThrowExceptionForHR(simple.SetMasterVolume(Math.Clamp(volume, 0, 1), ref context));
        });

    public AudioSessionActionResult SetMute(IReadOnlyList<string> sessionInstanceIds, bool muted) =>
        Apply(sessionInstanceIds, simple =>
        {
            Guid context = Guid.Empty;
            Marshal.ThrowExceptionForHR(simple.SetMute(muted, ref context));
        });

    private AudioSessionActionResult Apply(IReadOnlyList<string> sessionInstanceIds, Action<ISimpleAudioVolume> action)
    {
        ArgumentNullException.ThrowIfNull(sessionInstanceIds);
        var wanted = sessionInstanceIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return new(AudioSessionActionCode.SessionNotFound, "The audio session has ended.");

        lock (_gate)
        {
            IMMDeviceEnumerator? deviceEnumerator = null;
            IMMDevice? device = null;
            IAudioSessionManager2? manager = null;
            IAudioSessionEnumerator? sessionEnumerator = null;
            int changed = 0;
            try
            {
                OpenSessionEnumerator(out deviceEnumerator, out device, out manager, out sessionEnumerator);
                Marshal.ThrowExceptionForHR(sessionEnumerator.GetCount(out int count));
                for (int index = 0; index < Math.Min(count, MaximumSessions); index++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(sessionEnumerator.GetSession(index, out control));
                        var control2 = (IAudioSessionControl2)control;
                        string instanceId = ReadSessionString(control2.GetSessionInstanceIdentifier);
                        if (!wanted.Contains(instanceId)) continue;
                        action((ISimpleAudioVolume)control);
                        changed++;
                    }
                    finally { Release(control); }
                }
                return changed > 0
                    ? new(AudioSessionActionCode.Success, "Application audio updated.")
                    : new(AudioSessionActionCode.SessionNotFound, "The audio session has ended.");
            }
            catch (UnauthorizedAccessException)
            {
                return new(AudioSessionActionCode.AccessDenied, "Windows denied access to this audio session.");
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return new(AudioSessionActionCode.Failed, "Windows could not update this audio session.");
            }
            finally
            {
                Release(sessionEnumerator);
                Release(manager);
                Release(device);
                Release(deviceEnumerator);
            }
        }
    }

    private static IReadOnlyList<AudioSessionCandidate> EnumerateCandidates()
    {
        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        var candidates = new List<AudioSessionCandidate>();
        try
        {
            OpenSessionEnumerator(out deviceEnumerator, out device, out manager, out sessionEnumerator);
            Marshal.ThrowExceptionForHR(sessionEnumerator.GetCount(out int count));
            for (int index = 0; index < Math.Min(count, MaximumSessions); index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    Marshal.ThrowExceptionForHR(sessionEnumerator.GetSession(index, out control));
                    var control2 = (IAudioSessionControl2)control;
                    if (control2.GetState(out int state) < 0 || state != AudioSessionStateActive) continue;
                    if (control2.IsSystemSoundsSession() == 0) continue;
                    if (control2.GetProcessId(out uint processId) < 0 || processId == 0) continue;
                    string instanceId = ReadSessionString(control2.GetSessionInstanceIdentifier);
                    if (string.IsNullOrWhiteSpace(instanceId)) instanceId = $"pid:{processId}:{index}";
                    string sessionName = ReadSessionString(control2.GetDisplayName);
                    var simple = (ISimpleAudioVolume)control;
                    Marshal.ThrowExceptionForHR(simple.GetMasterVolume(out float volume));
                    Marshal.ThrowExceptionForHR(simple.GetMute(out bool muted));
                    (string identity, string displayName) = ResolveIdentity(processId, sessionName);
                    candidates.Add(new(identity, displayName, processId, instanceId, Math.Clamp(volume, 0, 1), muted));
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    // Sessions routinely disappear while they are being enumerated.
                }
                finally { Release(control); }
            }
            return candidates;
        }
        finally
        {
            Release(sessionEnumerator);
            Release(manager);
            Release(device);
            Release(deviceEnumerator);
        }
    }

    private static void OpenSessionEnumerator(
        out IMMDeviceEnumerator deviceEnumerator,
        out IMMDevice device,
        out IAudioSessionManager2 manager,
        out IAudioSessionEnumerator sessionEnumerator)
    {
        deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(deviceEnumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out device));
        Guid iid = typeof(IAudioSessionManager2).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object value));
        manager = (IAudioSessionManager2)value;
        Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessionEnumerator));
    }

    internal static AudioSessionSnapshot CreateSnapshot(IEnumerable<AudioSessionCandidate> source)
    {
        AudioApplicationSessionSnapshot[] applications = source
            .Where(item => !string.IsNullOrWhiteSpace(item.Identity) && !string.IsNullOrWhiteSpace(item.SessionInstanceId))
            .GroupBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                AudioSessionCandidate[] sessions = group.Take(MaximumSessions).ToArray();
                float minimum = sessions.Min(item => item.Volume);
                float maximum = sessions.Max(item => item.Volume);
                bool anyMuted = sessions.Any(item => item.IsMuted);
                bool allMuted = sessions.All(item => item.IsMuted);
                return new AudioApplicationSessionSnapshot(
                    group.Key,
                    SanitizeName(sessions.Select(item => item.DisplayName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))),
                    sessions.Select(item => (uint?)item.ProcessId).FirstOrDefault(),
                    sessions.Average(item => item.Volume),
                    allMuted,
                    maximum - minimum > 0.01f,
                    anyMuted != allMuted,
                    sessions.Length,
                    Array.AsReadOnly(sessions.Select(item => item.SessionInstanceId).Distinct(StringComparer.Ordinal).ToArray()));
            })
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(MaximumApplications)
            .ToArray();

        string status = applications.Length == 0
            ? "No applications are currently producing audio."
            : $"{applications.Length} active audio application(s).";
        return new(AudioSessionAvailability.Available, status, Array.AsReadOnly(applications));
    }

    private static (string Identity, string DisplayName) ResolveIdentity(uint processId, string sessionName)
    {
        string processName = "";
        string description = "";
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            try { description = process.MainModule?.FileVersionInfo.FileDescription ?? ""; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }

        string display = !string.IsNullOrWhiteSpace(description) ? description
            : !string.IsNullOrWhiteSpace(sessionName) ? sessionName
            : !string.IsNullOrWhiteSpace(processName) ? processName : $"Application {processId}";
        string identity = !string.IsNullOrWhiteSpace(processName) ? processName : $"pid:{processId}";
        return (identity.ToLowerInvariant(), SanitizeName(display));
    }

    private delegate int SessionStringReader(out IntPtr value);

    private static string ReadSessionString(SessionStringReader reader)
    {
        IntPtr value = IntPtr.Zero;
        int result = reader(out value);
        return result >= 0 ? TakeStringAndFree(value) : "";
    }

    internal static string SanitizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Application";
        var builder = new StringBuilder(Math.Min(value.Length, 80));
        bool space = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character)) { space = builder.Length > 0; continue; }
            if (char.IsControl(character)) continue;
            if (space && builder.Length < 80) builder.Append(' ');
            space = false;
            if (builder.Length >= 80) break;
            builder.Append(character);
        }
        return builder.Length == 0 ? "Application" : builder.ToString();
    }

    private static bool IsExpected(Exception exception) =>
        exception is COMException or InvalidCastException or InvalidOperationException or Win32Exception or UnauthorizedAccessException;
}

internal sealed record AudioSessionCandidate(
    string Identity,
    string DisplayName,
    uint ProcessId,
    string SessionInstanceId,
    float Volume,
    bool IsMuted);
