using Xunit;

namespace RemoteDesk.Tests;

public sealed class ReleaseScriptContractTests
{
    [Fact]
    public void DesktopAndAndroidReleaseVersionsStayAligned()
    {
        var project = System.Xml.Linq.XDocument.Parse(ReadRepositoryFile("src", "RemoteDesk", "RemoteDesk.csproj"));
        string version = project.Descendants("Version").Single().Value;
        string android = ReadRepositoryFile("src", "RemoteDesk.Android", "app", "build.gradle");
        Assert.Contains($"versionName \"{version}\"", android);
    }

    [Fact]
    public void BothLinuxPackagesIncludeThePersistentStartupModule()
    {
        Assert.Contains("remotedesk_linux_startup.py",
            ReadRepositoryFile("scripts", "Build-LinuxSystemPackage.ps1"));
        Assert.Contains("remotedesk_linux_startup.py",
            ReadRepositoryFile("scripts", "Publish-RemoteDesk.ps1"));
    }

    [Fact]
    public void FinalReleaseUsesIsolatedCleanSourceOutputAndRequiresSignedAndroid()
    {
        string publisher = ReadRepositoryFile("scripts", "Publish-RemoteDesk.ps1");
        Assert.Contains("ReleaseName requires clean-source mode", publisher);
        Assert.Contains("-ReleaseName $ReleaseName", publisher);
        Assert.Contains("Final Android release requires a signed APK", publisher);
        string local = ReadRepositoryFile("scripts", "Publish-LocalRelease.ps1");
        Assert.DoesNotContain("-AllowDirtySource", local);
        Assert.DoesNotContain("-SkipTests", local);
        Assert.Contains("Import-Clixml", local);
    }

    [Fact]
    public void SigningVaultKeepsKeysOutOfSourceAndNeverReplacesExistingKeyMaterial()
    {
        string script = ReadRepositoryFile("scripts", "Initialize-AndroidReleaseSigning.ps1");
        Assert.Contains("Keep the signing vault outside the repository", script);
        Assert.Contains("Existing signing vault preserved", script);
        Assert.Contains("-NoClobber", script);
        Assert.Contains("SetAccessRuleProtection($true, $false)", script);
        Assert.Contains("-storepass:env REMOTEDESK_KEYGEN_PASSWORD", script);
    }

