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

        // Without these, an exception on a COM callback / threadpool / timer
        // thread kills the process silently — no window, no log, no bug report.
        // They funnel everything into the same crash log Main writes to.
        Application.ThreadException += (s, e) => WriteCrash("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            WriteCrash(e.IsTerminating ? "fatal, all threads" : "non-UI thread", e.ExceptionObject as Exception);

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApp());
        }
        catch (Exception ex)
        {
            WriteCrash("Main", ex);
            throw;
        }
    }

    static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "volumerelay-crash.log"),
                $"\r\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\r\n{ex}\r\n");
        }
        catch { }
    }
}
