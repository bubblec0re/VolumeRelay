using System.Runtime.InteropServices;

namespace VolSync;

/// <summary>
/// Raw COM helpers for what NAudio 2.2.1 doesn't expose on this Windows SDK build
/// (endpoint property-store access). Code verified in the Spike project.
/// </summary>
static partial class NativeAudio
{
    static Guid CLSID_Enumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static Guid IID_Enumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    static readonly Guid PKEY_FriendlyName = new("19cde9eb-aa77-4c15-ba11-6806d56f72a9");
    static readonly Guid PKEY_HardwareIds = new("b3f8fa53-0004-438e-9003-51a46e139bfc");

    /// <summary>Enumerates active render endpoints: id, friendly name, virtual flag.</summary>
    public static List<(string Id, string Name, bool IsVirtual)> EnumerateRenderActive()
    {
        var result = new List<(string, string, bool)>();

        if (CoCreateInstance(ref CLSID_Enumerator, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref IID_Enumerator, out IntPtr enumPtr) != 0
            || enumPtr == IntPtr.Zero)
            return result;
        var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumPtr)!;

        if (enumerator.EnumerateAudioEndPoints(0 /* Render */, 1 /* Active */, out IntPtr collPtr) != 0)
            return result;
        var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collPtr)!;
        if (collection.GetCount(out uint count) != 0) return result;

        for (uint i = 0; i < count; i++)
        {
            if (collection.Item(i, out IntPtr devPtr) != 0) continue;
            var dev = (IMMDevice)Marshal.GetObjectForIUnknown(devPtr)!;

            string id;
            try
            {
                dev.GetId(out IntPtr idPtr);
                id = Marshal.PtrToStringUni(idPtr) ?? "";
                Marshal.FreeCoTaskMem(idPtr);
            }
            catch
            {
                continue;
            }

            string name = "";
            bool isVirtual = false;
            try
            {
                if (dev.OpenPropertyStore(0 /* Read */, out IntPtr storePtr) == 0)
                {
                    var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr)!;
                    name = GetStringProp(store, new PROPKEY(PKEY_FriendlyName, 1));
                    var hw = GetStringProp(store, new PROPKEY(PKEY_HardwareIds, 2));
                    isVirtual = hw.Contains(".ROOT\\", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // keep name/virtual as-is
            }

            result.Add((id, name, isVirtual));
        }
        return result;
    }

    /// <summary>ID of the default render endpoint (console role), or null.</summary>
    public static string? GetDefaultRenderId()
    {
        if (CoCreateInstance(ref CLSID_Enumerator, IntPtr.Zero, 1, ref IID_Enumerator, out IntPtr enumPtr) != 0
            || enumPtr == IntPtr.Zero)
            return null;
        var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumPtr)!;
        if (enumerator.GetDefaultAudioEndpoint(0 /* Render */, 1 /* Console */, out IntPtr devPtr) != 0)
            return null;
        var dev = (IMMDevice)Marshal.GetObjectForIUnknown(devPtr)!;
        try
        {
            dev.GetId(out IntPtr idPtr);
            string id = Marshal.PtrToStringUni(idPtr) ?? "";
            Marshal.FreeCoTaskMem(idPtr);
            return id;
        }
        catch
        {
            return null;
        }
    }

    static string GetStringProp(IPropertyStore store, PROPKEY key)
    {
        if (store.GetValue(ref key, out PROPVARIANT pv) != 0) return "";
        try
        {
            return PropVariantToString(ref pv);
        }
        finally
        {
            uint vt = pv.vt & 0xFFFF;
            IntPtr p = new IntPtr(BitConverter.DoubleToInt64Bits(pv.d));
            if (vt == 8 /* VT_BSTR */) Marshal.FreeBSTR(p);
            else if (vt == 0x1E /* VT_LPSTR */ || vt == 0x1F /* VT_LPWSTR */) Marshal.FreeCoTaskMem(p);
        }
    }

    static string PropVariantToString(ref PROPVARIANT v)
    {
        uint vt = v.vt & 0xFFFF;
        IntPtr p = new IntPtr(BitConverter.DoubleToInt64Bits(v.d));
        switch (vt)
        {
            case 8: return Marshal.PtrToStringBSTR(p) ?? "";
            case 0x1E: return Marshal.PtrToStringAnsi(p) ?? "";
            case 0x1F: return Marshal.PtrToStringUni(p) ?? "";
            default: return "";
        }
    }

    // ---- raw interfaces (new-SDK layout, verified in Spike) ----

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppvObject);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumerateAudioEndPoints([In] int dataFlow, [In] int state, [Out] out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint([In] int dataFlow, [In] int role, [Out] out IntPtr device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount([Out] out uint count);
        [PreserveSig] int Item([In] uint index, [Out] out IntPtr device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
        [PreserveSig] int GetId([Out] out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint iProp, out PROPKEY key);
        [PreserveSig] int GetValue(ref PROPKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPKEY
    {
        public Guid fmtid;
        public uint rid;
        public PROPKEY(Guid fmtid, uint rid) { this.fmtid = fmtid; this.rid = rid; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT
    {
        public uint vt;
        public ushort w1;
        public ushort w2;
        public double d;
        public double d2;
    }
}
