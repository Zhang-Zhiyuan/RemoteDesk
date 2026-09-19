# RemoteDesk

一个自用的远程桌面工具，支持 Windows、Linux 和 Android。局域网内可以直接连接，跨网络可以走自己的公网中继服务器。

安装包在 [GitHub Releases](https://github.com/Zhang-Zhiyuan/RemoteDesk/releases)，仓库的 [release](release/) 目录也保留成品。Windows 解压 ZIP 后运行 `RemoteDesk.exe`，Android 安装 APK，Linux 选择对应安装包。

## 功能

- IP 直连、局域网设备发现、自建中继与在线设备列表。
- 接入中继后自动更新本机 IP / 端口，也可手动上报；在线列表可查看最新地址并填入直连。IP 变化不影响按设备 ID 中继连接。
- H.264 硬件编解码，也可以切换到 JPEG 模式；硬件加速取决于设备和驱动。Windows 缺少 FFmpeg 时会在后台下载校验过的组件（约 104 MB），安装完成自动重试，期间仍可连接。
- 公网带宽不足时，Windows / Linux 被控端可自动降低传输尺寸至最高 1080p，保留硬编；不会改变电脑本身的屏幕分辨率。Windows 查看端提供可选的 GPU 缩放清晰增强。
- Windows 控制 Windows 时还可开启“原生补清”，空闲时补充鼠标附近的原始文字像素。默认关闭，需要兼容的 GPU；带宽不足时暂停补清。
- 多屏切换、原始比例显示、文本剪贴板和文件传输。
- 三端均可新增设备、保存连接、修改备注和删除记录；同一设备的重复记录会自动合并。
- 中继在线设备可设置“共享名称”，同一服务器上的其他客户端刷新后可见；换 IP 或重连不会丢失。
- Windows 支持托盘驻留、开机自启、远端更新和可选的锁屏控制。
- Windows 版启动时默认申请管理员权限，无需右键“以管理员身份运行”；系统 UAC 确认仍保留。开机自启使用管理员计划任务。

## 连接

1. 在被控设备上打开 RemoteDesk，设置设备密钥，启动被控端。
2. 在控制设备上选择发现的设备，或点击“新增设备”填写 IP 和设备密钥。端口可以自动探测，也可手动指定。
3. 点击连接。

默认连接端口是 **TCP 56565**，局域网发现使用 **UDP 56566**。

同网段可自动发现 IP；跨网段需填写 IP / 主机名，或使用中继在线列表。同一设备换地址后，连接成功会合并记录并保留备注；仅名称相同不会合并。旧版本没有设备标识时，不能可靠识别换 IP 的机器。

跨网络时，先在 Windows 的“公网中继”页部署自己的 Linux 服务器。三端接入时都填服务器地址和 root 密码，登录后自动记住；从在线列表选设备，再用该设备自己的设备密钥连接。root 密码不保存，也不用手填中继密钥或证书。具体见[中继部署说明](docs/PrivateRelay.md)。

设备密钥在连接成功后按设备分别记住。服务器设置支持退出登录；“允许本机上线”开关立即保存，不必重新登录。Android 首页分为“设备 / 本机被控 / 设置”，Windows 的管理员、自动启动和锁屏控制选项集中在“被控权限设置”。

同一台设备一次只接受一个控制端，后连接且认证成功的控制端会接管，前一个连接会断开。

远控窗口的 Ctrl+C / Ctrl+X 会尝试取回远端文字，Ctrl+V 会先同步本机文字再粘贴。Windows 查看端也支持会话中的右键文本复制粘贴同步，连接两端建议一起更新；文件不会因此自动上传。也可手动发送或取回文字，手机入口在“更多 → 文字剪贴板”。不会在未连接时无条件同步所有复制内容。

发送文件前会确认文件清单和远端接收位置，完成后显示实际保存路径。旧版被控端无法报告目录时会明确提示“位置未确认”。请求粘贴到远端当前窗口，不等于那个窗口已经接受文件。

## 使用前注意

- Android 8.0 及以上可安装。被控需要录屏授权和无障碍服务；锁屏或重启可能结束录屏，需要重新授权。
- Android 10 及以上通常不允许后台应用读取剪贴板；从被控手机取回文字时，可能需要先把 RemoteDesk 切到前台。读取失败不会清空控制端的剪贴板。
- Linux 推荐使用 X11 / Xorg 桌面，Wayland 暂不支持完整桌面控制。
- Windows 锁屏控制需先安装辅助服务并以管理员运行，适用范围见[锁屏控制说明](docs/WindowsLockScreen.md)；不支持首次登录前接管和 Ctrl+Alt+Del。
- 手机远控时可在“更多 → 发送文件”选择文件，确认后发送；也可以接收电脑发来的文件。直连和公网中继都支持。暂不支持从电脑直接读取手机其它应用复制的文件。
- 请使用不易猜的设备密钥。跨公网连接使用中继，不要直接把被控端口暴露到公网。

## 构建

需要自行编译时，以下命令均从仓库根目录执行。

### Windows

安装 .NET SDK **8.0.424**，然后运行：

```powershell
dotnet run --project src/RemoteDesk/RemoteDesk.csproj -c Release
```

Windows 版包含管理员启动清单，调试运行也请使用管理员终端 / IDE。
被控端的 H.264 硬件编码需要 FFmpeg。程序会先找现有安装；缺少时下载固定版本到当前用户的 `RemoteDesk/Dependencies` 目录，不修改系统 PATH。离线时可使用随包的 `Install-RemoteDeskFfmpeg.ps1 -ArchivePath <已下载的压缩包>` 安装；下载失败仍可使用 JPEG。

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
