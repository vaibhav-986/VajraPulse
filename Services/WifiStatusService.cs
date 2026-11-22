using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VajraPulse.Services;

public enum WifiConnectionState
{
    Checking,
    Connected,
    Connecting,
    Disconnected,
    Unavailable
}

public sealed record WifiStatusSnapshot(
    WifiConnectionState State,
    int InterfaceCount,
    int ConnectedInterfaceCount,
    DateTimeOffset ObservedAt,
    string Status);

public sealed class WifiStatusService
{
    public const int MaximumInterfaces = 16;
    private const uint ClientVersion = 2;

    public WifiStatusSnapshot Read()
    {
        IntPtr client = IntPtr.Zero;
        IntPtr list = IntPtr.Zero;
        try
        {
            uint result = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out client);
            if (result != 0)
                return Unavailable(DescribeError(result));

            result = WlanEnumInterfaces(client, IntPtr.Zero, out list);
            if (result != 0)
                return Unavailable(DescribeError(result));

            int count = Math.Clamp(Marshal.ReadInt32(list), 0, MaximumInterfaces);
            int connected = 0;
            bool connecting = false;
            int itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
            IntPtr cursor = IntPtr.Add(list, sizeof(int) * 2);
            for (int index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(cursor, index * itemSize));
                if (item.State is WlanInterfaceState.Connected or WlanInterfaceState.AdHocNetworkFormed) connected++;
                else if (item.State is WlanInterfaceState.Associating or WlanInterfaceState.Discovering or WlanInterfaceState.Authenticating)
                    connecting = true;
            }

            WifiConnectionState state = connected > 0
                ? WifiConnectionState.Connected
                : connecting ? WifiConnectionState.Connecting : WifiConnectionState.Disconnected;
            string status = state switch
            {
                WifiConnectionState.Connected => connected == 1 ? "Wi-Fi connected" : $"Wi-Fi connected on {connected} interfaces",
                WifiConnectionState.Connecting => "Wi-Fi is connecting",
                _ => count == 0 ? "No Wi-Fi interface was reported" : "Wi-Fi is not connected"
            };
            return new(state, count, connected, DateTimeOffset.UtcNow, status);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return Unavailable("Windows Wi-Fi status is unavailable.");
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    internal static WifiConnectionState AggregateState(params int[] states)
    {
        bool connecting = false;
        foreach (int value in states)
        {
            var state = (WlanInterfaceState)value;
            if (state is WlanInterfaceState.Connected or WlanInterfaceState.AdHocNetworkFormed) return WifiConnectionState.Connected;
            if (state is WlanInterfaceState.Associating or WlanInterfaceState.Discovering or WlanInterfaceState.Authenticating)
                connecting = true;
        }
        return connecting ? WifiConnectionState.Connecting : WifiConnectionState.Disconnected;
    }

    private static WifiStatusSnapshot Unavailable(string status) =>
        new(WifiConnectionState.Unavailable, 0, 0, DateTimeOffset.UtcNow, status);

    private static string DescribeError(uint error)
    {
        try { return "Wi-Fi status unavailable: " + new Win32Exception((int)error).Message; }
        catch { return "Windows Wi-Fi status is unavailable."; }
    }

    private enum WlanInterfaceState
    {
        NotReady,
        Connected,
        AdHocNetworkFormed,
        Disconnecting,
        Disconnected,
        Associating,
        Discovering,
        Authenticating
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public WlanInterfaceState State;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
}
