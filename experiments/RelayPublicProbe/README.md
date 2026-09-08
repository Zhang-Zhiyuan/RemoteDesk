# 公网中继交互验收

仅在明确获授权的服务器上手动执行。这个工具调用应用自身的 SSH/SFTP 自动安装器，
会安装/更新 `remotedesk-relay.service`；不访问真实桌面、输入、剪贴板或本机应用设置。
不要加入默认单元测试或 CI。

```powershell
dotnet build experiments/RelayPublicProbe/RelayPublicProbe.csproj -c Release
dotnet run --project experiments/RelayPublicProbe/RelayPublicProbe.csproj -c Release --no-build -- <服务器地址> 56567 root SHA256:<已验证的SSH主机指纹>
```

必须使用交互终端，密码在隐藏输入提示中输入；不要将密码放到命令参数、环境变量、
脚本或输出文件。SSH 指纹须从可信渠道或已经验证的 SSH 连接取得。

测试公网目录、访问密钥拒绝、TLS 固定、双向随机载荷 SHA-256、重复安装保留活跃
隧道及服务 PID、心跳/忙碌状态、查看端与被控注册重连。还使用真实 RemoteViewerClient
验证 RemoteDesk 口令认证、加密 1080p JPEG 原样到达/解码，以及鼠标按下/释放、键盘
按下/释放四条消息按序到达；合成被控端只解码输入，绝不向真实桌面注入输入。
合成被控端只监听本机回环，
结束后清理合成设备，服务器中继服务保留运行。单次 1 MiB 双向载荷统计是短期路径
吞吐观测，不是服务器限速或多运营商网络上限的证明。
