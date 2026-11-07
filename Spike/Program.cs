using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

var mode = args.Length > 0 ? args[0] : "table";

if (mode == "probe")
{
    // Compare creation paths in the same process.
    try
    {
        _ = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        Console.WriteLine("(a) new MMDeviceEnumerator()            : OK");
    }
    catch (Exception ex) { Console.WriteLine($"(a) new MMDeviceEnumerator()            : FAIL {ex.Message}"); }

    try
    {
        _ = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("A95664D2-9641-4F00-8FA9-7E8578605B2B"))!);
        Console.WriteLine("(b) GetTypeFromCLSID + CreateInstance : OK");
    }
    catch (Exception ex) { Console.WriteLine($"(b) GetTypeFromCLSID + CreateInstance : FAIL {ex.Message}"); }

    // What GUIDs + method vtables does NAudio declare for its COM interfaces?
    var asm = typeof(MMDeviceEnumerator).Assembly;
    foreach (var name in new[] { "NAudio.CoreAudioApi.Interfaces.IPropertyStore", "NAudio.CoreAudioApi.Interfaces.StorageAccessMode" })
    {
        var t = asm.GetType(name);
        if (t is null) { Console.WriteLine($"type {name}: not found"); continue; }
        var g = t.GetCustomAttributes().OfType<GuidAttribute>().FirstOrDefault();
        Console.WriteLine($"\n{name} GUID = {g?.Value}");
        if (t.IsEnum)
        {
            foreach (var v in Enum.GetValues(t)) Console.WriteLine($"  {v} = {(int)v}");
        }
        else
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(m => m.MetadataToken))
                Console.WriteLine($"  {m}");
        }
    }

    // Also: does NAudio's FriendlyName actually work right now?
    try
    {
        var e = new MMDeviceEnumerator();
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            Console.WriteLine($"NAudio FriendlyName OK: {d.FriendlyName}");
    }
    catch (Exception ex) { Console.WriteLine($"NAudio FriendlyName FAILED: {ex.Message}"); }

    // Dump AudioEndpointVolume members
    {
        var t2 = asm.GetType("NAudio.CoreAudioApi.AudioEndpointVolume");
        if (t2 != null) foreach (var m in t2.GetEvents()) Console.WriteLine($"AUV EVENT: {m.Name}");
    }

    // Dump AudioEndpointVolumeNotificationDelegate signature
    {
        var t4 = asm.GetType("NAudio.CoreAudioApi.AudioEndpointVolumeNotificationDelegate");
        if (t4 != null) { var m = t4.GetMethod("Invoke"); Console.WriteLine($"DELEGATE: {m}"); foreach (var p in m.GetParameters()) Console.WriteLine($"  PARAM: {p.ParameterType.Name} {p.Name}"); }
    }

    // Dump AudioVolumeNotificationData props
    {
        var t3 = asm.GetType("NAudio.CoreAudioApi.AudioVolumeNotificationData");
        if (t3 != null) foreach (var p in t3.GetProperties()) Console.WriteLine($"AVND PROP: {p.Name} {p.PropertyType.Name}");
    }

    // Dump IMMNotificationClient + MMDeviceEnumerator members
    foreach (var name in new[] { "NAudio.CoreAudioApi.Interfaces.IMMNotificationClient", "NAudio.CoreAudioApi.MMDeviceEnumerator" })
    {
        var t = asm.GetType(name);
        if (t is null) { Console.WriteLine($"{name}: NOT FOUND"); continue; }
        Console.WriteLine($"\n=== {name} ===");
        var guid = t.GetCustomAttribute<System.Runtime.InteropServices.GuidAttribute>()?.Value;
        Console.WriteLine($"  Guid: {guid}");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
    }

    // (c) direct P/Invoke CoCreateInstance (current-SDK GUIDs)
    var iid = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
    var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
    int hr = Native.CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*APTTHREAD*/, ref iid, out _);
    Console.WriteLine($"(c) P/Invoke CoCreateInstance           : hr=0x{hr:X8}");
    return;
}

