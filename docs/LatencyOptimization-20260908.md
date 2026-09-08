# 无损显示交接优化与安装验证（2026-09-08）

> 公开副本：历史记录中的设备及服务器地址已脱敏为示例地址，不是可连接的真实目标。

## 已完成的改动

Linux Pillow 显示路径在保持同一 RGB 转换、LANCZOS 缩放和目标尺寸的前提下，
将本地 PNG 交接改为二进制 PPM。Tk 直接读取 RGB，不再重复压缩、解压 PNG。
这只是进程内显示数据：没有改变网络码流、JPEG 质量、H.264 编码参数、协商能力、
分辨率设置或目标帧率。缺少 Pillow 时保留原有 PNG/ImageMagick 兼容路径。
网络图像校验没有扩大到 PPM，仍在解码前检查尺寸。

实际 UI 和互联测试查看器共用 `create_tk_frame_photo`，明确区分 PPM/PNG。
测试截图仍保存为真正的 PNG，避免内容变了但扩展名没变。旧窗口尺寸对应的帧
仍会被拒绝，未增加显示队列长度。

Windows 增加 TCP 帧发送的本地分段统计：组包、等待共享写锁、加密、Socket 写入。
按已有统计窗口汇总，只统计完成写入的 TCP 帧，不将 UDP 入队或被丢弃的帧混入。
没有改变消息字节、锁/加密序列或单帧一次网络写入的行为。写入完成不代表对端收到、
解码或显示，日志明确标注“非 RTT”；外层目标发布准入等待也不在这四项之内。
这一项是定位后续瓶颈的观测改进，不宣称它本身已经降低公网延迟。

## Jetson 真机测量

设备为 `198.51.100.74` 的 ARM64 Jetson；系统 Pillow 10.2.0、Tk 8.6。
使用独立 Xvfb、合成 1920×1080 JPEG90 桌面图。每种模式预热 2 次、记录 30 次。
GUI 时间包含 PhotoImage 创建和 Tk 空闲绘制处理，不包含网络、硬件屏幕扫描输出，
也不包含 H.264 到 JPEG 的兼容转换。

下表旧路径是本轮改动前已经优化过的 PNG 压缩级别 1，不是更慢的级别 6。

| 显示尺寸 | 本地转换+Tk交接：旧 → 新 | 本地转换：旧 → 新 | 进程 CPU 时间：旧 → 新 |
| --- | --- | --- | --- |
| 1280×720（1280×820 查看区域） | 137.68 → 80.33 ms | 94.14 → 50.85 ms | 129.63 → 72.27 ms |
| 原尺寸 1920×1080 | 173.00 → 67.37 ms | 98.73 → 15.38 ms | 163.09 → 57.41 ms |

这两个固定样例的本地交接耗时分别下降约 42%、61%。两种路径解码后的 RGB
SHA-256 完全一致；实际 Tk 像素及完整 UI 保存出的图片也分别通过一致性检查。
不能将这些比例当作所有桌面场景或公网端到端延迟的改善比例。

代价是本地交接缓冲区更大：1080P 从约 0.93 MB PNG 变成约 6.22 MB RGB/PPM。
数据不会经过公网，显示尺寸上限及单个待绘制帧的队列限制保持不变。

## 验证范围

- Windows Release 测试：1,570 通过，11 个有条件的实机测试跳过，0 失败。
- Windows 协议定向测试：54 通过；覆盖码流内容、单次写入、共享锁等待统计、
  取消时锁的所有权、并发帧/控制消息以及统计窗口重置。
- Windows InteropProbe 消费项目构建：0 警告、0 错误。
- 本地 Linux Python 测试：263 通过、1 个实际 Tk 用例跳过。
- Linux 真机：264 项全部通过，包括真实 Tk 像素比较。
- 候选及安装目录分别运行完整 GUI 测试：两个窗口尺寸的 RGB 一致；旧尺寸帧
  不覆盖新尺寸画面；没有启动被控监听或注入输入。
- 安装后的启动器运行 3 秒，再由测试主动 SIGTERM 退出；没有异常日志。

原始证据在 `artifacts/latency-20260908-01/`：
`conversion-benchmark.json`、`linux-tests.log`、`gui-installed/result.json`、
`windows-tests/windows.trx`。实验工具分别是
`experiments/linux_viewer_conversion_benchmark.py` 和
`experiments/linux_lossless_display_probe.py`。

## Linux 安装状态与回退

该机之前仅发现临时互联测试副本，没有 `/opt/remotedesk` 或用户级固定安装。
本轮新增用户级 ARM64 安装，直接使用该机已验证的系统 Python 和依赖，
没有把 amd64 私有运行时装到 ARM64 机器上，也没有改动个人配置或原有测试副本。

- 版本目录：`~/.local/share/remotedesk/releases/latency-20260908-01/app`
- 当前版本入口：`~/.local/share/remotedesk/current`
- 启动命令：`~/.local/bin/remotedesk-linux-app`
- 桌面菜单入口：`~/.local/share/applications/remotedesk.desktop`

安装前确认这些目标不存在。保留按版本区分的目录，后续更新可以保留前一版本，
验证后切换 `current`，异常时回切。本轮没有旧的固定安装可供回退。
安装后的 app SHA-256 与本地源码一致：
`AF94FCD524633AC22B2D634E5F5E111D9A1FAB88395A1E0B751F67F4C5B3906F`。

所有本轮 GUI/Xvfb 测试进程已退出，保留了测试前存在的 Xvfb PID 111221。
Windows 正在运行的安装、Android APK、公共中继服务均未替换；未提交 Git、上传
或重建三端正式发行包。Windows 的本轮改动是源码和测试构建中的诊断功能。

## 仍待优化

Linux 的 FFmpeg 兼容路径仍包含 H.264 解码后的 MJPEG 转换和 GPU 回读，
本轮不是原生 GPU 零拷贝完成验收。GOP30、HEVC 和静态补清也没有默认启用。
下一步应基于分段统计定位发送等待，同时继续验证原生硬件呈现与参考帧恢复。

Tk 自带 PPM/PNG 处理器及二进制 `data` 输入的接口依据见
[Tk 8.6 Photo 官方手册](https://www.tcl-lang.org/man/tcl8.6/TkCmd/photo.htm)。
