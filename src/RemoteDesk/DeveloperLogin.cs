namespace RemoteDesk;

internal static class DeveloperLogin
{
    internal const string Title = "Codex / Claude 登录";
    internal const string Action = Title + "…";
    internal static string Command(bool codex) => codex ? "codex login --device-auth" : "claude auth login";

    internal static async Task ShowAsync(Form owner, RemoteViewerClient? client, Action showTasks)
    {
        long generation = client?.InputConnectionGeneration ?? 0;
        var device = client?.CurrentDevice;
        bool remote = client is not null;
        bool Current() => !owner.IsDisposed && (!remote || client!.IsConnected && client.InputConnectionGeneration == generation);
        if (!Current()) return;
        bool desktop = device?.Platform is RemoteDevicePlatforms.Windows or RemoteDevicePlatforms.Linux;
        bool clipboard = desktop && device!.Capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardText) && !client!.IsLocalInputPaused;
        bool files = device?.Capabilities.HasFlag(RemoteDeviceCapabilities.FileReceive) == true;
        var codex = new TaskDialogCommandLinkButton("登录 Codex", Command(true));
        var claude = new TaskDialogCommandLinkButton("登录 Claude Code", Command(false));
        var legacy = new TaskDialogCommandLinkButton("发送已有的加密登录包", "选择旧版 .asxaccount 文件");
        var page = new TaskDialogPage {
            Caption = Title, Heading = remote ? "登录到远端 · " + (device?.MachineName ?? "当前连接") : "登录到本机 · " + Environment.MachineName,
            Text = !remote ? "选择已安装的客户端。登录会在本机终端打开，请按提示完成官方授权。"
                : clipboard ? "选择客户端后，登录命令会写入这台远端电脑的剪贴板。在远端终端粘贴、回车，按提示完成官方授权。"
                : "远端登录需要可写剪贴板的 Windows 或 Linux 桌面。也可以在目标电脑的 RemoteDesk 主页直接打开登录终端。"
        };
        if (!remote || clipboard) { page.Buttons.Add(codex); page.Buttons.Add(claude); }
        if (files) page.Buttons.Add(legacy);
        page.Buttons.Add(TaskDialogButton.Cancel);
        TaskDialogButton choice = TaskDialog.ShowDialog(owner, page);
        if (!Current() || choice == TaskDialogButton.Cancel) return;
        if (choice == legacy) { await AccountLoginPackageUi.SendAsync(owner, client!, showTasks); return; }
        if (choice != codex && choice != claude) return;
        try
        {
            if (!remote) { DesktopUserLaunch.SignIn(choice == codex); return; }
            bool sent = await client!.SendClipboardTextToRemoteAsync(Command(choice == codex),
                "登录命令已写入远端剪贴板，请在远端终端粘贴并回车。", expectedGeneration: generation);
            if (Current() && !sent) MessageBox.Show(owner, "命令未送达。请检查连接和仅观看开关，然后重试。", Title);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (Current()) MessageBox.Show(owner, error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
