# RemoteDesk

RemoteDesk 面向个人自托管远控，同时提供 Windows host/viewer、Linux/Ubuntu host/viewer 与 Android host/viewer。三端共享认证和加密 TCP 协议，并按双方能力协商 H.264 或 JPEG；Windows 与 Android 对端可在直连且能力重叠时另行协商低延迟 UDP，Linux 互连及公网中转均使用加密 TCP。历史安装包、升级注意事项及保留限制见 [1.0.0 发布说明](docs/Release-1.0.0.md)。不承诺所有设备/网络下 4K60。两台 Windows 电脑运行同一个客户端：被控电脑启动“被控端”，控制电脑可在“IP 直连”里填写被控电脑的地址、端口和相同口令，也可通过自己的公网 Linux 中继查看在线设备并连接。

2026-09-08 整体核验发现并修正 Windows 查看端的连接启动死锁，回归和真机复验范围见[整体核验记录](docs/OverallRecheck-20260908.md)。发行文件统一放在 `artifacts/release-1.0.0`；历史 release-candidate 不能当作 1.0.0。

随后完成 [Linux 无损显示交接优化与真机安装](docs/LatencyOptimization-20260908.md)：省去本地 PNG 压缩/解压，固定样例像素完全一致；Windows 增加 TCP 分段耗时统计。该结果不代表公网端到端延迟已按相同比例降低。

2026-09-07 补充：已在 realme GT8 Pro（Android 16）和 Jetson AGX Orin（Ubuntu 24.04 ARM64）完成有限真机测试，并修复安卓输入/密码显示和 Jetson 解码兼容问题。范围与限制见[真机验证记录](docs/PhysicalDevices-20260907.md)，不代表完整设备矩阵或 4K60 长稳通过。

## 当前源码与公开仓库

本仓库上传当前源码、构建脚本和测试，不包含安装包、签名私钥、个人配置或私有测试证据。旧开发历史留在本地，公开 `main` 从干净源码快照开始。文档中的 `artifacts/...` 多为仅本地保留的历史证据路径，不是 GitHub 下载链接；[产物说明](artifacts/README.md)列出了重新构建的入口。

最新源码包括 Android 1.0.1 黑屏交互修复，以及 Linux Jetson 原生 NVENC 编码、BT.709 色彩修正和 Unicode 输入改进。验收范围分别见[三端复核](docs/InteropRecheck-20260908.md)与[后续优化复核](docs/OptimizationRecheck-20260908.md)。后一次优化测试中 Windows 输入桌面不可访问，未重新完成六方向呈现验收；不能把前一轮通过记录当作最新源码的完整验收。

公开文档中的真实设备地址已替换为示例地址。`192.0.2.*`、`198.51.100.*` 和 `203.0.113.*` 不对应可连接的测试机器；部署时请填写自己的地址、强口令及可信证书指纹。真机测试必须显式启用并指定 `REMOTEDESK_REAL_MACHINE_HOST`，默认不会连接个人测试设备。实验脚本的额外参数见[公开源码说明](docs/PublicSource.md)。

## 使用方式

本次修复及验证范围见 [2026-09-06 发布整理](docs/Release-20260906.md)和[后续复查](docs/Recheck-20260906.md)。本地安装包与校验清单见 [artifacts](artifacts/README.md)；构建产物应通过 Releases 分发，避免继续提交到源码历史。

1. 两台电脑连接到同一个局域网。
2. 在被控电脑上打开 RemoteDesk，进入“被控端”，设置访问口令；可手动点击“启动被控端”，也可保持软件运行后由控制端用相同口令远程启动被控端。
3. 如果 Windows 防火墙提示，允许当前端口的局域网访问。默认端口是 `56565`。
4. 在控制电脑上打开 RemoteDesk，进入“IP 直连”，可从“设备”列表选择局域网内已打开 RemoteDesk 的电脑或历史连接设备，也可以手动填写内网 IP/主机名、端口和口令。点击“扫描/探测”时，程序会同时广播扫描、受限同网段定向探测、解析并探测手填目标与最近连接设备，并按各自端口做 TCP 被控端口兜底探测；只有读到 RemoteDesk 握手的端口才会被认定为可连接，例如 `192.0.2.249`。点击“诊断”可检查本机 IP、目标探测结果、视频模式和 `ffmpeg` 状态。
5. 点击“连接”。如果是手填 IP 或历史设备，程序会先轻量刷新该地址状态；如果对方软件已运行但被控端尚未启动，且对方允许远程启动，程序会先用相同口令请求对方启动被控端，再自动连接。连接模式默认“自动（H.264 清晰增强）”；阅读小字时可改“文字清晰（JPEG）”，需要排查手机硬编码低延迟链路时可改“强制 H.264”。
6. 连接成功后，远程桌面会在独立窗口中打开；底部“切换屏幕”会按被控端公布的顺序轮换各物理显示器，也可切换“1:1 清晰”和“适应窗口”，前者会禁止把较小的 1080p 画面插值放大；按 `F11` 或点击“全屏”可进入当前显示器的无边框真全屏，再按 `F11` 可恢复。主窗口仍可用于直接选择“所有屏幕”或指定远程屏幕、发送剪贴板、发送文件、粘贴已复制的文件、取回远端已复制的文件和更新 Windows 被控端。
7. 连接页支持键盘操作：在 IP、口令、端口或设备列表中按 Enter 可连接，F5 可重新扫描，Ctrl+D 可诊断，`Ctrl+Shift+R` 可取回远端已复制的文件；在设备列表中按 F2 可修改已保存设备的备注，按 Delete 可删除保存记录，也可以使用列表下方按钮或右键菜单。状态区域右键可复制当前状态或打开本机接收目录。
8. 程序会记住上次使用的连接 IP、端口、画面参数、屏幕选择、口令和最近连接过的设备；设备备注会在重新连接和兼容端口迁移后保留。口令会通过 Windows 当前用户 DPAPI 加密保存，不以明文写入配置。如果写入失败，窗口顶部会持续提示“设置未保存”并提供“重试保存”；备注修改和删除记录也只有成功写入后才提示已保存，未保存的更改仅在本次运行中保留。
9. 默认开启“清晰优先自适应”：压力变高时先降低帧率，再降低 JPEG 画质，只有连续严重压力才把分辨率降到可读下限；恢复时优先恢复分辨率，并按目标帧率核对余量，减少清晰/模糊来回跳变。新版查看端还会通过 `HighQualityJpeg` 能力位请求桌面文字清晰档：旧配置为 JPEG 60 时，本次会话至少使用 85，自适应下限为 70，不会因此降低源分辨率。
10. `100%（原生）+ 60 FPS` 和 `最高 1440p + 60 FPS` 分别请求 Windows 4K60 与 1440p60；后者会把 4K 源通过 WGC 内部 GPU 表面缩放精确输出为 `2560×1440`，源屏不超过 1440p 时不放大。设置值只是请求，不是实体结果：只有捕获、硬件编码、网络、硬件解码与显示链都维持实测节拍时才算真正 60 FPS。高帧率还需要查看端声明 `HighFrameRateH264`（能力位 `19`）；旧端自动限制为 30 FPS，GDI 软件捕获回退也限制为 30 FPS。`75%` / `50%` 仍是明确的比例缩放档。
11. 需要同步文本剪贴板时，在控制端点击“发送剪贴板”或“读取剪贴板”。Windows 远程桌面窗口中的 `Ctrl+V` / `Shift+Insert` 会按物理键直接交给远端应用，使用远端剪贴板和远端输入法；在远端应用里按 `Ctrl+C` / `Ctrl+X` 后，程序仍会自动读取远端文本剪贴板到本机。Windows 会优先截获本机的 `Win+Space`，因此远程窗口底部提供“远端输入法”按钮，点击后只在被控端发送物理 `Win+Space`，不会切换本机输入法。
12. 需要传文件到被控电脑时，连接成功后点击“发送文件”；也可以在控制端资源管理器复制文件或文件夹，再点击主窗口“粘贴文件”，还可以把本机文件或文件夹直接拖到远程桌面窗口。文件夹会自动打包为 zip 传输。若 Windows 拖放注册被系统拒绝，程序只禁用本次窗口的拖入功能并继续远程控制，不会再因未处理异常退出；此时仍可使用主窗口“粘贴文件”。新版本 Windows 被控端会尝试把拖放文件粘贴到远端当前窗口/桌面位置，失败或不支持时仍会保存到当前用户的 `Downloads\RemoteDeskReceived` 目录。
13. 需要把远端文件传回本机时，直接在远端资源管理器里选中文件或文件夹并按 `Ctrl+C` / `Ctrl+X`，控制端会自动读取远端文件剪贴板并显示传前确认；也可以在远程桌面窗口或主窗口点击“取回文件/取回远端文件”，或按 `Ctrl+Shift+R` 手动取回。Windows-to-Windows 还可按住左键把远端资源管理器中选中的项目拖出远程桌面窗口：保持左键按下，等待复制和回传准备完成后，本机文件拖放会自动接续，可继续拖到本机资源管理器或桌面再释放；这个拖出手势本身视为本次传输确认，不再弹出确认框，其他取回入口仍保留传前确认。如果在准备完成前松开左键，文件仍会回传到控制端当前用户的 `Downloads\RemoteDeskReceived`，并在本机剪贴板可用时进入文件剪贴板。文件经同一条加密连接分块回传，文件夹会自动打包为 zip；两个窗口都提供“接收目录”入口，等待和接收阶段会持续显示进度并阻止重复请求。Android 不支持从远程画面拖出文件。
14. 需要同步 Windows 端版本时，先在本项目重新打包，再连接对方并点击“同步更新”。程序会用内置构建时间版本号比较双方：本机较新时发送当前 `RemoteDesk.exe` 更新远端，远端较新时拉取远端 `RemoteDesk.exe` 更新本机；更新包会经过 SHA-256 校验，替换后以托盘模式重启，并删除旧 exe 备份。
15. 需要常驻时，可勾选“开机自启”和“托盘驻留”，关闭或最小化窗口后可从右下角托盘图标恢复或退出。

