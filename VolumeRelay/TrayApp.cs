using System.Drawing;
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
        // TargetName resolves the auto target to the device GG has selected;
        // it falls back to "auto" when nothing is resolved yet.
        menu.Items.Add(MakeTargetItem("auto", $"Auto (GG): {_engine.TargetName}"));

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
        menu.Items.Add(new ToolStripMenuItem(_engine.Paused ? "Resume relay" : "Pause relay", null,
            (s, e) => _engine.SetPaused(!_engine.Paused)));
        menu.Items.Add(new ToolStripMenuItem("Copy diagnostics", null, (s, e) => CopyDiagnostics()));
        menu.Items.Add(MakeAutoStartItem());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Close()));

        _notify.ContextMenuStrip = menu;
        UpdateTooltip();
    }

    void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(Diagnostics.Build(_engine));
            MessageBox.Show(
                "Diagnostics copied to clipboard — paste it into your bug report.\n\n" +
                "Review it first: it contains device names and hardware IDs.",
                "VolumeRelay");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not copy diagnostics: " + ex.Message, "VolumeRelay");
        }
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
        switch (_engine.Mode)
        {
            case RelayMode.Paused:
                _notify.Text = "VolumeRelay — paused";
                return;

            // Either way the app is doing nothing: the knob is driving a real
            // device directly, so report the relay as disabled.
            case RelayMode.DefaultIsTarget:
            case RelayMode.DefaultNotVirtual:
            {
                var dev = _engine.DefaultDeviceName;
                _notify.Text = Clip(dev == null
                    ? "VolumeRelay — disabled: real device selected"
                    : "VolumeRelay — disabled: real device " + dev + " selected");
                return;
            }

            case RelayMode.NoTarget:
                _notify.Text = Clip("VolumeRelay — " + (_engine.AutoStatus ?? "no target"));
                return;
        }

        var vol = _engine.TargetVolume * 100;
        var name = _engine.TargetName ?? "target";
        _notify.Text = Clip($"VolumeRelay — {vol:0}%: {name}");
    }

    /// <summary>NotifyIcon.Text throws above 127 chars (63 before .NET 6).</summary>
    static string Clip(string s) => s.Length <= 127 ? s : s[..124] + "…";

    /// <summary>
    /// Load the app icon from the embedded .ico (see csproj) so the tray icon
    /// matches the Explorer / SmartScreen dialog icon. The single-file publish
    /// has no loose files next to the exe, so a disk load is not an option.
    /// </summary>
    static Icon CreateIcon()
    {
        var asm = typeof(TrayApp).Assembly;
        var res = asm.GetManifestResourceNames()
            .First(n => n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(res)!;
        return new Icon(s, SystemInformation.SmallIconSize);
    }
}
