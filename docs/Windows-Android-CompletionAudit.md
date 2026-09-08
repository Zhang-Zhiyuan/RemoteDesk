# RemoteDesk Windows/Linux Desktop 完成度审计

> 公开副本：历史记录中的设备及服务器地址已脱敏为示例地址，不是可连接的真实目标。

> 历史快照：本文记录的是 2026-07-27 的 Windows+Linux Desktop 范围，保留用于追溯当时证据。它不是当前三端 canonical 发布说明；当前范围请看 `README.md`、`docs\Windows-Android-Acceptance.md` 与最新 release manifest。

审计时间：2026-07-27

本轮完成判断聚焦 Windows host/viewer 与 Linux/Ubuntu host/viewer，canonical RC 明确使用 `-SkipAndroid` 的 Desktop scope。Android 源码与既有实现保留，但本轮延期：不构建、不验收、不写入 manifest，也不随 Desktop 包分发。macOS、iOS 不纳入本轮完成判断。

## 总体结论

本机可自动证明的实现、打包和测试链路已经具备：

- Windows 可分发包：`artifacts\RemoteDesk-win-x64.zip`
- Windows 单文件程序：`artifacts\RemoteDesk-win-x64\RemoteDesk.exe`
- Linux portable 包：`artifacts\RemoteDesk-linux-host.zip`、`artifacts\RemoteDesk-linux-host.tar.gz`
- Ubuntu deb：`artifacts\remotedesk-linux-host_<version>_amd64.deb`
- 发布校验清单：`artifacts\RemoteDesk-release-manifest.json`
- 本次内部 RC 自动检查入口：`.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -AllowDirtySource -ScopeCheck -RunTests`
- 本次内部 RC 发布入口：`.\scripts\Publish-RemoteDesk.ps1 -SkipAndroid -AllowDirtySource`

最终 Desktop manifest 应只列 Windows exe/zip 与 Linux zip/tar.gz/deb 五项，并以实际工作树记录 `sourceDirty`、`internalCandidate`；单文件程序的 Authenticode 状态为 `NotSigned`。它只定位为内网/内部 RC，SHA-256 完整性校验不能替代发布者签名。目录内 Android 或其它旧产物不属于本次 canonical 集合。

仍不能仅靠本机证明的项目：

- 两台 Windows 机器之间超过 30 分钟的局域网稳定性、断网/重启恢复和多种 GPU/驱动矩阵回归。
- 使用可信代码签名证书的 Windows Authenticode 签名、安装信誉和升级测试。
- Linux/Ubuntu 实体机上的 NVENC/QSV/VA-API/V4L2、X11/Wayland 捕获输入和原生 surface 呈现矩阵。
- Android 真机与签名发布已延期，不作为本轮 Desktop RC 的完成条件，也不得写成已验证。

## 需求覆盖

### Windows-to-Windows

状态：本机实现和协议级自动测试已覆盖；实体目标 `192.0.2.249` 已实际走通 `gfxcapture/Windows Graphics Capture → D3D11 surface → NVENC → UDP → MF/D3D11` 原生 4K 硬件链。旧 exact 包连续三次通过的是 4K30 历史门槛，不能冒充本轮 4K60。最终 Desktop exact package 的 4K60/1440p60、30 分钟连续呈现与故障恢复结果必须以新验收门槛和最终报告为准。

已具备：

