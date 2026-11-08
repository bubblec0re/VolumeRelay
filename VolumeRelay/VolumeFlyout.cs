using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VolumeRelay;

/// <summary>
/// Volume flyout opened by left-clicking the tray icon. Shows and controls the
/// TARGET device's volume/mute — the native Windows flyout keeps showing the
/// pinned virtual pipe (~98%), which says nothing about what you hear.
///
/// Its look is deliberately its own, not a Windows-flyout imitation: a lookalike
/// popping up next to the real one would read as the OS flyout reporting wrong
/// values. The VolumeRelay icon leads the card, the accent is the app's own,
/// and the card sits above the tray icon instead of screen-bottom-center.
///
/// Non-activating (WS_EX_NOACTIVATE): it never steals focus from games or
/// fullscreen apps. No persisted state, no new dependencies.
/// </summary>
public class VolumeFlyout : Form
{
    const int AutoHideMs = 2500;

    // Layout in 96-dpi units; ClientSize is converted via LogicalToDeviceUnits.
    const int BaseWidth = 260;
    const int BaseHeight = 96;

    // The app's own accent — deliberately not the native flyout's blue.
    static readonly Color AccentDark = Color.FromArgb(38, 198, 162);
    static readonly Color AccentLight = Color.FromArgb(13, 148, 118);

    readonly VolumeRelayEngine _engine;
    readonly Icon _appIcon; // shared with the NotifyIcon; never disposed here
    readonly Font _titleFont;
    readonly Font _subFont;
    readonly Font? _glyphFont;
    readonly System.Windows.Forms.Timer _hideTimer;

