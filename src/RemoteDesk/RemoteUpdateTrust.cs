using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteDesk;

internal readonly record struct RemoteUpdateTrustAnchor(
    string SignerPublicKeySha256,
    string SignerCertificateSha256,
    string BuildStamp);

internal readonly record struct TrustedRemoteUpdatePackage(
    string FileSha256,
    string SignerCertificateSha256,
    string BuildStamp,
    bool AuthenticodeRequired);

internal static class RemoteUpdateTrust
{
    internal const string UnsignedSignerCertificateSha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static bool CanUseAsCurrentPackage(string executablePath)
    {
        return TryCreateAnchor(executablePath, out _, out _) ||
            TryReadUnsignedBuildStamp(
                executablePath,
                out _,
                out _);
    }

    public static bool TryCreateAnchor(
        string executablePath,
        out RemoteUpdateTrustAnchor anchor,
        out string error)
    {
        anchor = default;
        if (!OperatingSystem.IsWindows())
        {
            error = "远程更新发布者验证仅支持 Windows。";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"更新文件路径无效：{ex.Message}";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = "更新文件不存在。";
            return false;
        }

        int trustStatus = VerifyEmbeddedAuthenticodeSignature(fullPath);
        if (trustStatus != 0)
        {
            error = $"Authenticode 签名无效或不受信任（0x{trustStatus:X8}）。";
            return false;
        }

        try
        {
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(fullPath);
            using var signer = new X509Certificate2(certificate);
            byte[] publicKey = signer.PublicKey.ExportSubjectPublicKeyInfo();
            byte[] certificateHash = signer.GetCertHash(HashAlgorithmName.SHA256);
            try
            {
                string? buildStamp = RemoteDeskBuildInfo.ReadExecutableBuildStamp(fullPath);
                if (buildStamp is null)
                {
                    error = "已签名文件不包含可验证的 RemoteDesk 构建时间戳。";
                    return false;
                }

                anchor = new RemoteUpdateTrustAnchor(
                    Convert.ToHexString(SHA256.HashData(publicKey)),
                    Convert.ToHexString(certificateHash),
                    buildStamp);
                error = string.Empty;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
                CryptographicOperations.ZeroMemory(certificateHash);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            error = $"无法读取 Authenticode 签名者：{ex.Message}";
            return false;
        }
    }

    public static TrustedRemoteUpdatePackage ValidateUpgrade(
        string currentExecutablePath,
        string candidatePath)
    {
        if (TryCreateAnchor(
                currentExecutablePath,
                out RemoteUpdateTrustAnchor current,
                out string currentSignedError))
        {
            return ValidateSignedUpgrade(
                current,
                candidatePath);
        }

        if (!TryReadUnsignedBuildStamp(
                currentExecutablePath,
                out string currentBuildStamp,
                out string currentUnsignedError))
        {
            throw new System.Security.SecurityException(
                "当前 RemoteDesk 既不是可信签名包，也不是可识别的未签名个人包，" +
                $"远程更新已禁用：{currentSignedError}；{currentUnsignedError}");
        }

        if (!TryReadUnsignedBuildStamp(
                candidatePath,
                out string candidateBuildStamp,
                out string candidateError))
        {
            throw new System.Security.SecurityException(
                "当前 RemoteDesk 使用未签名个人更新模式，" +
                $"候选也必须是带有效构建号的未签名 RemoteDesk：{candidateError}");
        }

        ValidateBuildStampUpgrade(
            currentBuildStamp,
            candidateBuildStamp);
        return new TrustedRemoteUpdatePackage(
            CalculateFileSha256(candidatePath),
            UnsignedSignerCertificateSha256,
            candidateBuildStamp,
            AuthenticodeRequired: false);
    }

    internal static bool TryReadUnsignedBuildStamp(
        string executablePath,
        out string buildStamp,
        out string error)
    {
        buildStamp = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            error = "未签名个人更新仅支持 Windows。";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            error = $"更新文件路径无效：{ex.Message}";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = "更新文件不存在。";
            return false;
        }

        if (HasEmbeddedAuthenticodeSignature(fullPath))
        {
            error = "文件包含 Authenticode 签名，但签名未通过可信发布者验证。";
            return false;
        }

        string? detectedBuildStamp =
            RemoteDeskBuildInfo.ReadExecutableBuildStamp(fullPath);
        if (detectedBuildStamp is null)
        {
            error = "未签名文件不包含可验证的 RemoteDesk 构建时间戳。";
            return false;
        }

        buildStamp = detectedBuildStamp;
        error = string.Empty;
        return true;
    }

    private static TrustedRemoteUpdatePackage ValidateSignedUpgrade(
        RemoteUpdateTrustAnchor current,
        string candidatePath)
    {
        if (!TryCreateAnchor(
                candidatePath,
                out RemoteUpdateTrustAnchor candidate,
                out string candidateError))
        {
            throw new System.Security.SecurityException(
                $"远程更新包发布者验证失败：{candidateError}");
        }

        byte[] currentPublicKey = Convert.FromHexString(current.SignerPublicKeySha256);
        byte[] candidatePublicKey = Convert.FromHexString(candidate.SignerPublicKeySha256);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(currentPublicKey, candidatePublicKey))
            {
                throw new System.Security.SecurityException(
                    "远程更新包的签名公钥与当前 RemoteDesk 不一致。");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentPublicKey);
            CryptographicOperations.ZeroMemory(candidatePublicKey);
        }

        ValidateBuildStampUpgrade(current.BuildStamp, candidate.BuildStamp);
        return new TrustedRemoteUpdatePackage(
            CalculateFileSha256(candidatePath),
            candidate.SignerCertificateSha256,
            candidate.BuildStamp,
            AuthenticodeRequired: true);
    }

    internal static void ValidateBuildStampUpgrade(string currentBuildStamp, string candidateBuildStamp)
    {
        int? comparison = RemoteDeskBuildInfo.CompareBuildStamps(candidateBuildStamp, currentBuildStamp);
        if (comparison is null)
        {
            throw new System.Security.SecurityException("无法比较远程更新包与当前程序的构建版本。");
        }

        if (comparison <= 0)
        {
            throw new System.Security.SecurityException(
                $"已拒绝同版本或降级更新：当前 {currentBuildStamp}，候选 {candidateBuildStamp}。");
        }
    }

    private static string CalculateFileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool HasEmbeddedAuthenticodeSignature(
        string path)
    {
        try
        {
            using X509Certificate _ =
                X509Certificate.CreateFromSignedFile(path);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static int VerifyEmbeddedAuthenticodeSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };
        IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x10u | 0x100u,
                UiContext = 0
            };
            Guid action = WinTrustActionGenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle;

        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }
}
