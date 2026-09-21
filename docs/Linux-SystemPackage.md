# Linux 系统 Python 便携包

此包使用系统 Python 3.10 或更高版本，适用于 Ubuntu 22.04/24.04，包括 ARM64 Jetson。
与 amd64 deb 不同，不内置 x86-64 Python 或本机二进制库；Windows、Android
不能运行此包。桌面远控需要可用的图形会话，完整桌面控制优先使用 Xorg。

解压后运行 `./remotedesk-linux-app`。缺少 Tk、Pillow、cryptography、Paramiko、FFmpeg
等依赖时，程序按现有依赖检查流程提示申请安装；系统管理员授权仍由系统处理。
不要将启动器单独移出目录，`app` 目录必须保留在旁边。

Ubuntu 22.04 请使用此包或新版便携 tar.gz，不要强装要求 glibc 2.38 的 amd64 deb。
新版便携包会在内置运行时不兼容时改用系统 Python，不替换 glibc、驱动或系统 Python。
停在系统登录界面不等于已有可供控制的用户桌面；完整桌面实测需先登录 Xorg 会话。

被控端需要设置设备密钥并启动；登录自己的公网中继使用服务器 root 密码，登录后自动记住配置，不保存 root 密码。
包中不包含测试机密码、中继访问令牌、发布私钥或个人配置。

新版本可以解压到新目录，关闭旧程序后启动新版；保留旧目录即可回退。
配置保存位置独立于程序目录。不要把本包覆盖到另一个架构的私有 Python runtime 中。

Jetson 的 X11 被控端会优先探测系统已有的 GStreamer/NVENC（`nvv4l2h264enc`）硬编，
再尝试 FFmpeg 硬编，失败仍可回退 JPEG。此可选路径不自动安装或替换 NVIDIA 驱动、
JetPack 和插件；只有实际输出可独立解码的首帧才会启用。其他 Linux 平台的候选路径不变。
硬编保留原有分辨率、码率预算和全 IDR 恢复策略，NV12 转换/码流显式使用 BT.709；
X11 抓屏及缩放仍有 CPU 开销，不是全链路零拷贝。需要精细静态图像时仍可选择 JPEG。
