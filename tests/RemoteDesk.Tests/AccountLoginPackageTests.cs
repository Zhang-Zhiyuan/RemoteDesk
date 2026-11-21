using System.Text.Json.Nodes;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class AccountLoginPackageTests
{
    [Theory]
    [InlineData("plaintext")]
    [InlineData("profile")]
    [InlineData("bad-salt")]
    [InlineData("bad-ciphertext")]
    [InlineData("extension")]
    [InlineData("oversized")]
    public void RequiresAnEncryptedSingleAccountExport(string change)
    {
        string directory = Directory.CreateTempSubdirectory("RemoteDesk-package-").FullName;
        try
        {
            var root = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "account-login-v1.asxaccount")))!;
            string path = Path.Combine(directory, change == "extension" ? "sample.json" : "sample.asxaccount");
            switch (change)
            {
                case "plaintext":
                    root["cipher"] = new JsonObject { ["name"] = "none" };
                    root["version"] = 2;
                    root.AsObject().Remove("kdf");
                    break;
                case "profile": root["format"] = "api-switcher-profile"; break;
                case "bad-salt": root["kdf"]!["salt"] = ""; break;
                case "bad-ciphertext": root["payload"] = "invalid"; break;
            }
            File.WriteAllText(path, root.ToJsonString());
            if (change == "oversized") { using var file = File.OpenWrite(path); file.SetLength(AccountLoginPackage.MaximumBytes + 1); }
            var error = Assert.Throws<InvalidDataException>(() => AccountLoginPackage.Read(path));
            if (change == "plaintext") Assert.Contains("没有设置迁移密码", error.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
