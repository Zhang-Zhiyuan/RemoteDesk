# RemoteDesk

一个自用的远程桌面工具，支持 Windows、Linux 和 Android。局域网内可以直接连接，跨网络可以走自己的公网中继服务器。

安装包在 [GitHub Releases](https://github.com/Zhang-Zhiyuan/RemoteDesk/releases)，仓库的 [release](release/) 目录也保留成品。Windows 解压 ZIP 后运行 `RemoteDesk.exe`，Android 安装 APK，Linux 选择对应安装包。

## 功能

- IP 直连、局域网设备发现、自建中继与在线设备列表。
- 接入中继后自动更新本机 IP / 端口，也可手动上报；在线列表可查看最新地址并填入直连。IP 变化不影响按设备 ID 中继连接。
- H.264 硬件编解码，也可以切换到 JPEG 模式；硬件加速取决于设备和驱动。
- 多屏切换、原始比例显示、文本剪贴板和文件传输。
- 三端均可新增设备、保存连接、修改备注和删除记录；同一设备的重复记录会自动合并。
- Windows 支持托盘驻留、开机自启、远端更新和可选的锁屏控制。

## 连接

1. 在被控设备上打开 RemoteDesk，设置访问口令，启动被控端。
2. 在控制设备上选择发现的设备，或点击“新增设备”填写 IP 和口令。端口可以自动探测，也可手动指定。
3. 点击连接。

默认连接端口是 **TCP 56565**，局域网发现使用 **UDP 56566**。

同网段可自动发现 IP；跨网段需填写 IP / 主机名，或使用中继在线列表。同一设备换地址后，连接成功会合并记录并保留备注；仅名称相同不会合并。旧版本没有设备标识时，不能可靠识别换 IP 的机器。

两台设备不在同一个网络时，可以在 Windows 的“公网中继”页面填写自己的 Linux 服务器信息，自动部署中继服务。其他设备接入同一中继后，就能从在线列表选择目标。具体配置见[中继部署说明](docs/PrivateRelay.md)。

同一台设备一次只接受一个控制端，后连接且认证成功的控制端会接管，前一个连接会断开。

## 使用前注意

- Android 8.0 及以上可安装。被控需要录屏授权和无障碍服务；锁屏或重启可能结束录屏，需要重新授权。
- Linux 推荐使用 X11 / Xorg 桌面，Wayland 暂不支持完整桌面控制。
- Windows 锁屏控制需先安装辅助服务并以管理员运行，适用范围见[锁屏控制说明](docs/WindowsLockScreen.md)；不支持首次登录前接管和 Ctrl+Alt+Del。
- Android 目前只支持接收文件，不能从手机向外发送文件。
- 请使用不易猜的访问口令。跨公网连接使用中继，不要直接把被控端口暴露到公网。

## 构建

需要自行编译时，以下命令均从仓库根目录执行。

### Windows

安装 .NET SDK **8.0.424**，然后运行：

```powershell
dotnet run --project src/RemoteDesk/RemoteDesk.csproj -c Release
```

Windows 被控端的 H.264 硬件编码还需要支持相应编码器的 FFmpeg；没有时会回退到 JPEG。

### Linux

需要 Python 3.12 和图形桌面：

```bash
python3 scripts/linux/remotedesk_linux_app.py
```

Debian / Ubuntu 缺少运行依赖时，程序会列出缺项，经确认和系统授权后安装。便携包的使用方式见 [Linux 安装说明](docs/Linux-SystemPackage.md)。

### Android

需要 JDK 17 和 Android SDK 36。在 Windows 上构建调试 APK：

```powershell
.\src\RemoteDesk.Android\gradlew.bat -p src/RemoteDesk.Android assembleDebug
```

APK 位于 `src/RemoteDesk.Android/app/build/outputs/apk/debug/`。Linux / macOS 可进入 `src/RemoteDesk.Android` 后运行 `./gradlew assembleDebug`。

## 文档

[Android 开发说明](src/RemoteDesk.Android/README.md) · [打包与发布](artifacts/README.md) · [协议说明](docs/RemoteDesk-Protocol.md)

项目许可证尚未确定；第三方依赖的许可见 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)。
