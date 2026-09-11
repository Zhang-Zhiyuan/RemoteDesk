# 文件与剪贴板检查（2026-09-12）

这份记录对应发布前的专项检查，包含前一轮文本剪贴板修复。后续发布汇总见 [1.0.16](Release-1.0.16.md)。

## 修复

- Windows、Linux、Android 接收端提供按传输编号匹配的保存回执。校验、关闭文件和最终保存成功后才确认，发送进度不等于保存成功。
- Windows、Linux 和新增的 Android 发送端等待保存回执；拒收、断开、超时不会计为成功。保存确认超时会提醒先检查接收目录，避免重复发送。旧版保留原协议，不冒充已确认保存。
- Windows 文件选择、文件剪贴板粘贴、拖放的确认绑定原连接；连接切换后必须重新选择。Windows、Linux 文件发送失败弹窗提供原因。
- 修正跨平台接收目录提示，不再把 Android/Linux 都写成 Windows 的 Downloads 路径；文件重名自动改名，不覆盖已有文件。
- Android 的“更多 → 发送文件”使用系统文件选择器，显示名称、大小、目标设备、直连/中继和保存位置，支持取消及结果弹窗。单次一个文件，最多 1 GiB。文件提供方不提供大小时明确提示先下载到本地。[Android 文件选择器说明](https://developer.android.com/training/data-storage/shared/documents-files)
- 远程应用更新保留原来的校验和重启状态流程，不与普通文件保存回执混用。

发起端确认后，被控端通过设备密钥认证的会话可以无人值守接收，不再要求远端用户点击。不会自动执行收到的普通文件。

## 验证

证据保存在 `artifacts/transfer-20260912/`，没有读取或覆盖用户正在使用的剪贴板、桌面文件。

最终记录：`windows-tests-release.log`、`linux-tests-final.log`、
`android-package-final.log`、`public-relay-final/public-relay.json`、`android-ui/report.json`。

| 检查 | 结果 |
| --- | --- |
| Windows 回归 | 1884 通过，11 个既有硬件/真机用例跳过 |
| Linux / WSL Ubuntu 24.04 / Xvfb | 528 通过 |
| Android Debug / Release 单元测试 | 各 600 通过 |
| Android Release lint | 0 错误，5 个既有警告 |
| 已授权公网中继服务器 | 9 项通过 |
| Android 16 模拟器产品界面 | 12 项通过 |

公网测试使用真实中继、临时注册节点、生产 Windows 查看端和生产文件接收器。测试端数据是专门生成的：1 MiB 二进制文件、中文/emoji 文件名、同名再传、空文件、确认后取回文件、负向保存回执，以及中文/emoji/换行文本双向剪贴板。文件逐字节核对；Windows 系统剪贴板位于独立 window station。未连接现有用户节点、未重启或配置公网服务。

Android 界面测试实际打开系统文件选择器，验证取消选择和取消确认都不发送文件，再发送到加密测试接收端检查内容和保存回执，并重新验证文本剪贴板的发送、取回、粘贴及 Ctrl+C。签名候选 APK 在模拟器覆盖安装并正常启动。

一轮并行构建/回归中，Windows 既有的 `DeviceInfoQualificationIsBoundToCurrentConnectionOwner` 用例失败；未修改用例或放宽超时，单独复测及随后全套复测通过。不能据此宣称该偶发现象已彻底排除。

## 范围与限制

- 本次未连接到 USB 实体手机；安卓实测是 Android 16 模拟器，不能代替所有厂商手机验证。
- 公网 9 项测试不是六方向真机互控测试；安卓文件界面实测连接的是本地加密测试节点。
- Android 可以主动选文件发送，也可以作为被控端接收文件；不支持从另一台设备读取手机其它应用复制的文件。Linux 查看端本轮仍是主动发送入口；“取回远端文件”的完整确认界面由 Windows 查看端提供。
- Android 后台读取文本剪贴板仍可能受系统限制，前一轮说明见 [剪贴板检查](ClipboardSync-20260912.md)。不会自动绕过其它应用的存储或剪贴板访问权限。
- 候选 APK：`artifacts/transfer-20260912/RemoteDesk-android-transfer.apk`，仍沿用 1.0.15 / versionCode 18，不是新的正式 Release。
- APK 大小 515579 字节；SHA-256：`887dceb19185d0bebcbe77797dd03024487995311bf3b0a264cd7e0127e8e5de`。原发布签名验证通过，模拟器覆盖安装保留原应用数据。测试 APK 已卸载，模拟器已正常退出，公网临时测试节点已断开。
