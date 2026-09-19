using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using Xunit;
using Xunit.Abstractions;

namespace RemoteDesk.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RemoteDeskKeyboardRealMachineFactAttribute :
    FactAttribute
{
    internal const string EnabledVariable =
        "REMOTEDESK_REAL_MACHINE_KEYBOARD_TESTS";

    public RemoteDeskKeyboardRealMachineFactAttribute()
    {
        if (!IsEnabled(
                RemoteDeskRealMachineFactAttribute
                    .EnabledVariable))
        {
            Skip =
                $"Set {RemoteDeskRealMachineFactAttribute.EnabledVariable}=1 " +
                "to run real-machine tests.";
            return;
        }

        if (!IsEnabled(EnabledVariable))
        {
            Skip =
                $"Set {EnabledVariable}=1 to allow the interactive remote " +
                "keyboard probe.";
        }
    }

    private static bool IsEnabled(string variableName)
    {
        string? value =
            Environment.GetEnvironmentVariable(variableName);
        return string.Equals(
                value,
                "1",
                StringComparison.Ordinal) ||
            string.Equals(
                value,
                "true",
                StringComparison.OrdinalIgnoreCase);
    }
}

[Collection(WindowsRealMachineCollection.Name)]
public sealed class WindowsRemoteKeyboardRealMachineTests
{
    private static string Host
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(
                "REMOTEDESK_REAL_MACHINE_HOST");
            return string.IsNullOrWhiteSpace(configured)
                ? throw new InvalidOperationException(
                    "Set REMOTEDESK_REAL_MACHINE_HOST to an explicitly authorized test host.")
                : configured.Trim();
        }
    }
    private const string PortVariable =
        "REMOTEDESK_REAL_MACHINE_PORT";
    private static int Port =>
        ReadPositiveEnvironmentInteger(
            PortVariable,
            fallback: Protocol.DefaultPort,
            maximum: 65535);
    private const string PasswordVariable =
        "REMOTEDESK_REAL_MACHINE_PASSWORD";
    private const string TokenVariable =
        "REMOTEDESK_REAL_MACHINE_KEYBOARD_TOKEN";
    private const string PreparedVariable =
        "REMOTEDESK_REAL_MACHINE_KEYBOARD_PREPARED";
    private static readonly TimeSpan ConnectionTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClipboardTimeout =
        TimeSpan.FromSeconds(8);
    private static readonly MethodInfo SendControlMethod =
        typeof(RemoteViewerClient).GetMethod(
            "SendControlAsync",
            BindingFlags.Instance |
            BindingFlags.NonPublic,
            binder: null,
            [typeof(ReadOnlyMemory<byte>)],
            modifiers: null) ??
        throw new MissingMethodException(
            typeof(RemoteViewerClient).FullName,
            "SendControlAsync(ReadOnlyMemory<byte>)");

    private static readonly PhysicalKey LeftWindows =
        new((int)Keys.LWin, 0x5B, Extended: true);
    private static readonly PhysicalKey LeftControl =
        new((int)Keys.ControlKey, 0x1D, Extended: false);
    private static readonly PhysicalKey LeftShift =
        new((int)Keys.ShiftKey, 0x2A, Extended: false);
    private static readonly PhysicalKey RightControl =
        new((int)Keys.ControlKey, 0x1D, Extended: true);
    private static readonly PhysicalKey LeftAlt =
        new((int)Keys.Menu, 0x38, Extended: false);
    private static readonly PhysicalKey RightAlt =
        new((int)Keys.Menu, 0x38, Extended: true);
    private static readonly PhysicalKey MainEnter =
        new((int)Keys.Enter, 0x1C, Extended: false);
    private static readonly PhysicalKey NumpadEnter =
        new((int)Keys.Enter, 0x1C, Extended: true);
    private static readonly PhysicalKey KeyA =
        new((int)Keys.A, 0x1E, Extended: false);
    private static readonly PhysicalKey Key1 =
        new((int)Keys.D1, 0x02, Extended: false);
    private static readonly PhysicalKey KeyBackspace =
        new((int)Keys.Back, 0x0E, Extended: false);
    private static readonly PhysicalKey KeyC =
        new((int)Keys.C, 0x2E, Extended: false);
    private static readonly PhysicalKey KeyEscape =
        new((int)Keys.Escape, 0x01, Extended: false);
    private static readonly PhysicalKey KeyF4 =
        new((int)Keys.F4, 0x3E, Extended: false);
    private static readonly PhysicalKey KeyH =
        new((int)Keys.H, 0x23, Extended: false);
    private static readonly PhysicalKey KeyI =
        new((int)Keys.I, 0x17, Extended: false);
    private static readonly PhysicalKey KeyLeft =
        new((int)Keys.Left, 0x4B, Extended: true);
    private static readonly PhysicalKey KeyN =
        new((int)Keys.N, 0x31, Extended: false);
    private static readonly PhysicalKey KeyO =
        new((int)Keys.O, 0x18, Extended: false);
    private static readonly PhysicalKey KeyR =
        new((int)Keys.R, 0x13, Extended: false);
    private static readonly PhysicalKey KeyRight =
        new((int)Keys.Right, 0x4D, Extended: true);
    private static readonly PhysicalKey KeySpace =
        new((int)Keys.Space, 0x39, Extended: false);
    private static readonly PhysicalKey KeyS =
        new((int)Keys.S, 0x1F, Extended: false);
    private static readonly PhysicalKey KeyV =
        new((int)Keys.V, 0x2F, Extended: false);
    private static readonly PhysicalKey KeyZ =
        new((int)Keys.Z, 0x2C, Extended: false);

    private readonly ITestOutputHelper _output;

    public WindowsRemoteKeyboardRealMachineTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void NativeViewerKeyboardMessagesUseFourteenBytePhysicalProtocol()
    {
        PhysicalKey[] keys =
        [
            LeftWindows,
            LeftControl,
            RightControl,
            LeftShift,
            LeftAlt,
            RightAlt,
            MainEnter,
            NumpadEnter,
            Key1,
            KeyA,
            KeyBackspace,
            KeyC,
            KeyEscape,
            KeyH,
            KeyI,
            KeyLeft,
            KeyN,
            KeyO,
            KeyR,
            KeyRight,
            KeyS,
            KeySpace,
            KeyV,
            KeyZ
        ];

        foreach (PhysicalKey key in keys)
        {
            foreach (bool keyUp in new[] { false, true })
            {
                int messageId = keyUp
                    ? RemoteViewerWindow.WmKeyUp
                    : RemoteViewerWindow.WmKeyDown;
                Assert.True(
                    RemoteViewerWindow
                        .TryCreateRawKeyboardCommand(
                            messageId,
                            key.VirtualKey,
                            BuildKeyboardLParam(
                                key.ScanCode,
                                key.Extended,
                                keyUp),
                            out RemoteInputCommand command));

                Assert.Equal(
                    keyUp
                        ? RemoteInputKind.KeyUp
                        : RemoteInputKind.KeyDown,
                    command.Kind);
                Assert.NotEqual(
                    RemoteInputKind.TextInput,
                    command.Kind);
                Assert.Equal(
                    key.VirtualKey,
                    command.Data);
                Assert.Equal(
                    key.ScanCode,
                    command.X);
                Assert.Equal(
                    (int)key.Flags,
                    command.Y);

                byte[] payload =
                    RemoteMessageCodec.EncodeInput(command);
                Assert.Equal(
                    RemoteMessageCodec.InputPayloadLength,
                    payload.Length);
                Assert.Equal(14, payload.Length);
                Assert.Equal(
                    (byte)command.Kind,
                    payload[0]);
                Assert.Equal(
                    key.ScanCode,
                    BinaryPrimitives
                        .ReadInt32LittleEndian(
                            payload.AsSpan(2, 4)));
                Assert.Equal(
                    (int)key.Flags,
                    BinaryPrimitives
                        .ReadInt32LittleEndian(
                            payload.AsSpan(6, 4)));
                Assert.Equal(
                    key.VirtualKey,
                    BinaryPrimitives
                        .ReadInt32LittleEndian(
                            payload.AsSpan(10, 4)));
                Assert.Equal(
                    command,
                    RemoteMessageCodec.DecodeInput(
                        payload));
            }
        }
    }

    [Fact]
    public void KeyboardEntityLaunchCommandFitsWindowsRunDialog()
    {
        string token =
            new('a', 32);
        string path =
            $"%TEMP%\\rdkbd-{token}.txt";
        string windowTitle =
            $"rdkbd-{token}";
        string[] commands =
        [
            $"cmd.exe /d /c echo RD-READY-{token}>\"{path}\"",
            $"notepad.exe \"{path}\"",
            "powershell.exe -NoP -W Hidden -C " +
                "\"gps notepad|? MainWindowTitle -like " +
                $"'*{windowTitle}*'|spps -Force\"",
            $"cmd.exe /d /c del /f /q \"{path}\""
        ];

        Assert.All(
            commands,
            command =>
                Assert.True(
                    command.Length < 260,
                    $"The keyboard entity launch command is " +
                    $"{command.Length} characters and may be truncated " +
                    "by the Windows Run dialog."));
    }

    [RemoteDeskKeyboardRealMachineFact]
    [Trait("Category", "RealMachine")]
    [Trait("Category", "KeyboardEntity")]
    public async Task PhysicalKeyboardSupportsClipboardImeAndDisconnectRelease()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "The interactive keyboard entity probe requires Windows.");
        string password =
            Environment.GetEnvironmentVariable(
                PasswordVariable) ??
            throw new InvalidOperationException(
                $"{PasswordVariable} must be set.");
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"{PasswordVariable} must not be empty.");
        }

        string token =
            ReadEntityToken();
        string notepadReadyMarker =
            $"RD-READY-{token}";
        string remoteScratchPath =
            $"%TEMP%\\rdkbd-{token}.txt";
        string createScratchCommand =
            $"cmd.exe /d /c echo {notepadReadyMarker}>" +
            $"\"{remoteScratchPath}\"";
        string notepadLaunchCommand =
            $"notepad.exe \"{remoteScratchPath}\"";
        string notepadWindowTitle =
            $"rdkbd-{token}";
        string terminateNotepadCommand =
            "powershell.exe -NoP -W Hidden -C " +
            "\"gps notepad|? MainWindowTitle -like " +
            $"'*{notepadWindowTitle}*'|spps -Force\"";
        string deleteScratchCommand =
            $"cmd.exe /d /c del /f /q \"{remoteScratchPath}\"";
        string leftPasteMarker =
            $"RD-LEFT-{token}";
        string rightPasteMarker =
            $"RD-RIGHT-{token}";
        string releaseMarker =
            $"RD-RELEASE-{token}-";
        bool fixturePrepared =
            string.Equals(
                Environment.GetEnvironmentVariable(
                    PreparedVariable),
                "1",
                StringComparison.Ordinal);
        string localClipboardBefore =
            await ClipboardTextService.GetTextAsync();
        string? remoteClipboardBefore = null;
        bool remoteNotepadOpened =
            fixturePrepared;
        var remoteImeRestoreActions =
            new List<RemoteImeToggle>();
        RemoteViewerClient? activeClient = null;
        KeyboardViewerWindowHost? viewerHost = null;

        try
        {
            activeClient =
                await ConnectReadyClientAsync(
                    password);
            remoteClipboardBefore =
                await ReadRemoteClipboardTextAsync(
                    activeClient);

            if (!fixturePrepared)
            {
                await SetRemoteClipboardTextAsync(
                    activeClient,
                    createScratchCommand);
                await SendChordAsync(
                    activeClient,
                    LeftWindows,
                    KeyR);
                await Task.Delay(250);
                await SendChordAsync(
                    activeClient,
                    LeftControl,
                    KeyV);
                await SendStrokeAsync(
                    activeClient,
                    MainEnter);
                await Task.Delay(500);

                await SetRemoteClipboardTextAsync(
                    activeClient,
                    notepadLaunchCommand);
                await SendChordAsync(
                    activeClient,
                    LeftWindows,
                    KeyR);
                await Task.Delay(250);
                await SendChordAsync(
                    activeClient,
                    LeftControl,
                    KeyV);
                await SendStrokeAsync(
                    activeClient,
                    MainEnter);
                remoteNotepadOpened = true;
            }

            // Avoid sending readiness shortcuts into the previous remote
            // foreground window while Notepad is still creating its first
            // top-level window. Polling below remains the authoritative gate
            // for slower machines.
            await Task.Delay(900);
            viewerHost =
                await KeyboardViewerWindowHost
                    .StartAsync(
                        activeClient);
            Assert.True(
                viewerHost.LocalImeDisabled,
                "The local viewer surface must not compose IME text or " +
                "substitute Unicode input.");
            Assert.True(
                viewerHost.SystemKeyboardCaptureActive,
                "The real viewer must own the system keyboard hook so " +
                "Windows-reserved shortcuts can reach the remote desktop.");
            await WaitForRemoteEditorReadyAsync(
                viewerHost,
                activeClient,
                notepadReadyMarker);
            await ClearRemoteEditorAsync(
                viewerHost,
                activeClient);

            await SetRemoteClipboardTextAsync(
                activeClient,
                leftPasteMarker);
            await SendViewerChordAsync(
                viewerHost,
                activeClient,
                LeftControl,
                KeyV);
            await SendViewerStrokeAsync(
                viewerHost,
                activeClient,
                MainEnter);

            await SetRemoteClipboardTextAsync(
                activeClient,
                rightPasteMarker);
            await SendViewerChordAsync(
                viewerHost,
                activeClient,
                RightControl,
                KeyV);
            await SendViewerStrokeAsync(
                viewerHost,
                activeClient,
                NumpadEnter);
            await SendViewerStrokeAsync(
                viewerHost,
                activeClient,
                KeyZ);
            await SendViewerStrokeAsync(
                viewerHost,
                activeClient,
                LeftAlt);
            await SendViewerStrokeAsync(
                viewerHost,
                activeClient,
                RightAlt);
            await SendViewerStrokeAsync(
                viewerHost,
                activeClient,
                KeyEscape);

            await SendViewerChordAsync(
                viewerHost,
                activeClient,
                RightControl,
                KeyA);
            await SendViewerChordAsync(
                viewerHost,
                activeClient,
                LeftControl,
                KeyC);
            string copiedText =
                await WaitForRemoteClipboardAsync(
                    activeClient,
                    text =>
                        text.Contains(
                            leftPasteMarker,
                            StringComparison.Ordinal) &&
                        text.Contains(
                            rightPasteMarker,
                            StringComparison.Ordinal),
                    "Physical Ctrl+C did not copy both Ctrl+V markers.");
            string expectedClipboardText =
                leftPasteMarker +
                "\r\n" +
                rightPasteMarker +
                "\r\nz";
            Assert.True(
                string.Equals(
                    expectedClipboardText,
                    copiedText,
                    StringComparison.OrdinalIgnoreCase),
                "The physical main/numpad Enter and Z sequence did not " +
                "produce the exact expected remote text. " +
                $"Expected length {expectedClipboardText.Length}; " +
                $"actual length {copiedText.Length}.");
            Assert.True(
                copiedText.IndexOf(
                    leftPasteMarker,
                    StringComparison.Ordinal) <
                copiedText.IndexOf(
                    rightPasteMarker,
                    StringComparison.Ordinal));
            Assert.Contains(
                "\r\n",
                copiedText,
                StringComparison.Ordinal);

            string? pinyinText = null;
            for (int attempt = 0;
                 attempt < 4 &&
                 pinyinText is null;
                 attempt++)
            {
                // Traverse all four combinations of Windows keyboard layout
                // and IME language mode without assuming the remote session's
                // starting state: 00 -> 01 -> 11 -> 10.
                if (attempt == 2)
                {
                    await SwitchRemoteInputMethodThroughViewerAsync(
                        viewerHost,
                        activeClient,
                        addSettlingDelay: true);
                    remoteImeRestoreActions.Add(
                        RemoteImeToggle.Layout);
                }

                bool toggledPinyinMode =
                    attempt is 1 or 2 or 3;
                if (toggledPinyinMode)
                {
                    await SendViewerChordAsync(
                        viewerHost,
                        activeClient,
                        LeftControl,
                        KeySpace);
                    remoteImeRestoreActions.Add(
                        RemoteImeToggle.ControlSpace);
                    await Task.Delay(250);
                }

                await ClearRemoteEditorAsync(
                    viewerHost,
                    activeClient);
                await SendViewerTextKeysAsync(
                    viewerHost,
                    activeClient,
                    KeyN,
                    KeyI,
                    KeyH,
                    KeyA,
                    KeyO);
                await Task.Delay(350);
                await SendViewerStrokeAsync(
                    viewerHost,
                    activeClient,
                    KeySpace);
                await Task.Delay(350);
                string candidate =
                    await CopyRemoteEditorAsync(
                        viewerHost,
                        activeClient);
                _output.WriteLine(
                    "IME attempt {0}: clipboardLength={1}; " +
                        "containsAsciiPinyin={2}; containsChinese={3}",
                    attempt,
                    candidate.Length,
                    candidate.Contains(
                        "nihao",
                        StringComparison.OrdinalIgnoreCase),
                    candidate.Contains(
                        "你好",
                        StringComparison.Ordinal));
                if (candidate.Contains(
                        "你好",
                        StringComparison.Ordinal))
                {
                    pinyinText = candidate;
                }
            }

            Assert.NotNull(
                pinyinText);
            Assert.Contains(
                "你好",
                pinyinText!,
                StringComparison.Ordinal);

            await ClearRemoteEditorAsync(
                viewerHost,
                activeClient);
            await SetRemoteClipboardTextAsync(
                activeClient,
                releaseMarker);
            await SendViewerCommandAsync(
                viewerHost,
                activeClient,
                RightControl.Down);
            await SendViewerCommandAsync(
                viewerHost,
                activeClient,
                KeyV.Down,
                KeyV.Up);
            await activeClient.DisconnectAsync();
            await viewerHost.ReleaseLocalKeyAsync(
                RightControl);
            await viewerHost.DisposeAsync();
            viewerHost = null;
            activeClient.Dispose();
            activeClient = null;

            await Task.Delay(400);
            activeClient =
                await ConnectReadyClientAsync(
                    password);
            viewerHost =
                await KeyboardViewerWindowHost
                    .StartAsync(
                        activeClient);
            await SendViewerStrokeAsync(
                viewerHost,
                activeClient,
                Key1);
            string afterReconnect =
                await CopyRemoteEditorAsync(
                    viewerHost,
                    activeClient);
            int reconnectMarkerIndex =
                afterReconnect.IndexOf(
                    releaseMarker,
                    StringComparison.Ordinal);
            string reconnectSuffixCodePoints =
                reconnectMarkerIndex < 0
                    ? "marker-missing"
                    : string.Join(
                        ",",
                        afterReconnect
                            .Skip(
                                reconnectMarkerIndex +
                                    releaseMarker.Length)
                            .Take(8)
                            .Select(character =>
                                ((int)character).ToString(
                                    CultureInfo.InvariantCulture)));
            _output.WriteLine(
                "Reconnect release probe: clipboardLength={0}; " +
                    "markerIndex={1}; suffixCodePoints={2}",
                afterReconnect.Length,
                reconnectMarkerIndex,
                reconnectSuffixCodePoints);
            Assert.Contains(
                releaseMarker + "1",
                afterReconnect,
                StringComparison.Ordinal);

            _output.WriteLine(
                "Keyboard entity passed through the real viewer: " +
                "clipboardLength={0}; imeText={1}; reconnectLength={2}",
                copiedText.Length,
                pinyinText,
                afterReconnect.Length);
        }
        finally
        {
            if (viewerHost is not null &&
                activeClient?.IsConnected == true &&
                remoteImeRestoreActions.Count > 0)
            {
                try
                {
                    while (remoteImeRestoreActions
                               .Count > 0)
                    {
                        int index =
                            remoteImeRestoreActions
                                .Count - 1;
                        if (remoteImeRestoreActions[
                                index] ==
                            RemoteImeToggle.Layout)
                        {
                            await SwitchRemoteInputMethodThroughViewerAsync(
                                viewerHost,
                                activeClient,
                                addSettlingDelay: true);
                        }
                        else
                        {
                            await SendViewerChordAsync(
                                viewerHost,
                                activeClient,
                                LeftControl,
                                KeySpace);
                        }

                        remoteImeRestoreActions
                            .RemoveAt(index);
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine(
                        "Could not restore remote IME through the real " +
                        $"viewer; cleanup will use direct input: {ex}");
                }
            }

            if (viewerHost is not null)
            {
                try
                {
                    await viewerHost.ReleaseAllLocalKeysAsync();
                }
                catch
                {
                }

                await viewerHost.DisposeAsync();
                viewerHost = null;
            }

            if (remoteNotepadOpened &&
                (activeClient is null ||
                 !activeClient.IsConnected))
            {
                activeClient?.Dispose();
                activeClient = null;
                try
                {
                    activeClient =
                        await ConnectReadyClientAsync(
                            password);
                }
                catch
                {
                }
            }

            if (activeClient is not null)
            {
                try
                {
                    if (activeClient.IsConnected)
                    {
                        while (remoteImeRestoreActions
                                   .Count > 0)
                        {
                            int index =
                                remoteImeRestoreActions
                                    .Count - 1;
                            if (remoteImeRestoreActions[
                                    index] ==
                                RemoteImeToggle.Layout)
                            {
                                await SendChordAsync(
                                    activeClient,
                                    LeftWindows,
                                    KeySpace);
                            }
                            else
                            {
                                await SendChordAsync(
                                    activeClient,
                                    LeftControl,
                                    KeySpace);
                            }

                            remoteImeRestoreActions
                                .RemoveAt(index);
                        }

                        if (remoteNotepadOpened)
                        {
                            await RunRemoteCommandAsync(
                                activeClient,
                                terminateNotepadCommand,
                                settleMilliseconds: 600);
                            await RunRemoteCommandAsync(
                                activeClient,
                                deleteScratchCommand,
                                settleMilliseconds: 500);
                            remoteNotepadOpened =
                                false;
                        }

                        if (remoteClipboardBefore is not null)
                        {
                            await SetRemoteClipboardTextAsync(
                                activeClient,
                                remoteClipboardBefore);
                        }

                        await activeClient
                            .DisconnectAsync();
                    }
                }
                catch
                {
                }

                activeClient.Dispose();
            }

            await ClipboardTextService.SetTextAsync(
                localClipboardBefore);
        }
    }

    private static async Task<RemoteViewerClient>
        ConnectReadyClientAsync(string password)
    {
        var deviceReceived =
            new TaskCompletionSource<
                RemoteDeviceDescriptor>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        client.DeviceInfoReceived +=
            device =>
                deviceReceived.TrySetResult(
                    device);
        try
        {
            await client.ConnectAsync(
                Host,
                Port,
                password,
                ViewerVideoMode.StableJpeg);
            RemoteDeviceDescriptor device =
                await deviceReceived.Task
                    .WaitAsync(ConnectionTimeout);
            Assert.Equal(
                RemoteDevicePlatforms.Windows,
                device.Platform);
            Assert.True(
                device.Capabilities.HasFlag(
                    RemoteDeviceCapabilities
                        .InputControl));
            Assert.True(
                device.Capabilities.HasFlag(
                    RemoteDeviceCapabilities
                        .ClipboardText));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task SendViewerStrokeAsync(
        KeyboardViewerWindowHost viewerHost,
        RemoteViewerClient client,
        PhysicalKey key)
    {
        await SendViewerCommandAsync(
            viewerHost,
            client,
            key.Down,
            key.Up);
    }

    private static async Task SendViewerChordAsync(
        KeyboardViewerWindowHost viewerHost,
        RemoteViewerClient client,
        PhysicalKey modifier,
        PhysicalKey key)
    {
        await SendViewerCommandAsync(
            viewerHost,
            client,
            modifier.Down,
            key.Down,
            key.Up,
            modifier.Up);
    }

    private static async Task SendViewerTextKeysAsync(
        KeyboardViewerWindowHost viewerHost,
        RemoteViewerClient client,
        params PhysicalKey[] keys)
    {
        foreach (PhysicalKey key in keys)
        {
            await SendViewerStrokeAsync(
                viewerHost,
                client,
                key);
            await Task.Delay(35);
        }
    }

    private static async Task SendViewerCommandAsync(
        KeyboardViewerWindowHost viewerHost,
        RemoteViewerClient client,
        params RemoteInputCommand[] commands)
    {
        await viewerHost.InjectPhysicalAsync(
            commands);
        await Task.Delay(90);
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(4));
        await client.FlushInputAsync(
            timeout.Token);
        await Task.Delay(75);
    }

    private static async Task
        SwitchRemoteInputMethodThroughViewerAsync(
            KeyboardViewerWindowHost viewerHost,
            RemoteViewerClient client,
            bool addSettlingDelay)
    {
        await viewerHost
            .ClickRemoteInputMethodButtonAsync();
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(4));
        await client.FlushInputAsync(
            timeout.Token);
        if (addSettlingDelay)
        {
            await Task.Delay(400);
        }
    }

    private static async Task ClearRemoteEditorAsync(
        KeyboardViewerWindowHost viewerHost,
        RemoteViewerClient client)
    {
        await SendViewerChordAsync(
            viewerHost,
            client,
            LeftControl,
            KeyA);
        await SendViewerStrokeAsync(
            viewerHost,
            client,
            KeyBackspace);
    }

    private static async Task<string> CopyRemoteEditorAsync(
        KeyboardViewerWindowHost viewerHost,
        RemoteViewerClient client)
    {
        await SendViewerChordAsync(
            viewerHost,
            client,
            LeftControl,
            KeyA);
        await SendViewerChordAsync(
            viewerHost,
            client,
            LeftControl,
            KeyC);
        await Task.Delay(200);
        return await ReadRemoteClipboardTextAsync(
            client);
    }

    private static async Task WaitForRemoteEditorReadyAsync(
        KeyboardViewerWindowHost viewerHost,
        RemoteViewerClient client,
        string readyMarker)
    {
        long startedAt =
            Stopwatch.GetTimestamp();
        string latest = string.Empty;
        while (Stopwatch.GetElapsedTime(
                   startedAt) <
               ConnectionTimeout)
        {
            latest =
                await CopyRemoteEditorAsync(
                    viewerHost,
                    client);
            if (string.Equals(
                    latest.TrimEnd(
                        '\r',
                        '\n'),
                    readyMarker,
                    StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(150);
        }

        throw new Xunit.Sdk.XunitException(
            "Notepad did not expose the unique scratch-file readiness " +
            $"marker within {ConnectionTimeout.TotalSeconds:F0}s. " +
            $"Last clipboard length: {latest.Length}.");
    }

    private static async Task RunRemoteCommandAsync(
        RemoteViewerClient client,
        string command,
        int settleMilliseconds)
    {
        await SetRemoteClipboardTextAsync(
            client,
            command);
        await SendChordAsync(
            client,
            LeftWindows,
            KeyR);
        await Task.Delay(250);
        await SendChordAsync(
            client,
            LeftControl,
            KeyV);
        await SendStrokeAsync(
            client,
            MainEnter);
        await Task.Delay(
            settleMilliseconds);
    }

    private static async Task SendStrokeAsync(
        RemoteViewerClient client,
        PhysicalKey key)
    {
        await SendCommandsAsync(
            client,
            key.Down,
            key.Up);
    }

    private static async Task SendChordAsync(
        RemoteViewerClient client,
        PhysicalKey modifier,
        PhysicalKey key)
    {
        await SendCommandsAsync(
            client,
            modifier.Down,
            key.Down,
            key.Up,
            modifier.Up);
    }

    private static async Task SendCommandsAsync(
        RemoteViewerClient client,
        params RemoteInputCommand[] commands)
    {
        await client.SendInputsAsync(commands);
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(4));
        await client.FlushInputAsync(
            timeout.Token);
        await Task.Delay(75);
    }

    private static async Task<string>
        ReadRemoteClipboardTextAsync(
            RemoteViewerClient client)
    {
        var completion =
            new TaskCompletionSource<string>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        void ClipboardStatus(string message)
        {
            if (string.Equals(
                    message,
                    "已读取远程剪贴板到本机。",
                    StringComparison.Ordinal))
            {
                completion.TrySetResult(
                    message);
                return;
            }

            if (message.Contains(
                    "剪贴板",
                    StringComparison.Ordinal) &&
                (message.Contains(
                        "失败",
                        StringComparison.Ordinal) ||
                 message.Contains(
                        "超时",
                        StringComparison.Ordinal)))
            {
                completion.TrySetException(
                    new InvalidOperationException(
                        message));
            }
        }

        client.ClipboardStatusReceived +=
            ClipboardStatus;
        try
        {
            await client.ReadRemoteClipboardAsync(
                notifyRequest: false);
            await completion.Task.WaitAsync(
                ClipboardTimeout);
            return await ClipboardTextService
                .GetTextAsync();
        }
        finally
        {
            client.ClipboardStatusReceived -=
                ClipboardStatus;
        }
    }

    private static async Task<string>
        WaitForRemoteClipboardAsync(
            RemoteViewerClient client,
            Func<string, bool> predicate,
            string failureMessage)
    {
        long startedAt =
            Stopwatch.GetTimestamp();
        string latest = string.Empty;
        while (Stopwatch.GetElapsedTime(
                   startedAt) <
               ClipboardTimeout)
        {
            latest =
                await ReadRemoteClipboardTextAsync(
                    client);
            if (predicate(latest))
            {
                return latest;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException(
            $"{failureMessage} Last clipboard length: {latest.Length}.");
    }

    private static async Task SetRemoteClipboardTextAsync(
        RemoteViewerClient client,
        string text)
    {
        var completion =
            new TaskCompletionSource<(
                bool Success,
                string Message)>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        void ClipboardStatus(string message)
        {
            if (string.Equals(
                    message,
                    "已写入远程剪贴板",
                    StringComparison.Ordinal))
            {
                completion.TrySetResult(
                    (true, message));
            }
            else if (message.StartsWith(
                         "写入远程剪贴板失败：",
                         StringComparison.Ordinal))
            {
                completion.TrySetResult(
                    (false, message));
            }
        }

        client.ClipboardStatusReceived +=
            ClipboardStatus;
        try
        {
            var task =
                SendControlMethod.Invoke(
                    client,
                    [
                        (ReadOnlyMemory<byte>)
                            RemoteMessageCodec
                                .EncodeClipboardSetText(
                                    text)
                    ]) as Task<bool> ??
                throw new InvalidOperationException(
                    "Remote clipboard control invocation returned no task.");
            Assert.True(
                await task,
                "The remote clipboard control message was not sent.");
            (bool success, string message) =
                await completion.Task.WaitAsync(
                    ClipboardTimeout);
            Assert.True(
                success,
                message);
        }
        finally
        {
            client.ClipboardStatusReceived -=
                ClipboardStatus;
        }
    }

    private static int ReadPositiveEnvironmentInteger(
        string variableName,
        int fallback,
        int maximum)
    {
        if (fallback <= 0 || maximum < fallback)
        {
            throw new ArgumentOutOfRangeException(
                maximum < fallback
                    ? nameof(maximum)
                    : nameof(fallback));
        }

        string? value =
            Environment.GetEnvironmentVariable(
                variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed) ||
            parsed <= 0 ||
            parsed > maximum)
        {
            throw new InvalidOperationException(
                $"{variableName} must be an integer between 1 and " +
                $"{maximum}.");
        }

        return parsed;
    }

    private static string ReadEntityToken()
    {
        string? configured =
            Environment.GetEnvironmentVariable(
                TokenVariable);
        if (string.IsNullOrWhiteSpace(
                configured))
        {
            return Guid.NewGuid()
                .ToString("N");
        }

        if (!Guid.TryParseExact(
                configured,
                "N",
                out Guid token))
        {
            throw new InvalidOperationException(
                $"{TokenVariable} must be a 32-character hexadecimal " +
                "GUID without separators.");
        }

        return token.ToString("N");
    }

    private static nint BuildKeyboardLParam(
        int scanCode,
        bool extended,
        bool keyUp)
    {
        long value =
            1L |
            ((long)scanCode << 16);
        if (extended)
        {
            value |= 1L << 24;
        }

        if (keyUp)
        {
            value |=
                (1L << 30) |
                (1L << 31);
        }

        return (nint)value;
    }

    private sealed class KeyboardViewerWindowHost :
        IAsyncDisposable
    {
        private readonly TaskCompletionSource<
            RemoteViewerWindow> _ready =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        private readonly List<RemoteInputCommand>
            _pressedLocalKeys = [];
        private readonly Thread _thread;
        private readonly nint _previousForeground;
        private RemoteViewerWindow? _window;
        private PictureBox? _pictureBox;
        private int _localImeDisabled;
        private int _disposed;

        private KeyboardViewerWindowHost(
            RemoteViewerClient client)
        {
            _previousForeground =
                GetForegroundWindow();
            _thread = new Thread(
                () => RunMessageLoop(client))
            {
                IsBackground = true,
                Name =
                    "RemoteDesk keyboard entity viewer"
            };
            _thread.SetApartmentState(
                ApartmentState.STA);
            _thread.Start();
        }

        public bool LocalImeDisabled =>
            Volatile.Read(
                ref _localImeDisabled) != 0;

        public bool SystemKeyboardCaptureActive =>
            Volatile.Read(ref _window)?
                .SystemKeyboardCaptureActiveForEntityTests ==
            true;

        public static async Task<
            KeyboardViewerWindowHost> StartAsync(
                RemoteViewerClient client)
        {
            var host =
                new KeyboardViewerWindowHost(
                    client);
            try
            {
                await host._ready.Task.WaitAsync(
                    TimeSpan.FromSeconds(10));
                await host.FocusAsync();
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public Task InjectPhysicalAsync(
            IReadOnlyList<
                RemoteInputCommand> commands)
        {
            ArgumentNullException.ThrowIfNull(
                commands);
            return InvokeWindowAsync(
                () =>
                {
                    FocusWindow();
                    foreach (RemoteInputCommand
                             command in commands)
                    {
                        InputInjector.Apply(
                            command,
                            Rectangle.Empty,
                            new Size(1, 1));
                        ObserveLocalInput(
                            command);
                    }
                });
        }

        public Task ClickRemoteInputMethodButtonAsync() =>
            InvokeWindowAsync(
                () =>
                {
                    RemoteViewerWindow window =
                        _window ??
                        throw new InvalidOperationException(
                            "The real viewer window is unavailable.");
                    Button button =
                        window
                            .RemoteInputMethodButtonForEntityTests;
                    if (!button.Visible ||
                        !button.Enabled)
                    {
                        throw new InvalidOperationException(
                            "The remote input method button is unavailable.");
                    }

                    button.PerformClick();
                });

        public Task ReleaseLocalKeyAsync(
            PhysicalKey key)
        {
            InputInjector.TryReleaseKey(
                key.Down);
            lock (_pressedLocalKeys)
            {
                RemovePressedLocalKey(
                    key.Up);
            }

            return Task.CompletedTask;
        }

        public Task ReleaseAllLocalKeysAsync()
        {
            RemoteInputCommand[] pressed;
            lock (_pressedLocalKeys)
            {
                pressed =
                    _pressedLocalKeys.ToArray();
                _pressedLocalKeys.Clear();
            }

            for (int index =
                     pressed.Length - 1;
                 index >= 0;
                 index--)
            {
                InputInjector.TryReleaseKey(
                    pressed[index]);
            }

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            try
            {
                await ReleaseAllLocalKeysAsync();
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                    ObjectDisposedException)
            {
            }

            RemoteViewerWindow? window =
                Volatile.Read(
                    ref _window);
            if (window is not null &&
                !window.IsDisposed)
            {
                try
                {
                    window.BeginInvoke(
                        new Action(window.Close));
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                        ObjectDisposedException)
                {
                }
            }

            await _stopped.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            _thread.Join(
                TimeSpan.FromSeconds(1));
            if (_previousForeground != 0)
            {
                SetForegroundWindow(
                    _previousForeground);
            }
        }

        private Task FocusAsync() =>
            InvokeWindowAsync(
                FocusWindow);

        private Task InvokeWindowAsync(
            Action action)
        {
            RemoteViewerWindow window =
                Volatile.Read(
                    ref _window) ??
                throw new InvalidOperationException(
                    "The real viewer window is unavailable.");
            var completion =
                new TaskCompletionSource(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
            try
            {
                window.BeginInvoke(
                    new Action(
                        () =>
                        {
                            try
                            {
                                action();
                                completion.TrySetResult();
                            }
                            catch (Exception ex)
                            {
                                completion
                                    .TrySetException(
                                        ex);
                            }
                        }));
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                    ObjectDisposedException)
            {
                completion.TrySetException(
                    ex);
            }

            return completion.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
        }

        private void FocusWindow()
        {
            RemoteViewerWindow window =
                _window ??
                throw new InvalidOperationException(
                    "The real viewer window is unavailable.");
            PictureBox pictureBox =
                _pictureBox ??
                throw new InvalidOperationException(
                    "The real viewer input surface is unavailable.");
            nint foregroundWindow =
                GetForegroundWindow();
            uint foregroundThread =
                foregroundWindow == 0
                    ? 0
                    : GetWindowThreadProcessId(
                        foregroundWindow,
                        out _);
            uint viewerThread =
                GetCurrentThreadId();
            bool attached =
                foregroundThread != 0 &&
                foregroundThread != viewerThread &&
                AttachThreadInput(
                    viewerThread,
                    foregroundThread,
                    true);
            try
            {
                window.TopMost = true;
                window.Show();
                window.BringToFront();
                window.Activate();
                SetForegroundWindow(
                    window.Handle);
                pictureBox.Select();
                pictureBox.Focus();
            }
            finally
            {
                window.TopMost = false;
                if (attached)
                {
                    AttachThreadInput(
                        viewerThread,
                        foregroundThread,
                        false);
                }
            }

            if (GetForegroundWindow() !=
                    window.Handle ||
                !pictureBox.ContainsFocus)
            {
                throw new InvalidOperationException(
                    "The real viewer input surface did not obtain " +
                    "foreground keyboard focus.");
            }
        }

        private void ObserveLocalInput(
            RemoteInputCommand command)
        {
            lock (_pressedLocalKeys)
            {
                if (command.Kind ==
                    RemoteInputKind.KeyDown)
                {
                    if (!_pressedLocalKeys.Any(
                            pressed =>
                                SamePhysicalKey(
                                    pressed,
                                    command)))
                    {
                        _pressedLocalKeys.Add(
                            command);
                    }

                    return;
                }

                if (command.Kind ==
                    RemoteInputKind.KeyUp)
                {
                    RemovePressedLocalKey(
                        command);
                }
            }
        }

        private void RemovePressedLocalKey(
            RemoteInputCommand command)
        {
            int index =
                _pressedLocalKeys.FindLastIndex(
                    pressed =>
                        SamePhysicalKey(
                            pressed,
                            command));
            if (index >= 0)
            {
                _pressedLocalKeys.RemoveAt(
                    index);
            }
        }

        private static bool SamePhysicalKey(
            RemoteInputCommand left,
            RemoteInputCommand right) =>
            left.Data == right.Data &&
            left.X == right.X &&
            left.Y == right.Y;

        private void RunMessageLoop(
            RemoteViewerClient client)
        {
            try
            {
                using var window =
                    new RemoteViewerWindow(
                        client,
                        "RemoteDesk 实体键盘/输入法验证",
                        inputEnabled: true,
                        clipboardTextEnabled: true,
                        filePasteEnabled: false,
                        fileDropPasteEnabled: false,
                        remoteFilePullEnabled: false,
                        isAndroidRemote: false)
                    {
                        ShowInTaskbar = true
                    };
                window.SetRemotePlatform(
                    RemoteDevicePlatforms.Windows);
                window
                    .EnableInjectedKeyboardCaptureForEntityTests();
                PictureBox pictureBox =
                    window.Controls
                        .OfType<PictureBox>()
                        .Single();
                _window = window;
                _pictureBox = pictureBox;
                Volatile.Write(
                    ref _localImeDisabled,
                    pictureBox.ImeMode ==
                        ImeMode.Disable
                        ? 1
                        : 0);
                window.Shown +=
                    (_, _) =>
                    {
                        window.Activate();
                        pictureBox.Focus();
                        _ready.TrySetResult(
                            window);
                    };
                Application.Run(
                    window);
            }
            catch (Exception ex)
            {
                _ready.TrySetException(
                    ex);
            }
            finally
            {
                _pictureBox = null;
                _window = null;
                _stopped.TrySetResult();
            }
        }

        [System.Runtime.InteropServices.DllImport(
            "user32.dll")]
        [return:
            System.Runtime.InteropServices
                .MarshalAs(
                    System.Runtime.InteropServices
                        .UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(
            nint window);

        [System.Runtime.InteropServices.DllImport(
            "user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            nint window,
            out uint processId);

        [System.Runtime.InteropServices.DllImport(
            "user32.dll")]
        [return:
            System.Runtime.InteropServices
                .MarshalAs(
                    System.Runtime.InteropServices
                        .UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(
            uint attach,
            uint attachTo,
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.Bool)]
            bool attachInput);

        [System.Runtime.InteropServices.DllImport(
            "user32.dll")]
        private static extern nint GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }

    private enum RemoteImeToggle
    {
        Layout,
        ControlSpace
    }

    private readonly record struct PhysicalKey(
        int VirtualKey,
        int ScanCode,
        bool Extended)
    {
        public RemoteKeyboardFlags Flags =>
            RemoteKeyboardFlags.HasScanCode |
            (Extended
                ? RemoteKeyboardFlags.Extended
                : RemoteKeyboardFlags.None);

        public RemoteInputCommand Down =>
            RemoteInputCommand.KeyDown(
                VirtualKey,
                ScanCode,
                Flags);

        public RemoteInputCommand Up =>
            RemoteInputCommand.KeyUp(
                VirtualKey,
                ScanCode,
                Flags);
    }

}
