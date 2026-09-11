using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsAdministratorManifestTests
{
    [Fact]
    public void BuiltWindowsExecutableRequiresAdministratorWithoutUiAccessBypass()
    {
        // Inspect the actual native apphost resource, not just a loose XML file.
        // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE never executes it.
        string path = Path.Combine(AppContext.BaseDirectory, "RemoteDesk.exe");
        Assert.True(File.Exists(path), "The built product apphost must be present for manifest validation.");
        XElement manifest = ReadManifest(path);
        XNamespace asm3 = "urn:schemas-microsoft-com:asm.v3";
        XElement level = Assert.Single(manifest.Descendants(asm3 + "requestedExecutionLevel"));
        Assert.Equal("requireAdministrator", (string?)level.Attribute("level"));
        Assert.Equal("false", (string?)level.Attribute("uiAccess"));
        Assert.DoesNotContain(manifest.Descendants(), item => item.Name.LocalName == "autoElevate");

        XNamespace compatibility = "urn:schemas-microsoft-com:compatibility.v1";
        Assert.Contains(manifest.Descendants(compatibility + "supportedOS"),
            item => (string?)item.Attribute("Id") == "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
    }

    private static XElement ReadManifest(string path)
    {
        nint module = LoadLibraryExW(path, nint.Zero, 0x00000002 | 0x00000020);
        Assert.NotEqual(nint.Zero, module);
        try
        {
            nint resource = FindResourceW(module, (nint)1, (nint)24); // CREATEPROCESS_MANIFEST_RESOURCE_ID, RT_MANIFEST.
            Assert.NotEqual(nint.Zero, resource);
            uint length = SizeofResource(module, resource);
            Assert.InRange(length, 1u, 65536u);
            nint data = LockResource(LoadResource(module, resource));
            Assert.NotEqual(nint.Zero, data);
            byte[] bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return XElement.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\0'));
        }
        finally { FreeLibrary(module); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint LoadLibraryExW(string name, nint file, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint FindResourceW(nint module, nint name, nint type);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SizeofResource(nint module, nint resource);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LoadResource(nint module, nint resource);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LockResource(nint resource);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);
}
