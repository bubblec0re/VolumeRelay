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
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(300, 90);

        _autostart = new CheckBox
        {
            Text = "Start with Windows",
            Checked = settings.AutoStart,
            Location = new Point(12, 12),
            AutoSize = true,
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(218, 44),
            AutoSize = true,
        };
        ok.Click += (s, e) =>
        {
            _settings.AutoStart = _autostart.Checked;
            SettingsStore.Save(_settings);
            AutoStart.SetEnabled(_settings.AutoStart);
        };

        Controls.Add(_autostart);
        Controls.Add(ok);
        AcceptButton = ok;
    }
}