- 局域网发现、手填 IP/历史设备探测、TCP 握手兜底、历史设备、口令保存。
- 连接页工具提示、状态右键复制、Enter/F5/Ctrl+D 快捷操作和更低最小窗口尺寸，增强小屏和高 DPI 下的易用性。连接/断开期间会锁定目标输入和设备列表、拒绝重复连接入口并显示当前阶段；异常断线保留可操作的根因，远程更新引起的预期重启会提示稍后重连验版。
- TCP 探测、半连接和未认证连接不会占用唯一控制端名额；被控端只在认证成功后限制同一时间一个控制端。
- 对端软件已运行但被控端未启动时，可通过远程启动请求启动被控端。
- 独立远程窗口、低延迟连接状态，以及包含实际解码后端、FPS、采集/编码/解码、收帧到显示、Ping/Pong RTT 和鼠标应用回执的性能统计；状态复制和 tooltip 会同时带上性能细节。
- 设备能力位 `18` 为 `ShortGopH264`。Windows Host 仍支持对明确声明该能力且帧率关系安全的查看端发送严格 `GOP=2` IDR/P 流；Android/Linux 可独立声明该解码能力。实体测量发现部分 Windows inbox MF/驱动组合无法以零延迟节拍输出有效依赖帧，因此当前 Windows Viewer 不声明位 `18`，Windows-to-Windows 协商 `GOP=1` 全独立帧。配置的 FPS 是请求值，实际值仍受捕获后端、分辨率和设备限制。协议级 GOP2 保护与测试保留，但不能把 Host 支持误写成 Windows Viewer 当前已启用。
- Windows UDP 传输内部特性位 `4` 对应短 GOP，和反馈 `0`、FEC `1`、鼠标 `2`、鼠标应用回执 `3` 一样由双方能力本地推导，不写入 `LowLatencyVideoOffer`。依赖 P 帧只会在匹配 IDR 正在发送或已经完整发送后进入同一 UDP 恢复链；一组只允许一个 P。接收端遇到缺包、乱序、重复或孤立 P 会丢弃并等下一 IDR，不会把错误参考链交给解码器。
- UDP 发送不再让新 P 或新 IDR 抢占已经在途的恢复 IDR，防止持续压力下恢复帧饥饿；新的恢复 IDR 仍可在分片边界中止在途依赖 P。每个在途帧受 `180ms` 绝对预算限制，干净高速 LAN 上不超过 `256KiB` 的短 GOP 帧可在估计速率达到 `100 Mbit/s` 时跳过分片间延时。
- UDP 初始绑定由查看端在 `3.0s` 内每 `200ms` 重发鉴权 Probe，并把 UDP socket 绑定到已认证 TCP 实际选中的本地地址，减少多网卡/VPN 选错出口。Host 的 Offer→合法 Probe 第一阶段为 `3.5s`；收到首个合法 Probe 后重新启动独立 `2.0s` Ready 截止，不再让 BindAck→可靠 TCP Ready 收尾与旧的绝对边界竞争。超时日志聚合 Viewer Probe 成功次数以及 Host 原始包、地址拒绝、解密失败、合法 Probe 和 Ack 次数；握手期间 TCP 画面保持可用，稳态 UDP 无帧回退期限仍为 `2.5s`，不会因加固重连而延后既有失活检测。
- Host 会先初始化反馈时间基准，再原子发布 UDP Active 状态，避免并发健康检查在“Active 已可见、反馈基准仍为零”的窄窗口误杀刚建成的通道。Host 建链截止监视和 I/O 收尾各使用命名专用线程；Viewer 同一源端口两次无 Ack 后串行更换 socket，Host 仅在 Ready 前允许同一已认证 IP、更高包序号的 repin。相关测试集合与本次最终计数以发布 manifest/验收报告为准，不在审计文档中复制易过期的固定数字。
- Windows 硬件 H.264 候选必须实际产出首个恢复 AU。FFmpeg 暴露 `gfxcapture` 且物理显示器可准确映射时，WGC 两帧 D3D11 frame pool、捕获源和编码器固定到解析出的适配器；源尺寸与协商尺寸相同，不插入 `scale_d3d11` 或 CPU 整帧回读。可轮换同一显示器的可用编码适配器；WGC 不可用或运行中断时才继续尝试 DDA，再回退精确坐标 GDI 硬编码。
- 4K30 GOP1 H.264 预算为 `0.24 bit/pixel/frame`、约 `59.7 Mbps`；30→60 FPS 的每帧预算连续插值到 `0.16 bit/pixel/frame`，4K60 约 `79.6 Mbps`，并在 QHD→4K 之间平滑混合，避免跨过 FPS 或偶数尺寸边界时总码率反向下降。原生空间分辨率不变，统一以 `160 Mbps` 封顶。NVENC 使用带目标码率、峰值和四帧桌面恢复预算 VBV 的低延迟 VBR，停止以 filler NAL 补满近静态画面的 CBR；实际码率可以低于目标，VBV 仅允许细节丰富的 IDR 短时突发，并不增加展示队列。高帧率 UDP pacing 连同加密、分片和 FEC 余量约为 `102 Mbps`。该预算仍须由最终 exact package 长稳复验，不能提前描述为已通过。
- 真正 60 FPS 的实体门槛不是配置值：30 秒/60 秒至少呈现 `1710/3420` 个精确尺寸 H.264 帧，P50/P95/P99/max 不超过 `20/20/35/120ms`，大于 `50ms` 的间隔不超过 `1%`、大于 `100ms` 最多 `2` 次，MF/D3D11 failure 为 0，鼠标 EMA/P95/P99/max 不超过 `10/15/25/150ms`。4K60 与 1440p60 必须分别验证完整捕获、硬编、网络、硬解和显示链。
- 对 GOP1 全独立帧，如果 MF 接受 AU 后暂不输出，查看端会 drain 当前 MFT、取出被扣留的帧，再恢复流并标记下一输入 discontinuity；依赖 GOP 不使用此路径。最终 exact 包的 receive-to-present 结果以本轮 Desktop 验收报告为准，不在审计文档中复制易过期的固定数字。
- 外部 FFmpeg 高分辨率软件兼容回退使用单 worker MJPEG、`yuvj444p`、质量 `q=2`；D3D11 presenter 在源尺寸与窗口尺寸不同时 best-effort 使用驱动的硬件边缘增强，失败会关闭滤镜后重试，不影响画面。
- Windows 进程生命周期内 best-effort 请求 `1ms` 计时器分辨率、关闭 `ExecutionSpeed`/`IgnoreTimerResolution` 进程节流，并使用 `SustainedLowLatency` GC；退出时配对恢复。UDP 视频发送、Viewer 接收和本机 FFmpeg RTP reader 使用 `Highest`；Host 接收、鼠标发送和应用回执使用 `AboveNormal`。轮换握手端口后的新 Viewer receiver 继续复用同一 `Highest` 契约。
- Windows 硬编码 FFmpeg 进程保持 `Normal` 基础优先级并加入 `KILL_ON_JOB_CLOSE` Job Object。RTP reader 绝大多数时间阻塞，仅在本机 marker 数据报到达时短时运行；单独提升它避免 50ms 批量唤醒，而不提高整个 Host/FFmpeg 进程与输入竞争。健康收尾先向标准输入发送 `q` 并等待 `1000ms`；未退出才终止完整进程树并等待 `2000ms`。
- Windows 被控端在认证会话期间 best-effort 为已连接 WLAN 接口开启媒体流模式，并在释放会话 lease 时关闭本会话开启的模式；活动 UDP 视频 socket 会通过 qWAVE 标记为 `AudioVideo` 非自适应 flow。`.249` 当前日志已确认两项 API 成功，但这只证明策略已应用，仍须以实体尾延迟统计判断效果。
- 手动关闭远程窗口时由主界面统一执行查看端断连和状态刷新，减少关闭窗口后短暂残留连接或按钮状态漂移；主窗口的连接动作有显式重入保护，发现/诊断任务也不会在连接尚未完成时提前解锁控件。
- 远程窗口底部状态栏会稳定预留性能细节区域，并在性能数字刷新时局部重绘，减少长时间控制时的状态条闪烁；右键复制状态会把当前连接文案和完整性能细节一并写入剪贴板。`F11`/“全屏”进入当前显示器完整边界的无边框真全屏并隐藏状态/进度区，退出后恢复原窗口状态；4K 缩到 `80%` 以下时会提示用全屏尽量提高显示清晰度。硬件边缘增强只能改善非原生缩放观感，不能恢复已丢失像素。
- 鼠标、键盘、滚轮输入。
- 远程窗口会在事件层跳过同远程坐标的鼠标移动；新 Windows 对端协商后以鉴权 UDP 容量一邮箱发送最新移动，点击/滚轮/按键继续走可靠 TCP。可靠指针事件排队、写入及短暂排空期间，移动留在同一 TCP 顺序域；切换通道会清除过期移动，避免越序拉回。兼容输入队列仍会合并连续移动、忽略同坐标重复移动，并优先丢弃旧移动；输入循环按小批量连续写出。
- Windows 方向键、Insert/Delete、Home/End、PageUp/PageDown、右 Ctrl/Alt 等扩展键会带正确 `SendInput` 标志，提高互控键盘兼容性。
- 被控端注入系统前会校验输入命令，拒绝越界鼠标坐标、异常虚拟键值、无效鼠标按钮、非法 Unicode 码点和平台不支持的输入。
- Windows 输入注入会检查 `SetCursorPos` / `SendInput` 结果，注入失败时记录错误并保持连接。
- 高频输入异常会做日志节流合并，避免坏输入或系统持续拒绝注入时刷屏拖慢被控端 UI。
- 传输缩放下的启动、切屏和实际采集共用同一帧尺寸计算，减少首帧前或切屏瞬间鼠标坐标校验误差。
- 帧、心跳、控制回复和输入发送都会在同一写锁内完成加密与写入，防止并发发送打乱 AES-GCM 消息序列。
- 剪贴板双向手动同步；Windows 远程窗口复制、剪切快捷键会触发读取远端剪贴板，粘贴快捷键则物理直传并使用远端剪贴板。
- 文件传输到 `Downloads\RemoteDeskReceived`。
- 主窗口“粘贴文件”或远程窗口内直接粘贴本机剪贴板文件列表，可把控制端复制的普通文件或文件夹发送到被控端下载目录；文件夹会自动打包为 zip。
- 远程窗口支持本机文件或文件夹拖放；若 Windows 拒绝拖放注册，异常会被捕获并只禁用本窗口拖入，观看和输入控制继续工作，复制文件后仍可用主窗口“粘贴文件”发送。新版本 Windows 被控端会在接收文件后写入远端文件剪贴板并触发当前位置粘贴，Android/旧端保存到被控端下载目录，文件夹按 zip 文件接收。
- 新版本 Windows 被控端支持把远端剪贴板中的普通文件或文件夹回传到控制端：远端复制/剪切后自动触发，也可用远程窗口或主窗口按钮及 `Ctrl+Shift+R` 手动触发；等待与接收阶段显示持续状态和分段进度并合并重复请求，完成后保存到控制端 `Downloads\RemoteDeskReceived`、写入本机文件剪贴板，并可一键打开接收目录。文件夹会自动打包为 zip。该能力通过能力位协商，不会在旧端或 Android 端误启用。
- 新版本 Windows-to-Windows 文件发送、拖放、粘贴和拉取通过 `FileChecksum` 能力位协商 SHA-256 内容校验；双方声明支持后，接收端会拒绝缺失或失败的校验值，旧端和 Android 端不声明该能力时保持原流程。
- 新版本 Windows-to-Windows 文件发送、拖放、粘贴和拉取通过 `FileTransferCancel` 能力位协商传输取消；发送端中途失败时会通知接收端删除未完成的临时文件，旧端不声明该能力时保持原流程。
- 多屏目标选择。
- 托盘最小化/恢复、托盘退出、开机自启动。
- 退出和异常保护，降低 `RemoteDesk.exe`、`ffmpeg.exe` 残留概率。
- Windows 滚动诊断日志和“导出日志”，用于排查发现、连接、输入、文件传输和退出问题。

