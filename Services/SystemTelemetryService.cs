using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace VajraPulse.Services;

public enum TelemetryAvailability
{
    WarmingUp,
    Available,
    Unavailable
}

public sealed record CpuTelemetry(TelemetryAvailability Availability, double? Percent, double? CoreUsage, string Status);
public sealed record MemoryTelemetry(TelemetryAvailability Availability, ulong UsedBytes, ulong TotalBytes, double? Percent, string Status);
public sealed record NetworkTelemetry(TelemetryAvailability Availability, double? BytesReceivedPerSecond, double? BytesSentPerSecond, int ActiveInterfaceCount, string Status);
public sealed record SystemTelemetrySnapshot(DateTimeOffset ObservedAt, CpuTelemetry Cpu, MemoryTelemetry Memory, NetworkTelemetry Network);

public sealed class SystemTelemetryService
{
    public const int MaximumInterfaces = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, NetworkCounters> _networkBaseline = new(StringComparer.Ordinal);
    private CpuCounters? _cpuBaseline;
    private long _networkTimestamp;

    public SystemTelemetrySnapshot Sample()
    {
        lock (_gate)
        {
            return new SystemTelemetrySnapshot(DateTimeOffset.UtcNow, SampleCpu(), SampleMemory(), SampleNetwork());
        }
    }

    private CpuTelemetry SampleCpu()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
            return new(TelemetryAvailability.Unavailable, null, null, "Windows CPU counters are unavailable.");

        var current = new CpuCounters(idle.Value, kernel.Value, user.Value);
        if (_cpuBaseline is not CpuCounters previous)
        {
            _cpuBaseline = current;
            return new(TelemetryAvailability.WarmingUp, null, null, "Collecting the first CPU sample.");
        }

        _cpuBaseline = current;
        double? percent = CalculateCpuPercent(previous, current);
        double? coreUsage = null;
        try {
            var procCount = Environment.ProcessorCount;
            // Simplified core usage estimation
            coreUsage = percent.HasValue ? percent.Value / procCount : null;
        } catch {}
        return percent.HasValue
            ? new(TelemetryAvailability.Available, percent, coreUsage, "Aggregate processor use")
            : new(TelemetryAvailability.WarmingUp, null, null, "CPU counters were reset; collecting a new sample.");
    }

    private static MemoryTelemetry SampleMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0 || status.AvailablePhysical > status.TotalPhysical)
            return new(TelemetryAvailability.Unavailable, 0, 0, null, "Windows memory counters are unavailable.");

        ulong used = status.TotalPhysical - status.AvailablePhysical;
        double percent = Math.Clamp(used * 100d / status.TotalPhysical, 0, 100);
        return new(TelemetryAvailability.Available, used, status.TotalPhysical, percent, "Physical memory in use");
    }

    private NetworkTelemetry SampleNetwork()
    {
        try
        {
            var counters = new Dictionary<string, NetworkCounters>(StringComparer.Ordinal);
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(IsIncludedInterface)
                         .OrderBy(item => item.Id, StringComparer.Ordinal)
                         .Take(MaximumInterfaces))
            {
                try
                {
                    IPv4InterfaceStatistics statistics = adapter.GetIPv4Statistics();
                    counters[adapter.Id] = new NetworkCounters(statistics.BytesReceived, statistics.BytesSent);
                }
                catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or NotSupportedException)
                {
                    // One inaccessible adapter must not hide usable adapters.
                }
            }

            long now = Stopwatch.GetTimestamp();
            if (_networkTimestamp == 0)
            {
                ReplaceNetworkBaseline(counters, now);
                return new(TelemetryAvailability.WarmingUp, null, null, counters.Count, "Collecting the first network sample.");
            }

            double elapsed = (now - _networkTimestamp) / (double)Stopwatch.Frequency;
            long received = 0;
            long sent = 0;
            int comparable = 0;
            if (elapsed > 0 && double.IsFinite(elapsed))
            {
                foreach (var pair in counters)
                {
                    if (!_networkBaseline.TryGetValue(pair.Key, out NetworkCounters previous)) continue;
                    long receivedDelta = pair.Value.Received - previous.Received;
                    long sentDelta = pair.Value.Sent - previous.Sent;
                    if (receivedDelta < 0 || sentDelta < 0) continue;
                    received += receivedDelta;
                    sent += sentDelta;
                    comparable++;
                }
            }

            ReplaceNetworkBaseline(counters, now);
            if (counters.Count == 0)
                return new(TelemetryAvailability.Unavailable, null, null, 0, "No active network interface is available.");
            if (comparable == 0 || elapsed <= 0)
                return new(TelemetryAvailability.WarmingUp, null, null, counters.Count, "Network interfaces changed; collecting a new sample.");

            return new(TelemetryAvailability.Available, received / elapsed, sent / elapsed, counters.Count, "Aggregate active-interface traffic");
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or NotSupportedException)
        {
            return new(TelemetryAvailability.Unavailable, null, null, 0, "Windows network counters are unavailable.");
        }
    }

    private void ReplaceNetworkBaseline(Dictionary<string, NetworkCounters> counters, long timestamp)
    {
        _networkBaseline.Clear();
        foreach (var pair in counters) _networkBaseline[pair.Key] = pair.Value;
        _networkTimestamp = timestamp;
    }

    private static bool IsIncludedInterface(NetworkInterface adapter) =>
        adapter.OperationalStatus == OperationalStatus.Up
        && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback
        && adapter.NetworkInterfaceType is not NetworkInterfaceType.Tunnel;

    internal static double? CalculateCpuPercent(CpuCounters previous, CpuCounters current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User) return null;
        ulong idle = current.Idle - previous.Idle;
        ulong kernel = current.Kernel - previous.Kernel;
        ulong user = current.User - previous.User;
        ulong total = kernel + user;
        if (total == 0 || idle > total) return null;
        return Math.Clamp((total - idle) * 100d / total, 0, 100);
    }

    internal readonly record struct CpuCounters(ulong Idle, ulong Kernel, ulong User);
    private readonly record struct NetworkCounters(long Received, long Sent);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
