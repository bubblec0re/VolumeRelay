using Microsoft.Win32;

namespace VolSync;

/// <summary>
/// Auto-start with Windows via the HKCU Run key.
/// [DANGER] writes/removes HKCU\Software\Microsoft\Windows\CurrentVersion\Run\VolSync
/// — only called from the in-app toggle (default off).
/// </summary>
public static class AutoStart
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "VolSync";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key == null) return;
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, false);
    }
}
