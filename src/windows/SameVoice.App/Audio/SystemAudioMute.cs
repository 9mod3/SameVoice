using System.Runtime.InteropServices;

namespace SameVoice.App.Audio;

/// <summary>
/// Mutes the default Windows playback endpoint without changing the audio that
/// applications render into the shared mixer. Used by the Windows source page
/// for "mute this PC locally while still streaming to the output device".
/// </summary>
public static class SystemAudioMute
{
    private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public static bool TrySetMuted(bool muted, out string? error) =>
        TrySetMuted(null, muted, out error);

    public static bool TrySetMuted(string? deviceId, bool muted, out string? error)
    {
        error = null;
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? volumeObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(EnumeratorClsid);
            if (type is null) throw new InvalidOperationException("找不到 Windows 音频端点枚举器");
            enumeratorObject = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("无法创建 Windows 音频端点枚举器");
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;

            int hr = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out device)
                : enumerator.GetDevice(deviceId!, out device);
            if (hr < 0 || device is null) Marshal.ThrowExceptionForHR(hr);

            Guid iid = EndpointVolumeIid;
            hr = device.Activate(ref iid, 23 /* all */, IntPtr.Zero, out volumeObject);
            if (hr < 0 || volumeObject is null) Marshal.ThrowExceptionForHR(hr);

            var volume = (IAudioEndpointVolume)volumeObject;
            hr = volume.SetMute(muted, IntPtr.Zero);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Release(volumeObject);
            Release(device);
            Release(enumeratorObject);
        }
    }

    public static bool TryGetMuted(out bool muted) => TryGetMuted(null, out muted);

    public static bool TryGetMuted(string? deviceId, out bool muted)
    {
        muted = false;
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? volumeObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(EnumeratorClsid);
            if (type is null) return false;
            enumeratorObject = Activator.CreateInstance(type);
            if (enumeratorObject is null) return false;
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            int hr = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(0, 1, out device)
                : enumerator.GetDevice(deviceId!, out device);
            if (hr < 0 || device is null) return false;

            Guid iid = EndpointVolumeIid;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out volumeObject) < 0 || volumeObject is null)
                return false;

            var volume = (IAudioEndpointVolume)volumeObject;
            return volume.GetMute(out muted) >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(volumeObject);
            Release(device);
            Release(enumeratorObject);
        }
    }

    public static bool TryGetVolumeScalar(out float volumeScalar) =>
        TryGetVolumeScalar(null, out volumeScalar);

    public static bool TryGetVolumeScalar(string? deviceId, out float volumeScalar)
    {
        volumeScalar = 1f;
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? volumeObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(EnumeratorClsid);
            if (type is null) return false;
            enumeratorObject = Activator.CreateInstance(type);
            if (enumeratorObject is null) return false;
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            int hr = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(0, 1, out device)
                : enumerator.GetDevice(deviceId!, out device);
            if (hr < 0 || device is null) return false;

            Guid iid = EndpointVolumeIid;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out volumeObject) < 0 || volumeObject is null)
                return false;

            var volume = (IAudioEndpointVolume)volumeObject;
            return volume.GetMasterVolumeLevelScalar(out volumeScalar) >= 0;
        }
        catch
        {
            volumeScalar = 1f;
            return false;
        }
        finally
        {
            Release(volumeObject);
            Release(device);
            Release(enumeratorObject);
        }
    }

    public static bool TrySetVolumeScalar(float volumeScalar, out string? error) =>
        TrySetVolumeScalar(null, volumeScalar, out error);

    public static bool TrySetVolumeScalar(string? deviceId, float volumeScalar, out string? error)
    {
        error = null;
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? volumeObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(EnumeratorClsid);
            if (type is null) throw new InvalidOperationException("找不到 Windows 音频端点枚举器");
            enumeratorObject = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("无法创建 Windows 音频端点枚举器");
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            int hr = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(0, 1, out device)
                : enumerator.GetDevice(deviceId!, out device);
            if (hr < 0 || device is null)
                throw new InvalidOperationException("找不到指定的播放设备");

            Guid iid = EndpointVolumeIid;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out volumeObject) < 0 || volumeObject is null)
                throw new InvalidOperationException("无法访问播放音量控制");

            var volume = (IAudioEndpointVolume)volumeObject;
            hr = volume.SetMasterVolumeLevelScalar(Math.Clamp(volumeScalar, 0f, 1f), IntPtr.Zero);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Release(volumeObject);
            Release(device);
            Release(enumeratorObject);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); } catch { /* ignore */ }
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, IntPtr eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(IntPtr eventContext);
        [PreserveSig] int VolumeStepDown(IntPtr eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
    }
}
