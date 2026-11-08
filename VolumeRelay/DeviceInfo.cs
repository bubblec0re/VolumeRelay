namespace VolumeRelay;

/// <param name="VirtualSource">
/// How <see cref="IsVirtual"/> was decided: "hwid" (HardwareIds property, the
/// reliable criterion), "name" (fallback heuristic — property unreadable), or
/// "unknown" (neither matched / nothing readable). In diagnostics reports this
/// separates a confident verdict from a guess.
/// </param>
public record DeviceInfo(
    string Id,
    string Name,
    bool IsVirtual,
    bool IsDefault,
    string? HardwareId = null,
    string VirtualSource = "unknown")
{
    public string Display => Name + (IsVirtual ? " (virtual)" : "") + (IsDefault ? " (default)" : "");
}