### 私有公网中继

两台机器不在同一局域网、也不方便做端口映射时，可在 Windows 主界面的
“公网中继”页填写一台具有公网地址的 Linux 服务器。点击“配置/更新服务器”后，
RemoteDesk 会通过 SSH/SFTP 安装或更新独立的 `remotedesk-relay` systemd 服务；
管理员密码只用于本次操作，不会写入设置、日志或进程命令行。首次配置会固定
SSH 主机指纹和中继 TLS 证书指纹，后续指纹变化会拒绝连接。

在每台 Windows RemoteDesk 上用同一台 Linux 服务器完成一次配置，并保持
“将这台电脑发布到在线设备列表”勾选；被控端启动后，该机器就会主动连接公网
中继，不要求内网入站端口或路由器端口映射。控制端刷新在线设备，双击目标或
点击“连接所选设备”即可连接。“IP 直连”页仍可使用 IP/主机名连接，两种方式
可随时选择。中继只转发现有 RemoteDesk 加密字节流，不取得远控口令或会话密钥；
中继模式会关闭只适用于局域网端点的 UDP 协商，使用加密 TCP 上的 H.264/JPEG、
输入、剪贴板和文件传输。完整部署、端口与排障说明见
[`docs/PrivateRelay.md`](docs/PrivateRelay.md)。

Linux GUI 与 Android 现在也支持接入已配置的私有中转服务器、发布本机和选择在线
设备连接。服务器部署入口仍在 Windows；其它平台填写共享访问密钥及固定证书指纹。
三台真机经公网中转的六向短测、修复和复测限制见
[`docs/RelaySixDirections-20260908.md`](docs/RelaySixDirections-20260908.md)。

## 当前能力

