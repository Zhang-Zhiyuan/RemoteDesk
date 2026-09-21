# RemoteDesk 1.0.30

- Linux 便携包兼容 Ubuntu 22.04：内置运行时不适配时自动使用系统 Python 3.10+，不替换 glibc、不改系统 Python。
- 修复 Python 3.10 上中继连接的取消、超时和失败路径切换，避免身份校验错误被超时掩盖。
- 包含上一轮按键修复：右 Shift、左右修饰键、小键盘、长按快捷键，以及重连或失焦后的按键释放和粘贴顺序。
- Windows、Linux、Android 安装包统一为 1.0.30。Android 沿用原发布签名，可覆盖安装。

Ubuntu 22.04 使用 `RemoteDesk-linux-amd64.tar.gz` 或 `RemoteDesk-linux-system-python.tar.gz`；amd64 deb 仍要求 glibc 2.38。Linux 完整桌面控制需要 X11/Xorg。

本版仍为预发布。已完成的实机兼容性、文件/剪贴板和硬编解码检查见 [Ubuntu 22.04 检查记录](Ubuntu2204-CompatibilityAudit-20260921.md)，按键检查见 [输入边界检查](KeyboardBoundaryAudit-20260921.md)。这些检查不代表所有手机、国际键盘布局或锁屏环境都已验证；Android 锁屏后可能仍需重新授权录屏。
