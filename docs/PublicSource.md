# 公开源码说明

公开仓库只包含当前源码快照。原开发分支和旧二进制历史保留在本地，
不随公开 `main` 上传。历史文档中的提交号、安装包哈希和测试结果仅属于
当时的构建，不能证明当前源码已经完成相同验收。

## 隐私与产物

- 真实测试机器、服务器地址已脱敏为文档示例地址，个人用户路径改为示例。
- 不上传 `.env`、签名密钥、DPAPI 配置、设备口令、诊断截图、测试日志和安装包。
- `artifacts/RemoteDesk-release-manifest.json` 是历史 1.0.0 清单；其中哈希不能
  用于校验当前源码的新构建。其它历史证据路径只在本地保留。
- 公开源码不等于另行发布安装包；构建入口见 [README](../README.md#构建)。
- 仓库暂未指定 RemoteDesk 项目许可证；第三方许可仅适用于相应依赖。

## 真机实验必须明确选择目标

普通单元测试不启用远程实体测试。只有显式设置
`REMOTEDESK_REAL_MACHINE_TESTS=1`（键盘测试还需单独启用）后才运行真机用例，
并且必须提供 `REMOTEDESK_REAL_MACHINE_HOST`。缺少目标时失败，不回退到
开发者机器或本机。口令仍通过原有环境变量或内存管道提供，不写入源码。

`Invoke-RemoteDesk4K60Entity.ps1` 和 `Invoke-RemoteDeskKeyboardEntity.ps1`
还要求显式传入 `-SshTarget user@host` 与
`-RemoteWindowsProfile 'C:\Users\Example'`，替换为自己授权的测试机器。
这两份专用工具保留 Windows + Ubuntu-24.04 WSL、SSH 2222 端口和桌面目录
中的 RemoteDesk/FFmpeg 这一历史测试布局，不是通用自动部署入口。
必须先核对主机指纹、程序哈希、屏幕选择和交互会话，勿用于他人的活动桌面。
键盘脚本不再默认最小化任何特定应用。

通用三端互联检查工具及边界见
[InteropProbe](../experiments/InteropProbe/README.md)。

## 验收边界

本次源码上传前自动回归：Windows 1572 项通过、11 项环境跳过；Python 289 项
通过、5 项环境跳过；Android Debug 392 项通过，Debug lint 通过。
这不是新一轮真机互控测试，也未重跑 Android Release 验收。

以 [最新优化复核](OptimizationRecheck-20260908.md) 为准：包括 Jetson 原生编码
和 Unicode 输入修复，但最新一轮 Windows 输入桌面不可访问，未重新完成
全部六个方向的画面呈现验收。更早的六方向短测保留为历史证据，不能扩展为
长稳、所有设备、所有功能或“没有 bug”的保证。
