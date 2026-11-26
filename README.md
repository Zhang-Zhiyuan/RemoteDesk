# RemoteDesk

一个自用的远程桌面工具，支持 Windows、Linux 和 Android。局域网内可以直接连接，跨网络可以走自己的公网中继服务器。

安装包在 [GitHub Releases](https://github.com/Zhang-Zhiyuan/RemoteDesk/releases)，仓库的 [release](release/) 目录也保留成品。Windows 解压 ZIP 后运行 `RemoteDesk.exe`，Android 安装 APK，Linux 选择对应安装包。

仓库内的 Windows EXE 使用 Git LFS；直接下载安装包请用 Releases。

## 功能

- IPv4 / IPv6 直连、局域网设备发现、自建中继与在线设备列表。
- 接入中继后自动更新本机 IP / 端口，也可手动上报；在线列表可查看最新地址并填入直连。IP 变化不影响按设备 ID 中继连接。
- H.264 硬件编解码，硬件加速取决于设备和驱动。Windows 不再提供 JPEG 模式；锁屏时使用 H.264 备用编码。缺少 FFmpeg 时会在后台下载校验过的组件（约 104 MB），安装完成自动重试。
- 公网带宽不足时，Windows / Linux 被控端可自动降低传输尺寸至最高 1080p，保留硬编；不会改变电脑本身的屏幕分辨率。三端查看窗口默认等比放大。
- 远控窗口底部的“画面”可调整显示大小。Windows 硬解放大时默认用“文字优先”。两端均为 Windows 且硬件支持时，还可以打开“静止时补清文字”，取回鼠标附近的原始像素。
- 多屏切换、原始比例显示、文本剪贴板和文件传输。Windows 控制 Windows 时，支持通过“多屏分窗”把远端屏幕各开一个独立窗口（最多 4 块，需两端更新）。
- 三端均可新增设备、保存连接、修改备注和删除记录；同一设备的重复记录会自动合并。
- 中继在线设备可设置“共享名称”，同一服务器上的其他客户端刷新后可见；换 IP 或重连不会丢失。
- SSH 可走 IP 直连或公网中继。点“SSH 直连”可用终端或 VS Code 打开已有 SSH 地址，手机使用内置终端。通过中继连接时，可从设备列表选择 Windows 或运行中的 WSL 发行版。[SSH 使用说明](docs/PrivateRelay.md#ssh-终端)
- Windows 支持托盘驻留、开机自启、远端更新和可选的锁屏控制。
- Windows 版启动时默认申请管理员权限，无需右键“以管理员身份运行”；系统 UAC 确认仍保留。开机自启使用管理员计划任务。

## 连接

1. 在被控设备上打开 RemoteDesk，设置设备密钥，启动被控端。
2. 在控制设备上选择发现的设备，或点击“新增设备”填写 IP 和设备密钥。端口可以自动探测，也可手动指定。
3. 点击连接。

默认连接端口是 **TCP 56565**，局域网发现使用 **UDP 56566**。

IPv6 带端口时写成 `[IPv6地址]:端口`，例如 `[2001:db8::1]:56565`（示例地址不能实际连接）。直连需要两端网络和防火墙允许；中继上报 IPv6 地址需同步更新服务端。没有 IPv6 路由时仍可使用 IPv4。

同网段可自动发现 IP；跨网段需填写 IP / 主机名，或使用中继在线列表。同一设备换地址后，连接成功会合并记录并保留备注；仅名称相同不会合并。旧版本没有设备标识时，不能可靠识别换 IP 的机器。

跨网络时，先在 Windows 的“公网中继”页部署自己的 Linux 服务器。三端接入时都填服务器地址和 root 密码，登录后自动记住；从在线列表选设备，再用该设备自己的设备密钥连接。root 密码不保存，也不用手填中继密钥或证书。具体见[中继部署说明](docs/PrivateRelay.md)。

设备密钥在连接成功后按设备分别记住。服务器设置支持退出登录；“允许本机上线”开关立即保存，不必重新登录。Android 首页分为“设备 / 本机被控 / 设置”，Windows 的管理员、自动启动和锁屏控制选项集中在“被控权限设置”。

同一台设备的远程桌面一次只接受一个控制端，后连接且认证成功的控制端会接管，前一个连接会断开；SSH 终端使用独立通道，不参与桌面接管。

Windows 的远控窗口可以独立最小化、全屏和关闭，不再跟随主窗口最小化。双屏分窗时，关闭其中一个不会断开另一个。Windows 被控电脑的每块屏幕都会显示被控提示，点击任意一条上的“断开”可结束全部远控连接；提示目前不覆盖系统锁屏或登录界面。

远程桌面直连会拦截本机 IP 和解析到本机的主机名，通过中继也不能远控自己的桌面。

远控窗口的 Ctrl+C / Ctrl+X 会尝试取回远端文字，Ctrl+V 会先同步本机文字再粘贴。Windows 和 Linux X11 查看端也支持会话中的右键文本复制粘贴同步，连接两端建议一起更新；文件不会因此自动上传。也可手动发送或取回文字，手机入口在“更多 → 文字剪贴板”。不会在未连接时无条件同步所有复制内容。Linux 无法可靠判断剪贴板变更时保留手动取回，避免覆盖刚复制的文字。

发送文件前会确认文件清单和远端接收位置，完成后显示实际保存路径。旧版被控端无法报告目录时会明确提示“位置未确认”。请求粘贴到远端当前窗口，不等于那个窗口已经接受文件。

Windows / Linux 的“传输任务”可查看进度、保存回执和取消任务；关闭面板不会中断传输，取消不删除已完成的文件。旧端不支持安全取消时会说明原因。Android 可一次选择最多 32 个文件，确认后依次发送。

**在目标电脑上登录 Codex 或 Claude Code。** 桌面端点主页的“Codex / Claude 登录”，选择已安装的客户端，在终端完成官方授权。登录状态保存在执行命令的电脑上。

直连或中继远控时，在远控工具中点“Codex / Claude 登录”，把登录命令粘贴到远端终端运行。如果一台电脑已经登录，其他设备可通过中继 SSH 选“终端选项 → 远端 Codex / Claude → 使用此机已有登录”。命令在那台电脑运行，无需再次授权。Android 的入口在 SSH 终端工具栏的“账号登录”。已有的加密 `.asxaccount` 文件仍可从登录菜单发送。

三端的“仅观看”开关会暂停键鼠、文字粘贴和向远端写入剪贴板，保留画面与已确认的文件传输；重连后仍保持暂停，需手动恢复控制。Windows 同一连接的双屏窗口共用此开关。

Windows 新配置默认允许远端开关隐私屏。在远控窗口点“隐私屏”后，被控电脑的全部显示器会被遮住，远端仍能看到桌面。按 **Ctrl+Alt+Shift+P** 或断开开启遮屏的会话可恢复显示。Windows、Linux、Android 均可控制支持此功能的 Windows 10 2004 及更新版本。

## 使用前注意

从 1.0.55 起，在自己的中继发布新版后，已上线的设备会在空闲时自动更新，离线设备下次上线补更。Windows 的“公网中继 → 更新在线设备”可以立即通知设备。管理员发布命令和各平台的安装提示见[中继更新说明](docs/PrivateRelay.md#统一更新设备)。

- Android 8.0 及以上可安装。被控需要录屏授权和无障碍服务；锁屏或重启可能结束录屏，需要重新授权。
- Android 10 及以上通常不允许后台应用读取剪贴板；从被控手机取回文字时，可能需要先把 RemoteDesk 切到前台。读取失败不会清空控制端的剪贴板。
- Linux 推荐使用 X11 / Xorg 桌面，Wayland 暂不支持完整桌面控制。
- Windows 锁屏控制需先安装辅助服务并以管理员运行，适用范围见[锁屏控制说明](docs/WindowsLockScreen.md)；不支持首次登录前接管和 Ctrl+Alt+Del。
- 手机远控时可在“更多 → 发送多个文件”选择文件，确认后发送；也可以接收电脑发来的文件。直连和公网中继都支持。暂不支持从电脑直接读取手机其它应用复制的文件。
- 请使用不易猜的设备密钥。跨公网连接使用中继，不要直接把被控端口暴露到公网。

## 构建

需要自行编译时，以下命令均从仓库根目录执行。

### Windows

安装 .NET SDK **8.0.428**，然后运行：

```powershell
dotnet run --project src/RemoteDesk/RemoteDesk.csproj -c Release
```

Windows 版包含管理员启动清单，调试运行也请使用管理员终端 / IDE。
被控端的 H.264 编码需要 FFmpeg。程序会先找现有安装；缺少时下载固定版本到当前用户的 `RemoteDesk/Dependencies` 目录，不修改系统 PATH。离线时可使用随包的 `Install-RemoteDeskFfmpeg.ps1 -ArchivePath <已下载的压缩包>` 安装。组件尚未就绪时保留连接并重试，不自动切换 JPEG。

### Linux

需要 Python 3.10 或更高版本和图形桌面：

```bash
python3 scripts/linux/remotedesk_linux_app.py
```

Debian / Ubuntu 缺少运行依赖时，程序会先检查安装计划，经确认和系统授权后安装。软件源版本冲突或安装计划涉及驱动、内核时会停止并提示，不会自动改源。Ubuntu 22.04 请用便携包（自动使用系统 Python），不要强装要求 glibc 2.38 的 amd64 deb。安装方式见 [Linux 安装说明](docs/Linux-SystemPackage.md)。

### Android

需要 JDK 17 和 Android SDK 36。在 Windows 上构建调试 APK：

```powershell
.\src\RemoteDesk.Android\gradlew.bat -p src/RemoteDesk.Android assembleDebug
```

APK 位于 `src/RemoteDesk.Android/app/build/outputs/apk/debug/`。Linux / macOS 可进入 `src/RemoteDesk.Android` 后运行 `./gradlew assembleDebug`。

## 文档

[Android 开发说明](src/RemoteDesk.Android/README.md) · [打包与发布](artifacts/README.md) · [协议说明](docs/RemoteDesk-Protocol.md)

项目许可证尚未确定；第三方依赖的许可见 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)。
