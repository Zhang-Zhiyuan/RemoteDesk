# Read the actual EXE resource without launching it or requesting elevation.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $Path).Path
if (-not ('RemoteDeskBuild.ManifestReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace RemoteDeskBuild {
    public static class ManifestReader {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", ExactSpelling=true, SetLastError=true)]
        static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll", ExactSpelling=true)] static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", ExactSpelling=true)] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", ExactSpelling=true)] static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll", ExactSpelling=true)] static extern bool FreeLibrary(IntPtr module);
        public static string Read(string path) {
            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x22);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                IntPtr resource = FindResourceW(module, (IntPtr)1, (IntPtr)24);
                if (resource == IntPtr.Zero) throw new InvalidOperationException("EXE has no application manifest");
                uint length = SizeofResource(module, resource);
                if (length == 0 || length > 65536) throw new InvalidOperationException("Invalid manifest length");
                IntPtr data = LockResource(LoadResource(module, resource));
                if (data == IntPtr.Zero) throw new InvalidOperationException("Manifest resource unavailable");
                byte[] bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\0');
            } finally { FreeLibrary(module); }
        }
    }
}
'@
}
$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$settings.MaxCharactersInDocument = 65536
$text = [IO.StringReader]::new([RemoteDeskBuild.ManifestReader]::Read($executable))
$reader = [Xml.XmlReader]::Create($text, $settings)
try {
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($reader)
    $namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('a', 'urn:schemas-microsoft-com:asm.v3')
    $levels = $document.SelectNodes('//a:requestedExecutionLevel', $namespaces)
    if ($levels.Count -ne 1 -or $levels[0].GetAttribute('level') -cne 'requireAdministrator' -or
        $levels[0].GetAttribute('uiAccess') -cne 'false' -or
        $document.SelectNodes('//*[local-name()="autoElevate"]').Count -ne 0) {
        throw 'Windows package must request requireAdministrator with uiAccess=false and keep normal UAC consent.'
    }
    Write-Output 'Verified embedded EXE manifest: requireAdministrator; uiAccess=false; normal UAC consent.'
} finally { $reader.Dispose(); $text.Dispose() }
