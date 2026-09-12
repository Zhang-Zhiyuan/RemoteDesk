# 屏幕尺寸与缩放适配检查

这轮修改只处理界面、焦点和画面坐标，不降低远端画质，不改系统分辨率、网络或权限策略。

## 修复

- Windows：连接工具栏按父容器扣除内边距后的可用宽度重新换行；窗口放大后能解除旧的宽度限制。底部操作栏在窄窗口下换行，状态文字单独占一行。
- Windows 弹窗：新增设备、备注、共享名称、设备密钥、服务器登录/部署、地址选择统一使用 DPI 缩放和屏幕边界约束。内容放不下时可滚动，键盘选择控件时自动滚入可见区。
- Linux：大画面不再挤掉输入与操作栏；按钮换行按实际字体/控件宽度决定。滚动页支持水平溢出、新增内容后的高度更新、输入焦点跟随。缩小画面后仍可点击远端最右/最下方像素。
- Android：小屏操作栏保留至少 48dp 点击区域，放不下时横向滚动；输入区高度不再锁死。大字体首页使用紧凑标题区，底部导航不再把“本机被控”挤成两行。

## 验证范围

- Windows 单元回归覆盖 100%–300% 的控件尺寸与坐标计算、窄/宽窗口反复切换、1080p/4K/竖屏画面。新增实际窗口探针在隔离窗口站内检查 4 类弹窗、16 组尺寸，验证滚动与焦点可见性，不接触用户桌面、剪贴板或连接设置。
- Ubuntu 24.04 / Xvfb：396 项 Linux 测试通过，包括真实 Tk 布局、动态新增内容、缩小窗口后的控件边界及命中检查。
- Android：Debug、Release 各 605 项单元测试通过；lint 无错误，5 个现有警告。隔离 Android 16 模拟器检查 240×640、320×640、600×360dp 与 1/1.5/2 倍字体的 9 组组合，81 次坐标点击通过，验证文本发送及布局切换保留输入。
- 另在正式应用界面检查了 320dp、2 倍字体下首页、滚动和设备密钥弹窗；弹出系统键盘后输入框及确认/取消按钮仍在可见区域。

本轮没有连接到实体安卓手机；没有通过修改用户显示设置来模拟混合 DPI 真显示器。内部测试构建不等于 GitHub 正式发布，也没有覆盖远端安装。

## 重跑界面探针

Windows，仓库根目录 PowerShell：

```powershell
dotnet build experiments/InteropProbe/InteropProbe.csproj
'{"output":"artifacts/display-layout-recheck"}' | experiments/InteropProbe/bin/Debug/net8.0-windows/RemoteDesk.InteropProbe.exe layout-isolated
```

Linux：

```sh
REMOTEDESK_RUN_TK_TESTS=1 xvfb-run -a python3 -m unittest discover -s tests -p 'test_linux_*.py'
```

Android：构建并安装 `experiments/AndroidViewerProbe` 的测试 APK，在自有隔离模拟器设置 1280×900、160dpi 后运行 `experiments/Test-AndroidAdaptiveLayout.ps1`。脚本仅接受模拟器，结束后恢复字体设置；测试 APK 不可当作正式版发布。

实现注意：WinForms 的 `DpiChanged` 事件先于框架的自动缩放，因此弹窗的边界约束延迟到缩放后执行；Top 停靠控件的水平滚动范围需要显式设置。[WinForms Form](https://github.com/dotnet/winforms/blob/v8.0.0/src/System.Windows.Forms/src/System/Windows/Forms/Form.cs)、[ScrollableControl](https://github.com/dotnet/winforms/blob/v8.0.0/src/System.Windows.Forms/src/System/Windows/Forms/ScrollableControl.cs)。