if (mode == "props")
{
    // Read-only: dump every property (key + value) in each active render endpoint's store.
    // GUIDs from Windows SDK 10.0.26100 (this build — the classic GUIDs are NOT valid here):
    var iidEnumEndpoints = new Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"); // IMMDeviceCollection
    var iidDevice = new Guid("D666063F-1587-4E43-81F1-B948E807363F");      // IMMDevice
    var iidStorage = new Guid("0000000B-0000-0000-C000-000000000000");      // IStorage (COM foundation)
    var iidEnumProps = new Guid("00000557-0000-0000-C000-000000000000");    // IEnumPropSetPropVariant (COM foundation)
    // NOTE: the classic CLSID A95664D2-9641-4F00-8FA9-7E8578605B2B is NOT registered on this
    // Windows build (REGDB_E_CLASSNOTREG). NAudio's working CLSID is used instead.
    var clsidEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

    var clsid2 = clsidEnumerator;
    var iidEnum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"); // IMMDeviceEnumerator
    int hr0 = Native.CoCreateInstance(ref clsid2, IntPtr.Zero, 1 /*APTTHREAD*/, ref iidEnum, out IntPtr enumPtr0);
    if (hr0 != 0) throw new Exception($"CoCreateInstance failed 0x{hr0:X8}");
    var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumPtr0)!;

    uint fetched = 0;
    hrCheck(enumerator.EnumerateAudioEndPoints(0 /*Render*/, 1 /*Active*/, out IntPtr collectionPtr));
    var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collectionPtr)!;
    hrCheck(collection.GetCount(out uint deviceCount));

    for (uint i = 0; i < deviceCount; i++)
    {
        {
            hrCheck(collection.Item(i, out IntPtr devPtr));
            var dev = (IMMDevice)Marshal.GetObjectForIUnknown(devPtr)!;
            dev.GetId(out IntPtr idPtr);
            string? id = Marshal.PtrToStringUni(idPtr);
            Console.WriteLine($"=== [{id}]");

            hrCheck(dev.OpenPropertyStore(0 /*StorageAccessMode.Read (new API: 0!)*/, out IntPtr storePtr));
            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr)!;
            hrCheck(store.GetCount(out uint propCount));
            for (uint j = 0; j < propCount; j++)
            {
                hrCheck(store.GetAt(j, out PROPKEY key));
                hrCheck(store.GetValue(ref key, out PROPVARIANT val));
                Console.WriteLine($"  {key.fmtid} #{key.rid}  =  {Decode(val)}");
            }

            // Named access (if the store supports it) — gives property NAMES.
            try
            {
                var named = (INamedPropertyStore)Marshal.GetObjectForIUnknown(storePtr)!;
                named.GetNameCount(out uint nameCount);
                for (uint j = 0; j < nameCount; j++)
                {
                    named.GetNameAt(j, out IntPtr namePtr);
                    Console.WriteLine($"  [name] {Marshal.PtrToStringBSTR(namePtr)}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"  (no INamedPropertyStore: {ex.Message})"); }
            Console.WriteLine();
        }
    }
    return;
}

if (mode == "table")
{
    var enum2 = new MMDeviceEnumerator();
    string? defaultId = null;
    try { defaultId = enum2.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; }
    catch { }

    Console.WriteLine($"Default render endpoint: {defaultId ?? "<none>"}");
    Console.WriteLine();
    Console.WriteLine($"{"ID", -38}  {"Name", -42}  {"Vol", 5}  Mute  Flags");

    foreach (var dev in enum2.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        string vol = "?", mute = "?";
        try
        {
            using var v = dev.AudioEndpointVolume;
            vol = v.MasterVolumeLevelScalar.ToString("0.00");
            mute = v.Mute ? "MUTED" : "-";
        }
        catch (Exception ex)
        {
            vol = $"ERR {ex.HResult:X8}";
            mute = "-";
        }
        string flags = dev.ID == defaultId ? "[DEFAULT]" : "";
        Console.WriteLine($"{dev.ID,-38}  {dev.FriendlyName,-42}  {vol,5}  {mute,-5}  {flags}");
    }
}

if (mode == "ggport")
{
    var pids = System.Diagnostics.Process.GetProcessesByName("SteelSeriesSonar").Select(p => { var i = p.Id; p.Dispose(); return i; }).ToList();
    Console.WriteLine("Sonar PIDs: " + string.Join(",", pids));
    int size = 0;
    var rc1 = GgTcp.Ping(IntPtr.Zero, ref size);
    Console.WriteLine("rc1=" + rc1 + " size=" + size);
    if (rc1 == 122)
    {
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            var rc2 = GgTcp.Get(ptr, ref size);
            Console.WriteLine("rc2=" + rc2);
            if (rc2 == 0)
            {
                int rows = Marshal.ReadInt32(ptr);
                Console.WriteLine("rows=" + rows + " size=" + size);
                var bytes = new byte[size - 4];
                Marshal.Copy(IntPtr.Add(ptr, 4), bytes, 0, bytes.Length);
                Console.WriteLine("hex: " + Convert.ToHexString(bytes));
var rowPtr = IntPtr.Add(ptr, 4);
                for (int i = 0; i < rows; i++)
                {
                    var row = Marshal.PtrToStructure<GgTcp.TcpRow>(rowPtr);
                    if (row.state == 2 && pids.Contains((int)row.owningPid))
                        Console.WriteLine("LISTEN: port=" + ((row.localPort & 0xFF00) >> 8 | (row.localPort & 0x00FF)) + " pid=" + row.owningPid);
                    rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<GgTcp.TcpRow>());
                }
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    return;
}

if (mode == "setdev")
{
    // Spike: set master volume (+ optional mute) on a specific endpoint by ID, then read back.
    // Usage: setdev <endpointId> <volume 0..1> [true|false]
    var id = args.Length > 1 ? args[1] : throw new Exception("usage: setdev <id> <vol> [mute]");
    float vol = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1f;
    bool? mute = args.Length > 3 ? bool.Parse(args[3]) : null;

    var enum2 = new MMDeviceEnumerator();
    var dev = enum2.GetDevice(id);
    var v = dev.AudioEndpointVolume;
    v.MasterVolumeLevelScalar = vol;
    if (mute.HasValue) v.Mute = mute.Value;
    Console.WriteLine($"Set {dev.FriendlyName}: volume={v.MasterVolumeLevelScalar:F2} mute={v.Mute}");
    return;
}

if (mode == "watchdefault")
if (mode == "watchdefault")
{
    // Spike: register an IMMNotificationClient; watch for default-device changes + add/remove.
    var enum2 = new MMDeviceEnumerator();
    var watcher = new DefaultWatcher();
    watcher.OnDefault = (f, r, id) => Console.WriteLine($"[default-changed] {f}/{r} -> {id}");
    watcher.OnAdded = id => Console.WriteLine($"[device-added] {id}");
    watcher.OnRemoved = id => Console.WriteLine($"[device-removed] {id}");
    watcher.OnStateChanged = (id, s) => Console.WriteLine($"[state-changed] {id} -> {s}");
    hrCheck(enum2.RegisterEndpointNotificationCallback(watcher));
    Console.WriteLine("Watching... switch the default output device in Windows (Settings > Sound) (Ctrl+C to stop).");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (TaskCanceledException) { }
    enum2.UnregisterEndpointNotificationCallback(watcher);
    Console.WriteLine("Stopped.");
    return;
}

if (mode == "setvol")
{
    // Spike: set master volume + mute on a NON-default endpoint, read back, then restore.
    var enum2 = new MMDeviceEnumerator();
    string? defaultId = null;
    try { defaultId = enum2.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; } catch { }

    MMDevice? target = null;
    foreach (var dev in enum2.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        if (dev.ID == defaultId) continue;
        if (dev.FriendlyName.Contains("Arctis")) { target = dev; break; }
    }
    if (target is null) { Console.WriteLine("No non-default target found"); return; }

    using var v = target.AudioEndpointVolume;
    float originalVol = v.MasterVolumeLevelScalar;
    bool originalMute = v.Mute;
    Console.WriteLine($"Target: {target.FriendlyName} [{target.ID}]");
    Console.WriteLine($"Original: volume={originalVol:F2} mute={originalMute}");

    v.MasterVolumeLevelScalar = 0.50f;
    Console.WriteLine($"Set 0.50  -> readback {v.MasterVolumeLevelScalar:F2}");

    v.Mute = true;
    Console.WriteLine($"Mute=true -> readback {v.Mute}");
    v.Mute = false;
    Console.WriteLine($"Mute=false-> readback {v.Mute}");

    v.MasterVolumeLevelScalar = originalVol;
    Console.WriteLine($"Restored volume={v.MasterVolumeLevelScalar:F2} (was {originalVol:F2}), mute={v.Mute} (was {originalMute})");
    return;
}

if (mode == "notify")
{
    // Spike: subscribe to volume-change notifications on the DEFAULT render endpoint.
    var clsidEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
    var iidEnum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
    var clsid2 = clsidEnumerator;
    int hr0 = Native.CoCreateInstance(ref clsid2, IntPtr.Zero, 1, ref iidEnum, out IntPtr enumPtr0);
    if (hr0 != 0) throw new Exception($"CoCreateInstance failed 0x{hr0:X8}");
    var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumPtr0)!;
    hrCheck(enumerator.GetDefaultAudioEndpoint(0 /*Render*/, 0 /*Console*/, out IntPtr devPtr));
    var dev = (IMMDevice)Marshal.GetObjectForIUnknown(devPtr)!;
    dev.GetId(out IntPtr idPtr);
    Console.WriteLine($"Default endpoint: {Marshal.PtrToStringUni(idPtr)}");

    var iidVol = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
    hrCheck(dev.Activate(ref iidVol, 2 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out IntPtr volPtr));
    var vol = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(volPtr)!;
    vol.GetMasterVolumeLevelScalar(out float volNow);
    vol.GetMute(out bool muteNow);
    Console.WriteLine($"Current: volume={volNow:F2} mute={muteNow}");

    var cb = new VolumeCallback();
    hrCheck(vol.RegisterControlChangeNotify(cb));
    Console.WriteLine("Listening... change volume with the keyboard knob or Windows volume UI (Ctrl+C to stop).");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (TaskCanceledException) { }
    Console.WriteLine("Stopped.");
    return;
}

static int hrCheck(int hr)
{
    if (hr != 0) Console.Error.WriteLine($"[hr 0x{hr:X8}]");
    return hr;
}

static string Decode(PROPVARIANT v)
{
    uint vt = v.vt & 0x0000FFFF;
    long lo = BitConverter.DoubleToInt64Bits(v.d);
    IntPtr ptr = new IntPtr(lo);
    switch (vt)
    {
        case 0: return "VT_EMPTY";
        case 1: return "VT_NULL";
        case 0x0B: return $"VT_BOOL={((lo & 0x10000) != 0)}";
        case 3: return $"VT_I4={(int)lo}";
        case 5: return $"VT_R8={v.d}";
        case 8: return $"VT_BSTR={Marshal.PtrToStringBSTR(ptr)}";
        case 0x16: return $"VT_I1={(sbyte)lo}";
        case 0x17: return $"VT_UI1={(byte)lo}";
        case 0x18: return $"VT_I2={(short)lo}";
        case 0x19: return $"VT_UI2={(ushort)lo}";
        case 0x1A: return $"VT_UI4={(uint)lo}";
        case 0x1C: return $"VT_I8={lo}";
        case 0x1D: return $"VT_UI8={(ulong)lo}";
        case 0x1E: return $"VT_LPSTR={Marshal.PtrToStringAnsi(ptr)}";
        case 0x1F: return $"VT_LPWSTR={Marshal.PtrToStringUni(ptr)}";
        case 0x3F: return $"VT_FILETIME={lo}";
        case 0x50:
        {
            long hi = BitConverter.DoubleToInt64Bits(v.d2);
            int data1 = (int)(lo & 0xFFFFFFFF);
            ushort data2 = (ushort)((lo >> 32) & 0xFFFF);
            ushort data3 = (ushort)((lo >> 48) & 0xFFFF);
            byte[] data4 = BitConverter.GetBytes(hi);
            return $"VT_GUID={new Guid(data1, (short)data2, (short)data3, data4)}";
        }
        default: return $"vt=0x{v.vt:X4} raw=({v.d:R}, {v.d2:R})";
    }
}

// ===================== native interop =====================

static partial class Native
{
    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppvObject);
}