自动证据：

- Windows 测试项目：`tests\RemoteDesk.Tests`
- 查看端兼容被控端 loopback 测试：`tests\RemoteDesk.Tests\RemoteViewerClientLoopbackTests.cs`
- Windows 查看端输入、屏幕切换和剪贴板控制消息 loopback 测试：`ViewerSendsWindowsControlInputClipboardAndCaptureTargetMessages`
- 主窗口断线原因保留、远程更新重启提示和远程窗口关闭断连决策测试：`MainFormViewerWindowTests`
- 远程窗口状态栏布局、4K 缩放提示、`F11` 快捷键和拖放注册失败降级测试：`RemoteViewerWindowTests`
- 远程窗口鼠标移动发送节流测试：`ShouldSendMouseMove*`
- UDP 视频/鼠标能力协商、AEAD/replay/端点固定、latest-only 与 TCP 回退测试：`LowLatencyVideoTransportTests`
- 短 GOP 依赖帧匹配、孤立/缺口门控、恢复 IDR 完成优先和接收链恢复测试：`LowLatencyVideoTransportTests`、`LowLatencyVideoNetworkControlTests`
- WGC `gfxcapture` 能力探测、显示器/适配器映射、原生 surface 参数、GOP2、DDA/GDI 回退、4K 码率和 FFmpeg 优雅/强制退出测试：`FfmpegDesktopH264CaptureTests`、`RemoteHostServerTests`
- MF GOP1 drain/恢复流、高分辨率 `yuvj444p q=2` 兼容回退，以及 D3D11 非原生缩放边缘增强/失败单次重试测试：`MediaFoundationD3D11H264DecoderTests`、`FfmpegH264DecoderTests`、`D3D11HwndVideoPresenterTests`
- `1ms` 计时器配对释放与同步专用线程测试：`WindowsTimerResolutionTests`、`LowLatencyDedicatedThreadTests`
- WLAN 媒体流 lease 的筛选、恢复和失败降级测试：`WindowsWlanMediaStreamingTests`
- qWAVE flow 的 socket 标记、释放和失败降级测试：`WindowsQwaveVideoFlowTests`
- TCP 输入 late-filter、可靠指针顺序门与过期移动清理测试：`ProtocolBoundaryTests`、`RemoteViewerClientInputQueueTests`
- 主窗口连接快捷键判断测试：`IsPlainEnterOnlyAcceptsUnmodifiedEnter`
- 远程窗口剪贴板快捷键识别测试：`ClipboardPullShortcut*`、`RemotePasteTriggerCommands*`
- 远程窗口文件直接粘贴筛选与状态文案测试：`RemoteViewerClientFilePasteTests`
- 查看端请求远端剪贴板文件回传并保存到本机的 loopback 测试：`ViewerRequestsRemoteClipboardFilesAndReceivesFileToLocalDirectory`；另覆盖重复请求合并、请求状态生命周期和分段接收进度。
- 查看端连接支持校验的兼容被控端后发送文件 SHA-256 的 loopback 测试：`ViewerSendsFileChecksumWhenHostAdvertisesChecksumCapability`
- 查看端声明校验能力后拒绝缺失 SHA-256 的远端回传文件测试：`ViewerRejectsReturnedRemoteFileWhenNegotiatedChecksumIsMissing`
- 查看端收到远端文件回传取消后删除本机临时文件测试：`ViewerHandlesRemoteFileTransferCancelAndDeletesTemporaryFile`
- 被控端认证后唯一控制席位测试：`ActiveClientGateAllowsOneAuthenticatedClientAtATime`
- Windows 输入注入扩展键和坏输入拒绝测试：`InputInjectorTests`
- 被控端输入错误日志节流测试：`RemoteHostServerTests`
- 传输缩放帧尺寸一致性测试：`ScreenCaptureServiceTests`
- Windows 诊断日志滚动和导出测试：`WindowsDiagnosticLogTests`
- 发现/诊断/协议/设置/视频模式/输入队列/边界测试：`tests\RemoteDesk.Tests\*.cs`
- 协议并发发送顺序测试：`ConcurrentProtocolFrameAndControlWritesRemainReadable`
- 本次 canonical 自动验收命令：`.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -AllowDirtySource -ScopeCheck -RunTests`
- 本次 canonical 发布命令：`.\scripts\Publish-RemoteDesk.ps1 -SkipAndroid -AllowDirtySource`
- `-SkipAndroid` Desktop scope 同时验证 Windows 与 Linux 五个制品；本轮不得运行 Full/Android 发布后再把 APK 混入 manifest。

