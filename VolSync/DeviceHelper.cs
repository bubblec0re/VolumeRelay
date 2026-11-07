using NAudio.CoreAudioApi;

namespace VolSync;

public static class DeviceHelper
{
    /// <summary>
    /// All active render endpoints and the current default render endpoint ID.
    /// Uses NAudio's MMDeviceEnumerator for enumeration (reliable in WinExe context).
    /// Virtual detection: name contains "Virtual Audio Device" (covers Sonar VADs and similar).
    /// </summary>
    public static (string? defaultId, List<DeviceInfo> devices) GetRenderDevices()
    {
        var devices = new List<DeviceInfo>();
        string? defaultId = null;

        try
        {
            var enumerator = new MMDeviceEnumerator();
            try
            {
                defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID;
            }
            catch { }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var name = device.FriendlyName;
                    var isVirtual = LooksVirtual(name);
                    devices.Add(new DeviceInfo(device.ID, name, isVirtual, device.ID == defaultId));
                }
                catch { }
            }
        }
        catch { }

        return (defaultId, devices);
    }

    /// <summary>
    /// Virtual endpoints are recognised by name: Windows exposes no stable
    /// "is virtual" flag, and the property-store HardwareIds heuristic
    /// (ROOT\...) is not reachable through NAudio here. These markers cover
    /// the virtual audio drivers actually in use. Virtual endpoints are never
    /// offered as targets — a virtual device must not route into another one.
    /// </summary>
    static readonly string[] VirtualMarkers =
    {
        "Virtual Audio Device",
        "SteelSeries Sonar",
        "VB-Audio",
        "Virtual Cable",
        "CABLE Input",
        "CABLE Output",
        "VoiceMeeter",
    };

    static bool LooksVirtual(string name)
        => VirtualMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
}
