using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VolSync;

public class TrayApp : Form
{
    readonly Settings _settings;
    readonly VolSyncEngine _engine;
    readonly NotifyIcon _notify;

    public TrayApp()
    {
        // Tray-only app: never show a window. The form exists solely to own a
        // message loop / handle for Invoke, and as the owner for dialogs.
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        ShowIcon = false;

        _settings = SettingsStore.Load();

        // Refresh the Run entry so a moved or updated exe keeps a valid path.
        if (_settings.AutoStart) AutoStart.SetEnabled(true);

        _engine = new VolSyncEngine(_settings);

        _notify = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "VolSync",
            Visible = true,
        };
        _notify.DoubleClick += (s, e) => OpenSettings();

        _engine.StateChanged += OnStateChanged;
        _engine.VolumeChanged += OnVolumeChanged;

        _engine.Start();
        RebuildUi();
    }

    /// <summary>Keep the window permanently hidden while still creating a handle.</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated)
        {
            CreateHandle();
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(false);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _engine.Dispose();
        _notify.Visible = false;
        _notify.Dispose();
        base.OnFormClosed(e);
    }

    void OnStateChanged() => InvokeIfNeeded(RebuildUi);
    void OnVolumeChanged() => InvokeIfNeeded(UpdateTooltip);

    void InvokeIfNeeded(Action a)
    {
        if (IsHandleCreated) BeginInvoke(a);
        else a();
    }

    void RebuildUi()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(MakeTargetItem("auto", "Auto — follow GG's selection"));
        menu.Items.Add(new ToolStripSeparator());
        foreach (var d in _engine.Devices)
            menu.Items.Add(MakeTargetItem(d.Id, d.Display));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(_engine.Paused ? "Resume sync" : "Pause sync", null,
            (s, e) => _engine.SetPaused(!_engine.Paused)));
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (s, e) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Close()));
        _notify.ContextMenuStrip = menu;
        UpdateTooltip();
    }

    ToolStripMenuItem MakeTargetItem(string id, string text)
    {
        var item = new ToolStripMenuItem(text) { Tag = id, Checked = _engine.TargetId == id };
        item.Click += (s, e) => _engine.SetTarget(id);
        return item;
    }

    void UpdateTooltip()
    {
        if (_engine.Paused)
        {
            _notify.Text = "VolSync — paused";
            return;
        }

        var targetId = _engine.ResolvedTargetId;
        if (targetId == null)
        {
            var status = _engine.AutoStatus ?? "no target";
            _notify.Text = Clip("VolSync — " + status);
            return;
        }

        var vol = _engine.TargetVolume * 100;
        var name = _engine.TargetName ?? targetId;
        var prefix = _engine.TargetId == "auto" ? "Auto: " : "";
        _notify.Text = Clip($"VolSync — {vol:0}% → {prefix}{name}");
    }

    /// <summary>NotifyIcon.Text throws above 63 chars — keep it short.</summary>
    static string Clip(string s) => s.Length <= 63 ? s : s[..60] + "…";

    void OpenSettings()
    {
        using var form = new SettingsForm(_settings);
        form.ShowDialog(this);
        _engine.ReloadSettings(_settings);
        RebuildUi();
    }

    static Icon CreateIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(70, 150, 255));
            g.FillRectangle(brush, 1, 6, 3, 4);
            using var path = new GraphicsPath();
            path.AddPolygon(new[]
            {
                new Point(4, 6), new Point(8, 2), new Point(8, 14), new Point(4, 10),
            });
            g.FillPath(brush, path);
            using var pen = new Pen(brush, 1.4f);
            g.DrawArc(pen, 8, 4, 5, 8, -60, 120);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
