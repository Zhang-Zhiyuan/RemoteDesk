using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSecureDesktopFailureTests
{
    public static TheoryData<int, string> StageCases => new()
    {
        { (int)SecureDesktopFailureStage.Elevation, "没有管理员权限" },
        { (int)SecureDesktopFailureStage.Registration, "无法读取当前用户" },
        { (int)SecureDesktopFailureStage.MissingInstallation, "未找到当前用户的有效" },
        { (int)SecureDesktopFailureStage.DisabledInstallation, "服务未启用" },
        { (int)SecureDesktopFailureStage.InstallationValidation, "文件或权限校验未通过" },
        { (int)SecureDesktopFailureStage.PipeConnection, "辅助进程尚未连接成功" },
        { (int)SecureDesktopFailureStage.ServerIdentity, "身份校验未通过" },
        { (int)SecureDesktopFailureStage.HelperRejected, "暂时无法处理当前登录桌面" },
        { (int)SecureDesktopFailureStage.RequestValidation, "请求参数无效" },
        { (int)SecureDesktopFailureStage.Exchange, "通信中断" }
    };

    [Theory]
    [MemberData(nameof(StageCases))]
    public void FailureMessagesDistinguishTheActualStageWithoutExceptionText(int stage, string expected)
    {
        const string privateDetails = "C:\\Users\\Private\\password.txt key=private-clipboard-marker mouse=(100,200)";
        string message = WindowsSecureDesktopClient.DescribeFailure((SecureDesktopFailureStage)stage, new IOException(privateDetails));
        Assert.StartsWith("锁屏控制暂不可用：", message);
        Assert.Contains(expected, message);
        Assert.DoesNotContain(privateDetails, message);
        Assert.DoesNotContain("private-clipboard-marker", message);
        Assert.DoesNotContain("C:\\", message);
    }

    [Fact]
    public void EstablishedPipeTimeoutDoesNotMisdiagnoseMissingPermissionOrInstallation()
    {
        string message = WindowsSecureDesktopClient.DescribeFailure(
            SecureDesktopFailureStage.Exchange, new OperationCanceledException("private"));
        Assert.Contains("响应超时", message);
        Assert.DoesNotContain("管理员", message);
        Assert.DoesNotContain("未启用", message);
        Assert.DoesNotContain("private", message);
    }

    [Fact]
    public void PipeConnectionTimeoutExplainsThatInstallationIsAlreadyEnabled()
    {
        string message = WindowsSecureDesktopClient.DescribeFailure(
            SecureDesktopFailureStage.PipeConnection, new OperationCanceledException());
        Assert.Contains("已启用", message);
        Assert.Contains("是否正在运行", message);
        Assert.DoesNotContain("没有管理员权限", message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidProtocolDoesNotExposePayload(bool invalidJson)
    {
        Exception error = invalidJson ? new JsonException("private payload") : new InvalidDataException("private payload");
        string message = WindowsSecureDesktopClient.DescribeFailure(SecureDesktopFailureStage.Exchange, error);
        Assert.Contains("无效响应", message);
        Assert.DoesNotContain("private payload", message);
    }

    [Fact]
    public void HelperRejectionDoesNotPretendServiceIsMissing()
    {
        string message = WindowsSecureDesktopClient.DescribeFailure(
            SecureDesktopFailureStage.HelperRejected, new InvalidOperationException("native secret"));
        Assert.Contains("辅助进程已连接", message);
        Assert.Contains("Windows 应用日志", message);
        Assert.DoesNotContain("请在被控端启用", message);
        Assert.DoesNotContain("native secret", message);
    }

    [Theory]
    [InlineData((int)SecureDesktopIdentityQuery.OpenProcess)]
    [InlineData((int)SecureDesktopIdentityQuery.OpenProcessToken)]
    [InlineData((int)SecureDesktopIdentityQuery.QueryFullProcessImageName)]
    [InlineData((int)SecureDesktopIdentityQuery.ProcessIdToSessionId)]
    [InlineData((int)SecureDesktopIdentityQuery.GetTokenElevation)]
    [InlineData((int)SecureDesktopIdentityQuery.GetNamedPipeServerProcessId)]
    public void NativeIdentityQueryReportsOnlyFixedApiAndNumericError(int query)
    {
        var operation = (SecureDesktopIdentityQuery)query;
        var error = new SecureDesktopIdentityQueryException(operation, 5);
        string message = WindowsSecureDesktopClient.DescribeFailure(SecureDesktopFailureStage.ServerIdentity, error);
        Assert.Contains("API " + operation, message);
        Assert.Contains("Win32 5", message);
        Assert.DoesNotContain(error.Message, message);
        Assert.DoesNotContain("S-1-", message);
        Assert.DoesNotContain("C:\\", message);
    }

    [Theory]
    [InlineData((int)SecureDesktopServerIdentityMismatch.SystemAccount, "SYSTEM 身份")]
    [InlineData((int)SecureDesktopServerIdentityMismatch.Session, "不在当前用户会话")]
    [InlineData((int)SecureDesktopServerIdentityMismatch.Executable, "运行程序与已注册安装不一致")]
    public void MismatchUsesNonSensitiveFixedClassification(int mismatch, string expected)
    {
        var error = new SecureDesktopServerIdentityException((SecureDesktopServerIdentityMismatch)mismatch);
        string message = WindowsSecureDesktopClient.DescribeFailure(SecureDesktopFailureStage.ServerIdentity, error);
        Assert.Contains(expected, message);
        Assert.Contains("已拒绝连接", message);
        Assert.DoesNotContain(error.Message, message);
    }

    [Theory]
    [InlineData("S-1-5-18", 3, @"C:\Protected\RemoteDesk.exe", -1)]
    [InlineData("S-1-5-18", 3, @"c:\protected\remotedesk.EXE", -1)]
    [InlineData("S-1-5-21-1-2-3-1001", 3, @"C:\Protected\RemoteDesk.exe", (int)SecureDesktopServerIdentityMismatch.SystemAccount)]
    [InlineData("S-1-5-18", 4, @"C:\Protected\RemoteDesk.exe", (int)SecureDesktopServerIdentityMismatch.Session)]
    [InlineData("S-1-5-18", 3, @"C:\Protected\RemoteDesk.exe.20260919.old", (int)SecureDesktopServerIdentityMismatch.Executable)]
    [InlineData("S-1-5-18", 3, @"C:\Other\RemoteDesk.exe", (int)SecureDesktopServerIdentityMismatch.Executable)]
    public void IdentityClassificationsPreserveAllChecks(string sid, int session, string executable, int mismatch)
    {
        var actual = WindowsSecureDesktopClient.FindServerIdentityMismatch(sid, session, executable, 3, @"C:\Protected\RemoteDesk.exe");
        if (mismatch < 0) Assert.Null(actual);
        else Assert.Equal((SecureDesktopServerIdentityMismatch)mismatch, actual);
    }
}
