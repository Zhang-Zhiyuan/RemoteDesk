# 安卓与 Linux 真机验证（2026-09-07）

本轮使用用户授权的测试手机和 Linux 真机，实际安装、运行并修复问题。
没有修改其它远控软件、设备锁屏设置、系统驱动、SSH 配置或公网服务器。
测试口令使用临时随机值，不复用设备解锁口令；报告不包含凭据。

## 结果

| 项目 | 实测结果 |
| --- | --- |
| Android 构建和 JVM 回归 | 322 通过、0 失败、0 跳过；新增 10 项输入编辑测试 |
| Linux 真机 Python 回归 | 243 通过、0 失败，包含 12 项中继测试；新增 23 项依赖和 5 项 Jetson 测试 |
| Windows 打包契约回归 | 23 通过、0 失败；本轮未重跑完整 Windows 套件 |
| 手机 H.264 Surface 硬解 | GOP1 / 实验 GOP30 两轮均通过；每例实际呈现回调 180，Surface 重建后 60，PixelCopy 像素验证通过 |
| 手机作为被控端 | 两轮真实加密 TCP 会话通过错误口令拒绝、JPEG/H.264 出图、点击、快速中英文输入、重连和连接替换；第二轮另验证 Emoji 输入及退格 |
| Linux 作为被控端 | FFmpeg 安装前复现 1×1 占位黑图；安装后 1920×1080 JPEG 及重连各 45 帧，约 29.8 / 29.9 到达 FPS，鼠标点击和按键实际进入测试窗口 |
| Jetson 硬件解码 | 产品自动选择 `h264_nvv4l2dec`；GOP1 / 实验 GOP30 各 60 次提交得到 58 个有效非黑输出，剩余 2 个未排空，未撞四项关联上限 |
| Linux 依赖安装 | 产品助手实际询问并通过 sudo 安装 xclip，复检 `missing: []`；真实 Tk 确认/取消和进度窗口通过；随后实际 App 入口完成依赖检查、显示窗口并正常退出 |

设备为 realme GT8 Pro / RMX5200（Android 16 / API 36）和 Jetson AGX Orin
（Ubuntu 24.04.4 ARM64、L4T R39.2）。不是模拟器硬件标记推断。

手机使用真实 Qualcomm `c2.qti.avc.encoder` / `c2.qti.avc.decoder`。
被控画面按现有 1600 长边策略为 734×1600，物理屏幕为 1440×3136。
两轮 H.264 各 120 帧，约 31.6 / 31.1 到达 FPS；重连、替换各 30 帧。
编码器的 60 FPS 配置不是实际 60 FPS 保证。JPEG 测试约 18.5 FPS，且测试中
包含通过 ADB 读取合成输入结果的开销，不能当作纯视频性能基准。

## 修复的问题

### Android 输入与密码显示

