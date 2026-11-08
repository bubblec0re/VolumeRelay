using NAudio.CoreAudioApi;

namespace VolumeRelay;

public static class DeviceHelper
{
    /// <summary>
    /// Endpoint property-store key holding the device's HardwareIds
    /// (DEVPKEY_Device_HardwareIds on the endpoint store).
    /// Value shape: "{1}.ROOT\MEDIA\0000" (virtual) or
    /// "{1}.USB\VID_1038&amp;PID_22A5&amp;MI_00\..." / "{1}.HDAUDIO\..." (physical).
    /// Verified against real endpoints on Windows 11.
    /// </summary>
    static readonly PropertyKey HardwareIdsKey =
        new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);

    /// <summary>
    /// All active render endpoints and the current default render endpoint ID.
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
                    var (isVirtual, hardwareId, source) = Classify(device, name);
                    devices.Add(new DeviceInfo(
                        device.ID, name, isVirtual, device.ID == defaultId, hardwareId, source));
                }
                catch { }
            }
        }
        catch { }

        return (defaultId, devices);
    }

    /// <summary>
    /// Decides whether an endpoint is a virtual (software) device, and records
    /// the evidence used so diagnostics can distinguish a confident verdict from
    /// a name-based guess.
    ///
    /// Primary criterion is the device's HardwareIds: Windows enumerates software
    /// audio devices under the ROOT bus, while physical hardware always sits on a
    /// real bus (USB\, HDAUDIO\, PCI\, BTHENUM\, ...). This is a property of the
    /// device rather than a guess from its name, so a real device calling itself
    /// "Sonar Pro" or "CABLE Link" is not misfiled.
    ///
    /// Name markers are only a fallback for the rare case where the property
    /// store cannot be read at all.
    /// </summary>
    static (bool isVirtual, string? hardwareId, string source) Classify(MMDevice device, string name)
    {
        var raw = ReadHardwareIds(device);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            // "{1}.ROOT\MEDIA\0000" -> "ROOT\MEDIA\0000"
            var instance = raw;
            var dot = instance.IndexOf('.');
            if (dot >= 0 && instance.Length > dot + 1)
                instance = instance[(dot + 1)..];

            return (instance.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase), raw, "hwid");
        }

        return (LooksVirtualByName(name), raw, "name");
    }

    /// <summary>Raw HardwareIds value, or null when the property could not be read.</summary>
    static string? ReadHardwareIds(MMDevice device)
    {
        try
        {
            var value = device.Properties[HardwareIdsKey].Value;
            return value switch
            {
                string s => s,
                System.Collections.IEnumerable list => list.Cast<object>()
                    .Select(o => o?.ToString())
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fallback only — name matching is a heuristic, not a criterion.</summary>
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

    static bool LooksVirtualByName(string name)
        => VirtualMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
}