- 单程序双角色：同一个客户端既能作为被控端，也能作为控制端。
- TCP 局域网连接，默认端口 `56565`。
- Windows 控制端和被控端 TCP 连接会统一开启 `NoDelay`、较短 keep-alive 探测间隔，并把画面发送/接收侧缓冲限制为 `128 KiB`，减少拥塞时可积压的旧画面并更快感知异常断线。
- 新版本 Windows-to-Windows 会在双方能力协商成功后自动建立独立的加密 UDP 低延迟通道：JPEG 和 H.264 恢复帧会按 `1200` 字节以内的数据报分片发送，只重组最新完整帧，不会因丢包重传旧画面。协议仍支持 `ShortGopH264`（能力位 `18`）：经查看端明确声明且同时具备拥塞反馈时，严格 `GOP=2` 的一组 `IDR + SPS/PPS` 以及紧随其后的唯一一个 P 帧可在同一恢复链内走 UDP；P 帧只有在对应 IDR 正在发送或已完整发送后才会入队。接收端只接受该 IDR 后序号连续的 P 帧，缺包、乱序、重复或孤立 P 帧都会让它等待下一恢复 IDR。已经开始分片的恢复 IDR 不会被 P 帧或更新 IDR 抢占，新的恢复 IDR 只可在分片边界抢占正在发送的依赖 P 帧。普通档继续使用 `180ms` 入队预算，高帧率档缩短为 `50ms`；实际发送仍由最新帧替换和拥塞反馈约束。当前 Windows 查看端基于实体驱动结果不声明短 GOP，Windows-to-Windows 协商 `GOP=1` 全恢复帧；实际帧率取决于捕获后端、分辨率、GPU/驱动和链路，不能把配置上限写成实体稳定结果。Android/Linux 当前按各自实现独立声明短 GOP，但厂商 codec 与原生 surface 路径仍须在对应实体设备验证，声明能力不等于低延迟实测通过。未知标志或未协商的依赖帧会通过 TCP 顺序屏障安全回退。
- 双方同时声明低延迟鼠标能力时，只有可丢弃的 `MouseMove` 使用同一条 AES-GCM 鉴权、端点固定和防重放的 UDP 路径，并始终只保留最新坐标，点击、滚轮、按键、松开、控制、心跳和文件仍走可靠 TCP。支持输入应用回执的新版本会在系统真正注入鼠标后返回 latest-only 回执，用于显示实际“发出→被控端应用”的延迟。双方额外声明拥塞反馈能力时，查看端会用 `FeedbackV2` 回报近期交付/丢失状态，发送端据此做 pacing；双方同时声明 XOR FEC 时，发送端会在中等丢包时为每组最多 `16` 个数据分片增加 `1` 个校验分片，在链路恢复或严重拥塞时关闭校验，可无重传恢复组内恰好一个丢片。短 GOP 查看端可在健康交互时使用完整 IDR/P 对；当前 Windows 查看端使用 GOP1 兼容策略。Windows D3D11 硬解帧直接在串行渲染线程呈现，避免逐帧 UI 消息排队。Host 优先复用 TCP 本地端口号作为 UDP 端口；Viewer 在 `3.0s` 内每 `200ms` 发送 Probe，并在同一源端口连续两次无 Ack 后自动换端口，Host 仅在 TCP Ready 前接受同一已认证 IP、更高包序号的重新绑定，进入 Active 后端点不再迁移。Host 的 Offer→Probe 阶段保留 `3.5s`，收到首个合法 Probe 后再独立给可靠 TCP Ready `2.0s`。Viewer 的 UDP socket 绑定到 TCP 实际选中的本地地址，避免多网卡/VPN 选错出口。协商期间画面继续走 TCP；握手、反馈、交付进度或收帧异常仍会自动回退原画面及鼠标 TCP 通道，UDP 被防火墙拦截时也会回到 TCP。
- Windows 被控端认证会话建成后会 best-effort 为已连接 WLAN 接口开启媒体流模式，并把活动 UDP 视频 socket 通过 qWAVE 标记为 `AudioVideo` 非自适应流；会话结束时关闭本会话开启的媒体流模式并释放 qWAVE flow。两项优化均受 Windows 版本、驱动和网络策略影响，失败只记录诊断并继续原链路，不把“API 调用成功”当作无线延迟已经达标。
- 低延迟 Viewer 把硬件编码器冷启动首帧和稳态停帧分开判断：首帧等待从可靠 TCP Ready 写入成功后才开始计算，并保留独立的硬件发现/首帧预算；一旦收到完整帧，仍使用原有 `2.5s` 稳态停帧门槛。实体回归继续使用 `5s` MF/D3D11 直显就绪验收线，没有通过放宽验收阈值掩盖冷启动或调度尖峰。
- 支持 UDP 局域网自动发现：只要 RemoteDesk 正在运行，就会通过 UDP `56566` 响应控制端扫描，并标明被控端是否已监听、设备平台和能力。
- 支持对本机同网段 IPv4 地址、手填 IPv4/主机名和最近连接设备进行 UDP 定向探测，适合广播无法跨过的交换网络；大网段会限制为本机所在 `/24`，避免扫描过重。对手填目标和历史设备还会按各自端口尝试 TCP 被控端口兜底探测，并读取 RemoteDesk 握手魔数以减少普通 TCP 服务误报；即使 UDP 发现响应被拦，也能提示真正的被控端正在监听。
- TCP 探测、半连接和未认证连接不会占用唯一远程控制席位；Windows、Android 与 Linux 被控端都采用“最后认证成功的查看端接管”策略。同一时间仍只有一个控制端；后来者通过口令认证后，被控端会先在旧加密会话内发送明确的接管原因，再断开旧连接并移交画面与输入。旧查看端把该原因视为终止事件并停止自动重连，避免 A、B 循环互相抢占。
- 扫描结果和历史设备列表会按本机 IP、loopback 和本机机器名过滤本机，避免控制端误选自己。
- 支持同口令远程启动被控端：控制端扫描到“可远程启动”的机器后，点击连接会先发起带 HMAC-SHA256 校验的启动请求。
- 支持连接失败后的定向远程启动重试：手动输入 IP 时，即使没被扫描列表发现，也会尝试用同口令启动对方被控端后再连接。
- Windows 查看端只在当前连接代际收到 `DeviceInfo` 后获得自动重连资格，使用 `0.5/1/2/4/8/10` 秒有界退避；资格在接收线程按连接代际同步提交，远端发完 `DeviceInfo` 立即断开时不会因 UI 消息稍晚执行而漏掉重连。`DeviceInfo` 后还需连续稳定 5 秒才重置退避，避免远端在每次握手后立即断开时形成 500 ms 重连风暴。坏口令、协议不兼容、手动断开、被另一台查看端接管和旧代际 UI 回调都有独立终止/代际栅栏。
- 手填 IP 或历史设备连接前会刷新一次该地址的发现状态，优先给出“Android 已在线但等待录屏授权”等明确提示，减少直接连接失败后的猜测。
- 支持连接前诊断：连接页可一键检查本机 IPv4、目标 RemoteDesk 探测结果、TCP 端口握手原因、目标平台/能力、视频模式和 `ffmpeg` 可用性，方便定位扫不到、端口不通、端口开但不是 RemoteDesk、Android 未授权录屏或强制 H.264 缺少解码器等问题。
- Windows 主窗口使用统一品牌页眉、分区卡片、明确的主次/危险操作按钮和高对比状态区；连接页同时保留控件提示、输入框占位提示、状态右键复制、Enter/F5/Ctrl+D 快捷操作。连接/断开期间会锁定目标和设备列表并显示当前阶段，避免重复双击发起并行连接。异常断线会保留可操作的失败原因，远程更新触发的预期重启会明确提示稍后重连验版。主窗口、远程窗口和文件确认框会按当前显示器工作区与 DPI 自动约束尺寸，跨屏或显示器热插拔后重新校正位置，窄窗口下设置区、工作区和远程状态栏会自动换列/换行。
- 支持独立远程桌面窗口，远程画面和鼠标键盘输入不再嵌入主配置界面。
- 手动关闭远程桌面窗口时会由主界面统一断开查看端连接并刷新按钮状态，减少窗口销毁后连接状态短暂残留。
- 远程窗口会在事件层跳过同远程坐标的鼠标移动；新 Windows 对端协商成功后，移动事件进入容量为 `1` 的鉴权 UDP 最新值邮箱，不等待 TCP 画面/文件写锁。点击、抬键或滚轮正在可靠队列/写入时，后续移动会暂时留在同一 TCP 顺序域，并在写完后保留 `25ms` 排空窗口再恢复 UDP；切换路由时会丢弃旧通道尚未发送或尚未加密的过期移动，避免 TCP/UDP 越序把光标拉回旧坐标。兼容 TCP 输入队列仍会合并连续移动、忽略同坐标重复移动，并使用无头部搬移的队列出队；队列常规上限为 `256`，高频输入堆积时只淘汰旧鼠标移动，并为 `MouseUp` / `KeyUp` 预留有界空间。查看端按连接代际追踪已成功入队的鼠标按键和键盘所有权；即使切屏、拔屏、失焦或画面已清空，仍会用最后有效坐标 best-effort 可靠释放，发送失败则保留所有权供同代后续重试，避免远端卡键或按钮一直按下。被控端以同一会话锁串行化 UDP 移动与 TCP 点击的完整坐标定位/注入事务，避免移动在点击中途改写落点。
- Windows 键盘控制会为方向键、Insert/Delete、Home/End、PageUp/PageDown、右 Ctrl/Alt 等扩展键发送正确标志，提高互控时编辑器、终端和系统界面的按键兼容性。
- 被控端会在注入系统前校验输入命令，拒绝越界鼠标坐标、异常虚拟键值、无效鼠标按钮、非法 Unicode 码点和平台不支持的输入，避免坏输入影响远程会话稳定性。
- Windows 输入注入会检查 `SetCursorPos` / `SendInput` 结果；系统拒绝注入时会记录明确错误并继续保持连接，便于排查权限、会话桌面或安全软件拦截问题。
- 高频输入异常会做日志节流合并，避免坏输入或系统持续拒绝注入时刷屏拖慢被控端 UI。
- 同一加密会话的发送会在同一写锁内完成加密和网络写入，Windows、Android 与 Linux 都把长度头和密文合为一次批量写出，避免并发打乱 AES-GCM 消息序列并减少小包/系统调用。
- 支持开机自启到托盘、最小化/关闭到托盘，以及托盘菜单恢复、启动/停止被控端或退出。
- 支持全局异常退出保护：UI 线程或后台未处理异常会触发一次性退出协调，并在短暂等待后强制结束进程，降低异常后残留后台进程的概率。
- Windows 端会把被控端日志、查看端连接状态和诊断结果持续写入当前用户 AppData 下的滚动诊断日志，并可在被控端页面点击“导出日志”保存，方便排查某台机器扫不到、连接中断、输入异常或文件传输失败。
- 支持连接配置记忆，保存配置到当前用户的 AppData；被控端口令和连接口令均使用 Windows DPAPI 以当前用户范围加密保存。
- 配置加载会自动补齐缺失字段；如果 `settings.json` 损坏，会把坏文件移为 `.bad` 备份并回到默认配置，避免启动阶段卡死。
- 支持最近连接设备记忆，保存机器名、用户备注、IP、端口、屏幕名、平台、能力和上次连接时间；历史设备会和扫描到的在线设备一起显示在“设备”列表中，并可单独修改备注或删除保存记录。
- Android 说明：Android 已纳入构建与单元测试，签名版 1.0.1 有单台手机的有限实测记录；模拟器或单台真机验证不能替代不同厂商手机上的 MediaCodec、MediaProjection、无障碍输入和省电策略实测。
- 支持 Windows 和 Android 被控端接入的基础字段：设备发现和历史设备会携带平台信息，以及桌面、输入、剪贴板、文件、远程启动等能力标记。
- 支持连接后的设备信息协商：即使是手动输入 IP 连接，也会在认证成功后记录对方平台和能力，用于后续历史设备展示与跨平台被控端接入。
- 支持 Android 被控端与查看端：Android App 可用 Android Keystore 加密保存本地口令、请求屏幕录制授权、常驻前台服务、响应局域网扫描，并通过同一套认证/加密 TCP 协议输出自适应 JPEG 或 `MediaCodec` H.264 画面；启用 RemoteDesk 无障碍服务后，可进行基础点击、拖动和滚动输入；支持手动文本剪贴板同步和接收 Windows 控制端发送的文件。Android 查看端也可显式轮换 AVC 解码器并直接输出到 `SurfaceView`。
- Android App 只要前台界面打开，即使还没授权录屏，也会响应局域网扫描为“App 已打开，等待录屏授权”，方便控制端确认手机在线；真正连接仍需在手机上点击启动并授权屏幕录制。
- Android App 可手动启动“发现常驻”前台服务，在未打开界面、未授权录屏时仍响应局域网扫描；真正远程桌面仍需在手机上点击启动并授权屏幕录制。
- Android 主界面提供“复制连接信息”，会复制本机 IPv4、连接端口、发现端口、录屏/无障碍/H.264 状态；如果 UDP 扫描受网络限制，可以把复制出的 IP 直接填到 Windows 控制端。
- Android 预览发现和前台服务发现共用带短重试的 UDP 端口绑定逻辑，减少从界面预览切到正式被控服务时端口尚未释放导致的短暂扫不到。
- Android 被控端正式推流时会 best-effort 持有 Wi-Fi 低延迟/高性能锁和 partial wake lock，减少省电策略带来的网络与采集抖动；仅发现常驻模式不会启用这组更耗电的锁。
- Android 发现 UDP 异常时会独立关闭、有界退避并重绑发现 socket，不再停止已正常工作的 TCP/录屏会话。查看端将 JPEG 解码移到每个连接代际的 latest-only 容量 1 邮箱，TCP 收包和 UDP 重组不再同步等待 `BitmapFactory`。
- Android 查看端以 1 Hz 显示编码、分辨率、实际呈现 FPS、码率、TCP/UDP、采集/编码和输入 ACK 延迟。H.264 FPS 只在 `OnFrameRenderedListener` 确认 Surface 已呈现后计数，不把解码器接收或提交误报为用户已看到。远端未声明 `InputControl` 时明确显示“仅观看”并不吞掉触摸。
- Android 被控端的无障碍拖拽使用容量 1、latest-only 的 continued-stroke 段泵：移动在松手前持续提交，快速指针不积压历史坐标；断线、旋转和服务停止会有界结束已启动的 stroke，仅 MouseDown 而未移动时不会在清理阶段伪造点击。
- Android 主界面会显示电池优化状态，并提供系统入口请求允许忽略电池优化，减少长时间远程会话被省电策略限速或中断的概率。
- Android 主界面提供“导出诊断日志”，并会把最近事件持续写入 App 私有诊断日志；日志包含界面状态、发现服务、认证、视频编码、JPEG 画质/FPS/分辨率自适应、H.264 码率调整、剪贴板和文件接收事件，便于排查某台手机扫不到、黑屏、掉线或后台被限速的问题。
- Android 主界面会显示本机 H.264 编码能力，标出硬件/软件编码、CBR/VBR 支持或 JPEG 回退原因；正式启动时会按硬件、未知兼容、已知软件的顺序逐个用 codec name 显式创建，只有真实产出首个完整恢复 AU 后才把实际成功的 codec 回写到界面与日志，避免能力探测显示硬件、运行时却被 `createEncoderByType` 静默选到 AOSP 软件编码器。
- Android 被控端支持 JPEG 画面自适应：根据采集、编码和发送压力自动调整画质、帧率和最长边分辨率，优先压住延迟。
- Android 高分屏手机会先在虚拟显示层限制采集最长边，再进行像素拷贝和 JPEG 编码，同时保留真实屏幕尺寸用于输入坐标映射。
- Android 主面板会按可用宽度在单列/双列之间切换并限制大屏内容宽度；远程查看工具栏会结合宽度、高度和系统字体倍率选择横排、堆叠或矮屏紧凑模式，同时避让状态栏、导航栏、刘海与输入法区域。
- Android JPEG 采集链路会复用采集、裁剪、缩放和编码输出缓冲，减少每帧内存分配和 GC 抖动。
- Android 被控端会检测手机旋转或显示尺寸变化，自动刷新采集尺寸和输入坐标映射；H.264 硬编码流遇到尺寸变化时会重建旧编码器，尽量保持硬编码低延迟通道。
- 连接建立后控制端会发送查看端能力信息；Windows 8 及以上会声明进程内 MF/D3D11 H.264 硬解能力，并把 `ffmpeg` 作为额外回退，不再要求必须先安装 `ffmpeg` 才能协商 H.264。Android 被控端可切换到 `MediaCodec` H.264 硬编码；Android 查看端会按硬件、未知兼容、软件顺序显式轮换 AVC decoder，并直接渲染到 Surface。Windows 被控端会优先尝试 FFmpeg 的硬件 H.264 捕获链，任一端运行时实测失败都会自动保持或回到 JPEG。
- Windows 被控端会先解析物理显示器与 DXGI 适配器映射；当 FFmpeg 暴露 `gfxcapture` 时，优先建立 `Windows Graphics Capture → D3D11 BGRA surface → NVENC/AMF/QSV` 硬件链。WGC 两帧 frame pool、捕获源和编码器固定到解析出的适配器，源尺寸与协商尺寸相同，因此不会插入隐藏缩放或 CPU 整帧回读；同一显示器可轮换可用编码适配器。WGC 不可用或运行中断时，才继续尝试 DDA，再回退精确桌面坐标的 GDI + NVENC/MF/QSV/AMF。所有候选都关闭视频内的远端鼠标合成，候选必须真实产出首个完整恢复 AU 才算可用；冷启动为首个 WGC/NVENC 候选保留一次 `2.5s` 驱动加载窗口，真实首帧超时后直接跳过其余 WGC 候选进入 DDA，只有快速硬失败才继续轮换适配器，避免多个超时串成约 10 秒黑屏。编码进程退出、停帧或尺寸变化会安全重选。
- Windows H.264 捕获在协商 `ShortGopH264` 且源帧率关系安全时使用严格 `GOP=2` 的 `IDR/P` 交替序列；未协商该能力的查看端（包括当前 Windows Viewer）使用 `GOP=1`。两种模式都无 B 帧、无 lookahead，并使用容量为 `1` 的最新帧邮箱。编码平均码率和峰值仍受限，但 VBV 允许一个细节丰富的桌面恢复帧使用最多四帧预算，避免 1080p IDR 被压到约 `44 KiB` 后让小字发虚；这段容量不引入 B 帧或展示排队。NVENC 使用清晰度更高的 P4、温和空间 AQ 和低延迟 VBR，避免 CBR 为静态区域写入 filler NAL；它不会使用无限峰值的恒定质量模式。编码器输出通过仅限本机的 RTP 数据报保留包边界，收到 marker 包就立即组装当前 Annex-B AU，不再等待下一帧 AUD；若本机 UDP socket 已经可见更新的数据报，则先清空积压并只发布其中最新的完整 AU。为容纳硬件编码器冷启动时的短时 IDR 突发，RTP 接收缓冲在 `2–4MiB` 内动态限制；用户态仍只发布最新完整 AU，不让这段内核缓冲演变为显示队列。这里的“所有样本独立”实体证据仅覆盖 Windows-to-Windows GOP1；当前 Android 编码器同样要求 `GOP=1`、零 B 帧，并把未带完整 SPS/PPS/IDR 恢复语义的输出视为候选失败，但不同厂商真机上的 Windows MF `AllSamplesIndependent` 闭环仍待实体矩阵验证。
- Windows 进程生命周期内会 best-effort 请求 `1ms` 计时器分辨率并在退出时配对释放，同时关闭 `ExecutionSpeed` 与 `IgnoreTimerResolution` 进程节流标志，并把托管 GC 切到 `SustainedLowLatency` 后在最终释放时恢复原模式。这些是降低最小化、托盘驻留和 GC 唤醒抖动的提示，不是所有 Windows/驱动上的硬实时保证。
- UDP 视频发送、主机/查看端接收、鼠标发送和鼠标应用回执使用命名的同步专用线程，避免延迟关键循环依赖线程池 continuation 唤醒。UDP 视频发送、Viewer 接收和本机 FFmpeg RTP marker 读取使用 `Highest`；Host 接收、鼠标发送与应用回执保留 `AboveNormal`。反馈控制仍是独立异步任务。Windows 硬编码 FFmpeg 进程保持 `Normal` 基础优先级并加入 `KILL_ON_JOB_CLOSE` Job Object；仅提升短时阻塞热路径，不提高整个 Host/FFmpeg 进程而与输入竞争。健康退出先向标准输入发送 `q` 并等待 `1000ms`，超时才终止完整进程树并等待 `2000ms`。已经因超时/停帧执行过强杀的失败候选不会再重复优雅等待，只确认 `500ms` 后就把仍未退出的句柄交给后台 reaper，避免阻塞硬件 fallback；极端情况下后台会继续重复终止，直到确认退出后释放进程与 Job 句柄。
- 控制端可选择“稳定 JPEG”强制只使用 JPEG，或选择“强制 H.264”只声明 H.264 能力；强制 H.264 要求本机具备 MF/D3D11 硬解平台或可用 `ffmpeg`，可用于 Windows 或 Android 硬编码链路调试。
- Windows 查看端在自动低延迟模式下如果连续收不到可解码的 H.264 画面，会先重建失步的解码桥并请求恢复帧，仍无法恢复时才要求被控端回退 JPEG，避免 H.264 管道异常时长时间黑屏；强制 H.264 模式不会自动回退 JPEG。
- 控制端状态栏会显示本机声明的视频能力和 `ffmpeg` 路径；远程窗口会在 JPEG/H.264 画面流切换时立即提示，并显示实际解码后端、FPS、采集/编码/解码耗时、收帧到显示、码率、Ping/Pong RTT 和鼠标应用回执延迟，便于真机调试。右键“复制当前状态”和状态提示都会同时包含这组性能细节。外部 FFmpeg 硬编码链暂时无法提供准确的采集/编码拆分耗时，因此会显示 `—`，而不是把未知值误报为 `0.0ms`；被控端日志另行记录源帧在本机最新帧邮箱中的排队年龄。
- 远程窗口底部状态栏会稳定预留性能细节区域，并在性能数字刷新时局部重绘，减少长时间控制时的状态条闪烁。
- H.264 依赖 GOP 画面会按参考顺序送入解码器，压缩帧队列最多保留 `4` 帧；拥塞时从最新恢复点重新开始，找不到恢复点则丢弃孤立 P 帧、等待并节流请求新关键帧。Windows-to-Windows 协商 GOP1 时每个 SPS/PPS/IDR 都可独立解码，新恢复帧会立即替换尚未解码的旧压缩帧，避免把短暂调度停顿变成最多四帧的陈旧 FIFO。解码后等待 UI 显示的画面始终只保留最新一帧。
- Windows 查看端优先使用系统 inbox H.264 MFT，把 Annex-B AU 直接硬解为同一 D3D11 device 上的 NV12 texture；UI 只保留最新一个 GPU frame lease，经 `VideoProcessorBlt` 直接送到远程画面 HWND 的五缓冲 flip-model swap chain，以 `Present(0)` 提交，并在系统支持时同时启用 swap-chain/present tearing 标志。参考 Moonlight 的真实驱动行为，这条路径不强制 `MaximumFrameLatency=1`，避免某些驱动把非同步 Present 反向变成等待 DWM/VBlank；同时保留足够缓冲供 flip queue 与 DWM 使用。这条链不再执行 GPU→CPU readback、BGRA rawvideo pipe、Bitmap 复制或 GDI 画面呈现。Fit 模式由视频处理器直接生成黑边；源尺寸与窗口尺寸不同时，驱动支持的 D3D11 VideoProcessor 会 best-effort 启用温和边缘增强，若驱动拒绝则关闭该滤镜并重试，不影响画面。边缘增强只能改善非原生缩放观感，不能恢复已经丢失的像素。尺寸变化、旧 decoder generation、设备移除、依赖帧缺口及 HWND 重建都有显式恢复。遮挡和最小化不会触发重建风暴，WrongDevice 会用当前 frame 的 device lease 单次重建 presenter。
- 远程窗口底部提供“切换屏幕”，无需返回主窗口即可按远端屏幕列表循环选择；只有一个目标或远端不支持切屏时会禁用或隐藏，并在提示中显示下一目标。也可在“适应窗口”和“1:1 清晰”间切换；1:1 模式会让尺寸不超过查看区域的画面按原始像素居中，避免 1080p 在 2K/4K 查看端被插值放大后发虚，画面大于查看区域时仍会等比缩小。软件 Bitmap 与 MF/D3D11 硬件直显使用同一规则，鼠标坐标也按真实显示区域映射。`F11`/“全屏”会进入当前显示器完整边界的无边框真全屏，并隐藏状态栏和进度区；退出后恢复原窗口状态。状态栏会显示当前显示比例；4K 画面被窗口缩到 `80%` 以下时会提示用 `F11` 查看更清晰，但显示比例仍受控制端物理分辨率限制。
- MF/D3D11 或 presenter 的静态能力探测失败时，同一个 SPS/PPS/IDR 恢复 AU 会立即交给外部 `ffmpeg`，不会先丢帧或直接退 JPEG；依赖 P 帧失步则不会错误启动一个没有参考链的新软件解码器，而是等待并请求新的恢复 AU。对 `GOP=1` 全独立帧，若 MFT 接受当前 AU 后暂不输出，查看端会执行一次 drain、立即取出被驱动扣留的该帧，再恢复流并标记下一输入 discontinuity；不会对依赖 GOP 使用这条路径。旧 4K30 exact 包在 `192.0.2.249` 三次连续门槛中的 MF receive-to-present 为 `0.89/0.88/0.89ms`，均无直显失败；这只是历史基线，本机集成 smoke 也只验证基本参考链与资源释放，二者都不能替代最终 4K60/1440p60 或其它实体设备验收。
- 外部 `ffmpeg` 回退桥会显式指定 Annex-B H.264 输入、`32` 字节探测、低延迟解码、自动 slice 线程和零输出同步。约 `2MP`（含 1080p）以内直接读取固定尺寸 BGRA rawvideo，用池化缓冲和单次紧密内存复制生成 Bitmap；更高分辨率为避免超大原始管道吞吐，使用单 worker MJPEG，并显式选择 `yuvj444p`、质量 `q=2`，减少高分辨率兼容回退的色度模糊。每个网络 AU 后会补一个明确的 AUD 边界，后续 AU 若自带前导 AUD 会去重，因此首个 AU 可以一进一出，不再用两帧预热队列等待后续输入。整个写入、flush、匹配输出事务共用 `250ms` 绝对期限，失步就销毁进程而不复用不可信输出。
- 外部回退桥首次收到恢复 AU 时会让 Software、CUDA、D3D11VA、D3D12VA、DXVA2、QSV 和 AMF 同时用同一 AU 竞速，首个真实画面立即成为当前后端；慢候选的结果与进程在后台释放，不阻塞赢家返回。`h264_cuvid` 因实测会跨多个 AU 预读、无法满足一进一出事务而不参加。赢家按 FFmpeg 路径和精确分辨率缓存，同尺寸重建只启动已验证后端；后端失效才清缓存并重新竞速。远程窗口会显示实际 `H.264/MF/D3D11（硬解）` 或外部 `H.264/<backend>` 以及硬解/软件择优状态，而不是只显示能力列表。
- Windows 查看端在 H.264 短暂解码无画面时会先请求被控端输出新的关键帧，仍无法恢复时再回退 JPEG，减少不必要的编码通道切换。
- Android H.264 发送链路只有在双方声明高帧率能力且 Android 存在实际硬件 Surface AVC 候选时才请求 `60fps`；未知兼容或软件 codec 自动封顶 `30fps`。它会枚举全部 Surface 输入 AVC 编码器并逐个显式创建硬件候选，先按厂商 `VideoCapabilities` 对齐尺寸并验证 size/rate，硬件 60 FPS 不支持时再试 30 FPS；只有全部硬件 codec 的低延迟 CBR/VBR/default 组合都失败后，才进入兼容层。每次启动必须真实产出带 SPS/PPS 的恢复 AU 才算成功；静默启动或运行中无输出会拒绝当前 codec 并轮换下一候选。每个候选都使用实时优先级、GOP1、零 B 帧及输入/运行帧率限制；运行时还会检测输出 PTS 回退，并根据 TCP 发送压力与 UDP FeedbackV2 的保守上限动态调节码率、请求关键帧。
- Android 查看端会枚举全部 AVC decoder，按硬件、未知兼容、软件顺序以准确 codec name 显式创建；只有非空的真实画面输出通过 `releaseOutputBuffer(..., true)` 送到当前 `SurfaceView` 后才认定候选成功。codec-config、EOS、旧 Surface 的迟到输出都不会误报为画面；连续无 Surface 输出会拒绝并轮换候选，全部失败才协商 JPEG。窗口模式下画面区域会避开顶部工具栏，默认不把已经能完整显示的 1080p 画面插值放大；工具栏可切换“适应窗口/1:1 清晰”，也可在 H.264 流畅模式与 JPEG 文字清晰模式间即时切换。
- Android H.264 编码器带两阶段输出 watchdog：启动或运行中长期没有编码输出时先请求 IDR，继续无帧才在自动模式切换 JPEG；强制 H.264 模式会明确断开并记录原因，避免编码器“启动成功但不出帧”造成长期黑屏。
- Android 端从预览扫描切换到正式被控服务时，会先释放 UDP 发现端口，减少“手机端已点启动但服务绑定失败”的假启动问题。
- Android 被控端 TCP 认证阶段会设置超时，避免半连接长期占用唯一控制端名额；认证成功后恢复正常阻塞读取。
- HMAC-SHA256 挑战认证，不直接在网络中发送明文口令。
- 认证成功后使用 AES-GCM 加密会话，画面、输入和控制消息都会加密传输。
- JPEG 兼容画面路径可设置帧率和画质；具备硬件能力时优先协商低延迟 H.264。
- 支持清晰优先低延迟自适应：Windows 被控端按 `1` 秒窗口响应压力，先降低帧率、再降低 JPEG 质量，只有连续 `3` 个严重压力窗口且前两项已到可读下限时才最后降低分辨率；恢复需连续两个舒适窗口，并按目标 FPS 与下一档像素面积校验余量后优先恢复分辨率，避免 `75%` 与 `100%` 来回振荡。Windows H.264 不再因高分辨率或交互升帧而隐式缩到 1080p：选择 `100%` 时保留 4K 原生空间尺寸；只有超过协议 `16,777,216` 像素预算的超大拼接桌面会按安全上限等比缩放并明确提示。4K30 GOP1 预算为 `0.24 bit/pixel/frame`、约 `59.7 Mbps`；30→60 FPS 的每帧预算连续插值到 `0.16 bit/pixel/frame`，4K60 约 `79.6 Mbps`，并在 QHD→4K 之间平滑混合，避免跨过 FPS 或偶数尺寸边界时总码率反向下降。空间分辨率仍保持原生 4K，总上限 `160 Mbps`。高帧率 UDP 的持续拥塞估计连同分片/FEC 余量约为 `102 Mbps`；在无不利反馈的干净链路上，至少 `128 KiB` 的 GOP1 独立帧临时使用 `160 Mbps` 发送下限，使实体样本约 `189 KiB` 的线上数据在约 `9.7ms` 内完成，不把相邻源帧串行化。一旦出现丢包、反馈停滞或接收压力，拥塞目标立即恢复为严格上限。预算本身不构成验收；本机预发布源构建已经通过 30 秒原生 4K60 loopback 数量、节拍、MF/D3D11 和输入门槛，但最终 exact package 的双机网络与长稳仍需单独复验，真实拥塞也会快速降到 30 FPS。此前 4K30 exact 包三次连续样本均为 `864` 个原生 `3840×2160` 监测帧，这组 30 FPS 证据不会被冒充为新的长稳验收。
- 支持选择“所有屏幕”或指定显示器，适配常见双屏和多屏布局。
- Windows 被控端会在显示器拔插、边界或选中目标变化时动态重发屏幕列表和状态。指定物理屏幕丢失时会清空旧画面、暂停坐标输入并等待同一屏幕恢复，不会静默切到虚拟桌面或其他屏幕。切屏使用带代际的顺序屏障隔离旧帧；认证 UDP 心跳和鼠标路由仍健康时，只将画面有序切回 TCP，保留低延迟 UDP 鼠标。
- 支持连接后从控制端切换被控端捕获屏幕。
- 支持传输缩放，降低双屏和高分辨率场景下的带宽与 CPU 压力。
- 传输缩放下的启动、切屏和实际采集会共用同一套帧尺寸计算，避免首帧前或刚切换屏幕时鼠标坐标校验偏差。
- 控制端输入队列会合并连续鼠标移动，并在队列压力过高时只淘汰旧鼠标移动；点击、键盘和松开事件不会为了接纳普通新事件而被删除，降低操作滞后并避免输入状态卡住。
- 支持心跳检测，连接超时或异常断开时会及时回到未连接状态。
- 支持连接/认证超时、TCP keep-alive、临时屏幕采集失败重试。
- 支持文本剪贴板双向同步：可用按钮手动发送/读取；Windows 远程窗口中的 `Ctrl+V` / `Shift+Insert` 物理直传到远端，`Ctrl+C` / `Ctrl+X` 在远端执行后会触发读取远端剪贴板。
- 支持控制端向被控端发送文件，走已认证的加密连接并分块写入，完成后保存到被控端下载目录；新发送端使用 `32 KiB` 分块，接收端仍兼容协议允许的最大 `128 KiB` 块，缩短同一 TCP 连接上单个文件消息占用发送锁的时间。
- 支持把本机文件或文件夹直接拖放到远程桌面窗口；文件夹会自动打包为 zip。若操作系统拒绝为该窗口注册拖放，程序会记录并提示降级、只关闭拖入功能，观看和输入控制保持可用；主窗口“粘贴文件”仍可发送已复制的文件。连接到新版本 Windows 被控端时，会先聚焦拖放位置、再把接收后的文件放入远端文件剪贴板并触发粘贴。Android 或旧版 Windows 会保存到被控端接收目录。
- 支持直接粘贴本机剪贴板文件列表：控制端复制本机文件或文件夹后，点击主窗口“粘贴文件”，会复用加密文件传输发送到 Windows 或 Android 被控端；文件夹会作为 zip 文件接收。
- Windows 控制端发送文件、拖放文件、粘贴文件，Linux 图形控制端发送文件/文件夹，以及从新版本 Windows/Linux 被控端拉取文件时，会在真实传输前弹出确认窗口，列出文件/文件夹大小、原始位置和传输后位置；取消后不会开始分块传输。
- 支持新版本 Windows 被控端把远端剪贴板文件回传到控制端：远端复制或剪切文件/文件夹后会自动触发取回，也可在远程窗口或主窗口点击“取回文件/取回远端文件”，或按 `Ctrl+Shift+R` 手动触发。回传会复用同一套加密分块协议，显示确认清单、约每 10% 的接收进度与持续忙碌状态，自动合并重复请求；完成后文件保存到本机下载目录并写入本机文件剪贴板，文件夹会自动打包为 zip。该能力通过 `FileSend` 能力位协商，旧端和 Android 端不会误启用。
- 新版本 Windows-to-Windows 文件发送、拖放、粘贴和拉取会通过 `FileChecksum` 能力位协商 SHA-256 内容校验；双方声明支持后，缺失或不匹配的校验值会被拒绝保存。旧端或 Android 未声明该能力时仍使用原有加密分块流程。
- 新版本 Windows-to-Windows 文件传输中途失败时会通过 `FileTransferCancel` 能力位协商取消消息，接收端会立即关闭并删除未完成的临时文件；发送端也会检查源文件长度和修改时间，发现传输期间文件变化时主动中止。旧端未声明该能力时保持原有断线/下次传输清理路径。
- 支持 Windows-to-Windows 双向版本同步：发布脚本会把打包时间写入 exe，连接后可对比本机和远端版本；点击“同步更新”后，新的一端会把当前 `RemoteDesk.exe` 传给旧的一端，被更新端通过 SHA-256 校验后启动独立 updater，退出当前进程、替换原 exe、以 `--tray` 重启，并删除旧 exe 备份。旧端、Android 和 Linux 不声明该能力时不会显示为可用操作。
- 包含 Linux/WSL 被控端原型 `scripts/linux/remotedesk_linux_host.py`：Windows 控制端可连接它、通过常驻 `ffmpeg x11grab` 查看 X11 画面并注入基础输入、发送文件到 Linux，也可取回 Linux 指定文件或剪贴板文件。查看端声明 H.264 时，Linux host 会依次实际探测 NVENC、QSV、VA-API、V4L2 M2M 硬件编码器，首个能产出完整 Annex-B AU 的候选才会启用；否则按协商安全回退 JPEG。文件路径支持 GNOME 文件剪贴板、X11 `text/uri-list`，安装 `wl-clipboard` 后也支持 Wayland `wl-paste`。单次超过 32 项会在确认清单和完成状态中说明；该路径已支持 SHA-256 校验和取消消息，Wayland 原生采集、完整键盘和托盘体验仍需硬化。
- 鼠标移动、点击、滚轮和常见键盘输入转发。

