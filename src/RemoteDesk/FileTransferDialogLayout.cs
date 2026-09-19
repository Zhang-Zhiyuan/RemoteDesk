namespace RemoteDesk;

// Keep a useful, expanding file list on large displays while allowing every
// action to be reached on small/high-DPI desktops. A Fill-docked, scrolling
// TableLayoutPanel cannot reliably reveal its own overflow after shrinking.
internal static class FileTransferDialogLayout
{
    internal static void Attach(Form form, TableLayoutPanel content, Control flexibleContent)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        content.Dock = DockStyle.Top;
        content.AutoScroll = false;
        content.AutoSize = false;
        scroll.Controls.Add(content);
        form.Controls.Add(scroll);
        bool queued = false;
        bool applying = false;

        void Reflow()
        {
            queued = false;
            if (scroll.IsDisposed || !scroll.Visible || scroll.ClientSize.Width <= 0) return;
            applying = true;
            try
            {
                // A list with its own scrollbars should fit inside the outer
                // viewport; otherwise Tab can never reveal the whole control.
                var flexibleMaximum = new Size(0, Math.Max(flexibleContent.MinimumSize.Height,
                    scroll.ClientSize.Height - content.Padding.Vertical - flexibleContent.Margin.Vertical));
                if (flexibleContent.MaximumSize != flexibleMaximum) flexibleContent.MaximumSize = flexibleMaximum;
                int minimumHeight = content.Padding.Vertical;
                foreach (Control child in content.Controls)
                {
                    if (!child.Visible) continue;
                    int width = Math.Max(1, scroll.ClientSize.Width - content.Padding.Horizontal - child.Margin.Horizontal);
                    int height = child == flexibleContent ? child.MinimumSize.Height
                        : child is FlowLayoutPanel flow ? ResponsiveWindowLayout.MeasureFlowLayout(flow, width).Height
                        : child is TextBox { Multiline: true } || !child.AutoSize ? child.Height
                        : child.GetPreferredSize(new Size(width, 0)).Height;
                    minimumHeight += Math.Max(child.MinimumSize.Height, height) + child.Margin.Vertical;
                }
                int heightToUse = Math.Max(scroll.ClientSize.Height, minimumHeight);
                if (content.Height != heightToUse) content.Height = heightToUse;
                var extent = new Size(0, heightToUse);
                if (scroll.AutoScrollMinSize != extent) scroll.AutoScrollMinSize = extent;
            }
            finally { applying = false; }
        }

        void QueueReflow()
        {
            if (queued || applying || !scroll.IsHandleCreated || scroll.IsDisposed) return;
            queued = true;
            scroll.BeginInvoke((Action)Reflow);
        }

        void TrackFocus(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                child.Enter += (_, _) => scroll.ScrollControlIntoView(child);
                TrackFocus(child);
            }
        }

        scroll.ClientSizeChanged += (_, _) => QueueReflow();
        scroll.HandleCreated += (_, _) => QueueReflow();
        scroll.VisibleChanged += (_, _) => QueueReflow();
        content.Layout += (_, _) => QueueReflow();
        TrackFocus(content);
    }
}