    bool _dark = true;
    bool _hovered;
    bool _dragging;
    float _dragValue = -1f; // visual echo while dragging (drag is authoritative)
    string _title = "target";
    string _subtitle = "";
    float _volume;
    bool _muted;
    bool _controlsEnabled;
    int _holdMs;   // auto-hide countdown

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int sizeBytes);

    public VolumeFlyout(VolumeRelayEngine engine, Icon appIcon)
    {
        _engine = engine;
        _appIcon = appIcon;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        MinimizeBox = MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;

        _titleFont = new Font(Font, FontStyle.Bold);
        _subFont = new Font(Font.FontFamily, Font.Size * 0.85f);

        // Speaker / mute glyphs; fall back to a drawn shape if neither exists.
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            try { _glyphFont = new Font(name, Font.Size * 1.1f); break; }
            catch (ArgumentException) { }
        }

        _hideTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _hideTimer.Tick += (s, e) => HideTimerTick();

        CreateHandle(); // BeginInvoke surface must exist before the first show
        ClientSize = new Size(LogicalToDeviceUnits(BaseWidth), LogicalToDeviceUnits(BaseHeight));

        _engine.VolumeChanged += OnEngineVolumeChanged;
        Disposed += (s, e) => _engine.VolumeChanged -= OnEngineVolumeChanged;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000  // WS_EX_NOACTIVATE
                        | 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            // Windows 11 rounded corners; on Windows 10 the attribute is
            // unsupported and the call fails — the card simply stays square.
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        }
        catch { }
    }

    // ---- Show / hide ----

    /// <summary>Second left-click on the tray icon while visible hides the flyout.</summary>
    public void Toggle()
    {
        if (Visible) { HideFlyout(); return; }
        ShowAt();
    }

    void ShowAt()
    {
        UpdateFromEngine();

        // Right side, just above the system tray — the native flyout owns
        // bottom-center, so ours stays clear of it.
        var wa = (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
        ClientSize = new Size(LogicalToDeviceUnits(BaseWidth), LogicalToDeviceUnits(BaseHeight));
        int x = wa.Right - ClientSize.Width - 12;
        int y = wa.Bottom - ClientSize.Height - 12;
        Location = new Point(x, y);

        _holdMs = AutoHideMs;
        _hideTimer.Start();
        Show();
    }

    // No fade/slide animation: an animating layered window ghosted in captures
    // (the slider looked tilted) and moving windows are distraction anyway.

    void HideFlyout()
    {
        _hideTimer.Stop();
        Visible = false;
    }

    void HideTimerTick()
    {
        if (!Visible) { _hideTimer.Stop(); return; }
        // Poll instead of relying on enter/leave events: a non-activating
        // window can miss them, and a stuck "hovered" would pin the flyout.
        _hovered = _dragging || Bounds.Contains(Cursor.Position);
        if (_hovered) { _holdMs = AutoHideMs; return; }
        _holdMs -= _hideTimer.Interval;
        if (_holdMs <= 0) HideFlyout();
    }

    // ---- Engine sync ----

    void OnEngineVolumeChanged()
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            if (Visible)
            {
                UpdateFromEngine(); // keep an open flyout in sync
                _holdMs = AutoHideMs;
            }
            else
            {
                // Knob adjustments pop the flyout with the real target level —
                // the native one appearing alongside shows the pinned 98% pipe.
                ShowAt();
            }
        });
    }

    void UpdateFromEngine()
    {
        _dark = ReadDarkTheme();
        _volume = _engine.TargetVolume;
        _muted = _engine.TargetMuted;
        _title = _engine.TargetName ?? "target";
        _controlsEnabled = _engine.ResolvedTargetId != null;

        // Truthful states: passive modes are reported as what they are — the
        // flyout never claims the relay is active when it isn't.
        switch (_engine.Mode)
        {
            case RelayMode.Paused: _subtitle = "relay paused"; break;
            case RelayMode.NoTarget: _subtitle = _engine.AutoStatus ?? "no target"; break;
            case RelayMode.DefaultIsTarget: _subtitle = "knob drives this device natively"; break;
            case RelayMode.DefaultNotVirtual: _subtitle = "knob drives the default device natively"; break;
            default: _subtitle = ""; break;
        }

        BackColor = Card;
        Invalidate();
    }

    /// <summary>0 = dark (default on failure), 1 = light. Read live, never persisted.</summary>
    static bool ReadDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : true;
        }
        catch { return true; }
    }

    // ---- Painting ----

    Color Card => _dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(249, 249, 249);
    Color TextC => _dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(32, 32, 32);
    Color SubC => _dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(105, 105, 105);
    Color TrackC => _dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(214, 214, 214);
    Color AccentC => _dark ? AccentDark : AccentLight;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var L = ComputeRects();

        // The app icon leads the card — this is clearly a VolumeRelay flyout.
        g.DrawIcon(_appIcon, L.IconR);
        TextRenderer.DrawText(g, _title, _titleFont, L.TitleR, TextC,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, _subtitle, _subFont, L.SubR, SubC,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        string pct = _controlsEnabled ? $"{Math.Round(_volume * 100)}%" : "--";
        TextRenderer.DrawText(g, pct, _titleFont, L.PctR, _muted ? SubC : TextC,
            TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        DrawMuteGlyph(g, L.MuteR);
        DrawSlider(g, L.TrackR);
    }

    readonly record struct FlyoutLayout(
        Rectangle IconR, Rectangle TitleR, Rectangle SubR, Rectangle PctR, Rectangle MuteR, Rectangle TrackR);

    FlyoutLayout ComputeRects()
    {
        int U(int v) => LogicalToDeviceUnits(v);
        int pad = U(14);
        int iconSize = U(18);
        int w = ClientSize.Width;

        var iconR = new Rectangle(pad, pad, iconSize, iconSize);
        int textX = pad + iconSize + U(8);
        var titleR = new Rectangle(textX, pad - U(2), w - textX - pad, _titleFont.Height);
        var subR = new Rectangle(textX, pad - U(2) + _titleFont.Height, w - textX - pad, _subFont.Height);

        // Slider row sits directly under the header; the % sits right of the track.
        int rowH = U(24);
        int rowY = Math.Max(iconR.Bottom, subR.Bottom) + U(6);
        var muteR = new Rectangle(pad, rowY, rowH, rowH);
        int pctW = U(38);
        var pctR = new Rectangle(w - pad - pctW, rowY, pctW, rowH);
        int trackH = U(6);
        var trackR = new Rectangle(
            muteR.Right + U(12), rowY + (rowH - trackH) / 2,
            pctR.X - U(12) - muteR.Right, trackH);

        return new FlyoutLayout(iconR, titleR, subR, pctR, muteR, trackR);
    }

    void DrawSlider(Graphics g, Rectangle trackR)
    {
        // Keep the two bars deliberately simple: exact same integer Y/height,
        // drawn as rectangles. Rounded paths/arc math introduced a sloped edge
        // and a tiny bulb at the start on some DPI scales.
        using var trackBrush = new SolidBrush(TrackC);
        g.FillRectangle(trackBrush, trackR);

        if (!_controlsEnabled) return;

        float value = Math.Clamp(_dragging && _dragValue >= 0 ? _dragValue : _volume, 0f, 1f);
        int diameter = LogicalToDeviceUnits(14);
        int radius = diameter / 2;
        int minCenter = trackR.Left + radius;
        int maxCenter = trackR.Right - radius;
        int centerTravel = Math.Max(0, maxCenter - minCenter);
        int thumbCx = minCenter + (int)Math.Round(value * centerTravel);
        int cy = trackR.Top + trackR.Height / 2;

        // Fill bar shares the exact Y and height with the background bar.
        // Flat ends avoid the rounded-start artifact; thumb covers the join.
        if (thumbCx > trackR.Left)
        {
            using var fillBrush = new SolidBrush(AccentC);
            g.FillRectangle(fillBrush, trackR.Left, trackR.Top,
                thumbCx - trackR.Left, trackR.Height);
        }

        using var thumbBrush = new SolidBrush(AccentC);
        g.FillEllipse(thumbBrush, thumbCx - radius, cy - radius, diameter, diameter);
    }

    void DrawMuteGlyph(Graphics g, Rectangle r)
    {
        Color c = !_controlsEnabled ? TrackC : _muted ? AccentC : TextC;
        if (_glyphFont != null)
        {
            string glyph = _muted ? "\uE74F" : "\uE767"; // mute / speaker
            TextRenderer.DrawText(g, glyph, _glyphFont, r, c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        else
        {
            // Drawn fallback when neither glyph font exists.
            using var pen = new Pen(c, LogicalToDeviceUnits(2));
            g.DrawEllipse(pen, r.X + r.Width / 4, r.Y + r.Height / 4, r.Width / 2, r.Height / 2);
            if (_muted) g.DrawLine(pen, r.X, r.Bottom, r.Right, r.Y);
        }
    }

    // ---- Hover / mouse interaction ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _holdMs = AutoHideMs;
        var L = ComputeRects();
        if (_controlsEnabled && L.MuteR.Contains(e.Location))
        {
            _engine.SetTargetMute(!_engine.TargetMuted);
            return;
        }
        if (_controlsEnabled && HitTrack(L.TrackR, e.X, out var v))
        {
            _dragging = true;
            Capture = true;
            _dragValue = v;
            _engine.SetTargetVolume(v); // engine fires VolumeChanged; drag echo wins until release
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _holdMs = AutoHideMs;
        if (HitTrack(ComputeRects().TrackR, e.X, out var v) && v != _dragValue)
        {
            _dragValue = v;
            _engine.SetTargetVolume(v);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        _dragValue = -1f;
        Capture = false;
        UpdateFromEngine();
    }

    /// <summary>Value (0..1) for an x inside the slider's hit area.</summary>
    bool HitTrack(Rectangle trackR, int x, out float value)
    {
        int radius = LogicalToDeviceUnits(7);
        var hit = Rectangle.Inflate(trackR, radius, LogicalToDeviceUnits(10));
        int minCenter = trackR.Left + radius;
        int maxCenter = trackR.Right - radius;
        value = Math.Clamp((float)(x - minCenter) / Math.Max(1, maxCenter - minCenter), 0f, 1f);
        return hit.Contains(x, trackR.Y + trackR.Height / 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.VolumeChanged -= OnEngineVolumeChanged;
            _hideTimer.Dispose();
            _titleFont.Dispose();
            _subFont.Dispose();
            _glyphFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
