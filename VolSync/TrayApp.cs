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
        _settings = SettingsStore.Load();
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
            _notify.Text = "VolSync — " + status;
            return;
        }

        var vol = _engine.TargetVolume * 100;
        var name = _engine.TargetName ?? targetId;
        var prefix = _engine.TargetId == "auto" ? "Auto: " : "";
        _notify.Text = $"VolSync — {vol:0}% → {prefix}{name}";
    }

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