实体诊断与本轮验收证据：

- 实体目标 `192.0.2.249` 为 Windows 25H2（build `26200.8655`），主显示器是 AMD Radeon Graphics 驱动的 `3840×2160 @ 144Hz`，机器另有可用于编码的 NVIDIA 适配器。当前实际选择 `gfxcapture/monitor0/adapter1 → h264_nvenc`：WGC 在解析出的 NVIDIA D3D11 device 上提供原生 BGRA surface，直接送入 NVENC，不经过 GDI、CPU 整帧回读或空间缩放。此前 `ddagrab` 能打开 AMD output 但 `AcquireNextFrame` 不产帧，现仅作为 WGC 失败后的兼容候选。
- 旧 4K30 exact 包三次连续 30 秒样本各收到 `864` 个原生 `3840×2160` 监测帧。这些数据只作为历史回归基线，不能写成 4K60 已通过。
- 预最终候选曾完成一次 1440p60 实体门槛：60 秒内 `3735` 个精确 `2560×1440` 样本，P95/P99 `18.01/19.44ms`，MF/D3D11 failure 为 0；它不是最终 exact package，最终发布包仍须复验。
- 旧 NVENC CBR→capped VBR 探针证明移除 filler airtime 不改变对应 SSIM；最终 4K60/1440p60 的实际码率、帧节拍、MF/D3D11 与鼠标数字必须从最终 exact package 日志引用，不能沿用旧 4K30 数字。
- `.249` 日志确认 WLAN 媒体模式、qWAVE `AudioVideo` 和固定对端零分配发送成功。独立 Win32 ICMP 探针复现了近似周期的 Wi-Fi 停顿，支持无线链路/驱动是重要贡献因素，但不能证明每一个视频 outlier 都来自捕获、codec、transport 或 presentation 之外。应以有线/更强 AP 或管理员单独调整漫游积极性后复验，不通过降分辨率或增加旧画面缓冲掩盖。
- 路由复现实测曾证明部分 UDP 源端口稳定黑洞（例如 `56555`，相邻端口正常），因此实现会在两次无 Ack 后更换 Viewer 源端口，并允许 Host 在 Ready 前做认证 repin；此结论与当前无线尾延迟相互独立。

