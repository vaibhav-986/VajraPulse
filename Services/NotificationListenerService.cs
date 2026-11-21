using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace VajraPulse.Services;

public sealed record NotificationApp(string Id, string Name);
public sealed record PulseNotification(string AppId, string AppName, string Title, string Body, DateTimeOffset Created);
public sealed record NotificationAccess(bool Allowed, string Message);

public sealed class NotificationListenerService : IDisposable
{
    private UserNotificationListener? _listener;
    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _seeded, _disposed;
    public bool Enabled { get; private set; }
    public string Status { get; private set; } = "Notification previews are off.";
    public IReadOnlyList<NotificationApp> Apps { get; private set; } = Array.Empty<NotificationApp>();
    public static bool HasPackageIdentity
    {
        get { uint length = 0; return GetCurrentPackageFullName(ref length, IntPtr.Zero) == 122; }
    }

    public async Task<NotificationAccess> EnableAsync(bool requestPermission)
    {
        if (_disposed) return new(false, "Listener is closed.");
        if (!HasPackageIdentity)
        {
            Status = "Optional packaged installation is required. See packaging\\README.md.";
            return new(false, Status);
        }
        try
        {
            _listener ??= UserNotificationListener.Current;
            var access = requestPermission ? await _listener.RequestAccessAsync() : _listener.GetAccessStatus();
            Enabled = access == UserNotificationListenerAccessStatus.Allowed;
            Status = Enabled ? "Allowed. Choose apps below; existing alerts are not replayed." : access == UserNotificationListenerAccessStatus.Denied
                ? "Access denied. Enable it in Windows notification privacy settings." : "Permission has not been granted.";
            _seeded = false;
            _seen.Clear();
            return new(Enabled, Status);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            Enabled = false;
            Status = "Windows could not enable the listener: " + ex.Message;
            return new(false, Status);
        }
    }

    public async Task<IReadOnlyList<PulseNotification>> SyncAsync(IEnumerable<string> selectedApps, bool allApps, bool hideContent)
    {
        if (!Enabled || _disposed || !await _gate.WaitAsync(0)) return Array.Empty<PulseNotification>();
        try
        {
            if (_listener == null || _listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Enabled = false;
                Status = "Notification permission was revoked.";
                return Array.Empty<PulseNotification>();
            }
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (_disposed || !Enabled) return Array.Empty<PulseNotification>();
            var allowed = new HashSet<string>(selectedApps, StringComparer.Ordinal);
            var fresh = new List<PulseNotification>();
            var known = new HashSet<string>(StringComparer.Ordinal);
            var apps = new Dictionary<string, NotificationApp>(StringComparer.Ordinal);
            foreach (var notification in notifications.Take(1000))
            {
                try
                {
                    string key = notification.Id + ":" + notification.CreationTime.UtcTicks;
                    known.Add(key);
                    string appId = notification.AppInfo.AppUserModelId;
                    string appName = notification.AppInfo.DisplayInfo.DisplayName;
                    apps[appId] = new(appId, appName);
                    if (!_seeded || _seen.Contains(key) || (!allApps && !allowed.Contains(appId))) continue;
                    string title = "New notification", body = "Content hidden";
                    if (!hideContent)
                    {
                        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                        var text = binding?.GetTextElements();
                        title = text?.FirstOrDefault()?.Text ?? "New notification";
                        body = text == null ? "" : string.Join(" ", text.Skip(1).Select(t => t.Text));
                    }
                    fresh.Add(new(appId, appName, Limit(title, 180), Limit(body, 500), notification.CreationTime));
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
            }
            _seen = known;
            _seeded = true;
            Apps = apps.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            Status = allApps ? "Previewing new alerts from all apps." : $"Previewing new alerts from {allowed.Count} selected app(s).";
            return fresh.OrderBy(n => n.Created).TakeLast(5).ToArray();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "Windows notification sync is unavailable.";
            return Array.Empty<PulseNotification>();
        }
        finally { _gate.Release(); }
    }
    public void Disable()
    {
        Enabled = false; _seeded = false; _seen.Clear(); Apps = Array.Empty<NotificationApp>();
        Status = "Notification previews are off.";
    }
    public void Dispose() { _disposed = true; Disable(); _listener = null; }
    private static string Limit(string text, int length) => text.Length <= length ? text : text.Substring(0, length) + "…";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);
}
