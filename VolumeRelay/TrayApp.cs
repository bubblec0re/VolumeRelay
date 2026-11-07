using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VolumeRelay;

public class TrayApp : Form
{
    readonly VolumeRelayEngine _engine;
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

        // A form that is never shown has no window handle, so BeginInvoke would
        // be unavailable and COM callbacks would touch the tray UI from their
        // own thread. Create it on the UI thread before anything can fire.
        CreateHandle();

        // Refresh the Run entry so a moved or updated exe keeps a valid path.
        if (AutoStart.IsEnabled()) AutoStart.SetEnabled(true);

        _engine = new VolumeRelayEngine();

        _notify = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "VolumeRelay",
            Visible = true,
        };

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

    void OnStateChanged()
    {
        InvokeIfNeeded(RebuildUi);
    }
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

        // Only physical outputs are offered: a virtual device must never route
        // into another virtual device, and the default/virtual pipe is itself
        // virtual, so it must not be selectable as a target either.
        var physical = _engine.Devices.Where(d => !d.IsVirtual).ToList();
        menu.Items.Add(new ToolStripSeparator());
        if (physical.Count == 0)
            menu.Items.Add(new ToolStripMenuItem("No physical outputs") { Enabled = false });
        else
            foreach (var d in physical)
                menu.Items.Add(MakeTargetItem(d.Id, d.Display));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(_engine.Paused ? "Resume sync" : "Pause sync", null,
            (s, e) => _engine.SetPaused(!_engine.Paused)));
        menu.Items.Add(MakeAutoStartItem());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Close()));

        _notify.ContextMenuStrip = menu;
        UpdateTooltip();
    }

    ToolStripMenuItem MakeAutoStartItem()
    {
        var item = new ToolStripMenuItem("Start with Windows")
        {
            // The Run key is the single source of truth.
            Checked = AutoStart.IsEnabled(),
        };
        item.Click += (s, e) =>
        {
            var enable = !AutoStart.IsEnabled();
            AutoStart.SetEnabled(enable);
            item.Checked = enable;
        };
        return item;
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
            _notify.Text = "VolumeRelay — paused";
            return;
        }

        var targetId = _engine.ResolvedTargetId;
        if (targetId == null)
        {
            var status = _engine.AutoStatus ?? "no target";
            _notify.Text = Clip("VolumeRelay — " + status);
            return;
        }

        var vol = _engine.TargetVolume * 100;
        var name = _engine.TargetName ?? targetId;
        var prefix = _engine.TargetId == "auto" ? "Auto: " : "";
        _notify.Text = Clip($"VolumeRelay — {vol:0}% → {prefix}{name}");
    }

    /// <summary>NotifyIcon.Text throws above 63 chars — keep it short.</summary>
    static string Clip(string s) => s.Length <= 63 ? s : s[..60] + "…";

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