## 限制与安全边界

- 这是局域网 MVP，不建议暴露到公网。
- 会话加密强度取决于访问口令，请使用足够长且不易猜的口令。
- 自动发现使用 UDP 广播、受限同网段定向探测，以及手填 IP/历史设备的 TCP 被控端口兜底探测；TCP 兜底只读取 RemoteDesk 握手魔数，不发送口令，仅用于局域网内查找正在运行的 RemoteDesk。远程启动请求需要同口令 HMAC 校验，真正桌面连接仍需要原有口令认证。
- TCP 兜底探测读到握手后会主动断开；Windows 与 Android 被控端都会把这类早期断开视为未完成认证，不再记录成口令认证失败。
- 被控端口令和连接口令会被 Windows DPAPI 加密保存到当前用户配置中；它们不是明文，但仍应保护好 Windows 当前用户账户。
- 不支持锁屏界面、UAC 安全桌面、Ctrl+Alt+Del 等系统安全场景。
- 多屏模式会捕获 Windows 虚拟桌面区域；显示器越多、分辨率越高，对局域网带宽和 CPU 压力越大。
- 传输缩放会降低远程画面清晰度，但鼠标坐标会映射回真实屏幕坐标。`100%` 传输只保证源端不主动降采样；若控制端窗口小于远端 4K 画面，显示仍会缩小。可使用“1:1 清晰”避免把较小画面放大，或按 `F11` 尽量使用当前显示器的完整像素；D3D11 驱动支持时会对非原生显示比例做温和硬件边缘增强，但它不能恢复缩放丢失的细节。
- 剪贴板同步当前仅支持文本，不做后台轮询自动同步；本机到远端使用按钮，远端到本机也可由远程窗口中的复制/剪切快捷键触发。
- Android 剪贴板读取可能受系统版本和前后台状态限制；Android 后台服务也无法可靠取得其它应用复制文件的 `content URI` 授权，因此当前不声明 `FileSend`，仅支持由 Windows 向 Android 发送文件；若收到不兼容的取回请求，会明确说明限制并结束等待状态。
- 文件传输单文件上限为 `1GB`；每个已认证接收会话最多接受 `128` 个文件、累计最多声明 `2GB`，且开始接收前必须为目标磁盘保留文件大小外加 `256MiB` 安全余量。传输中断时接收端会清理未完成的私有随机临时文件，新端会优先使用取消消息即时释放临时文件，保存前会在双方支持时强制校验 SHA-256，校验缺失或失败会拒绝落盘；并发接收同名文件会原子落盘并自动改名，不覆盖已有文件，也不会把用户自己的 `*.rdtransfer` 当作临时文件清理。若源文件传输中被修改，发送端会中止本次传输并提示重发。直接粘贴、拖放或拉取文件一次最多处理 `32` 个项目，文件夹会先安全打包为 zip 再走同一套分块协议；打包时排除输出包自身并跳过符号链接/重解析点。支持传前确认的路径会先展示大小、原始位置和保存位置，确认后才开始发送；从远程画面直接拖出是显式手势，会自动确认本次回传。Windows 定位拖放依赖远端当前窗口支持文件粘贴，若目标不接受粘贴，文件仍保留在 `Downloads\RemoteDeskReceived`。
- Linux/WSL 被控端已具备常驻 `ffmpeg x11grab` 采集：优先使用 NVENC、QSV、VA-API 或 V4L2 M2M 输出 H.264 Annex-B，全 IDR、无 B 帧、四帧有界桌面恢复 VBV、AUD 分帧并为恢复帧补齐 SPS/PPS；缩放使用 bicubic 而非会明显软化文字的 fast-bilinear，NVENC 使用 P4/VBR 与温和空间 AQ。编码线程只覆盖 latest-only 邮箱，网络发送受阻时不会累积历史画面。候选必须通过首 AU 实际探测，运行中失效会轮换剩余硬编，全部不可用时仅在查看端声明 JPEG 后回退 MJPEG/ImageMagick。显示尺寸由独立低频后台任务刷新，输入与采集热路径只读缓存，不再调用 `xdpyinfo`。Wayland 原生采集、完整键盘映射及物理机端到端延迟仍需继续硬化和实测。
- 协议层会按消息类型限制负载大小：输入、控制、心跳和画面帧分别有独立上限，异常长度会被拒绝。三端的发送和接收入口都把单帧限制为最多 `16,777,216` 像素，并在 JPEG/PNG 真正解码分配前核对压缩图内部尺寸；Windows/Linux 外部 FFmpeg 解码桥还限制单次分配最多 `128MiB`。
- Windows 运行的是带有效嵌入构建号的 `RemoteDesk.exe` 时会声明远程更新能力。已签名安装仍只接受系统信任、签名公钥与当前程序一致且构建号严格更新的候选；当前程序未签名时进入个人内网模式，只接受同样未签名且构建号严格更新的 RemoteDesk。两种模式都在替换前后复验文件 SHA-256 和对应签名状态，拒绝同版、降级、签名模式混用和带无效签名的包，并在新进程路径、监听端口或 `RDK1` 健康检查失败时自动回滚。未签名模式依赖现有口令认证和加密通道，适合自用内网，不等同于公开分发的发布者身份保证。
- 已加入 Windows 协议、消息、局域网发现、远程启动、设置迁移、连接诊断、查看端视频模式和查看端 loopback 兼容被控端测试，以及 Android 控制消息解析、诊断日志、连接信息格式化和 H.264 编码能力文案单元测试，覆盖认证握手、口令拒绝、加密消息往返、异常负载长度、控制消息尾随数据、输入消息长度、视频帧标志校验、H.264 关键帧请求、定向发现响应解析、TCP 兜底探测、远程启动签名校验、过期请求拒绝、Android 查看端能力归一化、Android 文件块异常长度拒绝、日志环形保留、复制连接信息、编码能力回退提示，以及 Windows 查看端连接兼容被控端后接收 Android 设备信息、屏幕列表、JPEG 帧并发送文件分块。
- Android 本地口令会使用 Android Keystore 加密保存；旧版明文偏好值会在首次读取后迁移并删除。
- Android 10 及以上接收文件会导出到公共下载目录 `Downloads/RemoteDeskReceived`；旧系统会回退到应用私有下载目录 `Android/data/com.remotedesk.agent/files/Download/RemoteDeskReceived`。
- Android 主界面提供“查看接收文件”入口，可列出、打开、分享接收文件；应用私有回退目录中的文件会通过只读内容 Provider 暴露给用户选择的应用。
- 主窗口支持“粘贴文件”和非输入框区域的 `Ctrl+V` / `Shift+Insert`：如果本机剪贴板是文件列表，会直接发送文件。Windows 远程窗口内的键盘快捷键全部按物理扫描码交给远端，其中 `Ctrl+V` / `Shift+Insert` 使用远端剪贴板，`Ctrl+C` / `Ctrl+X`、`Ctrl+Insert` / `Shift+Delete` 在远端执行后会探测远端文本和文件剪贴板；文本会写入本机剪贴板，文件会进入确认后的回传流程。Windows 查看端还可把远端资源管理器中的选中项目拖出远程窗口，并在文件回传落地后接续为本机 `FileDrop` 拖放。
- 连接 Android 被控端时，远程窗口内 `Ctrl+鼠标滚轮` 会映射为手机双指缩放；普通滚轮仍是滚动。
- 连接 Android 被控端时，远程窗口右键映射为返回，中键映射为主页，并会发送完整按下/释放事件；Android 端会显式识别释放事件且不要求画面尺寸；Enter 会作为文本换行输入。
- 开机自启是当前用户 HKCU Run 启动项，需用户在界面勾选；程序常驻时会显示托盘图标，可从托盘菜单退出。
- Windows 被控端支持观看和输入控制、剪贴板、文件传输、屏幕选择和远程启动；Android 被控端目前支持被扫描、发现常驻、远程观看屏幕、手动文本剪贴板同步、文件接收，以及通过无障碍服务进行基础点击/拖动/滚动、双指缩放、少量系统动作快捷键和聚焦输入框文本输入。Android 因系统录屏权限限制，暂不支持像 Windows 那样静默远程启动录屏被控端，也暂不支持完整键盘和完整多指手势。
- RemoteDesk 使用自有加密协议，不是通用 RDP/VNC 客户端。Linux/Ubuntu 包已实现 X11 采集、条件式 XTest/`xdotool` 输入、文本/文件剪贴板和双向文件传输，但尚未实现 Windows/Android 的低延迟 UDP 位 `13`–`17`，与其它端互连时使用加密 TCP；Wayland 原生全桌面、完整键盘、托盘与不同硬件编解码器仍需物理机实测和硬化。Android 查看端键盘、文件/剪贴板完整 UI 和被控端无人值守录屏等功能仍未与 Windows 完全对齐。
- 已按“最新状态优先”的方向收紧 Windows、Android 与 Linux 的画面/输入队列、TCP 缓冲和文件分块，增强 Android `MediaCodec` H.264 实时参数及 Windows `ffmpeg` 恢复策略，并为 Windows-to-Windows 分离出可丢弃的加密 UDP 画面与鼠标移动通道；新 Windows 对端可协商 `FeedbackV2` pacing、`16+1` XOR FEC、UDP 鼠标应用回执和坏源端口自动轮换。Windows 被控端具备 WGC/D3D11 surface、DDA/GDI fallback、NVENC/MF/QSV/AMF 硬件 H.264、协商式 GOP1/GOP2 latest-only、本机 RTP marker 即时分帧、无滞后光标合成与故障回退；当前 Windows 查看端基于实体驱动兼容性选择 GOP1，并使用进程内 MF/D3D11 硬解、NV12 GPU surface latest-only、五缓冲 flip-model/可选 tearing 直显，外部 FFmpeg/Bitmap/GDI 仅保留为兼容回退。原生横屏 UHD60 的 WGC 会请求 `240 FPS`、在约 `160 Hz` 的 compositor 上按源时间每个 60 Hz 桶最多保留一帧，再声明连续 60 FPS 时间基；不复制静态帧，30 FPS、竖屏和缩放路径不受影响。本机 `gfxcapture/monitor1/adapter0 → h264_nvenc` 预发布源构建已通过 30 秒 4K60 loopback 严格门槛；`.249` 旧 exact 包仍只保留为 4K30 历史证据，最终 exact package 的双机网络与长稳不能由本机结果替代。WLAN 媒体模式与 qWAVE 已在日志中确认启用；独立 ICMP 探针复现近似周期的无线停顿，支持无线链路/驱动是重要贡献因素，但不能据此排除所有应用侧 outlier。

