# Windows / Linux / Android 复测（2026-09-07）

后续状态：本文保留修复前的原始复测结论。用户授权直接修正后，安卓误判
和 Linux 解码启动阻塞已修复并复验；详见[修复与验证记录](PlatformFixes-20260907.md)。

## 结论

三端均重新构建或运行了测试，也进行了本机 Windows 硬解、WSL Linux
实际主机链路和 MuMu Android Surface 解码测试。**不能判定三端全部通过：
发现一个现有安卓兼容性 Bug、一个实验高清流的 Linux 接入问题，以及
隔离 Linux 4K 流程未达到 60 FPS。**

本轮只新增/调整独立测试工具和报告，未修改正式应用的视频策略，未安装
新版正式 APK/EXE，未更新远端、中继或 GitHub。仓库此前已有的改动保留。

| 平台 | 本轮自动化回归 | 实际运行结果 |
| --- | --- | --- |
| Windows | 1,563 通过，11 跳过，0 失败 | MF/D3D11 硬解 GOP1/GOP30 各 3 轮；断链失效、IDR 恢复、重置通过 |
| Linux | Ubuntu 24.04 内执行 214 项，全部通过 | 隔离 1080P H.264 接收约 59.85 FPS，重连成功；4K 单独复测约 27 FPS；GOP30 兼容解码启动阻塞 |
| Android | 全量重新执行 Gradle 构建及 307 项 JVM 测试，全部通过 | MuMu Android 15 实际 Surface 已有正确像素，但 0 呈现回调被误判为解码失败 |

自动化通过不等于所有功能的实机验收。Windows 跳过的 11 项实机用例没有
被计入通过；安卓 JVM 测试不替代设备上的 MediaCodec 行为。

## 环境与证据

- Windows 本机 GPU：NVIDIA GeForce RTX 2080 Ti，驱动 616.56。
- Linux：WSL Ubuntu 24.04，Python 3.12.3，FFmpeg 6.1.1-3ubuntu5，
  独立 Xvfb 显示，实际可访问 NVIDIA 编解码器。
- Android：现有 MuMu 虚拟机，Android 15 / API 35；构建目标 API 36，
  JDK 17、Gradle 8.11.1、AGP 8.10.1。
  模拟器报告的 `SM-A5360` 和 Samsung 指纹不是物理三星设备证据，
  `OMX.qcom` 名称及硬件标记也不能证明真实高通硬件能力。
- GOP1/GOP30 都复用上一轮合成浅色/深色文字桌面的 1920×1080、180 帧
  文件，没有采集用户桌面。来源见[画质实验](VideoQuality-20260907.md)。

本轮原始记录目录：
`artifacts/platform-check-20260907-160851-4d1bf4/`。以下链接均指向该目录，
运行产物被 Git 忽略，报告及复现工具保留在源码目录中。

## 1. Windows：硬解及恢复通过

[回归 TRX][win-tests] 和[原生恢复结果][win-recovery]为本轮实际运行结果。

每份码流执行 3 轮，每轮都调用项目实际 Annex-B 解析器和
`MediaFoundationD3D11H264Decoder`：

1. 连续解码 180/180 帧，校验输出 1920×1080。
2. 显式通知访问单元断链，确认转为等待独立帧。
3. GOP30 中故意提交无参考链的 P 帧，确认拒绝。
4. 提交 IDR 及后续帧，恢复 30/30。
5. 执行 discontinuity reset，再输出 30/30。

合计 6 轮、1,440 个有效硬解输出。这里测试的是解码器状态恢复，不是
向真实网络制造丢包，也没有验证完整窗口呈现或持续远控 60 FPS。

工具：[VideoQualityProbe / RecoveryProbe](../experiments/VideoQualityProbe/README.md)。
原有运行中 Windows RemoteDesk 进程保持响应，没有重启或替换。

## 2. Android：已显示画面却被回调策略判失败

这是当前 GOP1 也会触发的兼容性 Bug，不是仅实验 GOP30 的问题。

测试 APK 使用独立 ID `com.remotedesk.codecprobe`，直接编译正式解码器
源码，向真实 SurfaceView 送入合成 H.264。没有网络、屏幕捕获、无障碍或
存储权限，不读取正式 app 数据。测试前半段检查 PixelCopy 实际像素，
后半段原计划检查持续呈现、Surface 重建及关闭。

在隐藏和可见的 MuMu 窗口下都复现失败。为排除“隐藏窗口不呈现”的解释，
最终[像素证据][android-pixels]来自可见窗口：

