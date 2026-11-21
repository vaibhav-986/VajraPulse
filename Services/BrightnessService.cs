using System;
using System.Runtime.InteropServices;

namespace VajraPulse.Services;

public sealed record BrightnessSnapshot(bool IsAvailable, int Percent);

public static class BrightnessService
{
    public static BrightnessSnapshot Read() => Query(null);
    public static BrightnessSnapshot Set(int percent) => Query(Math.Clamp(percent, 0, 100));
    private static BrightnessSnapshot Query(int? set)
    {
        object? locator = null, services = null, monitors = null, methods = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type == null) return new(false, 0);
            locator = Activator.CreateInstance(type);
            services = ((dynamic)locator!).ConnectServer(".", "root\\wmi");
            monitors = ((dynamic)services).ExecQuery("SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE");
            string? instance = null;
            int current = 0;
            foreach (object monitor in (dynamic)monitors)
            {
                try { instance = (string)((dynamic)monitor).InstanceName; current = Convert.ToInt32(((dynamic)monitor).CurrentBrightness); }
                finally { Release(monitor); }
                break;
            }
            if (instance == null) return new(false, 0);
            if (set.HasValue)
            {
                methods = ((dynamic)services).ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods");
                bool changed = false;
                foreach (object method in (dynamic)methods)
                {
                    try
                    {
                        if (!string.Equals((string)((dynamic)method).InstanceName, instance, StringComparison.OrdinalIgnoreCase)) continue;
                        uint result = Convert.ToUInt32(((dynamic)method).WmiSetBrightness(0u, (byte)set.Value));
                        changed = result == 0;
                        break;
                    }
                    finally { Release(method); }
                }
                if (!changed) return new(false, current);
                current = set.Value;
            }
            return new(true, Math.Clamp(current, 0, 100));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or FormatException or OverflowException or NotSupportedException)
        { return new(false, 0); }
        finally { Release(methods); Release(monitors); Release(services); Release(locator); }
    }
    private static void Release(object? obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
}
