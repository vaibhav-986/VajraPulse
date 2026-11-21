using System;
using System.Runtime.InteropServices;

namespace VajraPulse.Services;

public sealed class AudioService : IDisposable
{
    private IAudioEndpointVolume? _endpoint;
    private string? _deviceId;
    public bool IsAvailable { get; private set; }
    public float Volume { get; private set; }
    public bool IsMuted { get; private set; }
    public string? DeviceId => _deviceId;
    public string DeviceName { get; private set; } = "Audio output";
    public bool IsHeadphones { get; private set; }

    public void Refresh()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
            Marshal.ThrowExceptionForHR(device.GetId(out string id));
            if (_endpoint == null || id != _deviceId)
            {
                ReleaseEndpoint();
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out object obj));
                _endpoint = (IAudioEndpointVolume)obj;
                _deviceId = id;
                ReadDeviceProperties(device);
            }
            Marshal.ThrowExceptionForHR(_endpoint.GetMasterVolumeLevelScalar(out float volume));
            Marshal.ThrowExceptionForHR(_endpoint.GetMute(out bool muted));
            Volume = volume;
            IsMuted = muted;
            IsAvailable = true;
        }
        catch (COMException) { ReleaseEndpoint(); }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    public void SetVolume(float level)
    {
        if (_endpoint == null) return;
        try
        {
            Guid context = Guid.Empty;
            Marshal.ThrowExceptionForHR(_endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 1), ref context));
            Volume = Math.Clamp(level, 0, 1);
        }
        catch (COMException) { ReleaseEndpoint(); }
    }

    public void ToggleMute()
    {
        if (_endpoint == null) return;
        try
        {
            Guid context = Guid.Empty;
            Marshal.ThrowExceptionForHR(_endpoint.SetMute(!IsMuted, ref context));
            IsMuted = !IsMuted;
        }
        catch (COMException) { ReleaseEndpoint(); }
    }

    private void ReadDeviceProperties(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out store));
            var friendlyKey = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
            PropVariant value = default;
            try
            {
                if (store.GetValue(ref friendlyKey, out value) >= 0 && value.Type == 31)
                    DeviceName = Marshal.PtrToStringUni(value.Pointer) ?? "Audio output";
            }
            finally { PropVariantClear(ref value); }
            var formKey = new PropertyKey(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);
            value = default;
            try
            {
                if (store.GetValue(ref formKey, out value) >= 0 && value.Type == 19)
                    IsHeadphones = value.UInt32 is 3 or 5;
            }
            finally { PropVariantClear(ref value); }
        }
        catch (COMException) { }
        finally { if (store != null) Marshal.ReleaseComObject(store); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId; public uint Id;
        public PropertyKey(Guid format, uint id) { FormatId = format; Id = id; }
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] public uint UInt32;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant variant);

    private void ReleaseEndpoint()
    {
        if (_endpoint != null) Marshal.ReleaseComObject(_endpoint);
        _endpoint = null;
        _deviceId = null;
        DeviceName = "Audio output";
        IsHeadphones = false;
        IsAvailable = false;
    }
    public void Dispose() => ReleaseEndpoint();

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }
}