| 测试流 | 取样时呈现回调 | 左半屏红通道均值 | 右半屏红通道均值 | 后续结果 |
| --- | ---: | ---: | ---: | --- |
| 当前 GOP1 | 0 | 243.92 | 37.56 | 解码候选被禁用 |
| 实验 GOP30 | 0 | 243.58 | 33.20 | 解码候选被禁用 |

左浅右深的像素符合合成源图，说明 Surface 已收到正确图像，不是纯黑。
但解码器提交第 30 个 Surface 输出后，因为没有 `OnFrameRendered`
回调，`recordSurfaceOutputSubmitted` 抛异常。硬件标记候选被剔除后，
兼容候选也同样失败，最终通知“没有可用解码器”。

定位：

- `AndroidH264SurfaceDecoder.java` 的 `recordSurfaceOutputSubmitted`。
- `AndroidH264FrameRenderedPolicy.java` 的 30 次提交阈值；首次呈现
  4 秒 watchdog 也依赖该回调，修复时需要一起审核。

配置低延迟可选项失败后，现有不带该选项的重试能够启动解码；它不是这次
“已经有正确像素仍失败”的直接原因。诊断记录保留在 JSON 中。

建议下一步区分“回调不可用”和“确实未呈现”，引入有界实际呈现证据或
兼容策略，保留真正黑屏时的恢复机制以及 Surface/代次隔离。
不应简单把所有黑屏 watchdog 关掉。

**尚未修复正式代码。** 因前半段失败，Surface 重建后连续呈现和关闭恢复
没有完成设备验收；不能把未到达的检查写成通过，也不能声称安卓 60 FPS。
GOP30 冷启动无参考 P 帧拒绝已通过。

[安卓复现工具说明](../experiments/AndroidCodecProbe/README.md)。
临时 APK 已卸载，导出的 JSON 和可重新安装的测试 APK 留在本地证据目录。
保留 APK 的 SHA-256：
`60147A6BD9AD76193A61D013DE15B4C9A15C93F82D9EC897CF0C27E8C9E96308`。

## 3. Linux：1080P 链路正常，高清流启动存在兼容障碍

### 当前主机路径

[最后一次完整运行][linux-runtime]启动真实 Linux 主机和独立动态测试窗口，
仅监听 127.0.0.1 随机端口，关闭发现，随机密码仅经 stdin 传入。

| 会话 | 收到帧数 | 帧到达速率 | 从连接开始到首帧 |
| --- | ---: | ---: | ---: |
| JPEG 1920×1080 | 45/45 | 30.04 FPS | 149 ms |
| H.264 GOP1 1920×1080 | 180/180 | 59.85 FPS | 2,373 ms |
| H.264 断开后新连接 | 60/60 | 60.34 FPS | 1,699 ms |

两次完整运行均通过错误密码拒绝、随后正确认证、加密帧收发、空负载
Ping/Pong、尺寸和独立帧标记检查。60.34 是短样本测量波动，不是超过
60 FPS 的能力保证。首帧时间包括连接、认证和编解码探测，不是每帧延迟。

主机日志确认实际 NVENC 编码。捕获后的 60 帧再通过正式兼容解码器：
CUDA 路径取得 58 个非黑输出，软件路径 59 个，均为 1920×1080。
测试没有 drain 最后全部输出；后述对照也证明原命令有启动帧丢失，
因此不能将这两个差额一概解释为缓存或写成 60/60。

这条接收端路径是 H.264 → FFmpeg → MJPEG → Pillow，不是 mpv 原生
GPU Surface 零拷贝呈现。网络到达 FPS 不能替代用户看到的呈现 FPS。

### 实验 GOP30 启动阻塞

[最终隔离对照][linux-fixtures]使用相同的 Windows 合成码流，调用实际
`H264AnnexBDecoder`，没有修改正式源码：

- 原命令 GOP1 正常取得 CUDA 58 / 软件 59 个输出。
- 原命令 GOP30 两个后端都输出 0；第 5 次尝试时 4 个 correlation
  槽已满，FFmpeg 进程仍活着，`correlation_overflowed=true`。
- 在单独实验进程中，只把 `-fflags nobuffer+discardcorrupt` 改成
  `discardcorrupt`，保留同一正式解码器及 4 槽限制：GOP30 的 CUDA
  路径输出 58 个非黑帧，软件路径 60 个，均不再溢出。
