# RemoteDesk 1.0.31

- 修复 Android 接收重名文件后扩展名异常，例如 `name.txt (1)`；已知类型按系统 MIME 映射保存，未知类型保持二进制。
- Linux 设备能力列表补全名称，不再把已知的设备身份和视频诊断能力显示为 Unknown。
- 三端安装包统一为 1.0.31，Android 沿用原发布签名，可保留数据覆盖安装。

本版仍为预发布。中继实测覆盖 Windows → Android、Windows → Linux 和 Linux → Android；Android 文件名修复另在 Android 16 模拟器验证。测试范围和网络限制见 [检查记录](Relay-Android-HYX-Audit-20260921.md)，不代表所有设备和锁屏场景均已验证。

Ubuntu 22.04 使用便携 tar.gz 包；amd64 deb 要求 glibc 2.38。升级本版客户端不需要重启或升级已兼容的公网中继服务。
