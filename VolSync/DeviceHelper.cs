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
                    var isVirtual = name.Contains("Virtual Audio Device", StringComparison.OrdinalIgnoreCase);
                    devices.Add(new DeviceInfo(device.ID, name, isVirtual, device.ID == defaultId));
                }
                catch { }
            }
        }
        catch { }

        return (defaultId, devices);
    }
}
