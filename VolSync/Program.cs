using System.IO;
using System.Windows.Forms;
using VolSync;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "VolSync_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("VolSync is already running — look for its icon in the system tray.", "VolSync");
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApp());
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "volsync-crash.log"), ex.ToString()); } catch { }
            throw;
        }
    }
}
