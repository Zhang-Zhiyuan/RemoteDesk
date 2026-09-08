# 2026-09-08 追加优化与实测

本轮针对 Linux Jetson 硬编、中文漏字及公网传输开展检查；不是新版本六方向全部通过的声明。
完整成功、失败及对照证据保存在 `artifacts/optimization-20260908-02/`，未覆盖上一轮记录。

## 已修复、已部署

- Jetson AGX Orin / Jetson Linux R39.2：原来只枚举 FFmpeg 编码器，未使用系统已有的
  GStreamer `nvv4l2h264enc`。现增加本机 Jetson 候选，仍须实际首帧验证，失败保留
  FFmpeg/JPEG 回退。未安装或替换驱动、JetPack、系统插件。
- 新路径的 I420 → NV12 转换曾实测偏色。现 CPU 直接生成 NV12，再上传 NVMM，转换和
  H.264 VUI 明确标记 BT.709。保留原有分辨率/码率预算、全 IDR、SPS/PPS/AUD、无 B 帧，
  缩放保持比例并补边。并非全链路零拷贝，也不把有损 4:2:0 H.264 宣称为 JPEG/无损等价。
- Linux 在硬编负载下通过 xdotool 发送 Unicode，12 ms 时序仍会漏字：本地 20 组压力测试
  漏 1 组；公网测试也出现“中文测试”缺“试”。非 ASCII 改为 50 ms 时序余量后，默认代码
  和实际安装代码各 20 组全部精确匹配；鼠标、物理按键及 ASCII 原有时序不变。
  不改持久键盘映射，不借用或覆盖剪贴板。

Jetson 使用 NVIDIA 提供的硬编插件；官方背景见
[Jetson R39.2 Accelerated GStreamer](https://docs.nvidia.com/jetson/archives/r39.2/DeveloperGuide/SD/Multimedia/AcceleratedGstreamer.html)。
启用判定以本机插件探测和实际出帧为准，不仅依赖型号名称。

Linux 已安装到 `~/.local/share/remotedesk/releases/optimization-20260908-02`，
`current` 已切换；原 `releases/1.0.0` 保留。更新前 GUI 未运行，配置校验不变。
安装后的实际模块路径和 SHA-256 写入 `installed-quality/result.json`、`installed-unicode/result.json`。

## 实测结果与边界

| 检查 | 结果 |
| --- | --- |
| 安装后原生 1920×1080 硬编 | 60 个独立帧，约 29.9 FPS，首 AU 约 146 ms |
| 1280×720 缩放、1024×768 补边 | 各 60 帧，约 30 FPS，尺寸及黑边正确 |
| 固定文字/色块图 | 原生亮度 PSNR 38.4 dB；偏色修正前 34.6 dB；8 个纯色采样最大通道误差 8/255 |
| Android → Linux 局域网 | 真机硬解 1080P，结束时 29.9 FPS；画面、点击、中文/emoji 正确 |
| Android → Linux 私有公网中转 | 修正漏字后通过；结束时约 6 FPS / 1.77 Mbps，强制 TLS/TCP 中转，无 LAN/UDP 回退 |
| Unicode 压力测试 | 基线 19/20；30 ms、50 ms 对照各 20/20；修改后默认及安装版本各 20/20 |
| Python 回归 | 294 项：289 通过、5 跳过 |
| Windows 诊断项目 / Android 查看端测试 APK | 构建通过；手机签名正式版 1.0.1 未替换 |

Linux 是真实 Jetson，但抓屏和输入对象为独立 Xvfb/Tk，不等同于用户原生 Xorg/Wayland 会话。
PSNR 只针对固定测试图及所述编码参数，不代表所有桌面内容的清晰度保证。
首 AU 是本机编码器指标，不是网络握手或端到端操作延迟。
局域网结果在同一最终 NV12/BT.709 编码路径上获得；Unicode 时序的最终版本另外通过公网和安装后压力测试。

Windows 本轮能接收/解码 H.264，但输入桌面不可访问（OpenInputDesktop：Access Denied；
GetForegroundWindow：0），DXGI 持续报告 occluded。未将这些帧记作实际前台显示通过，
也没有为此修改或更新 Windows 正式版。需桌面恢复可访问后补验；不会绕过锁屏。

诊断程序已修正启动 JPEG 预热帧误判后续 H.264 成功的问题：要求观察期内继续显示新帧，
桌面不可访问时明确失败。`jetson-lan-first` 的旧 `complete=true` 不能作为本轮显示验收证据；
后续收紧判定的失败结果完整保留。

## 未保留的候选

Windows 公网 socket 自动缓冲对照没有取得可复现收益，已撤回，未部署。
512 KiB 原生中转随机字节往返：基线约 4.37 / 5.32 / 6.37 秒，候选约 5.08 / 11.65 / 10.66 秒；
所有字节一致性校验通过。当前到中转的 ICMP RTT 约 309 ms，线路延迟仍明显。
该对照是传输诊断，不是视频帧率；未降低画质预算，也未改中转服务器配置来掩盖瓶颈。

## 收尾

手机测试 APK 已卸载，正式版全屏共享已恢复，并验证自动返回桌面；既有密码未更改。
Windows 原正式进程保留。测试中间产物按精确路径清理，报告及最终 Linux 便携包保留。
清理记录、安装哈希与后续补验状态以本轮审计目录中的记录为准。