- 绕开 correlation 队列、完整输入 60 帧并送 EOF 的命令级对照中，
  原命令 GOP30 输出 30 帧，去掉 `nobuffer` 输出 60 帧；两个后端一致。
  GOP1 相应为 59 → 60。末尾仅含 AUD 的边界会产生无图像 EOF 警告，
  日志原样保留，不能将退出码 0 理解为无任何警告。

定位：`build_ffmpeg_h264_decoder_command`、`_try_reserve_correlation`。
这些结果支持“启动丢失关键帧后，等待下一 IDR，但有界队列在此前已满”
的解释。不是 NVDEC 完全不支持该流，也不是扩大队列就完成了高清接入。
后续还需验证帧关联、丢帧恢复和真实延迟；本轮参数对照不构成正式修复。
该 GOP30 模式本就未在产品协商中启用，此结果属于接入障碍，不能据此
说所有当前 Linux 连接都有此故障。

[Linux 复现工具说明](../experiments/LinuxRuntimeProbe.md)。
最终 fixture 命令退出码 1 如实表示原产品路径失败，实验成功不会掩盖它。

## 4. Linux 隔离 4K60：两轮均未达标

已有 4K 探针执行 20 秒动态 Xvfb → x11grab → CPU NV12 转换 → CUDA
上传/缩放 → NVENC GOP1。它使用 P1/CBR，不是正式主机 P4/VBR 的精确
复刻，也不是物理 Linux 显示器上的零拷贝捕获。

- MuMu 同时运行时：278 帧，编码约 13.92 FPS。
- 关闭 MuMu 后[单独复测][linux-4k]：546 帧，编码 27.32 FPS、按墙钟
  26.74 FPS，实测 36.29 Mbps；未满足 57 FPS / 1,140 帧验收门槛。
- 单独复测的全部 546 帧都含 SPS/PPS/IDR，3840×2160 尺寸及 NVENC
  CUDA 路径确认，编码及 ffprobe 正常退出；不是假分辨率或编码器没启动。

资源竞争影响明显，但移除模拟器负载后仍不达标。瓶颈尚未完整分解到
动态源、CPU 捕获/转换、上传和编码各阶段，不能推断 RTX 2080 Ti 本身
做不到任何形式的 4K60，也不能代表物理 Xorg/Wayland 的同等性能。
两轮探针均验证专属进程、显示锁和临时目录已清理。按既有探针策略，
原始大体积 4K 码流已删除，不能从报告还原；帧数、NAL、尺寸及日志保留。

## 边界与收尾

- 未覆盖物理 Android 手机、物理 Linux Xorg/Wayland、多 GPU 厂商、
  三台设备互控、真实键鼠点击/拖拽、完整切屏、后台/旋转恢复、长时间
  持续会话、弱网丢包或公网中继性能。这些不能标记为验收通过。
- 本轮初始 Linux 试跑的探针误发 8 字节心跳，主机按协议正确拒绝；
  已修正探针为空负载并完整复跑。这个初始失败不是产品 Bug，旧证据保留。
- 52 个 Android JVM 测试套件合计 307 项均重新执行，非仅报告缓存结果。
  [Linux unittest 日志][linux-tests]记录 214 项实际执行结果。
- 临时 Android 测试包已卸载，MuMu 恢复到测试前的关闭状态。
  独立 Linux host / ffplay / FFmpeg / Xvfb 进程已结束；未全局关闭
  ADB 服务或 WSL。原 Windows app 继续运行，正式安装和配置未替换。
- 当前优先事项是安卓误判修复；Linux 高清流启动/参考帧恢复随后接入；
  4K 性能需独立分段测量。不能把单元测试全部通过当作“没有 Bug”。

[win-tests]: ../artifacts/platform-check-20260907-160851-4d1bf4/windows.trx
[win-recovery]: ../artifacts/platform-check-20260907-160851-4d1bf4/windows-recovery.json
[android-pixels]: ../artifacts/platform-check-20260907-160851-4d1bf4/android-pixels.json
[linux-runtime]: ../artifacts/platform-check-20260907-160851-4d1bf4/linux-runtime-v3/result.json
[linux-fixtures]: ../artifacts/platform-check-20260907-160851-4d1bf4/linux-decode-fixtures-v3/result.json
[linux-tests]: ../artifacts/platform-check-20260907-160851-4d1bf4/linux-unittest.log
[linux-4k]: ../artifacts/platform-check-20260907-160851-4d1bf4/linux-4k60-solo/RemoteDesk-linux-4k60-20260907T083048Z-9abbab5e0eac4f3fb9aa4388423e430c.json
