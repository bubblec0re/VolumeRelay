namespace VolumeRelay;

public record DeviceInfo(string Id, string Name, bool IsVirtual, bool IsDefault)
{
    public string Display => Name + (IsVirtual ? " (virtual)" : "") + (IsDefault ? " (default)" : "");
}