## 构建

```powershell
dotnet build
dotnet test
```

仓库固定使用 .NET SDK `8.0.424`（`global.json` 为 exact pin，`rollForward` 为 `disable`）。Android 构建优先使用 `src\RemoteDesk.Android\gradlew.bat`，其 wrapper 固定 Gradle `8.11.1` 并校验官方 distribution SHA-256；只有显式传入 `-GradlePath` 时才覆盖仓库 wrapper。发布脚本会在任何 canonical 制品变更前解析本次 scope 所需的 .NET、Gradle、Java 和 WSL Python，并先完成自动测试/Android 构建；正式打包使用同卷临时备份，完整制品集合和 manifest 校验成功后才提交，失败会恢复旧集合。Linux 发布与 Linux 测试、runtime 复制、zip/tar/deb 打包共用同一个 `-LinuxDistro`（默认 `Ubuntu-24.04`），并把该 distro 写入 manifest toolchain；复验时若选择的 distro 与 manifest 不一致会明确失败，避免只探测一个 WSL 环境却用另一个环境生成包。

`RemoteDesk-release-manifest.json` 是 canonical 制品身份的唯一来源：除六个制品的长度和 SHA-256 外，它还记录 `sourceRevision`、版本化的 `sourceFingerprint`/`sourceFileCount`、dirty/internal-candidate 标志和实际 toolchain 版本。`Invoke-RemoteDeskCheck.ps1` 会重新计算当前源码指纹并验证 manifest 工具链；源码或环境变化后不会把旧 manifest 报为 exact package。

