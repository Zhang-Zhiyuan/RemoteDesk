namespace RemoteDesk;

// The standard flat-button disabled renderer can use nearly black text on our
// dark viewer toolbar (notably with classic rendering / a private desktop).
// Keep unavailable actions legible without making them look enabled.
internal sealed class ViewerActionButton : Button
{
    internal static readonly Color DisabledTextColor = Color.FromArgb(148, 163, 184);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled || SystemInformation.HighContrast)
        {
            base.OnPaint(e);
            return;
        }
        using var background = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(background, ClientRectangle);
        if (Width > 1 && Height > 1 && FlatAppearance.BorderSize > 0)
        {
            using var border = new Pen(FlatAppearance.BorderColor, FlatAppearance.BorderSize);
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
        Rectangle textBounds = Rectangle.Inflate(ClientRectangle, -2, -2);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, DisabledTextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
    }
}
