# 跨平台问题修复与验证（2026-09-07）

## 已完成

根据“发现问题直接修正”的要求，本轮已修改正式源码，修复上轮复现的
安卓 Surface 呈现误判和 Linux H.264 启动阻塞，并简化 Linux NVENC
上传路径。不是只提交问题报告。原始失败证据见[修复前复测](PlatformRecheck-20260907.md)。

| 检查 | 最终结果 |
| --- | --- |
| Windows 回归 | 1,563 通过、11 跳过、0 失败 |
| Android 全量重新构建及 JVM 测试 | 312 通过、0 失败，比上轮增加 5 项 |
| Linux 实际 Ubuntu 内执行回归 | 215 通过、0 失败，比上轮增加 1 项 |
| MuMu 实际 Surface 解码 | GOP1/GOP30 均通过持续输出、实际像素、Surface 重建及关闭检查 |
| Linux 实际 1080P 链路 | 180/180 个 H.264 帧，约 59.84 FPS；断开后重连通过 |
| Linux 实验 GOP30 兼容解码 | 当前正式解码命令不再启动阻塞；旧参数负对照仍复现失败 |
| Linux 4K 性能 | 同画质设置下有小幅改善，仍未达到 60 FPS，不记作达标 |

记录目录：`artifacts/platform-fixes-20260907-1704/`。
这些是本机 Windows、WSL Ubuntu 24.04 和 MuMu Android 15 的结果，不是
物理 Android/Linux、所有显卡、真实公网或长期会话的全面验收。

## 安卓：区分呈现证据与帧率统计

`AndroidH264SurfaceDecoder` 不再把“提交 30 个输出但没有回调”直接当成
黑屏并停用解码器。新增有界备用检查：

- 首次提交后延迟 150 ms，若还没有真实呈现回调，异步 PixelCopy 检查
  该 Surface 是否已有可读取缓冲；1×1 临时取样不保留、不写磁盘。
- 每个解码器代次最多 3 次、同一时刻最多一个检查；正常回调先到则跳过。
- 不以亮度判成功：真正全黑的远程桌面同样是合法内容。
- 回调和检查结果都校验 codec、Surface、解码器代次和 Surface 代次。
  首次启动通知与备用确认串行，防止切换/关闭后的旧确认影响新画面。
- 实际没有输出、输入队列饱和，以及没有任何呈现证据的 4 秒超时恢复
  仍然保留。没有通过放宽队列或停用全部 watchdog 来掩盖黑屏。
- 新的 `onSurfaceBufferAvailable` 只确认首帧，不冒充逐帧呈现回调。
  缺少回调时 FPS 显示 `—`；后续真实回调到来会恢复帧率计数。
  切换前的 JPEG 计数也不会被错误显示为当前 H.264 的 0 FPS。