Windows 查看端的 MF/D3D11 H.264 硬解使用系统 inbox codec，不要求
安装 `ffmpeg`；`ffmpeg.exe` 仍是查看端兼容回退和 Windows 被控端硬件
捕获所需的可选外部组件。被控端的完整 GPU 路径要求该 FFmpeg 构建包含
`gfxcapture`，或兼容回退所需的 `ddagrab`/`scale_d3d11`，以及至少一个实际可用的
`h264_nvenc` / `h264_mf` / `h264_qsv` / `h264_amf`，并包含 RTP muxer 与
UDP protocol。程序会用真实首帧
逐个探测，不会因为编码器仅出现在列表里就假定驱动可用。未安装
FFmpeg、驱动探测失败或多屏映射不安全时，程序仍可运行并自动使用
兼容路径或 JPEG；仓库与发布脚本不会静默下载或捆绑第三方 FFmpeg。

1.0.0 的本机最终发布命令（先提交源码，使用已初始化的仓库外 Android 发布密钥）为：

```powershell
pwsh -NoProfile -File .\scripts\Publish-LocalRelease.ps1
```

开发期间需要构建未提交源码时，仍可明确创建内部候选；它不能冒充最终发布包：

```powershell
.\scripts\Publish-RemoteDesk.ps1 -AllowDirtySource
.\scripts\Invoke-RemoteDeskCheck.ps1 -AllowDirtySource -ScopeCheck -RunTests
```