必须实机验证：

- 最终 exact package 连续呈现超过 30 分钟后是否仍满足精确尺寸、57 FPS、节拍、MF/D3D11 和鼠标门槛；定期认证探针不能替代连续画面 telemetry。
- 物理断网/恢复、远端应用重启、远端整机重启、真实 GPU 驱动 reset/TDR 和多显示器切换是否分别完成闭环。防火墙阻断、显示栈快捷键和单显示器环境必须按真实动作/限制命名，不能扩大解释。
- 扫不到的具体内网 IP 是否能通过 `-Target <IP>` 定位 TCP/UDP 原因。
- 不同 Windows GPU/驱动组合下 WGC、DDA、NVENC、MF、QSV、AMF 的探测、跨适配器映射、熔断、回退和长时间稳定性。
- 在曾触发 “DragDrop registration did not succeed” 的同一 Windows 会话中复验：窗口应仅降级禁用拖入，不再弹出未处理异常，观看/输入和主窗口“粘贴文件”仍可用。自动异常路径与 STA 窗口测试已通过，但不替代该用户会话确认。
- Windows QSV 以及 Linux NVENC/QSV/VA-API/V4L2/DRM-EGL 等对应硬件路径需在具备该设备的实体机上逐项确认；能力枚举或单元测试不等同于真实硬件成功。
- 托盘关闭、托盘退出、开机自启在真实用户会话中是否符合预期。
- 断网、关闭被控端、退出程序后控制端是否能可靠恢复且无残留进程。

