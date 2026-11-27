using System;
using System.Runtime.InteropServices;
using static VajraPulse.Services.CoreAudioInterop;

namespace VajraPulse.Services;

public sealed class AudioService : IDisposable
{
    private IAudioEndpointVolume? _endpoint;
    private IAudioMeterInformation? _meter;
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
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out device));
            Marshal.ThrowExceptionForHR(device.GetId(out string id));
            if (_endpoint == null || id != _deviceId)
            {
                ReleaseEndpoint();
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object obj));
                _endpoint = (IAudioEndpointVolume)obj;
                try
                {
                    Guid meterIid = typeof(IAudioMeterInformation).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(ref meterIid, ClsCtxAll, IntPtr.Zero, out object meter));
                    _meter = (IAudioMeterInformation)meter;
                }
                catch (COMException) { _meter = null; }
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
            Release(device);
            Release(enumerator);
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

    public float GetPeakLevel()
    {
        if (_meter == null) return 0f;
        try
        {
            if (_meter.GetPeakValue(out float peak) < 0) return 0f;
            return Math.Clamp(peak, 0f, 1f);
        }
        catch (COMException) { return 0f; }
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
        finally { Release(store); }
    }

    private void ReleaseEndpoint()
    {
        Release(_meter);
        _meter = null;
        Release(_endpoint);
        _endpoint = null;
        _deviceId = null;
        DeviceName = "Audio output";
        IsHeadphones = false;
        IsAvailable = false;
    }

    public void Dispose() => ReleaseEndpoint();
}
