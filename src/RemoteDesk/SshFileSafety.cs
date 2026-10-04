using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteDesk;

// Shared by profile/config writes in the elevated GUI and reads in the ordinary
// console helper. Does not depend on deployment resources, settings or the GUI.
internal static class SshFileSafety
{
    internal static PinnedDirectories PinDirectories(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("目录必须为绝对路径。", nameof(directory));
        directory = Path.GetFullPath(directory);
        var result = new PinnedDirectories();
        try
        {
            string current = Path.GetPathRoot(directory) ?? throw new ArgumentException("Invalid absolute directory.");
            result.Handles.Add(OpenPath(current, directory: true));
            foreach (string component in directory[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                Directory.CreateDirectory(current);
                result.Handles.Add(OpenPath(current, directory: true));
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenPath(string path, bool directory)
    {
        const uint openReparsePoint = 0x00200000, backupSemantics = 0x02000000;
        // Directory metadata only; files need read access. Neither permits
        // delete sharing, and files also deny concurrent modification.
        SafeFileHandle handle = CreateFileW(path, directory ? 0x80u : 0x80000000u,
            directory ? FileShare.ReadWrite : FileShare.Read, nint.Zero, 3,
            openReparsePoint | (directory ? backupSemantics : 0), nint.Zero);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInfo info, 8))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    ((info.Attributes & FileAttributes.Directory) != 0) != directory)
                throw new IOException("SSH 文件路径不能包含重解析点、符号链接或非预期文件类型。");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal sealed class PinnedDirectories : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { internal FileAttributes Attributes; internal uint ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare sharing,
        nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileAttributeTagInfo information, uint size);
}
