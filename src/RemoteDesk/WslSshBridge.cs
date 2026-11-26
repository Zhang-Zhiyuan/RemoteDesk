using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteDesk;

internal static class WslSshBridge
{
    internal static async Task<Stream> OpenAsync(WslSshTarget distro, CancellationToken token)
    {
        var id = Guid.NewGuid();
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create("RemoteDesk.WslSsh." + id.ToString("N"), PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            65536, 65536, acl);
        try
        {
            string helper = SshProxyDeployment.EnsureInstalled();
            // Guest executables inherit the interactive user's Windows privileges, including with WSL interop.
            StartHelper(helper, id, Guid.Parse(distro.Id));
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            byte[] ready = new byte[1];
            await pipe.ReadExactlyAsync(ready, token).ConfigureAwait(false);
            if (ready[0] != 1) throw new IOException(ready[0] switch {
                4 => "WSL SSH 需要普通桌面用户权限。",
                5 => $"WSL {distro.Name} 已停止或未在当前用户下注册。",
                6 => $"WSL {distro.Name} 无法启动 Python 3。",
                _ => $"WSL {distro.Name} 的 OpenSSH 服务不可达。" });
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    private static void StartHelper(string path, Guid pipe, Guid distro)
    {
        string args = $"--wsl-ssh {pipe:N} {distro:D}";
        using var current = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            return;
        }
        // Derive a basic-user token from this process. This also works when Explorer is elevated or absent.
        if (!SaferCreateLevel(2, 0x20000, 1, out var level, 0)) throw new Win32Exception();
        try
        {
            if (!SaferComputeTokenFromLevel(level, current.AccessToken, out var limited, 0, 0)) throw new Win32Exception();
            using (limited)
            {
                // A filtered administrator SID alone can still carry a high integrity label.
                // Restrict the new token to medium integrity before it can launch guest code.
                var medium = new SecurityIdentifier("S-1-16-8192");
                byte[] bytes = new byte[medium.BinaryLength];
                medium.GetBinaryForm(bytes, 0);
                nint sid = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, sid, bytes.Length);
                    var label = new IntegrityLabel { Sid = sid, Attributes = 0x20 };
                    if (!SetTokenInformation(limited, 25, ref label, Marshal.SizeOf<IntegrityLabel>() + bytes.Length))
                        throw new Win32Exception();
                }
                finally { Marshal.FreeHGlobal(sid); }
                var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
                if (!CreateProcessAsUser(limited, path, new StringBuilder($"\"{path}\" {args}"), 0, 0, false,
                    0x08000000, 0, Environment.SystemDirectory, ref si, out var info)) throw new Win32Exception();
                CloseHandle(info.Thread); CloseHandle(info.Process);
            }
        }
        finally { SaferCloseLevel(level); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public int X, Y, Width, Height, Columns, Rows, Fill, Flags;
        public short Show, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IntegrityLabel { public nint Sid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int information,
        ref IntegrityLabel label, int length);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferCreateLevel(uint scope, uint level, uint flags, out nint handle, nint reserved);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferComputeTokenFromLevel(nint level, SafeAccessTokenHandle source,
        out SafeAccessTokenHandle result, uint flags, nint reserved);
    [DllImport("advapi32.dll")] private static extern bool SaferCloseLevel(nint level);
    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string executable, StringBuilder command,
        nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment,
        string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
