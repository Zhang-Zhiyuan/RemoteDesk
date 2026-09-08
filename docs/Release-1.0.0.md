# RemoteDesk 1.0.0 发布说明

此版本面向个人自托管远控，支持 Windows、Linux 与 Android 作为控制端或被控端。
发布包和最终核验结果统一放在 `artifacts/release-1.0.0`，本地私有检查记录在
`artifacts/release-1.0.0-audit`；后者可能包含设备信息，不应作为公开发布附件。

## 包与运行范围

- Windows x64：`RemoteDesk-win-x64.zip`，解压后运行 `RemoteDesk.exe`。
  自包含 .NET 运行时；本次不含 Windows Authenticode 签名，系统可能提示未知发布者。
- Linux x64：`RemoteDesk-linux-host.tar.gz` / `.zip`，或 Ubuntu 24.04+
  `remotedesk-linux-host_1.0.0_amd64.deb`。不能将 amd64 私有运行时用在 ARM64 上。
- Linux ARM64 / 系统 Python：`RemoteDesk-linux-system-python.tar.gz`，
  使用系统 Python 3.12 及依赖，适用于本项目实测的 Jetson；见
  [系统 Python 包说明](Linux-SystemPackage.md)。
- Android：`RemoteDesk-android-release.apk`，版本代码 2、版本号 1.0.0，
  使用独立发布密钥签名，不是调试构建。支持 Android 8.0 / API 26 及以上。

Android 的旧调试版与本次正式包签名不同，系统不会允许直接覆盖。不要为更新
而自动卸载用户旧版或清空数据；已有正式签名版本的后续升级必须保留同一发布密钥。
密钥不放入源码或发布包。Windows 本地发布辅助脚本从仓库外的 DPAPI 保护凭据
加载 Android 签名信息；应另行安全备份密钥和可恢复凭据。

## 本批包含

- IP 直连、私有公网中继、在线设备选择、接管旧连接。
- 三端桌面显示、鼠标/键盘与文本输入、切屏、文件与剪贴板相关能力协商。
- 手机查看端布局、触摸和键盘交互改进；硬解失败恢复及代次校验。
- Windows 连接启动死锁、输入归属/权限反馈、捕获切换和会话退出修正。
- Linux 依赖申请安装、Jetson 兼容硬解、无损 RGB 显示交接优化。
- Windows TCP 发送分段统计，用于区分组包、锁等待、加密与写入成本。

实机历史证据和测试边界分别见 [整体核验](OverallRecheck-20260908.md)、
[六方向中继](RelaySixDirections-20260908.md) 与
[无损显示优化](LatencyOptimization-20260908.md)。历史测试不自动等同于本次
发行文件的验证，最终产物的哈希、构建来源与本轮检查结果以发布目录中的记录为准。

## 保留的限制

Linux 完整桌面捕获/控制优先使用 Xorg；Wayland 的权限限制可能影响全桌面功能。
手机被控需要系统屏幕共享授权及无障碍权限。Linux 兼容显示仍可能进行 GPU 回读
和 MJPEG 转换，不应称为所有设备均已实现零拷贝。尚未默认启用 GOP30、HEVC 或
静态细节补清，也不承诺所有网络和设备下 4K60 或固定的端到端延迟。

## 可重复发布

源码提交并通过检查后，运行 `scripts/Publish-LocalRelease.ps1`。该路径默认使用
独立的 `release-<版本>` 目录，拒绝覆盖已有同名发布目录，不跳过测试、不允许脏源码，
并要求 Android 正式包完成签名。Linux 系统 Python 补充包通过
`scripts/Build-LinuxSystemPackage.ps1 -OutputDirectory <发布目录>` 构建。

清理只删除已确认可重新生成的构建目录和过期产物；保留当前发布、必要回滚包、
源码、签名密钥、用户配置及整理后的检查记录。不会清理整个用户级 Gradle、NuGet
缓存或其它项目目录。