// ===================== raw COM interop (read-only) =====================

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

// New-SDK layout (10.0.26100): slim interface — no GetCanonicalName/GetComObject/GetPropertyStore.
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

[ComImport, Guid("71604b0f-97b0-4764-8577-2f13e98a1422"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface INamedPropertyStore
{
    [PreserveSig] int GetNamedValue([In] string name, out PROPVARIANT pv);
    [PreserveSig] int SetNamedValue([In] string name, ref PROPVARIANT pv);
    [PreserveSig] int GetNameCount(out uint count);
    [PreserveSig] int GetNameAt(uint i, out IntPtr name); // BSTR
}

[StructLayout(LayoutKind.Sequential)]
struct PROPKEY
{
    public Guid fmtid;
    public uint rid;
}

[StructLayout(LayoutKind.Sequential)]
struct PROPVARIANT
{
    public uint vt;
    public ushort w1;
    public ushort w2;
    public double d;   // union bytes 8..15
    public double d2;  // union bytes 16..23
}

[StructLayout(LayoutKind.Sequential)]
struct STATSTG
{
    public ulong mtime, atime, ctime;
    public uint sizeHi, sizeLo, type, stateMode, attributes;
}

// ===================== volume notification (new-SDK layout) =====================

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify([In] IAudioEndpointVolumeCallback callback);
    [PreserveSig] int UnregisterControlChangeNotify([In] IAudioEndpointVolumeCallback callback);
    [PreserveSig] int GetChannelCount([Out] out int count);
    [PreserveSig] int SetMasterVolumeLevel([In] float db, [In] ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar([In] float scalar, [In] ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel([Out] out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar([Out] out float scalar);
    [PreserveSig] int SetChannelVolumeLevel([In] uint channel, [In] float db, [In] ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar([In] uint channel, [In] float scalar, [In] ref Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel([In] uint channel, [Out] out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar([In] uint channel, [Out] out float scalar);
    [PreserveSig] int SetMute([In] bool mute, [In] ref Guid eventContext);
    [PreserveSig] int GetMute([Out] out bool mute);
    [PreserveSig] int GetVolumeStepInfo([Out] out uint step, [Out] out uint steps);
    [PreserveSig] int VolumeStepUp([In] ref Guid eventContext);
    [PreserveSig] int VolumeStepDown([In] ref Guid eventContext);
    [PreserveSig] int QueryHardwareSupport([Out] out uint support);
    [PreserveSig] int GetVolumeRange([Out] out float minDb, [Out] out float maxDb, [Out] out float stepDb);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify([In] IntPtr pNotify);
}

[StructLayout(LayoutKind.Sequential)]
struct AVolNotifyData
{
    public Guid guidEventContext;
    public int bMuted;
    public float fMasterVolume;
    public uint nChannels;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
    public float[] afChannelVolumes;
}

[ComVisible(true)]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[ClassInterface(ClassInterfaceType.None)]
class DefaultWatcher : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
{
    public Action<string, string, string>? OnDefault;
    public Action<string>? OnAdded;
    public Action<string>? OnRemoved;
    public Action<string, int>? OnStateChanged;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => OnStateChanged?.Invoke(deviceId, (int)newState);
    public void OnDeviceAdded(string pwstrDeviceId) => OnAdded?.Invoke(pwstrDeviceId);
    public void OnDeviceRemoved(string deviceId) => OnRemoved?.Invoke(deviceId);
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        => OnDefault?.Invoke(flow == DataFlow.Render ? "Render" : "Capture", role == Role.Console ? "Console" : "Communications", defaultDeviceId);
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}

[ComVisible(true)]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[ClassInterface(ClassInterfaceType.None)]
class VolumeCallback : IAudioEndpointVolumeCallback
{
    public int OnNotify(IntPtr p)
    {
        var d = Marshal.PtrToStructure<AVolNotifyData>(p);
        Console.WriteLine($"[notify] volume={d.fMasterVolume:F2}  mute={d.bMuted != 0}  channels={d.nChannels}  ctx={d.guidEventContext}");
        return 0;
    }
}


class GgTcp
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct TcpRow { public uint state; public uint localAddr; public uint localPort; public uint remoteAddr; public uint remotePort; public uint owningPid; }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr t, ref int size, bool ordering, int ver, int reserved, uint tblClass);

    public static uint Ping(IntPtr t, ref int size) => GetExtendedTcpTable(t, ref size, false, 2, 5, 0);
    public static uint Get(IntPtr t, ref int size) => GetExtendedTcpTable(t, ref size, false, 2, 5, 0);
}