### Android（本轮延期）

Android 源码、历史测试和独立签名能力仍保留，但本轮不运行 Gradle、adb、APK 构建或 Android 实机验收。Android 旧产物、协议级历史证据和厂商 codec 能力枚举均不进入本轮 Desktop manifest 与最终结论；后续另开真机和签名发布周期。

### Linux/Ubuntu Desktop 状态

状态：本轮 `-SkipAndroid` Desktop canonical 包含 Linux portable zip/tar.gz 与 Ubuntu deb。X11 host/viewer、基础输入、剪贴板和双向文件传输已有实现与 Python 单元测试；这些自动证据属于发布门槛，但不等于物理 Linux 实体体验已闭环。

- Linux host 可用常驻 `ffmpeg x11grab` 实际探测 NVENC、QSV、VA-API、V4L2 M2M H.264，失败时按协商回退 MJPEG/ImageMagick；当前 Linux host 编码仍是全 IDR、无 B 帧。
- Linux GUI 已提供 720p、900p、1080p、1440p、4K 与 15/24/30/60fps。高于 30 FPS 只有在 viewer 通过位 19 声明真实硬解/呈现能力后才启用，参数可选不等于实体 60 FPS 已通过。
- Linux viewer/host 在协议/策略层声明能力位 `18`，可在 TCP 画面路径上处理 Windows GOP2；具体 decoder/interop 的零延迟行为仍须对应实体硬件验证。它们不声明 Windows UDP 能力位 `13-17`。
- XTest 或 `xdotool` 与 `DISPLAY` 可用时 Linux host 会声明基础输入控制；X11 文本/文件剪贴板和双向文件传输已有实现。Wayland 原生捕获、完整键盘、托盘和不同桌面环境仍需实体机验证。
- Linux viewer 可尝试以 `mpv --wid` 验证 NVDEC、VA-API 或 DRM PRIME 非 copy-back surface 呈现；QSV/V4L2 仍是 FFmpeg 兼容路径。状态栏只有在运行时属性同时确认 decoder 与 interop 后才会声明无 CPU 回读。
- `x11grab` 帧在进入 NVENC 前仍从系统内存做显式 CUDA upload；这是硬件编码，不是捕获到编码的零拷贝链。只有 mpv IPC 同时确认非 copy-back decoder 与匹配 interop 时，viewer 侧才能写“已验证无 CPU 回读”。
- Ubuntu 24.04 WSL/Xvfb + RTX 的 1440p60 样本只证明 host NVENC 编码与加密传输节拍；Xvfb 是 CPU-backed framebuffer，当前 WSL viewer 是 llvmpipe，不能据此宣称物理捕获、真实网络、硬解或显示已通过，更不能扩展为 Linux 4K60 结论。
- Desktop scope（`-SkipAndroid`）运行 Windows Release 与 Linux Python 测试。也可单独执行 `python -m unittest discover -s tests -p "test_linux_*.py" -v` 复验；最终测试计数以随发布保存的验收报告为准。

