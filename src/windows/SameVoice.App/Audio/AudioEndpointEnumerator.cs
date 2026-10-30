using System.Runtime.InteropServices;

namespace SameVoice.App.Audio;

public sealed record AudioEndpointInfo(string Id, string Name)
{
    public bool IsDefault => string.IsNullOrEmpty(Id);
    public bool LooksVirtual =>
        Name.Contains("Scream", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Streaming", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>Enumerates active Windows render endpoints with their friendly names.</summary>
public static class AudioEndpointEnumerator
{
    private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid FriendlyNameFmtid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    public static IReadOnlyList<AudioEndpointInfo> GetRenderEndpoints()
    {
        var result = new List<AudioEndpointInfo> { new("", "默认播放设备") };
        object? enumeratorObject = null;
        IMMDeviceCollection? collection = null;
        try
        {
            var type = Type.GetTypeFromCLSID(EnumeratorClsid);
            if (type is null) return result;
            enumeratorObject = Activator.CreateInstance(type);
            if (enumeratorObject is null) return result;
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            if (enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out collection) < 0 ||
                collection is null)
                return result;

            if (collection.GetCount(out int count) < 0) return result;
            for (int i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                IPropertyStore? store = null;
                try
                {
                    if (collection.Item(i, out device) < 0 || device is null) continue;
                    if (device.GetId(out string id) < 0 || string.IsNullOrWhiteSpace(id)) continue;
                    string name = id;
                    if (device.OpenPropertyStore(0, out store) >= 0 && store is not null)
                    {
                        var key = new PROPERTYKEY { fmtid = FriendlyNameFmtid, pid = 14 };
                        if (store.GetValue(ref key, out PROPVARIANT value) >= 0)
                        {
                            try
                            {
                                if (value.vt == 31 && value.pointerValue != IntPtr.Zero)
                                    name = Marshal.PtrToStringUni(value.pointerValue) ?? id;
                            }
                            finally
                            {
                                PropVariantClear(ref value);
                            }
                        }
                    }
                    result.Add(new AudioEndpointInfo(id, name));
                }
                finally
                {
                    Release(store);
                    Release(device);
                }
            }
        }
        catch
        {
            // Device enumeration is best-effort; keep the default entry.
        }
        finally
        {
            Release(collection);
            Release(enumeratorObject);
        }
        return result;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); } catch { /* ignore */ }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore? properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }
}
