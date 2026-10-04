namespace RemoteDesk;

public sealed partial class MainForm
{
    internal static void UpdateDirectDeviceLists(ComboBox combo, ListBox list,
        IReadOnlyList<RemoteDeviceListItem> devices, RemoteDeviceListItem? selected)
    {
        int selectedIndex = -1;
        bool unchanged = list.Items.Count == devices.Count && combo.Items.Count == devices.Count;
        for (int i = 0; i < devices.Count; i++)
        {
            if (ReferenceEquals(devices[i], selected)) selectedIndex = i;
            if (unchanged && (list.Items[i] is not RemoteDeviceListItem old ||
                !old.HasSameSnapshot(devices[i]) || !ReferenceEquals(combo.Items[i], old))) unchanged = false;
        }
        // Avoid clearing/reinserting native items on every automatic scan. This
        // also avoids selection notifications and repainting unchanged rows.
        if (unchanged)
        {
            SelectRow();
            return;
        }

        int oldTopIndex = list.Items.Count > 0 ? list.TopIndex : 0;
        RemoteDeviceListItem? oldTop = oldTopIndex >= 0 && oldTopIndex < list.Items.Count
            ? list.Items[oldTopIndex] as RemoteDeviceListItem : null;
        combo.BeginUpdate();
        list.BeginUpdate();
        try
        {
            if (list.Items.Count == devices.Count && combo.Items.Count == devices.Count)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    RemoteDeviceListItem row = list.Items[i] is RemoteDeviceListItem old && old.HasSameSnapshot(devices[i])
                        ? old : devices[i];
                    if (!ReferenceEquals(list.Items[i], row)) list.Items[i] = row;
                    if (!ReferenceEquals(combo.Items[i], row)) combo.Items[i] = row;
                }
            }
            else
            {
                combo.Items.Clear();
                list.Items.Clear();
                object[] rows = devices.Cast<object>().ToArray();
                combo.Items.AddRange(rows);
                list.Items.AddRange(rows);
            }
            SelectRow();
            if (devices.Count > 0)
            {
                int restoredTop = -1;
                for (int i = 0; oldTop is not null && i < devices.Count; i++)
                {
                    RemoteDeviceListItem device = devices[i];
                    if (RemoteDeviceIdentity.Same(oldTop.DeviceId, device.DeviceId) ||
                        (!RemoteDeviceIdentity.Conflicts(oldTop.DeviceId, device.DeviceId) &&
                         oldTop.Port == device.Port &&
                         string.Equals(oldTop.Address, device.Address, StringComparison.OrdinalIgnoreCase)))
                    { restoredTop = i; break; }
                }
                list.TopIndex = restoredTop >= 0 ? restoredTop : Math.Clamp(oldTopIndex, 0, devices.Count - 1);
            }
        }
        finally
        {
            list.EndUpdate();
            combo.EndUpdate();
        }

        void SelectRow()
        {
            if (combo.SelectedIndex != selectedIndex) combo.SelectedIndex = selectedIndex;
            if (list.SelectedIndex != selectedIndex) list.SelectedIndex = selectedIndex;
        }
    }
}
