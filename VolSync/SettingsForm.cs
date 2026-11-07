using System.Drawing;
using System.Windows.Forms;

namespace VolSync;

public class SettingsForm : Form
{
    readonly Settings _settings;
    readonly CheckBox _autostart;

    public SettingsForm(Settings settings)
    {
        _settings = settings;

        Text = "VolSync settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(300, 90);

        // The Run key is the source of truth, not the saved JSON flag:
        // the entry may have been deleted or the exe may have moved.
        _autostart = new CheckBox
        {
            Text = "Start with Windows",
            Checked = AutoStart.IsEnabled(),
            Location = new Point(12, 12),
            AutoSize = true,
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(196, 44),
            AutoSize = true,
        };
        ok.Click += (s, e) =>
        {
            _settings.AutoStart = _autostart.Checked;
            SettingsStore.Save(_settings);
            AutoStart.SetEnabled(_settings.AutoStart);
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(240, 44),
            AutoSize = true,
        };

        Controls.Add(_autostart);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