实际远控空输入框时，旧代码把 hint 当正文，并从缓存节点取得旧文本，
快速输入 `RemoteDesk实测42` 最终变成 `Remote text input target2`。
现在每次编辑前刷新聚焦节点，明确排除 hint，并统一处理选区、插入和
按 Unicode code point 退格。两轮真机复验得到完整文本，Emoji 删除不会留半个代理对。
服务实例失效时不再执行排队的文本编辑；诊断只记录异常类型，不记录字段内容。
实现依据：[Android 节点刷新](https://developer.android.com/reference/android/view/accessibility/AccessibilityNodeInfo#refresh())和[提示文字标记](https://developer.android.com/reference/android/view/accessibility/AccessibilityNodeInfo#isShowingHintText())。

通用 UI 样式调用 `setSingleLine(true)` 会替换输入框的 transformation，
导致密码框丢失遮罩。现在保留原 transformation。独立测试 Activity 在真实
Android 控件上验证文本、Web 和数字密码三类输入仍被遮罩。

### Jetson 解码兼容

这台 Jetson 的 FFmpeg 同时列出 CUVID 和 NVIDIA V4L2 解码器，但桌面 CUDA
候选实际初始化报 `CUDA_ERROR_INVALID_DEVICE`，并不能据列表判定可用。
产品现在在 FFmpeg 明确列出 `h264_nvv4l2dec` 时优先选择 Jetson 后端，
保留其它平台选择和软件回退。实测成功出图，但仍是 NV12 → CPU/MJPEG/Tk
兼容显示路径，不声称原生 GPU 零回读。Jetson 原生多媒体背景见
[NVIDIA 文档](https://docs.nvidia.com/jetson/archives/r39.2/DeveloperGuide/SD/Multimedia/AcceleratedGstreamer.html)。

## Linux 启动时申请安装

新模块 `scripts/linux/remotedesk_linux_dependencies.py` 已接入 app / host
入口，在 Tk、Pillow、cryptography 等业务依赖导入之前运行，打包脚本也会携带它。

1. 使用当前运行 App 的同一 Python 检查模块，检查 FFmpeg 能否实际运行，
   检查 X11/XTest、显示器枚举及剪贴板工具；Wayland 环境额外检查 wl-clipboard。
2. 缺项时列出用途、系统包名和安装命令。用户确认后才申请管理员权限。
3. 图形会话使用系统 polkit；终端使用 sudo。只有固定参数的系统 apt-get
   被提权，RemoteDesk 本身不以管理员身份重新运行，也不读取或保存管理员密码。
4. 从已有系统软件源安装固定允许列表中的包，不修改驱动、源或签名校验，
   不执行系统升级或包删除；安装过程中窗口保持响应。
5. 安装结束后复查同一个 Python。失败、取消、无授权代理或仍缺依赖会明确提示，
   不把 apt 返回成功当作运行就绪，也不会循环安装。

当前自动安装支持 Debian / Ubuntu / Jetson。桌面需要可用的 polkit 授权代理；
若 Tk 尚未安装，依次尝试系统 zenity / kdialog / xmessage。既没有可用弹窗
也没有终端时，只给出诊断，不静默提权。GUI 启动器将取消码 125 作为正常取消；
依赖失败码为 78。授权代理行为依据 [pkexec 文档](https://polkit.pages.freedesktop.org/polkit/pkexec.1.html)。

源码运行与只读检查：

```sh
python3 scripts/linux/remotedesk_linux_app.py
python3 scripts/linux/remotedesk_linux_dependencies.py --mode app --check
```

`--check` 只输出 JSON，从不安装。自动测试/无人值守可显式设置
`REMOTEDESK_AUTO_INSTALL=0` 禁用检查和申请；缺失依赖仍可能导致后续运行失败，
此开关不是修复。只启动 host 时使用 `--mode host` 检查，不强制检查 Tk。

物理机最初没有 FFmpeg/ImageMagick/xclip，本轮仅安装系统源中的 NVIDIA FFmpeg
和 xclip。FFmpeg 是前段经明确授权安装；之后用产品新助手实际检测并安装 xclip。
Tk 图形确认/取消与进度测试使用模拟安装子进程，不冒称完成了登录桌面中的
polkit + apt 全流程。没有实际登录该用户桌面时，不能替其批准桌面授权对话框。

自动补依赖不解决缺图形会话、Wayland 全桌面权限、显卡驱动、架构不符或 glibc
不兼容；当前 amd64 发布包不能直接装到 ARM64 Jetson。此轮 Jetson 使用源码与
系统 Python。旧发布包须重新构建才包含本模块，六制品正式包本轮未覆盖发布。

## 证据与安装产物

本地记录：`artifacts/physical-devices-20260907-01/`（忽略入 Git）。

- `android-realme-codec-probe.json` / `android-realme-codec-repeat.json`：两轮硬解。
- `android-host-baseline/result.json`：修复前输入覆盖失败。
- `android-host-fixed/result.json` / `android-host-repeat/result.json`：两轮被控实测。
- `android-product-test.log`：产品实际硬编与连接日志；测试主动断开造成的 EOF 也保留。
- `linux/host-baseline/result.json` / `linux/host-final/result.json`：依赖安装前后。
- `linux/dependency-ui-fixed/result.json` / `linux/dependency-ui-startup/result.json`：真实 Tk 窗口和实际 App 启动测试。
- `linux/jetson-product-decoder/result.json`：实际产品有界硬解结果。
- `android-junit/`、`linux/unit-final.log`、`dotnet/linux-dependency-packaging.trx`：回归结果。
- `linux-evidence.tar.gz`：从授权真机取回的合成测试证据。

`RemoteDesk-android-debug.apk` 是本轮修正版，已安装到测试手机；读取设备
实际 `base.apk` 的 SHA-256 与本地文件一致：

```text
563F1365A99FA268E8C5485A72FC1ECB151A5FDD723D307AEC451115058EBEA0
```

它仍是内部 debug 签名包，不是正式发布。没有替换仓库原六制品 manifest，
也没有提交或推送工作区中其它尚未整理的修改。

## 测试边界与收尾

手机端到端测试通过专属 USB/ADB TCP 转发，画面、点击和文字均只落到合成测试
Activity；硬解探针是另一组本地样片测试。没有把这两组结果组合称为手机控制端
到远端主机的完整 UI 验收，没有覆盖真实 Wi-Fi/UDP、公网中继、长时后台、旋转、
所有手势、文件传输或多厂商设备。Linux 画面和输入在真机的独占 Xvfb 中测试，
不是 GDM 登录屏幕或用户 Wayland 桌面。Jetson 作为 host 的产品 H.264 硬编路径
尚未验证，独立 GStreamer 编码成功不能代替它。

测试结束停止手机屏幕共享，移除临时探针 APK、专属 ADB 转发和临时测试口令；
保留修正版 RemoteDesk，以及用户授权的通知、后台白名单和无障碍能力。
再次开始被控时请设置自己的访问口令，并按 Android 系统要求确认本次屏幕共享。
锁屏口令未写入源码、脚本或报告，也没有用于建立远控口令。

这台 realme 在强行停止产品后会关闭无障碍服务；清理临时口令后已通过正常
设置界面重新授权，并再次确认启用，未把强停前的状态当作最终状态。当前没有
运行中的 MediaProjection。Linux 仅删除本轮创建的独占 `/tmp/remotedesk-physical-*`
测试目录（约 14 MB），所需的 FFmpeg/xclip 保留；合成测试证据已取回本地。
原先就存在的 Xvfb 进程及其它系统服务未停止。
