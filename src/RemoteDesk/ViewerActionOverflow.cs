namespace RemoteDesk;

// Availability remains on the original buttons. Overflow moves them to an
// unshown owner instead of setting Visible=false (which would lose capability
// changes). Menu commands invoke the same actions, not a hidden PerformClick.
internal sealed class ViewerActionOverflow : IDisposable
{
    private readonly FlowLayoutPanel _panel;
    private readonly Panel _overflowOwner = new();
    private readonly (Button Button, Action Execute)[] _commands;
    private readonly Button[][] _priorityGroups;
    private readonly ContextMenuStrip _menu = new();
    private readonly Action _releaseInput;
    internal Button MoreButton { get; }
    internal IReadOnlyList<Button> Buttons { get; }
    internal IReadOnlyList<Button> OverflowButtons => _commands.Select(c => c.Button)
        .Where(b => b.Parent == _overflowOwner && b.Visible).ToArray();

    internal ViewerActionOverflow(FlowLayoutPanel panel, Button moreButton,
        (Button Button, Action Execute)[] commands, Button[][] priorityGroups, Action releaseInput)
    {
        _panel = panel;
        _commands = commands;
        _priorityGroups = priorityGroups;
        _releaseInput = releaseInput;
        MoreButton = moreButton;
        Buttons = commands.Select(c => c.Button).Append(moreButton).ToArray();
        panel.Controls.Add(moreButton);
        moreButton.Visible = false;
        moreButton.TabStop = true;
        moreButton.Click += (_, _) =>
        {
            _releaseInput();
            moreButton.Select();
            moreButton.Focus();
            RebuildMenu();
            _menu.Show(moreButton, new Point(0, moreButton.Height), ToolStripDropDownDirection.AboveRight);
        };
    }

    internal void Apply(int availableWidth, Font font)
    {
        // Effective Visible is inherited. A hidden/full-screen toolbar must not
        // turn availability into overflow placement, or reparenting oscillates.
        if (!_panel.Visible) return;
        _overflowOwner.Font = font;
        _menu.Font = font;
        Button[] available = _commands.Select(c => c.Button).Where(b => b.Visible).ToArray();
        int width = Math.Max(1, availableWidth - _panel.Padding.Horizontal);
        var inline = new HashSet<Button>(available);
        int Measure(Button button) => button.GetPreferredSize(Size.Empty).Width + button.Margin.Horizontal;
        bool overflow = available.Sum(Measure) > width;
        if (overflow)
        {
            inline.Clear();
            int remaining = Math.Max(0, width - Measure(MoreButton));
            foreach (Button[] group in _priorityGroups)
            {
                Button[] present = group.Where(available.Contains).ToArray();
                int wanted = present.Sum(Measure);
                if (wanted > remaining) continue;
                foreach (Button button in present) inline.Add(button);
                remaining -= wanted;
            }
        }
        _panel.SuspendLayout();
        _overflowOwner.SuspendLayout();
        try
        {
            foreach (var command in _commands)
            {
                Control parent = inline.Contains(command.Button) ? _panel : _overflowOwner;
                if (command.Button.Parent != parent) parent.Controls.Add(command.Button);
            }
            MoreButton.Visible = overflow;
            int index = 0;
            foreach (var command in _commands.Where(c => inline.Contains(c.Button)))
                _panel.Controls.SetChildIndex(command.Button, index++);
            _panel.Controls.SetChildIndex(MoreButton, index);
        }
        finally { _overflowOwner.ResumeLayout(true); _panel.ResumeLayout(true); }
    }

    internal void RebuildMenu()
    {
        foreach (ToolStripItem item in _menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _menu.Items.Clear();
        foreach (var command in _commands.Where(c => c.Button.Parent == _overflowOwner && c.Button.Visible))
        {
            var item = new ToolStripMenuItem(command.Button.Text) { Enabled = command.Button.Enabled,
                AccessibleName = command.Button.AccessibleName ?? command.Button.Text };
            item.Click += (_, _) =>
            {
                if (command.Button.Visible && command.Button.Enabled) command.Execute();
            };
            _menu.Items.Add(item);
        }
    }

    internal ContextMenuStrip MenuForTests => _menu;

    public void Dispose()
    {
        _menu.Dispose();
        _overflowOwner.Dispose();
    }
}