    [Fact]
    public void CandidatePublishingIsOptInAndNeverReusesAnExistingDirectory()
    {
        string script = ReadRepositoryFile("scripts", "Publish-RemoteDesk.ps1");
        Assert.Contains("[string]$CandidateName = \"\"", script, StringComparison.Ordinal);
        Assert.Contains("-CandidateName requires -AllowDirtySource", script, StringComparison.Ordinal);
        Assert.Contains("CandidateName must be a simple, non-reserved directory name", script, StringComparison.Ordinal);
        Assert.Contains("Candidate output already exists", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.FileAttributes]::ReparsePoint", script, StringComparison.Ordinal);
        Assert.Contains("$artifacts = Resolve-PublishArtifactsDirectory", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DebianPackageRequiresUnicodeInputHelperAndAndroidReleaseRunsLint()
    {
        string script = ReadRepositoryFile("scripts", "Publish-RemoteDesk.ps1");
        string depends = script.Split('\n').Single(line => line.StartsWith("Depends:", StringComparison.Ordinal));
        string recommends = script.Split('\n').Single(line => line.StartsWith("Recommends:", StringComparison.Ordinal));
        Assert.Contains("xdotool", depends, StringComparison.Ordinal);
        Assert.DoesNotContain("xdotool", recommends, StringComparison.Ordinal);
        Assert.Contains("\"lintRelease\"", script, StringComparison.Ordinal);
        Assert.Contains("\"lintDebug\"", script, StringComparison.Ordinal);
        Assert.Contains("$androidTasks += \"testReleaseUnitTest\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherRejectsSourceChangesBeforeWritingTheManifest()
    {
        string script = ReadRepositoryFile("scripts", "Publish-RemoteDesk.ps1");
        int validation = script.IndexOf("$finalSourceState = Get-SourceState -Root $root", StringComparison.Ordinal);
        int manifest = script.IndexOf("$manifestEntries = Write-ArtifactManifest", StringComparison.Ordinal);
        Assert.True(validation > 0 && manifest > validation);
        Assert.Contains("$finalSourceState.Fingerprint.Hash -cne $sourceState.Fingerprint.Hash", script, StringComparison.Ordinal);
        Assert.Contains("Source changed during publishing", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFingerprintExcludesSessionWorktreesAndDiffArtifacts()
    {
        string script = ReadRepositoryFile(
            "scripts",
            "RemoteDesk-ReleaseCommon.ps1");

        Assert.Contains(
            "\".claude/worktrees/\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\".diff\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"artifacts/\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"release/\"",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseCommonParsesWslPythonAfterPrecedingProxyWarnings()
    {
        string script = ReadRepositoryFile(
            "scripts",
            "RemoteDesk-ReleaseCommon.ps1");

        Assert.Contains(
            "Where-Object { $_ -match \"^Python\\s+\\d+\\.\\d+\\.\\d+\" }",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$versionLines.Count -ne 1",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "return $versionLines[0]",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CheckerOnlyReportsAdbProcessesOwnedByThisRun()
    {
        string script = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");

        Assert.Contains(
            "$script:InitialAdbProcessIds = @(",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Get-CheckerAdbProcesses",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "pre-existing adb server preserved",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$RunTests -and $runAndroidChecks",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPublishRunsEveryPlatformTestSuiteAndRequiresSixArtifacts()
    {
        string script = ReadRepositoryFile("scripts", "Publish-RemoteDesk.ps1");

        Assert.Contains(
            @"dotnet test .\RemoteDesk.sln -c Release",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-LinuxPythonTests -Root $root",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            @"python3 -m unittest discover -s tests -p ""test_*.py"" -v",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "& wsl.exe -d $Distro --cd $wslRoot -- bash -lc",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            @"cd ""$1"" && exec python3 -m unittest",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$androidTasks += \"testDebugUnitTest\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Full\" { 6 }",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-CanonicalPublishArtifactSet",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SkipAndroidUsesExplicitDesktopScopeInPublisherAndChecker()
    {
        string publishScript = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        string checkScript = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");
        const string desktopScope =
            "Windows host/viewer; Linux host/viewer package";

        Assert.Contains(desktopScope, publishScript, StringComparison.Ordinal);
        Assert.Contains(
            "-SkipAndroid cannot be combined with -AndroidRelease.",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(desktopScope, checkScript, StringComparison.Ordinal);
        Assert.Contains(
            "Resolve-RequestedReleaseScopeKind",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "-SkipAndroid:$SkipAndroid",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "& wsl.exe -d $Distro --cd $wslRoot -- bash -lc",
            checkScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            @"cd ""$1"" && exec python3 -m unittest",
            checkScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DebianPackageCarriesThirdPartyNoticesAndLicenseTexts()
    {
        string script = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");

        Assert.Contains(
            @"$PortableStaging ""THIRD-PARTY-NOTICES.md""",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            @"$packageRoot ""opt\remotedesk\THIRD-PARTY-NOTICES.md""",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            @"$PortableStaging ""third-party-licenses""",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            @"$packageRoot ""opt\remotedesk\third-party-licenses""",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxPackageWritersNormalizeCleanCheckoutCrlfToLf()
    {
        string script = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        int helperStart = script.IndexOf(
            "function Write-Utf8NoBomContent",
            StringComparison.Ordinal);
        int helperEnd = script.IndexOf(
            "function New-LinuxHostWrapper",
            helperStart,
            StringComparison.Ordinal);

        Assert.True(helperStart >= 0 && helperEnd > helperStart);
        string helper = script[helperStart..helperEnd];
        Assert.Contains(
            "$Content.Replace(\"`r`n\", \"`n\").Replace(\"`r\", \"`n\")",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "$builder.Append(\"`n\")",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "$text += \"`n\"",
            helper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[Environment]::NewLine",
            helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxPortableZipPreservesAndValidatesUnixExecutableModes()
    {
        string publishScript = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        string checkScript = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");
        int artifactStart = publishScript.IndexOf(
            "function New-LinuxHostArtifact",
            StringComparison.Ordinal);
        int artifactEnd = publishScript.IndexOf(
            "$root = Resolve-RepoRoot",
            artifactStart,
            StringComparison.Ordinal);
        Assert.True(artifactStart >= 0 && artifactEnd > artifactStart);
        string artifactWriter = publishScript[artifactStart..artifactEnd];

        Assert.DoesNotContain(
            "Compress-Archive",
            artifactWriter,
            StringComparison.Ordinal);
        Assert.Contains(
            "function New-LinuxHostZip",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "tmp=\"$(mktemp -d)\"",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "find \"$tmp\" -type d -exec chmod 755 {} +",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "find \"$tmp\" -type f -exec chmod 644 {} +",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "find \"$tmp/runtime/bin\" -maxdepth 1 -type f -exec chmod 755",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "with zipfile.ZipFile(",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-LinuxPortableZipModes -ZipPath $zipPath",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"runtime/bin/python\")",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-LinuxPortableZipModes -ZipPath $linuxHostZip",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "all four Linux portable entry scripts have Unix mode 0755",
            checkScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherPreflightsAndTransactionsCanonicalArtifacts()
    {
        string script = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");

        int preflight = script.IndexOf(
            "Validating release toolchain before touching canonical artifacts",
            StringComparison.Ordinal);
        int tests = script.IndexOf(
            "Running Windows tests",
            StringComparison.Ordinal);
        int transaction = script.IndexOf(
            "$transaction = Start-PublishArtifactTransaction",
            StringComparison.Ordinal);
        int completion = script.IndexOf(
            "Complete-PublishArtifactTransaction -Transaction $transaction",
            StringComparison.Ordinal);
        int restore = script.IndexOf(
            "Restore-PublishArtifactTransaction `",
            StringComparison.Ordinal);
        Assert.True(preflight >= 0);
        Assert.True(tests > preflight);
        Assert.True(transaction > tests);
        Assert.True(completion > transaction);
        Assert.True(restore > transaction);
        Assert.Contains(
            "catch {",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$transactionCommitted = $true",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "cleanup failure must not roll back",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Canonical artifact rollback was incomplete",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Clearing previous release manifest",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseManifestCarriesExactSourceFingerprintAndToolchain()
    {
        string publisher = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        string checker = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");
        string common = ReadRepositoryFile(
            "scripts",
            "RemoteDesk-ReleaseCommon.ps1");

        Assert.Contains(
            "Get-RemoteDeskSourceFingerprint",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "sourceFingerprintVersion",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "sourceFileCount",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RemoteDeskSourceFingerprint",
            checker,
            StringComparison.Ordinal);
        Assert.Contains(
            "manifest source fingerprint does not match",
            checker,
            StringComparison.Ordinal);
        Assert.Contains(
            "manifest toolchain $field does not match current",
            checker,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RemoteDeskDotNetSdkVersion",
            checker,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RemoteDeskGradleVersion",
            checker,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RemoteDeskDotNetSdkVersion",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RemoteDeskGradleVersion",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RemoteDeskLinuxPythonVersion",
            common,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidBuildUsesRepositoryPinnedGradleWrapper()
    {
        string publisher = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        string checker = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");
        string wrapperProperties = ReadRepositoryFile(
            "src",
            "RemoteDesk.Android",
            "gradle",
            "wrapper",
            "gradle-wrapper.properties");
        string globalJson = ReadRepositoryFile("global.json");

        Assert.Contains(
            @"RemoteDesk.Android\gradlew.bat",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            @"RemoteDesk.Android\gradlew.bat",
            checker,
            StringComparison.Ordinal);
        Assert.Contains(
            "distributionSha256Sum=f397b287023acdba1e9f6fc5ea72d22dd63669d59ed4a289a29b1a76eee151c6",
            wrapperProperties,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"version\": \"8.0.424\"",
            globalJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LowLatencyWireConstantsStayAlignedAcrossWindowsAndroidAndLinux()
    {
        string windowsCapabilities = ReadRepositoryFile(
            "src",
            "RemoteDesk",
            "RemoteDeviceInfo.cs");
        string windowsControls = ReadRepositoryFile(
            "src",
            "RemoteDesk",
            "RemoteMessages.cs");
        string windowsDatagrams = ReadRepositoryFile(
            "src",
            "RemoteDesk",
            "LowLatencyVideoProtocol.cs");
        string androidProtocol = ReadRepositoryFile(
            "src",
            "RemoteDesk.Android",
            "app",
            "src",
            "main",
            "java",
            "com",
            "remotedesk",
            "agent",
            "RemoteDeskProtocol.java");
        string androidDatagrams = ReadRepositoryFile(
            "src",
            "RemoteDesk.Android",
            "app",
            "src",
            "main",
            "java",
            "com",
            "remotedesk",
            "agent",
            "LowLatencyVideoProtocol.java");
        string linuxProtocol = ReadRepositoryFile(
            "scripts",
            "linux",
            "remotedesk_protocol_probe.py");

        Assert.Contains(
            "AuthenticatedUdpHeartbeat = 1 << 20",
            windowsCapabilities,
            StringComparison.Ordinal);
        Assert.Contains(
            "CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT = 1 << 20",
            androidProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "HighQualityJpeg = 1 << 21",
            windowsCapabilities,
            StringComparison.Ordinal);
        Assert.Contains(
            "CAPABILITY_HIGH_QUALITY_JPEG = 1 << 21",
            androidProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "LowLatencyVideoStopped = 31",
            windowsControls,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONTROL_LOW_LATENCY_VIDEO_STOPPED = 31",
            androidProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "Heartbeat = 9",
            windowsDatagrams,
            StringComparison.Ordinal);
        Assert.Contains(
            "KIND_HEARTBEAT = 9",
            androidDatagrams,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONTROL_LOW_LATENCY_VIDEO_STOPPED = 31",
            linuxProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "LOW_LATENCY_FALLBACK_PRESERVE_UDP_INPUT = 4",
            linuxProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT = 1 << 20",
            linuxProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "CAPABILITY_HIGH_QUALITY_JPEG = 1 << 21",
            linuxProtocol,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DirtySourceRequiresAnExplicitInternalCandidateOverride()
    {
        string publishScript = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        string checkScript = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");

        Assert.Contains(
            "[switch]$AllowDirtySource",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$sourceState.Dirty -and -not $AllowDirtySource",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "internalCandidate = $InternalCandidate",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch]$AllowDirtySource",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-GitRepositoryHead -Root $Root",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$repositoryHead.Revision -cne $manifestSourceRevision",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$manifestSourceDirty -and -not $AllowDirtySource",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "-AllowDirtySource:$AllowDirtySource",
            checkScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPackageCarriesExactRuntimePackNotices()
    {
        string publishScript = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        string checkScript = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");

        Assert.Contains(
            "Copy-DotNetRuntimePackNotices",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "microsoft.netcore.app.runtime.win-x64",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"DOTNET-RUNTIME-LICENSE.TXT\"",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT\"",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"DOTNET-RUNTIME-LICENSE.TXT\"",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT\"",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Install-RemoteDeskFfmpeg.ps1\"",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Install-RemoteDeskFfmpeg.ps1\"",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "foreach ($expectedEntryName in $expectedEntryNames)",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-FileSha256Hex -Path $canonicalFile.FullName",
            checkScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPublishSupportsTimestampedCertificateStoreSigning()
    {
        string commonScript = ReadRepositoryFile(
            "scripts",
            "RemoteDesk-ReleaseCommon.ps1");
        string publishScript = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        string checkScript = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");

        Assert.Contains(
            "[string]$WindowsSigningCertificateThumbprint",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$WindowsTimestampServer",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Set-RemoteDeskWindowsAuthenticodeSignature",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Set-AuthenticodeSignature",
            commonScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "-HashAlgorithm SHA256",
            commonScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "-TimestampServer",
            commonScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "windowsAuthenticode = $WindowsAuthenticode",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RemoteDeskWindowsAuthenticodeMetadata",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "authenticated personal-LAN remote update mode available",
            checkScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveAcceptancePasswordDoesNotUseProcessArguments()
    {
        string checkScript = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");

        Assert.Contains(
            "[switch]$PromptForPassword",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "-AsSecureString",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "ZeroFreeBSTR",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--password-fd\", \"0\"",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "prefer -PromptForPassword",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$dirtySourceArgument",
            checkScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-RemoteDeskCheck.ps1$dirtySourceArgument",
            checkScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsFfmpegCompanionInstallerPinsAndVerifiesItsDependency()
    {
        string installer = ReadRepositoryFile(
            "scripts",
            "Install-RemoteDeskFfmpeg.ps1");

        Assert.Contains(
            "ffmpeg-8.1.2-essentials_build.zip",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "1326dde4c84ff1f96fe6b8916c5bed29e163e9b5dccf995f6f3db069d143ec5e",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Name \"gfxcapture\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"FFMPEG-LICENSE.txt\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"FFMPEG-DEPENDENCY.json\"",
            installer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsArtifactCheckerUsesPowerShellFiveCompatibleOrdinalSearch()
    {
        string checker = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");

        Assert.Contains(
            "$script.IndexOf(",
            checker,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$script.Contains(",
            checker,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LongSoakRegressionHasReleaseGatesAndAuditableReports()
    {
        string script = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskSoakRegression.ps1");

        Assert.Contains(
            "[ValidateRange(30, 10080)]",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[double]$MinimumAvailabilityPercent = 99.5",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[double]$MinimumRemoteProcessAvailabilityPercent = 99.0",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$SshCommandTimeoutSeconds = 30",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$SshUserKnownHostsFile = \"\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"UserKnownHostsFile=$knownHostsPath\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "`$ProgressPreference = 'SilentlyContinue'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'^\\d+\\|'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$MaximumGpuRecoveryEvents = 0",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RemoteDesk-soak-report.json\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RemoteDesk-soak-report.md\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RemoteDesk-soak-checkpoint.json\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RemoteDesk-soak-cleanup.json\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConvertTo-Json -Depth 20",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "gpuCollectionErrors = @($script:GpuCollectionErrors)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$frameCount -gt 0",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Remote process sample coverage\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Maximum monitoring sample gap\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Maximum remote observation gap\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "No connection or state-changing action was executed.",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LongSoakRegressionFaultScenariosAreExplicitAndCleanedUp()
    {
        string script = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskSoakRegression.ps1");

        Assert.Contains(
            "[switch]$EnableNetworkFault",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch]$EnableRemoteRestart",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch]$EnableDisplaySwitch",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (-not (Test-IsAdministrator))",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$PSCmdlet.ShouldProcess(",
            script,
            StringComparison.Ordinal);
        int cleanupStart = script.IndexOf(
            "function Stop-LocalNetworkFault",
            StringComparison.Ordinal);
        int cleanupEnd = script.IndexOf(
            "function Invoke-NetworkFaultScenario",
            cleanupStart,
            StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0 && cleanupEnd > cleanupStart);
        string mandatoryCleanup = script[cleanupStart..cleanupEnd];
        Assert.DoesNotContain(
            "$PSCmdlet.ShouldProcess(",
            mandatoryCleanup,
            StringComparison.Ordinal);
        int displayStart = script.IndexOf(
            "function Invoke-DisplaySwitchScenario",
            StringComparison.Ordinal);
        int displayEnd = script.IndexOf(
            "function Restore-OriginalCaptureTarget",
            displayStart,
            StringComparison.Ordinal);
        Assert.True(displayStart >= 0 && displayEnd > displayStart);
        Assert.Contains(
            "$PSCmdlet.ShouldProcess(",
            script[displayStart..displayEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "-EnableRemoteRestart requires the explicit ",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-EnableDisplaySwitch requires ",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[void](Stop-LocalNetworkFault)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[void](Restore-OriginalCaptureTarget)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "os.environ[name]",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"--password\", $Password",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"linux\\remotedesk_protocol_probe.py\"",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"soak\\$stamp-$safeTarget\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$script:NetworkFaultActive -or",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "displayRestore = [ordered]@{",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$script:DisplayRestoreRequired = $true",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "finally {",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$PasswordEnvironmentVariable = \"\"",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GpuResetProbeUsesTheNativeX64InputUnionSize()
    {
        string script = ReadRepositoryFile(
            "experiments",
            "Invoke-RemoteDeskGpuResetProbe.ps1");

        Assert.Contains(
            "[StructLayout(LayoutKind.Explicit, Size = 32)]",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "inputSize != 40",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$workerResult.inputStructureSize -ne 40",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "inputSize != 32",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxToolchainFingerprintUsesTheSelectedWslDistro()
    {
        string publisher = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        string common = ReadRepositoryFile(
            "scripts",
            "RemoteDesk-ReleaseCommon.ps1");
        string checker = ReadRepositoryFile(
            "scripts",
            "Invoke-RemoteDeskCheck.ps1");

        Assert.Contains(
            "param([string]$Distro = \"Ubuntu-24.04\")",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "$wslArguments = @(\"-d\", $Distro, \"--\", \"python3\", \"--version\")",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-RemoteDeskLinuxDistro -Distro $Distro",
            common,
            StringComparison.Ordinal);
        Assert.Contains(
            "$LinuxDistro = Assert-RemoteDeskLinuxDistro -Distro $LinuxDistro",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "$LinuxDistro = Assert-RemoteDeskLinuxDistro -Distro $LinuxDistro",
            checker,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RemoteDeskLinuxPythonVersion -Distro $LinuxDistro",
            checker,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$LinuxDistro = \"Ubuntu-24.04\"",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RemoteDeskLinuxPythonVersion -Distro $LinuxDistro",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-LinuxPythonTests -Root $root -Distro $LinuxDistro",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "New-LinuxHostArtifact -Root $root -Artifacts $artifacts -Version $releaseVersion -Distro $LinuxDistro",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "linuxDistro = if (-not $WindowsOnly) { $LinuxDistro } else { $null }",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "manifest toolchain linuxDistro does not match current",
            checker,
            StringComparison.Ordinal);
        Assert.Contains(
            "WSL distro $(Format-MarkdownCell",
            checker,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionCleanupDoesNotContainDuplicateCompletionPath()
    {
        string script = ReadRepositoryFile(
            "scripts",
            "Publish-RemoteDesk.ps1");
        int start = script.IndexOf(
            "function Complete-PublishArtifactTransaction",
            StringComparison.Ordinal);
        int end = script.IndexOf(
            "$root = Resolve-RepoRoot",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string completion = script[start..end];
        Assert.Equal(
            1,
            completion.Split("            return", StringSplitOptions.None).Length - 1);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        foreach (string startingPath in new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        })
        {
            DirectoryInfo? directory = new(startingPath);
            while (directory is not null)
            {
                string candidate = Path.Combine(
                    [directory.FullName, .. segments]);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Repository file was not found: {Path.Combine(segments)}");
    }
}