具备受信任的 Windows 代码签名证书时，可直接从证书库签名并强制加时间戳；
证书私钥或 PFX 密码不会作为脚本参数传入：

```powershell
.\scripts\Publish-RemoteDesk.ps1 `
  -WindowsSigningCertificateThumbprint <40位证书指纹> `
  -WindowsTimestampServer <Authenticode时间戳服务URL>
```

脚本只在签名回读为 `Valid`、签名者指纹一致且时间戳存在时继续打包；
这些信息会写入 release manifest，并由验收脚本与 canonical exe 逐项复核。

`-WindowsOnly`、`-LinuxOnly` 与 `-SkipAndroid` 仍可用于局部开发检查，但它们生成的局部集合不是当前三端 canonical，也不作为本文的完整发布命令。

完整发布会依次运行 Windows Release、Linux Python、Android Debug/Release 单元测试及 Release lint，再生成自包含 `win-x64` exe、Windows zip、Linux zip/tar.gz/deb 和签名 Android Release APK。最终辅助脚本默认输出到 `artifacts\release-1.0.0`，记录源码提交、源码指纹、工具链与六个规范制品的 SHA-256，缺项或构建途中源码变化则失败。通用脚本不带 `-AndroidRelease` 时生成的 debug APK 仍只用于内部验证。

Linux ARM64 / 系统 Python 补充包单独构建：

```powershell
pwsh -NoProfile -File .\scripts\Build-LinuxSystemPackage.ps1 -OutputDirectory .\artifacts\release-1.0.0
```

`-AllowDirtySource` 不是干净正式发布的替代品；最终辅助脚本不提供跳过测试或脏源码绕过选项。

最终构建号、制品 SHA-256 和测试结果以 `artifacts\release-1.0.0` 中的清单和核验记录为准。历史 loopback 或实体目标性能数据不能代替最终包的双机长稳验收，不保证所有网络下 4K60。

通用构建默认产生内部测试包；选择正式发布流程才生成签名 Android Release APK，已有 Android 1.0.1 签名包在单台测试手机上验证。当前 Windows `RemoteDesk.exe` 没有 Authenticode 签名，manifest 的 SHA-256 完整性校验不能替代发布者身份签名。签名私钥不随源码提供，自行构建时需要配置自己的发布密钥；不要把 debug APK 当作正式签名包。

Windows 包中的 `DOTNET-RUNTIME-LICENSE.TXT` 与 `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT` 只描述随包携带的 .NET Runtime；它们不是 RemoteDesk 项目本身的授权声明。仓库目前没有项目级许可证，公开分发前需要由项目所有者明确选择并添加。

Linux/Ubuntu 被控端产物包括：

- `artifacts\release-1.0.0\RemoteDesk-linux-host.zip`：Windows 上方便解压查看的 x64 自包含包。
- `artifacts\release-1.0.0\RemoteDesk-linux-host.tar.gz`：Ubuntu/Linux x64 自包含便携包。
- `artifacts\release-1.0.0\remotedesk-linux-host_1.0.0_amd64.deb`：Ubuntu x64 安装包。
- `artifacts\release-1.0.0\RemoteDesk-linux-system-python.tar.gz`：使用系统 Python 3.12 的 Linux 包，支持实测 ARM64 Jetson。

Ubuntu 安装示例：

```bash
sudo apt install ./remotedesk-linux-host_1.0.0_amd64.deb
remotedesk-linux-app
remotedesk-linux-doctor
read -rsp 'RemoteDesk password: ' REMOTEDESK_PASSWORD; echo
printf '%s' "$REMOTEDESK_PASSWORD" | remotedesk-linux-host --password-fd 0 --port 56565 --receive-dir ~/Downloads/RemoteDeskReceived
unset REMOTEDESK_PASSWORD
sudo dpkg -r remotedesk-linux-host
```

Linux 查看端在当前连接代际收到 `DeviceInfo` 后才获得自动重连资格，使用 `0.5/1/2/4/8/10` 秒有界退避，并在连续稳定 5 秒后才重置退避；坏口令、首次未认证连接和手动断开不会形成重连风暴。连接到支持捕获目标的多屏 Host 时，查看工具栏可选择屏幕；目标缺失会显式标记不可用而不偷换其他屏幕，重连后会在新代际恢复同一目标。Linux UI 的日志、状态和画面事件都有容量/时间预算，停止 Host 与重连重回收不再阻塞 Tk 主线程。大目录传输预览在单飞后台任务中遍历，并以 viewer/connection generation 拒绝迟到结果；剪贴板工具在每会话有界 worker 中运行，不能阻塞唯一 TCP reader；文件回传 worker 的退出等待有上限。Linux 查看端也会跟踪成功发送的鼠标按键，失焦、最小化和断连时先在后台可靠释放并 flush，再关闭连接。

