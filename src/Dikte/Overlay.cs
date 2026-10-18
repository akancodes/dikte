using System.Drawing.Drawing2D;

namespace Dikte;

enum OverlayMode { Hidden, Listening, HandsFree, Working, Done, Message }

/// <summary>
/// The small pill at the bottom of the screen. It never takes focus and lets clicks through,
/// so the text still lands in the window the user is typing in.
/// </summary>
sealed class Overlay : Form
{
    const int BaseWidth = 260, BaseHeight = 44;
    const int Bars = 18;

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 33 };
    readonly float[] _levels = new float[Bars];
    Func<float>? _level;
    OverlayMode _mode = OverlayMode.Hidden;
    string _text = "";
    string _hint = "";
    DateTime _since;
    DateTime _hideAt = DateTime.MaxValue;
    Font? _font, _small;

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 27);
        DoubleBuffered = true;
        _tick.Tick += (_, _) => OnTick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21, HTTRANSPARENT = -1, MA_NOACTIVATE = 3;
        if (m.Msg == WM_NCHITTEST) { m.Result = HTTRANSPARENT; return; }
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND: Windows 11 rounds the corners
        Native.DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    public void ShowListening(Func<float> level, string title = "Listening", string hint = "Release to send · Space: hands-free · Esc")
    {
        _level = level;
        Array.Clear(_levels);
        Set(OverlayMode.Listening, title, hint, TimeSpan.Zero);
    }

    public void ShowHandsFree() => Set(OverlayMode.HandsFree, "Listening hands-free", "Hotkey to send · Esc to cancel", TimeSpan.Zero);

    public void ShowWorking(string title = "Transcribing")
    {
        _level = null;
        Set(OverlayMode.Working, title, "Esc to cancel", TimeSpan.Zero);
    }

    public void ShowDone(string text)
    {
        _level = null;
        Set(OverlayMode.Done, text, "", TimeSpan.FromMilliseconds(900));
    }

    public void ShowMessage(string text, string hint = "", double seconds = 4)
    {
        _level = null;
        Set(OverlayMode.Message, text, hint, TimeSpan.FromSeconds(seconds));
    }

    public void HideNow()
    {
        _mode = OverlayMode.Hidden;
        _level = null;
        _tick.Stop();
        Hide();
    }

    public OverlayMode Mode => _mode;

    void Set(OverlayMode mode, string text, string hint, TimeSpan autoHide)
    {
        bool wasHidden = _mode == OverlayMode.Hidden || !Visible;
        _mode = mode;
        _text = text;
        _hint = hint;
        _since = DateTime.Now;
        _hideAt = autoHide == TimeSpan.Zero ? DateTime.MaxValue : DateTime.Now + autoHide;
        if (wasHidden) Place();
        if (!Visible) Show();
        _tick.Start();
        Invalidate();
    }

    void Place()
    {
        var fg = Native.GetForegroundWindow();
        var screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.FromPoint(Cursor.Position);
        float scale = DeviceDpi / 96f;
        var size = new Size((int)(BaseWidth * scale), (int)(BaseHeight * scale));
        var area = screen.WorkingArea;
        Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Bottom - size.Height - (int)(28 * scale), size.Width, size.Height);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _font?.Dispose();
        _small?.Dispose();
        _font = _small = null;
    }

    void OnTick()
    {
        if (DateTime.Now >= _hideAt)
        {
            HideNow();
            return;
        }
        if (_level is not null)
        {
            Array.Copy(_levels, 1, _levels, 0, Bars - 1);
            // Speech peaks sit far below full scale; a square root makes quiet talk visible.
            _levels[Bars - 1] = MathF.Min(1, MathF.Sqrt(_level()) * 1.4f);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float s = DeviceDpi / 96f;
        // Sizes in pixels times the window's own DPI scale, so the pill looks the same on every monitor.
        _font ??= new Font("Segoe UI Semibold", 13f * s, GraphicsUnit.Pixel);
        _small ??= new Font("Segoe UI", 11f * s, GraphicsUnit.Pixel);
        var font = _font;
        var small = _small;

        float t = (float)(DateTime.Now - _since).TotalSeconds;
        float pad = 14 * s, mid = Height / 2f;

        var accent = _mode switch
        {
            OverlayMode.Listening => Color.FromArgb(239, 68, 68),
            OverlayMode.HandsFree => Color.FromArgb(249, 115, 22),
            OverlayMode.Working => Color.FromArgb(96, 165, 250),
            OverlayMode.Done => Color.FromArgb(74, 222, 128),
            _ => Color.FromArgb(250, 204, 21),
        };

        // Status dot: pulses while listening, spins while working.
        float dot = 9 * s;
        if (_mode == OverlayMode.Working)
        {
            using var pen = new Pen(accent, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, pad, mid - dot / 2 - 1 * s, dot + 2 * s, dot + 2 * s, t * 360 % 360, 270);
        }
        else
        {
            float pulse = _mode is OverlayMode.Listening or OverlayMode.HandsFree ? 0.75f + 0.25f * MathF.Sin(t * 6) : 1;
            using var brush = new SolidBrush(Color.FromArgb((int)(255 * pulse), accent));
            g.FillEllipse(brush, pad, mid - dot / 2, dot, dot);
        }

        float textX = pad + dot + 10 * s;
        float right = Width - pad;
        if (_level is not null && _mode is OverlayMode.Listening or OverlayMode.HandsFree)
        {
            // Level meter on the right.
            float barW = 3 * s, gap = 2 * s;
            float meterW = Bars * (barW + gap);
            float x0 = Width - pad - meterW;
            using var bar = new SolidBrush(Color.FromArgb(220, 244, 244, 245));
            for (int i = 0; i < Bars; i++)
            {
                float h = MathF.Max(2 * s, _levels[i] * (Height - 18 * s));
                g.FillRectangle(bar, x0 + i * (barW + gap), mid - h / 2, barW, h);
            }
            right = x0 - 8 * s;
        }

        using var white = new SolidBrush(Color.FromArgb(244, 244, 245));
        using var grey = new SolidBrush(Color.FromArgb(161, 161, 170));
        using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        string title = _mode == OverlayMode.Working ? _text + new string('.', 1 + (int)(t * 3) % 3) : _text;
        if (_hint.Length > 0)
        {
            g.DrawString(title, font, white, new RectangleF(textX, mid - 16 * s, right - textX, 18 * s), fmt);
            g.DrawString(_hint, small, grey, new RectangleF(textX, mid + 1 * s, right - textX, 15 * s), fmt);
        }
        else
        {
            g.DrawString(title, font, white, new RectangleF(textX, mid - 9 * s, right - textX, 19 * s), fmt);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _font?.Dispose();
            _small?.Dispose();
        }
        base.Dispose(disposing);
    }
}
