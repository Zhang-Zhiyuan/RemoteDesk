# 新版放大（实验）

默认关闭。开关只在当前查看窗口生效，不记成全局默认；关闭后恢复原来的放大方式。

- Windows：底部“允许放大”旁的“新版放大：关 / 开”。先允许放大，再切换算法。
- 安卓：底部“缩放”旁边，小屏可左右滑动工具栏。缩放菜单里也有开关。
- Linux：远控窗口底部的“新版放大：关 / 开”。

这是接收端 GPU 放大实验，不是 AI 补字，没有接入 FSR。只改善放大显示，不改变远端分辨率、带宽策略、输入坐标或协议。1:1 不需要增强，JPEG 和兼容显示继续走原版。

Windows 右键“新版放大”可选 NIS、文字笔画或双三次。NIS 是初始选项，适合 1～2 倍放大。底部统计显示实际使用的算法。

“文字笔画”适合放大终端和文档。从 1.75 倍起，它在双三次采样时增强已有细笔画的亮度对比。轻度放大使用双三次，避免把小字加粗。这个模式只用当前帧，首次着色器编译在后台完成。

安卓在硬解 Surface 后接 OpenGL ES 双三次＋防光晕；Linux 使用已激活的 mpv 原生呈现后端，切换 Catmull-Rom 与防振铃参数。这两个平台本轮没有接入 NIS。Linux 未激活原生呈现时不会冒充开启成功。

Windows / 安卓初始化或绘制失败时会自动关掉实验并尝试恢复原路径。Linux 保存原始参数后才修改，部分设置失败会恢复；恢复不了时交由既有呈现后端恢复流程处理。

Windows 控制 Windows 时，可以另开“原生补清”。停下操作后，被控端传回鼠标附近的无损图块。“文字笔画”会按屏幕像素的覆盖面积放大这些图块。窗口的画面宽或高小于原屏幕的一半时暂停请求，放大窗口后自动恢复。实际效果见[原生补清的适用条件](NativeDetailReleaseAudit-20260914.md)。

## 复测

- Windows：`ExperimentalUpscalingTests`；设置 `REMOTEDESK_RUN_HARDWARE_SMOKE=1` 可实测显卡上的开关、像素、尺寸、裁切和失败回退。
- NIS：`NisUpscalingTests` 检查固定版本着色器、系数、范围、RGB 通道、奇数尺寸和回退；`dotnet run --project experiments/VideoQualityProbe -c Release -- --upscale <新输出目录>` 对照同一张合成中文图。GPU 计时不包括 DXGI 等待和交换链提交，不是公网端到端延迟；PSNR 也不等于文字可读性。
- Linux：`tests/test_linux_upscaling.py`；`experiments/linux_upscale_probe.py <新输出目录>` 检查真实 mpv 渲染变化和像素级回退，需要图形会话。该探针允许软件光栅化，不能替代显卡性能测试。
- 安卓：用 `gradlew :app:assembleDebug -PupscaleProbe` 构建独立测试包，不覆盖正式安装。`UpscaleProbeActivity` 检查颜色、方向及重建；配合本地合成远端可验证真实查看窗口及 GL 故障回退。测试入口只存在于 debug，release 不包含。

仍需在手机和 Linux 真机上比较中文小字、滚动、延迟与发热，不能用模拟器结果宣称画质或性能提升幅度。
