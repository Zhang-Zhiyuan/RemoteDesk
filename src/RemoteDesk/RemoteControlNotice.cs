using System.Runtime.InteropServices;

namespace RemoteDesk;

// A separate, unowned window remains visible when MainForm is hidden to tray.
// Never activate it: showing the notice must not steal the user's typing focus.
internal sealed class RemoteControlNotice : Form
{
    internal const string NoticeText = "●  RemoteDesk · 正在被远程控制";
    private readonly Label _message = new()
    {
        Text = NoticeText, TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false, AutoSize = false, Cursor = Cursors.SizeAll
    };
    private readonly Button _disconnect = new NonActivatingButton()
    {
        Text = "断开", FlatStyle = FlatStyle.Flat, TabStop = false,
        BackColor = Color.FromArgb(185, 28, 28), ForeColor = Color.White
    };
    private Point? _dragOrigin;
    private bool _userPositioned;
    private bool _arranging;

    public event Action? DisconnectRequested;

    public RemoteControlNotice()
    {
        Text = "RemoteDesk — 正在被远程控制";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // Form.TopMost calls FocusActiveControlInternal during Show, even with
        // ShowWithoutActivation. Use the native topmost style/NOACTIVATE instead.
        BackColor = Color.FromArgb(127, 29, 29);
        ForeColor = Color.White;
        Font = SystemFonts.MessageBoxFont!;
        DoubleBuffered = true;
        _disconnect.FlatAppearance.BorderColor = Color.FromArgb(252, 165, 165);
        _disconnect.AccessibleDescription = "断开当前远程控制，保留被控端监听。";
        _message.AccessibleDescription = "当前有人通过 RemoteDesk 查看或控制这台电脑；可拖动此提示。";
        Controls.AddRange([_message, _disconnect]);
        _disconnect.Click += (_, _) => DisconnectRequested?.Invoke();
        _message.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragOrigin = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
                _message.Capture = true;
            }
        };
        _message.MouseMove += (_, _) =>
        {
            if (_dragOrigin is not { } origin) return;
            _userPositioned = true;
            var proposed = new Rectangle(Cursor.Position.X - origin.X, Cursor.Position.Y - origin.Y, Width, Height);
            Bounds = PlaceWithin(Screen.FromRectangle(proposed).WorkingArea, Size, proposed.Location);
        };
        _message.MouseUp += (_, _) => { _dragOrigin = null; _message.Capture = false; };
        _message.MouseCaptureChanged += (_, _) => { if (!_message.Capture) _dragOrigin = null; };
        DpiChanged += (_, _) => ArrangeOnScreen();
        FontChanged += (_, _) => ArrangeOnScreen();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var value = base.CreateParams;
            value.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // NOACTIVATE | TOOLWINDOW | TOPMOST
            return value;
        }
    }

    public void ShowActive()
    {
        _disconnect.Enabled = true;
        _disconnect.Text = "断开";
        ArrangeOnScreen();
        if (!Visible) Show(); // Deliberately no owner, Activate or BringToFront.
        SetWindowPos(Handle, (nint)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
    }

    public void SetDisconnecting()
    {
        _disconnect.Enabled = false;
        _disconnect.Text = "断开中";
    }

    internal void ArrangeOnScreen(Rectangle? workingArea = null)
    {
        if (_arranging || IsDisposed) return;
        _arranging = true;
        try
        {
            Rectangle area = workingArea ?? (_userPositioned
                ? Screen.FromRectangle(Bounds).WorkingArea
                : (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea);
            int padding = ResponsiveWindowLayout.ScaleLogical(10, DeviceDpi);
            int gap = ResponsiveWindowLayout.ScaleLogical(12, DeviceDpi);
            int contentMaximum = Math.Max(1, area.Width - padding * 2);
            int buttonWidth = Math.Min(contentMaximum, TextRenderer.MeasureText("断开中", Font).Width + padding * 2);
            int buttonHeight = Font.Height + padding * 2;
            bool stacked = contentMaximum - buttonWidth - gap < Font.Height * 7;
            int available = Math.Max(1, contentMaximum - (stacked ? 0 : gap + buttonWidth));
            // Use the label's own flags/padding, including fallback-font glyph
            // overhang. A bare MeasureText width leaves a lone CJK glyph wrapped.
            Size text = _message.GetPreferredSize(new Size(available, 0));
            text.Width = Math.Min(available, text.Width + ResponsiveWindowLayout.ScaleLogical(4, DeviceDpi));
            text.Height = _message.GetPreferredSize(new Size(text.Width, 0)).Height;
            if (stacked)
            {
                int width = Math.Max(text.Width, buttonWidth);
                ClientSize = new Size(width + padding * 2, text.Height + gap + buttonHeight + padding * 2);
                _message.Bounds = new Rectangle(padding, padding, width, text.Height);
                _disconnect.Bounds = new Rectangle(padding + width - buttonWidth, padding + text.Height + gap, buttonWidth, buttonHeight);
            }
            else
            {
                int height = Math.Max(text.Height, buttonHeight);
                ClientSize = new Size(text.Width + buttonWidth + gap + padding * 2, height + padding * 2);
                _message.Bounds = new Rectangle(padding, padding, text.Width, height);
                _disconnect.Bounds = new Rectangle(ClientSize.Width - padding - buttonWidth, padding, buttonWidth, height);
            }
            Bounds = PlaceWithin(area, Size, _userPositioned ? Location : null);
        }
        finally { _arranging = false; }
    }

    internal static Rectangle PlaceWithin(Rectangle area, Size size, Point? position = null)
    {
        int width = Math.Min(Math.Max(1, size.Width), Math.Max(1, area.Width));
        int height = Math.Min(Math.Max(1, size.Height), Math.Max(1, area.Height));
        int x = position?.X ?? area.Left + (area.Width - width) / 2;
        int y = position?.Y ?? area.Top + 8;
        return new Rectangle(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)), width, height);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) { m.Result = (nint)3; return; } // MA_NOACTIVATE, but deliver the click.
        base.WndProc(ref m);
        if (m.Msg is 0x007E or 0x001A && IsHandleCreated && !IsDisposed)
            BeginInvoke((Action)(() => ArrangeOnScreen()));
    }

    private sealed class NonActivatingButton : Button
    {
        public NonActivatingButton() => SetStyle(ControlStyles.Selectable, false);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