Linux 包内置 `remotedesk-linux-app` 图形入口，主窗口使用统一品牌页眉、清晰的被控/控制分区与状态卡片；各页在窗口高度不足时可独立滚动。主窗口、查看窗口和传输确认框会按屏幕可用范围及 Tk DPI 比例自动选取尺寸，窄窗口下标题、操作区与查看工具栏自动换行，远程画面也会随实际查看区域等比缩放。图形入口默认保持被控端关闭，只有用户填写非空口令并点击启动后才监听；它也可作为基础控制端连接其它 RemoteDesk 主机，支持 JPEG/H.264 画面、鼠标、滚轮、文本输入和图形化发送文件/文件夹（文件夹自动打包为 zip，传输前会确认大小、原始位置和远端保存位置）。Linux 被控端在 XTest 或 `xdotool` 与 `DISPLAY` 可用时会声明 `InputControl`，可接收远端鼠标、滚轮、常用键和文本输入。Linux viewer 和 host 都会异步发送/处理输入，并只保留最新未发送鼠标移动，减少拖动和点击时的历史事件堆积；Linux host 还会把画面发送与输入/控制消息读取拆到独立线程，避免大帧发送时拖慢鼠标和键盘响应。Win/Linux viewer 会更快请求 H.264 恢复帧，且在 H.264 恢复帧到来时会丢弃旧 H.264 待渲染队列，并且 Linux viewer 会合并尚未绘制的旧画面事件，只把最新帧交给显示路径，降低 UI 队列延迟。鼠标、滚轮和常用键优先走原生 XTest 注入，避免频繁启动 `xdotool` 子进程。全键盘快捷键仍是后续增强项。包内置 RemoteDesk 协议运行时和 `cryptography` 等 Python 依赖；当前 amd64 deb 使用 Ubuntu 24.04 WSL runtime 构建，要求 `libc6 >= 2.38`，目标系统建议 Ubuntu 24.04 或更新版本。GUI 启动、低延迟 JPEG 显示和连续采集依赖 `python3-tk`、`python3-pil`、ImageMagick 与 `ffmpeg`，建议用 `sudo apt install ./remotedesk-linux-host_1.0.0_amd64.deb` 安装以自动补齐依赖。原生 H.264 surface 呈现由推荐依赖 `mpv` 提供；若系统未自动安装，可运行 `sudo apt install mpv`。若用 `sudo dpkg -i`，请在依赖报错后运行 `sudo apt -f install`。Linux viewer 会优先使用 `python3-pil` 进行内存 JPEG 解码以降低延迟，未安装时自动回退到 ImageMagick。Linux host 在 `--capture x11` 下会读取 `CONTROL_VIEWER_INFO`：查看端支持 H.264 时，常驻 `ffmpeg x11grab` 管线优先实际探测并使用 NVENC、QSV、VA-API、V4L2 M2M 硬编；没有可用硬编或查看端只支持 JPEG 时，自动使用常驻 MJPEG，必要时再回退 ImageMagick。图形入口的可选启动参数默认是 1920x1080、60fps，并可切换到 540p、720p、900p、1080p、1440p、4K 与 15/24/30/60fps。物理 Ubuntu 若运行在 Wayland 会话下，X11 捕获和 XTest/`xdotool` 输入可能只覆盖 XWayland 窗口；完整桌面控制建议登录时选择 Ubuntu on Xorg。Windows 被控端开启自适应时，会先降低帧率和 JPEG 画质，只有持续严重压力才最后降低传输分辨率。X11 剪贴板可使用 `xclip`/`xsel`；Wayland 文件剪贴板需安装可选包 `wl-clipboard`（提供 `wl-paste`）。Unicode 文字输入需要 `xdotool`，即使原生 XTest 鼠标/实体按键已经可用；启动时的运行环境检查会将它列为缺项，并在同意后申请安装。Xvfb、x11vnc、wmctrl、dbus-x11 仍是可选的桌面测试工具。图形入口启动失败时会记录到 `~/.cache/remotedesk/remotedesk-linux-app.log`；运行 `remotedesk-linux-doctor` 可输出系统版本、依赖、DISPLAY、session type、ImageMagick、ffmpeg、剪贴板工具、native XTest 和包内 Python/Tk/cryptography/PIL 检查结果。

Linux viewer 在带 `DISPLAY` 的 X11/XWayland Tk 窗口中会取得远程画面控件的真实 XID，并优先把 Annex-B H.264 AU 直接送入可选的 `mpv --wid` 原生呈现器。它只尝试非 copy-back 的 NVDEC、VA-API、DRM PRIME，使用 `gpu-next + OpenGL + x11egl`，并分别强制 `cuda`、`vaapi`、`drmprime` GPU interop；QSV/V4L2 仍走下述 FFmpeg 兼容硬解路径，不把映射不确定的 surface 宣称为零回读。连接完成后会在声明 H.264 前预热首个 mpv 候选；强制原始 H.264 最小探测、单层 swapchain、关闭 OpenGL swap interval，并在一个 IPC 往返中批量读取硬解和 interop 属性。只有官方运行时属性 `hwdec-current` 与 `hwdec-interop` 同时精确等于请求的非 `*-copy` decoder 和 interop，状态栏才会显示“已验证无 CPU 回读”，verbose 日志仅用于提前否决和故障诊断。软件解码、`nvdec-copy`/`vaapi-copy` 等回读后端、llvmpipe、EGL/interop 初始化失败、进程退出或 1.5 秒内未完成双重确认都会自动关闭该候选，并无人工干预地继续 FFmpeg GPU→CPU 回读/MJPEG/Tk 或 JPEG。验证期最多同步生成一帧 Tk 兼容预览，且只等待 `40 ms`；后续 AU 优先持续送入 mpv，避免兼容解码等待拖慢原生激活。mpv 管道写入在独立线程中执行，待写 AU 最多保留 `2` 个；新的完整恢复 AU 会替换全部旧待写帧，预测链溢出则整链丢弃并请求新恢复帧，不会阻塞网络收包。JPEG 切换会关闭 mpv，窗口缩放和输入坐标仍由同一个 Tk XID 与远端尺寸映射负责。原生 Wayland Tk 窗口目前不启用这条 X11 嵌入路径。

Linux 当前源码还加入了**启动时申请安装缺失依赖**：先列出缺项并询问，确认后由系统授权窗口或终端 sudo 安装，复检通过才启动 App。支持 Debian / Ubuntu / Jetson，不会以 root 运行 RemoteDesk；取消就退出本次启动。使用方法和未覆盖条件见[运行环境说明](docs/PhysicalDevices-20260907.md#linux-启动时申请安装)。此更新已接入打包脚本，但旧的发布包不会自动获得新代码。

### Android

Android host/viewer 已纳入完整构建和 Gradle 单元测试。通用构建默认产出的 APK 是 debug 签名内部包；正式签名配置见构建章节和 `src/RemoteDesk.Android/release-signing.properties.example`。已在一台 Android 16 真机完成有限安装、画面和输入检查，范围见前述复核报告；这不能代替多厂商真机上的 `MediaCodec`、`MediaProjection`、无障碍输入、Wi-Fi/UDP、后台限制和断网恢复验收。

## 环境检查脚本

可用仓库内脚本快速检查三端发布产物、本机 IPv4、`ffmpeg` 和残留进程：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Invoke-RemoteDeskCheck.ps1
```

按当前 Windows+Linux+Android 完整目标范围检查：

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -ScopeCheck
```

运行完整三端自动检查时，同时验证 Windows、Linux/Ubuntu 与 Android 产物、局域网基础信息、Windows Release 测试、Linux Python 测试和 Android Gradle 单元测试：

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -ScopeCheck -RunTests
```

[Windows-Android-Acceptance.md](docs/Windows-Android-Acceptance.md) 是三端验收清单；[Windows-Android-CompletionAudit.md](docs/Windows-Android-CompletionAudit.md) 仅保留 2026-07-27 的 Desktop 历史证据。两者都不能代替实体测试结果。`artifacts/RemoteDesk-release-manifest.json` 只对应历史 1.0.0 制品，不是当前源码快照的构建或验收证明；发布新包时必须重新生成、验证清单。未验证边界见本 README 的“下一步优先级”及最新复核报告。

遇到某台机器扫不到时，可指定目标 IP 做 TCP 握手和 UDP 发现检查；加 `-Password` 会额外做 RemoteDesk 口令认证验证：

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -AllowDirtySource -Target 192.0.2.249
.\scripts\Invoke-RemoteDeskCheck.ps1 -AllowDirtySource -Target 192.0.2.249 -PromptForPassword
```

Windows-to-Linux 工作可先启动 WSL 桌面测试沙盒；沙盒说明见 [Linux-Sandbox.md](docs/Linux-Sandbox.md)。`-LinuxProtocolProbe` 会优先使用指定的 WSL 发行版，未安装时自动回退到本机 Python，并在结果中标明执行器。Linux host 当前支持发现、认证、常驻 FFmpeg X11 H.264/MJPEG 采集、文本剪贴板、双向文件传输、SHA-256 校验和取消消息；在 XTest 或 `xdotool` 与 `DISPLAY` 可用时还会声明基础输入控制。它不声明远程启动或 Windows UDP 画面能力，硬编、X11/Wayland 覆盖范围和输入可用性应以目标实体机探测结果为准。

```powershell
.\scripts\Start-RemoteDeskLinuxSandbox.ps1 -InstallDependencies
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -StartLinuxSandbox -LinuxSandboxStatus
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -Target 192.0.2.249 -PromptForPassword -LinuxProtocolProbe
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -Target 192.0.2.249 -PromptForPassword -LinuxProtocolProbe -LinuxProtocolProbeSendFile
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -Target 192.0.2.249 -PromptForPassword -LinuxProtocolProbe -LinuxProtocolProbePullRemoteFiles
```

如需把通用自动摘要写到另一个文件，不覆盖人工汇总的最终验收报告：

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -ScopeCheck -RunTests -WriteAcceptanceReport -AcceptanceReportPath artifacts\RemoteDesk-full-check-summary.md
```

`-RunTests` 在完整 scope 下运行 Windows Release、Linux Python 与 Android Gradle 单元测试；安装、启动、抓日志等模拟器/真机操作仍需显式传入对应 Android 参数。也可单独复跑 Linux 测试：

```powershell
python -m unittest discover -s tests -p "test_linux_*.py" -v
```
`-WriteAcceptanceReport` 会写入通用自动摘要，默认覆盖 `artifacts\RemoteDesk-acceptance-report.md`。如已经人工汇总某个 exact package 的实体测试报告，请使用 `-AcceptanceReportPath <其他路径>`，不要覆盖该报告。

## 下一步优先级

1. Windows 真实 4K60/1440p60：用最终 exact package 同步验证捕获、NVENC、网络、MF/D3D11 显示与鼠标回执，并把 30 分钟以上连续画面、断网恢复、远端整机重启、显示栈/驱动恢复和多显示器切换分别记录；不能用设置值、定期探针或旧 4K30 数据代替。
2. Linux 物理机闭环：在物理 Xorg/目标 GPU/真实网络上验证 1440p60 与 4K60 的捕获→硬编→传输→硬解→显示；补 NVENC、QSV、VA-API、V4L2、DRM/EGL 与长稳/恢复矩阵。WSL/Xvfb 结果只作为编码与协议节拍证据。
3. Android 真机闭环：在至少两类不同厂商/系统版本的物理手机上验证 Android↔Windows、Android↔Linux 与 Android↔Android，覆盖 MediaProjection 授权、MediaCodec 编解码、无障碍输入、TCP/UDP 回退、切换网络、锁屏/后台和断线重连；模拟器通过不代表这些项目已验收。
4. 发布收尾：完整制品清单必须在最终源码后重建并回读实际安装包哈希；继续保管和验证 Android 正式发布密钥，为 Windows 配置可信代码签名，并为 Linux 补齐发行包签名。项目级许可证仍待所有者明确选择，依赖许可证不能替代项目授权。
