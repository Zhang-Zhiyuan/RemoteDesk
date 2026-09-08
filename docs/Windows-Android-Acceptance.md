# RemoteDesk Windows / Android / Linux 三端验收清单

当前 canonical internal candidate 的发布范围是 Windows host/viewer、Android host/viewer 与 Linux/Ubuntu host/viewer（Full scope）。自动构建和单元测试纳入三端门槛；Windows 双机、Android 真机和物理 Linux/GPU 的体验项仍必须单独记录，不能用自动测试冒充。macOS、iOS 不纳入本轮验收。

`docs\Windows-Android-CompletionAudit.md` 是 2026-07-27 的 Windows+Linux Desktop 历史快照，不代表当前制品范围；当前 exact package 身份只以 release manifest 和本次验收报告为准。

## 本机自动检查

先在仓库根目录运行：

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -AllowDirtySource -ScopeCheck -RunTests
```

发布前生成三端候选包：

```powershell
.\scripts\Publish-RemoteDesk.ps1 -AllowDirtySource
```

通过标准：

- Windows exe/zip、Linux/Ubuntu zip、tar.gz、deb 与 Android APK 六个规范制品存在，manifest scope、build stamp、大小和 SHA-256 全部匹配，zip 内的 Windows exe 也与规范单文件一致。
- `.NET SDK` 和项目使用的 FFmpeg 运行时可用。
- 本机有可用 IPv4。
- 没有本轮检查留下的 `RemoteDesk` 或 `ffmpeg` 进程。
- Windows Release、Linux Python 与 Android Gradle 单元测试全部通过。

目录中未被本次 manifest 列出的旧 APK 或其它产物不属于当前候选。默认 debug APK 只用于内部真机验证；公开发布必须另行生成、保管并验证正式 Android 签名。

## Windows-to-Windows 实机验收

准备：

- 两台 Windows 机器在同一内网，或网络策略允许 `56565/TCP` 和 `56566/UDP`。
- 两台机器运行同一份 `artifacts\RemoteDesk-win-x64\RemoteDesk.exe`。
- 两台机器使用同一个连接口令。

检查项：

- 被控机打开软件但未启动被控端时，控制机扫描可以看到该机器在线，并能显示可远程启动状态。
- 控制机不会把本机 IP、loopback 或本机机器名显示成可连接的远程设备。
- 控制机手动填写被控机 IP 后，诊断能显示 TCP/UDP 探测结果。
- 连接页在小屏窗口下能自适应换行；Enter 连接、F5 扫描、Ctrl+D 诊断、`Ctrl+Shift+R` 取回远端文件和状态右键菜单均可用。
- 控制机连接后会打开独立远程窗口，不占用主窗口布局。
- 远程窗口按 `F11` 或点击“全屏”后进入当前显示器边界的无边框真全屏，状态/进度区隐藏；再次按 `F11` 后恢复原窗口边框、位置、大小和最大化状态。跨显示器与不同 DPI 下不应跑出可用工作区。
- 鼠标移动、点击、滚轮和键盘输入能在被控机响应。
- 支持鼠标应用回执时，状态与实体日志应记录真正注入后的延迟；移动不应在画面或文件队列后重放旧坐标，点击/松开和键盘事件不能因移动洪峰丢失。
- 远程窗口状态栏稳定显示 FPS、采集、编码、解码、码率和 RTT，不出现明显闪烁。
- 发送本机剪贴板到远程、读取远程剪贴板到本机均能返回状态。
- Windows 远程窗口内 `Ctrl+V` / `Shift+Insert` 使用远端剪贴板并能正常粘贴；通过“发送剪贴板”同步本机文本后也能粘贴。远端应用内 `Ctrl+C` / `Ctrl+X` 后，本机文本剪贴板能自动更新。“远端输入法”按钮应只对支持输入控制的 Windows 远端显示，点击后在被控端发送物理 `Win+Space`，不触发本机输入法切换，并在发送后把焦点还给远程画面。
- 发送文件后，被控机保存到 `Downloads\RemoteDeskReceived`，中断传输不会留下最终半成品文件。
- 新版本 Windows-to-Windows 文件发送、拖放、粘贴和拉取成功状态应包含 SHA-256 已校验；双方声明支持后，故意省略或损坏校验值时接收端应拒绝保存最终文件。
- 新版本 Windows-to-Windows 文件发送、拖放、粘贴和拉取中途取消或发送端失败时，接收端应删除 `.rdtransfer` 临时文件，不留下最终半成品。
- 在控制端资源管理器复制普通文件或文件夹后，主窗口“粘贴文件”能直接发送到被控机下载目录；文件夹应作为 zip 文件接收。
- 把控制端普通文件或文件夹拖到远程桌面窗口后，新版本 Windows 被控端会尝试粘贴到远端当前窗口/桌面位置；如果目标窗口不接受文件粘贴，文件仍应保留在 `Downloads\RemoteDeskReceived`。文件夹应作为 zip 文件粘贴或保存。
- 在会让 OLE 拖放注册失败的系统/会话中，远程窗口不应弹出未处理的 “DragDrop registration did not succeed” 异常；只禁用本窗口拖入并显示降级状态，观看、键鼠、文本剪贴板、发送文件以及主窗口“粘贴文件”仍应可用。
- 在被控机资源管理器复制普通文件或文件夹并按 `Ctrl+C` / `Ctrl+X` 后，远程窗口应自动发起取回确认；远程窗口“取回文件”、主窗口“取回远端文件”和 `Ctrl+Shift+R` 也能手动发起。等待与接收期间按钮应保持禁用并显示进度，重复操作不应生成第二个请求；完成后文件应位于控制机 `Downloads\RemoteDeskReceived` 并进入本机文件剪贴板，“接收目录”按钮可直接定位。文件夹应自动打包为 zip，不可访问文件或旧版本被控端应给出明确提示。
- 在被控机资源管理器选中文件或文件夹，按住左键拖出远程桌面窗口并继续按住时，查看端应把该手势视为确认，自动复制和回传，不再显示传前确认；准备完成后应接续为本机文件拖放，可继续拖到本机资源管理器或桌面再释放。若准备完成前松开左键，不应启动本机拖放，但文件仍应完成回传并保存到 `Downloads\RemoteDeskReceived`、进入本机文件剪贴板；文件夹应作为 zip 文件回传。快捷键、按钮等其他取回入口仍应显示传前确认。
- 复现扫不到、掉线、输入异常或文件失败后，可在被控端页面点击“导出日志”，导出的 Windows 诊断日志应包含发现、连接、状态和文件传输相关记录。
- 多屏机器可以切换捕获屏幕；选择 `100%` 时，单屏和不超过协议安全像素预算的拼接桌面应保持原生空间尺寸，不能为了交互升帧静默降到 1080p。超过 `16,777,216` 像素的超大拼接桌面必须等比缩到预算内并明确提示；除此之外，只有用户明确选择 `75%`/`50%` 或自适应确认持续严重压力后才允许降低传输分辨率。
- 4K 画面在较小窗口 Fit 显示时，状态栏应给出实际缩放比例和 `F11` 清晰度提示；真全屏应明显改善可读性。支持 D3D11 edge enhancement 的驱动会对非原生缩放做保守锐化，滤镜失败时应自动关闭并继续显示。缩放无法恢复被显示器像素数丢弃的细节，必要时仍需全屏或原生 1:1 显示。
- FFmpeg 提供 `gfxcapture` 且显示器/适配器可准确映射时，应优先看到 `gfxcapture/monitorN/adapterN → <hardware encoder>`；WGC frame pool、D3D11 surface 和编码器应位于解析出的适配器，原生尺寸下不插入隐藏缩放或 CPU 整帧回读。WGC 不可用或运行中断时才允许依次回退 DDA、GDI 硬编码；回退不能造成长期黑屏。
- NVENC 低延迟链应使用有目标码率、峰值和有界桌面关键帧 VBV 的 VBR，静态画面不应靠 filler NAL 补满 CBR；1080p 恢复帧不能再被单帧桶压到约 `44 KiB`，动态高细节画面仍须受原有峰值预算约束，不能用无限峰值的恒定质量模式换取主观清晰度。
- 在已连接 Wi-Fi 的 Windows 被控端，认证会话期间日志应记录 WLAN 媒体流模式的实际启用结果；活动 UDP 视频 socket 支持 qWAVE 时应记录 `AudioVideo` flow 标记成功。两项 best-effort 优化失败不得中断会话，也不能仅凭成功日志判定低延迟通过。
- 旧 4K30 基线仍可按 30 秒至少 `810` 帧（`27 FPS`）单独记录，但不得把它写成 4K60/1440p60 已通过。
- 真实 60 FPS 档必须同时覆盖捕获、硬件编码、网络、硬件解码和显示，不能只把配置参数改成 60。30 秒观察期至少收到并由 MF/D3D11 呈现 `1710` 帧，60 秒至少 `3420` 帧（最低 `57 FPS`）；4K60 的每个样本必须精确为 `3840×2160`，1440p60 的每个输出样本必须精确为 `2560×1440`，错尺寸和非 H.264 样本均为 0。
- 60 FPS 档帧间隔 P50/P95/P99/max 分别不得超过 `20/20/35/120ms`；大于 `50ms` 的间隔不超过样本 `1%`，大于 `100ms` 的间隔最多 `2` 次；MF/D3D11 presentation failure 必须为 0。鼠标应用回执 EMA/P95/P99/max 分别不得超过 `10/15/25/150ms`。不得为通过验收而放宽阈值、复制帧、降低空间分辨率或只引用平均 FPS。
- Windows 远程更新分为两种不可混用的模式：已签名安装只接受系统信任、签名公钥相同且 build stamp 严格更新的 Authenticode 包；未签名个人内网安装只接受同样未签名、带有效 RemoteDesk build stamp 且严格更新的包。两者都必须复验 SHA-256，拒绝同版、降级、模式混用和无效签名。成功后，新 PID 的规范 exe 路径、TCP 端口归属和回环 `RDK1` 握手必须同时通过，随后才删除 `.old`。用不可启动的受控测试包做回滚验收时，应隔离坏包、恢复旧 exe、重启并复验；若恢复失败必须保留恢复文件与明确日志。未签名模式只定位为同口令自用内网，不得描述为可信发布者更新。
- 最小化到托盘、关闭到托盘、托盘恢复、托盘启动/停止被控端和托盘退出均正常。
- 勾选开机自启后，当前用户 HKCU Run 项生效；取消后启动项移除。
- 退出应用后没有残留 `RemoteDesk.exe` 或 `ffmpeg.exe` 进程。
- 最终 exact package 必须完成至少 30 分钟的连续画面会话，持续记录精确尺寸、真实呈现帧数、MF/D3D11 失败、UDP abandoned、鼠标应用回执和进程状态；定期 TCP/认证探针只能补充服务健康，不能替代连续 presentation telemetry。
- 断网/恢复、远端应用重启、远端整机重启、显示栈/驱动恢复和多显示器切换必须分别记录故障类型、故障是否被观察、恢复时间、恢复后的捕获/编码/网络/显示路径及最终尺寸。仅添加本地防火墙规则不得描述为物理网卡断开；`Win+Ctrl+Shift+B` 只能描述为 Windows 显示栈重置手势，除非事件日志证明发生 TDR/device removal，否则不能宣称完成 GPU 驱动重置；只有一个有效物理捕获目标时不得宣称完成多显示器切换。

排障命令：

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -AllowDirtySource -Target <被控机IP> -PromptForPassword
```