## 发布状态

本轮 Desktop canonical 五个制品：

```text
artifacts\RemoteDesk-win-x64\RemoteDesk.exe
artifacts\RemoteDesk-win-x64.zip
artifacts\RemoteDesk-linux-host.zip
artifacts\RemoteDesk-linux-host.tar.gz
artifacts\remotedesk-linux-host_<version>_amd64.deb
```

当前 canonical 构建号、五个制品的 SHA-256 及最终测试计数，以本轮 `-SkipAndroid` 发布生成的 `artifacts\RemoteDesk-release-manifest.json` 和最终验收报告为准。远端部署状态也应以该报告中的构建号、哈希回读和实体测试时间为准，不沿用本文旧值。

`artifacts\RemoteDesk-win-x64-updated.zip` 和 `artifacts\RemoteDesk-win-x64-updated` 属于旧别名产物，已从当前发布集合移除；分发时以 `artifacts\RemoteDesk-release-manifest.json` 中列出的产物为准。

本轮 canonical 集合包含 Windows 与 Linux 五个制品，不含 Android。目录中的旧 APK 或其它未列入 manifest 的产物不得作为配套包分发。Windows `RemoteDesk.exe` 当前为 `NotSigned`，公开分发还需可信 Authenticode 证书与签名/时间戳流程。

## 每次交付前命令

```powershell
.\scripts\Publish-RemoteDesk.ps1 -SkipAndroid -AllowDirtySource
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -AllowDirtySource -ScopeCheck -RunTests
git diff --check
rg -n "TO[D]O|FI[X]ME|X[X]X" src tests scripts docs README.md
Get-Process RemoteDesk,ffmpeg -ErrorAction SilentlyContinue
```

Desktop 发布要求 Windows self-contained、win-x64、single-file，并要求 Linux zip/tar.gz/deb 同时生成且与 manifest 匹配；任一步失败都必须终止。`Invoke-RemoteDeskCheck.ps1` 只要出现失败检查（`[!!]`）就会以非零退出码结束。旧 `.249` 4K30 数据只是历史基线；最终 4K60/1440p60、Windows 长稳/恢复、Linux 物理机硬件路径和签名发布仍按 `docs\Windows-Android-Acceptance.md` 继续验收。Android 本轮延期。

`-AllowDirtySource` 仅用于复验本次已明确标记 `sourceDirty=true`、`internalCandidate=true` 的内部 RC；干净的正式候选不应依赖该参数。
