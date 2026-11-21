using System.Text.Json;

namespace RemoteDesk;

// A user-selected API Switcher export. Keep the approved ciphertext in memory
// from preview to receipt, even if the selected path is subsequently replaced.
internal sealed class AccountLoginPackage
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    internal const string ImportHint = "在 Windows 的 API 切换器中，打开对应客户端的“官方账号”，选择“导入登录包”，输入迁移密码后按需切换账号。";
    internal const string InvalidPackage = "请选择 API 切换器从“官方账号”导出的完整 .asxaccount 登录包。";
    private readonly byte[] _bytes;
    private readonly string _sourcePath;
    private AccountLoginPackage(string sourcePath, byte[] bytes) => (_sourcePath, _bytes) = (sourcePath, bytes);
    internal int Length => _bytes.Length;
    internal Stream OpenRead() => new MemoryStream(_bytes, writable: false);
    internal RemoteFilePastePlan CreateSendPlan() => new([_sourcePath], 0, 0, false,
        [new RemoteFilePasteItem(_sourcePath, RemoteFilePasteItemKind.File) { AccountPackage = this }]);

    internal static AccountLoginPackage Read(string path)
    {
        if (!Path.GetExtension(path).Equals(".asxaccount", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(InvalidPackage);
        using var file = File.OpenRead(path);
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("登录包大小应在 1 字节至 2 MiB 之间。");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new IOException("登录包已改变，请重新选择。");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != "api-switcher-account-login")
                throw new InvalidDataException(InvalidPackage);
            int version = root.GetProperty("version").GetInt32();
            var cipher = root.GetProperty("cipher");
            if (version == 2 && cipher.GetProperty("name").GetString() == "none")
                throw new InvalidDataException("这个登录包没有设置迁移密码。请在 API 切换器中重新导出，设置至少 8 个字符的迁移密码后再发送。");
            var kdf = root.GetProperty("kdf");
            if (version == 1 && root.GetProperty("compression").GetString() == "zlib" &&
                kdf.GetProperty("name").GetString() == "PBKDF2HMAC-SHA256" &&
                kdf.GetProperty("iterations").GetInt32() is >= 100_000 and <= 2_000_000 &&
                kdf.GetProperty("salt").GetBytesFromBase64().Length == 16 &&
                cipher.GetProperty("name").GetString() == "AES-256-GCM" &&
                cipher.GetProperty("nonce").GetBytesFromBase64().Length == 12 &&
                root.GetProperty("payload").GetBytesFromBase64().Length > 16)
                return new(path, bytes);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Avoid putting any source JSON into an error dialog or diagnostic log.
        }
        throw new InvalidDataException(InvalidPackage);
    }
}

internal static class AccountLoginPackageUi
{
    internal const string Action = "发送账号登录包…";

    internal static async Task SendAsync(Form owner, RemoteViewerClient client, Action showTasks)
    {
        long generation = client.InputConnectionGeneration;
        bool Current() => !owner.IsDisposed && !owner.Disposing && client.IsConnected &&
            client.InputConnectionGeneration == generation;
        if (!Current()) return;
        using var picker = new OpenFileDialog
        {
            Title = "选择 API 切换器设置迁移密码后导出的单账号登录包",
            Filter = "API 切换器账号登录包 (*.asxaccount)|*.asxaccount",
            CheckFileExists = true, RestoreDirectory = true
        };
        if (picker.ShowDialog(owner) != DialogResult.OK || !Current()) return;
        try
        {
            string path = picker.FileName;
            var package = await Task.Run(() => AccountLoginPackage.Read(path));
            if (!Current()) return;
            var location = await client.GetRemoteFileReceiveLocationAsync(generation);
            if (!Current()) return;
            string name = Path.GetFileName(path);
            string note = $"接收设备：{location.Device}\r\n{location.Note}";
            var item = new FileTransferConfirmationItem("加密账号登录包", path, name, package.Length, location.FormatFile(name));
            if (!FileTransferConfirmation.Confirm(owner, "确认发送账号登录包", AccountLoginPackage.ImportHint,
                    [item], note) || !Current()) return;
            showTasks();
            var result = await client.SendFilePastePlanToRemoteAsync(package.CreateSendPlan(), "登录包发送失败", generation);
            if (Current()) FileTransferResultDialog.ShowResults(owner, result,
                result.Results?.Any(item => item.SaveConfirmed) == true ? note + "\r\n" + AccountLoginPackage.ImportHint : note);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current()) MessageBox.Show(owner, error.Message, "账号登录包传输", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
