using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace VolumeRelay;

/// <summary>
/// Builds the "Copy diagnostics" report: everything the engine, device
/// enumeration and GG client know, as one block of text the user pastes into a
/// bug report. Assembled on demand only — nothing is collected or written to
/// disk unless the user clicks the menu item, and they can read (and edit)
/// everything before submitting it.
/// </summary>
public static class Diagnostics
{
    public static string Build(VolumeRelayEngine engine)
    {
        var sb = new StringBuilder();

        // ---- Environment ----
        sb.AppendLine("## VolumeRelay diagnostics");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App version: {Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine($"OS: {OsVersion()}");
        sb.AppendLine($"Process: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, runtime {Environment.Version}");
        sb.AppendLine();

        // ---- Devices ----
        var devices = engine.Devices;
        sb.AppendLine($"## Render endpoints ({devices.Count})");
        foreach (var d in devices)
        {
            sb.AppendLine($"- {(d.IsDefault ? "[DEFAULT] " : "")}{d.Name} " +
                          $"(virtual: {d.IsVirtual}, via {d.VirtualSource})");
            sb.AppendLine($"  id: {d.Id}");
            sb.AppendLine($"  hardwareIds: {d.HardwareId ?? "(property not readable)"}");
        }
        sb.AppendLine();

        // ---- Engine state ----
        sb.AppendLine("## Engine");
        sb.AppendLine($"Mode: {engine.Mode}");
        sb.AppendLine($"Paused: {engine.Paused}");
        sb.AppendLine($"Target selection: {engine.TargetId}, resolved: {engine.ResolvedTargetId ?? "(none)"} ({engine.TargetName})");
        sb.AppendLine($"Pipe (Windows default): {engine.PipeId ?? "(none)"} ({engine.DefaultDeviceName ?? "unknown name"})");
        sb.AppendLine($"Pin: {engine.PinValue:0.000}, last pipe volume: {engine.LastPipeVolume:0.000}, last pipe mute: {engine.LastPipeMute}");
        sb.AppendLine($"Device watcher registered: {engine.WatcherRegistered}");
        sb.AppendLine();

        // ---- GG ----
        sb.AppendLine("## SteelSeries GG");
        sb.AppendLine($"Last status: {engine.AutoStatus ?? "target resolved"}");
        var trace = engine.Gg.TraceText;
        sb.AppendLine(trace.Length == 0 ? "(no GG activity recorded)" : trace);
        sb.AppendLine();

        // ---- Event log ----
        var events = engine.RecentEvents;
        sb.AppendLine($"## Recent events (last {events.Count}, in-memory only)");
        foreach (var line in events)
            sb.AppendLine(line);
        sb.AppendLine();

        // ---- Crash log tail, if one exists ----
        var crashPath = Path.Combine(Path.GetTempPath(), "volumerelay-crash.log");
        if (File.Exists(crashPath))
        {
            try
            {
                var content = File.ReadAllText(crashPath);
                if (content.Length > 4000)
                    content = "…(earlier entries cut)" + content[^4000..];
                sb.AppendLine("## Crash log (%TEMP%\\volumerelay-crash.log)");
                sb.AppendLine(content);
            }
            catch { }
        }

        return sb.ToString();
    }

    static string OsVersion()
    {
        var v = Environment.OSVersion.VersionString;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var display = key?.GetValue("DisplayVersion") as string;
            var ubr = key?.GetValue("UBR") as int?;
            if (!string.IsNullOrEmpty(display)) v += $" ({display})";
            if (ubr is int build) v += $".{build}";
        }
        catch { }
        return v;
    }
}
