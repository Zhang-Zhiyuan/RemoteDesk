# 文本剪贴板修复核验

这份记录对应发布前的专项检查，涵盖复制、剪切、粘贴和手动收发文字，不是后台无条件同步所有剪贴板内容。后续发布汇总见 [1.0.16](Release-1.0.16.md)。

## 修复

- Windows 粘贴等待远端写入确认；读取结果绑定连接和本机剪贴板序号。空结果、超时结果和旧连接结果不覆盖本机内容。
- Linux 控制端补齐收发入口、Ctrl+C / Ctrl+X 取回和 Ctrl+V 同步粘贴；保留 Ctrl+Shift+V。收发在后台工作线程进行，按钮适应窄窗口。
- Android 控制端增加“更多 → 文字剪贴板”，复制快捷键联动取回，粘贴先等写入确认。被控端通过无障碍支持 Ctrl+A / C / X / V，中文及多行粘贴不再依赖逐字注入。
- 三端统一 256,000 个 UTF-16 单元的文本上限，不静默截断。Linux 显式使用 UTF-8，不再依赖系统区域设置或转换 CRLF。
- Windows / Android 延迟执行的剪贴板写入会检查请求是否仍有效；Linux 被控会在处理排队操作前检查会话。
- 剪贴板确认与屏幕可用状态提示分开处理，避免误确认和状态消息互相覆盖。

## 验证结果

- Windows：1,878 项通过，11 项既有硬件 / 真机门控测试跳过；编译无警告。
- Linux / WSL + 独立 Xvfb：524 项通过，包括真实 X11 剪贴板中文、表情、换行往返。
- Android：Debug / Release 各 596 项通过；lint 无错误，保留 5 项既有警告。
- Windows 独立窗口站：4 项实测通过，涵盖真实剪贴板、加密会话回传、空回复保护和等待期间新复制内容保护。未访问用户交互窗口站的剪贴板。
- Android 16 模拟器：7 项被控输入框 / 系统剪贴板检查通过；8 项控制端真实 UI + 加密测试服务检查通过。
- 修复候选 APK 使用原发布签名，覆盖安装成功。SHA-256：`dd2cf042c980c4a29de48d9766ca1b86aea96bb45ad00a5f267fdeff7d97baec`。

证据位于本地 `artifacts/clipboard-20260912/`，专项测试在
`tests/test_clipboard_sync.py`、`ClipboardRequestTrackerTests.cs`、
`ClipboardAcknowledgementLoopbackTests.cs` 和 `AndroidViewerClipboardTest.java`。
真实运行脚本为 `experiments/verify_clipboard_viewer.py`、InteropProbe 的
`clipboard-isolated` 模式，以及 AndroidRelayHostProbe 的 `ClipboardProbeActivity`。

## 边界

Android 10 及以上限制后台应用读取剪贴板。Android 16 实测确认：后台读取失败会明确报错，不再伪装成空文本成功。取回被控手机的文字可能需要把 RemoteDesk 切到前台；未绕过系统限制。

本轮没有连接 USB 真机，未进行真实公网中继剪贴板实测。临时测试应用、ADB 映射和测试权限已清理，模拟器已退出。已生成修复候选 APK；未覆盖现有 GitHub Release，也未替换正在运行的 Windows 服务。
