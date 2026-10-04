# RemoteDesk 1.0.40

- 新增中继 SSH 终端。Windows / Linux 目标需自行运行 SSH 服务，并开启“允许通过中继连接本机 SSH”；Android 可作为 SSH 客户端。
- Windows / Linux 可配置 `ssh 5090` 一类终端别名，无需保持 RemoteDesk 主窗口打开。仍需目标设备密钥和系统 SSH 账号认证。
- SSH 与远程桌面互不接管，最多同时打开 4 条 SSH 连接；支持私钥、主机身份校验及离线移除别名。
- 修复 Linux SSH 结束时可能丢失尾部输出，以及 Android 切换登录方式时被迟到的私钥读取覆盖。
- 修复 Windows 窄窗口按钮遮挡、长别名显示不全、操作提示过期和文件确认表头换行。

三端版本统一为 1.0.40，Android 沿用原签名。中继服务也需更新；升级不会自动安装目标 SSH 服务或开启其访问开关。设备密钥、SSH 登录凭据和中继 root 密码用途不同。

本次不调整画面分辨率、码率或画质策略。已验证剪贴板、文件传输与取消、SSH 并发及 DPI 布局；Linux / Android 运行验证使用 WSL / 模拟器，未覆盖全部真机、OEM 和锁屏场景。继续通过预发布渠道提供。

Windows 仍是单个自包含 EXE，内含独立 SSH 命令行助手。Releases 提供完整下载；仓库 `release/windows-x64/RemoteDesk.exe` 改用 Git LFS 存储，未迁移旧提交。