最终详细报告保存在 `artifacts\RemoteDesk-acceptance-report.md`。`-WriteAcceptanceReport` 会生成通用摘要并覆盖该文件；在人工汇总实体连续测试后不要再次对同一路径使用该参数。如需单独保存自动摘要，应指定其他路径：

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -AllowDirtySource -Target <被控机IP> -PromptForPassword -WriteAcceptanceReport -AcceptanceReportPath artifacts\RemoteDesk-check-summary.md
```

## Linux/Ubuntu 实机验收

- 从本轮 manifest 中安装或解压 Linux/Ubuntu canonical 包，运行 `remotedesk-linux-doctor`，记录 OS、会话类型、DISPLAY、FFmpeg、mpv、GPU、硬件编解码与输入依赖。
- Linux GUI 必须显示 720p、900p、1080p、1440p、4K，以及 15/24/30/60fps 档位；1440p60 和 4K60 必须通过 `HighFrameRateH264` 位 19 协商，不能只把 host 参数设置成 60。
- host 日志必须记录实际产出首个完整 AU 的硬件编码器。NVENC、QSV、VA-API、V4L2 M2M 只能在对应实体硬件实际成功后记为通过；枚举到候选、命令可启动或单元测试通过均不够。
- viewer 只有在 mpv IPC 同时确认非 copy-back `hwdec-current` 与匹配的 `hwdec-interop` 后，才可声明原生 GPU surface 呈现和位 19。FFmpeg 下载 surface、MJPEG/Tk、llvmpipe 或 `*-copy` 路径不得描述为零回读。
- 1440p60 与 4K60 应使用和 Windows 相同的真实 60 FPS 数量/节拍门槛，并记录捕获、编码、网络、解码、显示和输入。若当前 Linux telemetry 不能提供某一阶段，报告必须明确标成未测，而不是记为 0ms。
- 至少完成 30 分钟连接、网络故障恢复、host 重启、GPU/显示恢复和多显示器/捕获目标切换；X11、XWayland 和原生 Wayland 的覆盖范围分别记录。
- WSL/Xvfb 的 RTX/NVENC 1440p60 流只证明 host 编码器与加密传输节拍。Xvfb 是 CPU-backed framebuffer，WSL 当前呈现为 llvmpipe，因此该结果不能代替物理 Xorg 捕获、真实网络、硬解和显示的端到端验收。

## Android 真机验收

- 只安装本次 manifest 所列 APK，并回读包版本、签名证书与 APK SHA-256；debug 与 release 包不得混写为同一发布级别。
- Android 作为被控端时，分别授予通知、屏幕录制和 Accessibility 权限；拒绝、撤销或系统回收任一权限后应给出可操作状态，不得静默假在线。
- Android 作为查看端连接 Windows、Android 与 Linux host，JPEG 与设备实际支持的 H.264 Surface 解码均需记录；厂商 codec 枚举成功不等于真实首帧和持续输出通过。
- 验证横竖屏切换、分辨率变化、前后台切换、锁屏/解锁、网络断开恢复和 host/viewer 重连。过期连接的帧、输入与状态不得污染新会话。
- 验证触摸点击、拖动、长按、滚动、双指缩放及 Accessibility 注入；高频移动不得饿死按下/抬起等离散事件。
- 验证双向文本剪贴板、普通文件发送/接收、取消、SHA-256 错误与空间不足。临时文件不能在失败后成为最终文件；单会话累计配额必须生效。
- host 与 viewer 口令应分别从 Android Keystore 加密存储读取，不得出现在 Activity/Service Intent、saved instance state 或日志中。
- 完成至少 30 分钟真机会话，记录实际解码器、分辨率、FPS、输入延迟、温度/耗电、进程与网络恢复；至少覆盖一台目标 Android 版本和一台实际发布支持的厂商设备。
- 正式发布另需 release keystore 的离线保管、签名证书回读、旧版到新版升级和防降级验收；debug 签名结果不能替代。

## 发布验收

当前 Full canonical 产物：

```text
artifacts\RemoteDesk-win-x64\RemoteDesk.exe
artifacts\RemoteDesk-win-x64.zip
artifacts\RemoteDesk-linux-host.zip
artifacts\RemoteDesk-linux-host.tar.gz
artifacts\remotedesk-linux-host_<version>_amd64.deb
artifacts\RemoteDesk-android-debug.apk
```

当前默认包定位为未签名的内网/内部候选：manifest 必须为 `Windows host/viewer; Android; Linux host/viewer package` scope，并明确记录真实的 `sourceDirty`、`internalCandidate`、build stamp、Windows Authenticode 状态、长度和 SHA-256。Windows 实体目标必须回读同一 exe 哈希，Linux zip/tar.gz/deb 与 Android APK 也必须逐项匹配 manifest。`NotSigned` 的 exe 和 debug 签名 APK 不应描述为已签名生产版；正式公开分发前仍需可信 Windows Authenticode 证书/时间戳以及 Android release keystore，并完成安装和升级验证。

## 仍需外部设备证明的项目

- Windows 最终 exact package 的 30 分钟连续呈现、断网/恢复、远端整机重启、真实 GPU 驱动重置和不同 GPU/驱动组合，必须以最终报告的实际结果为准。
- 多显示器切换只能在至少两个有效物理捕获目标上闭环；单显示器实体只能记录为环境限制。
- Linux/Ubuntu 物理机上的 1440p60/4K60 捕获到呈现闭环，以及 NVENC、QSV、VA-API、V4L2、DRM/EGL surface 和长稳/恢复矩阵。
- Android 真机、厂商 codec、输入、文件、正式签名与升级仍需实体证明；自动构建和 JVM 单元测试不得写成真机已验证。
