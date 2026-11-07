using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolumeRelay;

/// <summary>COM callback for endpoint add/remove/state/default events.</summary>
[ComVisible(true)]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[ClassInterface(ClassInterfaceType.None)]
public class DeviceWatcher : IMMNotificationClient
{
    readonly Action<string> _onAdded;
    readonly Action<string> _onRemoved;
    readonly Action<string> _onStateChanged;
    readonly Action<string, string, string> _onDefaultChanged;

    public DeviceWatcher(
        Action<string> onAdded,
        Action<string> onRemoved,
        Action<string> onStateChanged,
        Action<string, string, string> onDefaultChanged)
    {
        _onAdded = onAdded;
        _onRemoved = onRemoved;
        _onStateChanged = onStateChanged;
        _onDefaultChanged = onDefaultChanged;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _onStateChanged(deviceId);
    public void OnDeviceAdded(string id) => _onAdded(id);
    public void OnDeviceRemoved(string id) => _onRemoved(id);

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id)
        => _onDefaultChanged(
            flow == DataFlow.Render ? "Render" : "Capture",
            role == Role.Console ? "Console" : "Communications",
            id);

    public void OnPropertyValueChanged(string id, PropertyKey key)
    {
        // not needed
    }
}
