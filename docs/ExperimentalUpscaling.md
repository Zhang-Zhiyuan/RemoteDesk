# 新版放大（实验）

默认关闭。开关只在当前查看窗口生效，不记成全局默认；关闭后恢复原来的放大方式。

- Windows：底部“允许放大”旁的“新版放大：关 / 开”。先允许放大，再切换算法。
- 安卓：底部“缩放”旁边，小屏可左右滑动工具栏。缩放菜单里也有开关。
- Linux：远控窗口底部的“新版放大：关 / 开”。

这是接收端 GPU 放大实验，不是 AI 补字，没有接入 FSR。只改善放大显示，不改变远端分辨率、带宽策略、输入坐标或协议。1:1 不需要增强，JPEG 和兼容显示继续走原版。

Windows 接入 NVIDIA NIS 1.0.3（不要求 RTX）。右键“新版放大”可以选择 NIS 或双三次＋防光晕进行对照；选算法不会自动打开开关。NIS 使用温和锐化，限 1～2 倍放大，超出范围或初始化失败则使用双三次，底部统计显示实际算法。保留已有转色流程，没有直接使用官方示例的固定 NV12 转色系数。

安卓在硬解 Surface 后接 OpenGL ES 双三次＋防光晕；Linux 使用已激活的 mpv 原生呈现后端，切换 Catmull-Rom 与防振铃参数。这两个平台本轮没有接入 NIS。Linux 未激活原生呈现时不会冒充开启成功。

Windows / 安卓初始化或绘制失败时会自动关掉实验并尝试恢复原路径。Linux 保存原始参数后才修改，部分设置失败会恢复；恢复不了时交由既有呈现后端恢复流程处理。

后续的“静止画面原生细节补传”不在这一版里。放大算法不能保证还原已经丢失的中文笔画。

## 复测

- Windows：`ExperimentalUpscalingTests`；设置 `REMOTEDESK_RUN_HARDWARE_SMOKE=1` 可实测显卡上的开关、像素、尺寸、裁切和失败回退。
- NIS：`NisUpscalingTests` 检查固定版本着色器、系数、范围、RGB 通道、奇数尺寸和回退；`dotnet run --project experiments/VideoQualityProbe -c Release -- --upscale <新输出目录>` 对照同一张合成中文图。GPU 计时不包括 DXGI 等待和交换链提交，不是公网端到端延迟；PSNR 也不等于文字可读性。
- Linux：`tests/test_linux_upscaling.py`；`experiments/linux_upscale_probe.py <新输出目录>` 检查真实 mpv 渲染变化和像素级回退，需要图形会话。该探针允许软件光栅化，不能替代显卡性能测试。
- 安卓：用 `gradlew :app:assembleDebug -PupscaleProbe` 构建独立测试包，不覆盖正式安装。`UpscaleProbeActivity` 检查颜色、方向及重建；配合本地合成远端可验证真实查看窗口及 GL 故障回退。测试入口只存在于 debug，release 不包含。

仍需在手机和 Linux 真机上比较中文小字、滚动、延迟与发热，不能用模拟器结果宣称画质或性能提升幅度。