依据：Android 的[呈现回调文档](https://developer.android.com/reference/android/media/MediaCodec.OnFrameRenderedListener)
说明该通知用于时间统计，可能延迟/批量发送；旧系统还可能缺少部分通知。
[PixelCopy 文档](https://developer.android.com/reference/android/view/PixelCopy)
说明可读取 Surface 最近入队的缓冲。这里确认的是缓冲可用，而非准确
屏幕扫描时刻，所以不能拿它推算 60 FPS。

两轮 MuMu 实际测试均通过。最终 [android-final.json][android]：

| 流 | 实际提交 Surface 输出 | 真正呈现回调 | 独立缓冲确认 | 重建后输出 |
| --- | ---: | ---: | ---: | ---: |
| GOP1 | 177 | 0 | 1 | 57 |
| GOP30 | 178 | 0 | 1 | 58 |

每流初始提供 180 帧，重建后再提供 60 帧；上述提交数不是呈现 FPS。
两流重建后都有新的缓冲确认，并通过源图左浅右深像素校验；持续超过
4 秒 watchdog，未再错误停用。GOP30 冷启动无参考 P 帧拒绝、重复 close
及关闭后拒绝新帧也通过。最终 APK 使用与这些检查相同的正式解码器源码。

## Linux：保留解码启动所需的首个关键帧

`build_ffmpeg_h264_decoder_command` 将 `nobuffer+discardcorrupt` 改为
`discardcorrupt`，保留首个探测用 IDR。仍保留 4 槽 correlation 上限、
低延迟标记、小探测窗口和单线程解码，没有增大缓存来延后故障。

[真实码流负对照][fixtures]同时运行当前正式命令与单独加回旧参数的实例：

- 当前 GOP1/GOP30：每后端提供 60 帧，CUDA 取得 58 个非黑输出、软件
  取得 60 个；无 correlation 溢出。
- 旧参数 GOP30：仍在第 5 次尝试时 4 槽满、0 输出，FFmpeg 进程活着。
- 完整输入加 EOF 的命令级验证：当前两后端都是 60/60；旧参数 GOP30
  是 30/60。末尾 AUD 的 EOF 警告保留在报告中。

未 drain 的 CUDA 58/60 不能写成 60/60 实时呈现，解码兼容路径仍有
MJPEG/Pillow CPU 回读。此修复解除实验帧间流的解码启动障碍，**没有
把 GOP30 或 HEVC 默认启用，也没有修改传输协商和丢帧恢复合同**。

## Linux：NVENC 上传优化及性能边界

对现有 CPU X11 捕获，移除独立 `hwupload_cuda` 滤镜，让 `h264_nvenc`
使用自身 NV12 上传缓冲。仍是硬件编码；P4、VBR、空间 AQ、GOP1、
码率上限、无 B 帧/无 lookahead/低延迟参数均未降低。

隔离动态 Xvfb 分段验证显示，采集、NV12 转换、单独 CUDA 上传都接近
60 FPS；组合编码阶段才明显变慢。减少滤镜线程会更慢，没有采用该改法。
交替比较两种上传路径，再重复并加入实际硬解像素检查：

- 旧独立上传路径：短样本约 34–36 FPS。
- 内部上传路径：短样本约 36–39 FPS，有波动，收益有限。
- 最后验证中新路径分别输出 191 / 180 个 4K 帧，全部通过恢复单元检查，
  每组首帧实际 NVDEC 解码为 3840×2160 且非平坦像素。
- 这不等于解决所有 4K 性能问题，未达到 60 FPS。也不能把上轮不同
  P1/CBR 强制 CUDA 缩放探针的 27 FPS 与当前结果直接当成同配置增幅。

证据：[分段线程测试][stages]、[上传交替测试][upload]、[加入像素验证的复测][verified]。
原始大体积 4K 码流只在内存中检查，未保存为磁盘中间产物。

最后使用实际修正后的主机和解码器完成[完整本地协议链路][runtime]：
错误密码拒绝、正确认证、空负载心跳、JPEG 45/45、H.264 180/180、
重连 H.264 60/60 均通过。H.264 到达约 59.84 FPS，重连短样本 60.33 FPS；
首帧约 2.19 / 1.50 秒，包括连接、认证及编解码探测，不是逐帧延迟。

## 构建产物与清理

可供手动安装的[修正后安卓 Debug APK][apk]已生成，SHA-256：
`E091AD5F9A387CB81EB47123275B2841AADDE42EFB045DD8428DB669B5C0DEA7`。
本轮没有覆盖正在使用的正式安装，没有更新远端/公网服务器或推送 GitHub。
Linux 改动位于源码，未替换之前发布的压缩包。

MuMu 的临时测试 APK 已卸载，模拟器恢复关闭。测试专属 Linux host、
FFmpeg、ffplay 和 Xvfb 已停止；没有全局关闭 ADB/WSL，原 Windows
RemoteDesk 进程保持响应。原有源码改动和历史测试证据均保留。

[android]: ../artifacts/platform-fixes-20260907-1704/android-final.json
[fixtures]: ../artifacts/platform-fixes-20260907-1704/linux-decode-fixtures/result.json
[runtime]: ../artifacts/platform-fixes-20260907-1704/linux-runtime/result.json
[stages]: ../artifacts/platform-fixes-20260907-1704/linux-capture-stages/result.json
[upload]: ../artifacts/platform-fixes-20260907-1704/linux-upload-stages/result.json
[verified]: ../artifacts/platform-fixes-20260907-1704/linux-upload-verified/result.json
[apk]: ../artifacts/platform-fixes-20260907-1704/RemoteDesk-android-debug.apk
