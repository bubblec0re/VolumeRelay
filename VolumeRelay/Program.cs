using System.IO;
using System.Windows.Forms;
using VolumeRelay;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "VolumeRelay_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("VolumeRelay is already running — look for its icon in the system tray.", "VolumeRelay");
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApp());
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "volumerelay-crash.log"), ex.ToString()); } catch { }
            throw;
        }
    }
}
