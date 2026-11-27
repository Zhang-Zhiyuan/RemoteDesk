# 远控画面缩放

三端远控窗口默认按比例适应窗口。低分辨率桌面会自动放大。用底部的“画面”调整当前窗口，远端显示分辨率和传输尺寸保持各自的设置。

| 查看端 | 显示大小 | 放大效果 |
| --- | --- | --- |
| Windows | “适应窗口”或“限制放大（最大 1:1）” | H.264 硬件显示默认“文字优先”。可在“放大效果”里切换照片、平滑、像素边缘或系统缩放。 |
| Linux | “适应窗口”或“限制放大（最大 1:1）” | 默认由当前显示后端缩放。使用 mpv 原生显示时，可开启 GPU 放大增强。 |
| Android | “适应窗口”、原始像素 1:1、分级或双指缩放 | 默认使用系统显示合成。H.264 的 GPU 放大增强可在同一菜单单独开启。 |

Windows 的“文字优先”在 1.75 倍及以上增强已有的细笔画，轻度放大用双三次采样，只处理当前帧。“照片优先”使用 NIS，适合 1～2 倍放大。“保留像素边缘”适合整数倍查看低分辨率桌面。着色器在后台准备，底部状态显示实际采用的算法。[NVIDIA Image Scaling](https://github.com/NVIDIAGameWorks/NVIDIAImageScaling)

Windows 控制支持补清的 Windows 被控端时，可以在“画面”里勾选“静止时补清文字”。两端都支持新版时，停下滚动或输入后，被控端按可见区域从中心向外补发原始像素。普通横屏或竖屏 1080p 桌面都能覆盖到四角。直连已建立的 UDP 视频通道可以继续使用。公网中继按实际收到并显示的帧分配补清带宽，完成时间取决于画面内容和线路余量。窗口缩得过小时暂停，放大后自动恢复。连接旧版时，补清范围仍在鼠标附近。[原生画质实测](Native1080pOn4K.md)

原生 1080p 的中文、代码和 Windows ClearType 黑字在 4K 上的对照见[实测数据与原尺寸裁图](Native1080pOn4K.md)。

## 复测

- Windows：`ExperimentalUpscalingTests`；设置 `REMOTEDESK_RUN_HARDWARE_SMOKE=1` 可实测显卡上的开关、像素、尺寸、裁切和失败回退。
- NIS：`NisUpscalingTests` 检查固定版本着色器、系数、范围、RGB 通道、奇数尺寸和回退；`dotnet run --project experiments/VideoQualityProbe -c Release -- --upscale <新输出目录>` 对照同一张合成中文图。GPU 计时不包括 DXGI 等待和交换链提交，不是公网端到端延迟；PSNR 也不等于文字可读性。
- Linux：`tests/test_linux_upscaling.py`；`experiments/linux_upscale_probe.py <新输出目录>` 检查真实 mpv 渲染变化和像素级回退，需要图形会话。该探针允许软件光栅化，不能替代显卡性能测试。
- 安卓：用 `gradlew :app:assembleDebug -PupscaleProbe` 构建独立测试包，不覆盖正式安装。`UpscaleProbeActivity` 检查颜色、方向及重建；配合本地合成远端可验证真实查看窗口及 GL 故障回退。测试入口只存在于 debug，release 不包含。

仍需在手机和 Linux 真机上比较中文小字、滚动、延迟与发热，不能用模拟器结果宣称画质或性能提升幅度。
