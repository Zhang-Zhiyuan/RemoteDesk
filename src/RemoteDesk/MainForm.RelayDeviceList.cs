namespace RemoteDesk;

public sealed partial class MainForm
{
    private bool _relayDeviceListUpdating;

    internal bool ApplyRelayDeviceList(IReadOnlyList<RelayOnlineDevice> devices, long configurationGeneration)
    {
        if (_isClosing || IsDisposed || configurationGeneration != _relayConfigurationGeneration) return false;
        // Ignore the temporary deselection caused by replacing native rows, but
        // allow deliberate selection changes while the network request awaits.
        _relayDeviceListUpdating = true;
        try { UpdateRelayDeviceList(_relayDevicesList, devices, _settings.Relay.DeviceId); }
        finally { _relayDeviceListUpdating = false; }
        SelectRelayDeviceKey();
        return true;
    }

    internal static void UpdateRelayDeviceList(
        ListView list, IReadOnlyList<RelayOnlineDevice> devices, string? localDeviceId)
    {
        bool sameOrder = list.Items.Count == devices.Count;
        for (int i = 0; sameOrder && i < devices.Count; i++)
            sameOrder = list.Items[i].Tag is RelayOnlineDevice previous &&
                (string.Equals(previous.DeviceId, devices[i].DeviceId, StringComparison.OrdinalIgnoreCase) ||
                 DeviceKey(previous) == DeviceKey(devices[i]));

        // A normal heartbeat changes only LastSeenSeconds. Retain native rows,
        // keyboard focus, scroll position and the user's in-progress selection.
        if (sameOrder)
        {
            list.BeginUpdate();
            try
            {
                for (int i = 0; i < devices.Count; i++)
                    UpdateRelayDeviceListItem(list.Items[i], devices[i], localDeviceId);
            }
            finally { list.EndUpdate(); }
            return;
        }

        ListViewItem? top = list.IsHandleCreated && list.Items.Count > 0 ? list.TopItem : null;
        int topIndex = top?.Index ?? 0;
        ListViewItem? focused = list.FocusedItem;
        var selected = list.Items.Cast<ListViewItem>().Where(item => item.Selected).ToHashSet();
        var reusable = new Dictionary<string, Queue<ListViewItem>>(StringComparer.Ordinal);
        foreach (ListViewItem item in list.Items)
        {
            if (item.Tag is not RelayOnlineDevice previous) continue;
            string key = DeviceKey(previous);
            if (!reusable.TryGetValue(key, out var matches)) reusable[key] = matches = new();
            matches.Enqueue(item);
        }
        var rows = new ListViewItem[devices.Count];
        for (int i = 0; i < devices.Count; i++)
        {
            RelayOnlineDevice device = devices[i];
            rows[i] = reusable.TryGetValue(DeviceKey(device), out var matches) && matches.Count > 0
                ? matches.Dequeue() : new ListViewItem();
            UpdateRelayDeviceListItem(rows[i], device, localDeviceId);
        }

        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            list.Items.AddRange(rows);
        }
        finally { list.EndUpdate(); }

        foreach (ListViewItem item in rows)
            item.Selected = selected.Contains(item);
        if (focused?.ListView == list) focused.Focused = true;
        if (list.IsHandleCreated && list.Items.Count > 0)
            list.TopItem = top?.ListView == list ? top : list.Items[Math.Min(topIndex, list.Items.Count - 1)];
    }

    private static string DeviceKey(RelayOnlineDevice device) =>
        RemoteDeviceIdentity.Normalize(device.DeviceId) ?? device.DeviceId;

    private static void UpdateRelayDeviceListItem(
        ListViewItem item, RelayOnlineDevice device, string? localDeviceId)
    {
        bool isLocal = RelayDeviceSelectionPolicy.IsLocalDevice(device, localDeviceId);
        string address = device.AddressDisplay;
        while (item.SubItems.Count < 6) item.SubItems.Add(string.Empty);
        SetCell(0, isLocal ? $"{device.MachineName}（本机）" : device.MachineName);
        SetCell(1, device.Platform);
        SetCell(2, device.BuildDisplay);
        SetCell(3, isLocal ? (device.SupportsSsh ? "本机（SSH 可用）" : "本机（不可远控桌面）") : device.StatusText);
        SetCell(4, device.LastSeenDisplay);
        SetCell(5, address);
        Color color = isLocal ? MutedTextColor : TextColor;
        if (item.ForeColor != color) item.ForeColor = color;
        string tooltip = $"设备 ID：{device.DeviceId}\n{address}\n仅可达的地址能直连；跨网请选择中继连接。";
        if (item.ToolTipText != tooltip) item.ToolTipText = tooltip;
        // Even identical visible text may carry a new endpoint/naming capability.
        item.Tag = device;

        void SetCell(int index, string text)
        {
            if (item.SubItems[index].Text != text) item.SubItems[index].Text = text;
        }
    }
}
