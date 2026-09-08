# 构建产物与历史清单

公开仓库只跟踪本说明和 `RemoteDesk-release-manifest.json`。安装包、缓存、
签名材料、截图、设备配置和私有诊断报告均保留在本地，不应提交到源码历史。

## 获取与构建

当前源码包含 Windows、Linux 和 Android，以及自托管中继服务。
构建环境和脚本参数见 [项目 README](../README.md#构建)；
Linux 系统 Python/ARM64 安装见 [Linux-SystemPackage](../docs/Linux-SystemPackage.md)。
正式 Android 构建需要自己的签名密钥，仓库只提供配置模板。

`scripts/Publish-LocalRelease.ps1` 是本地正式发布辅助入口；请先阅读其参数、
版本及输出目录，避免把旧版目录或调试 APK 当作新版本安装包。
需要发布二进制时，应在最终源码上重新构建、验证，并单独作为 GitHub Release
附件上传。本次源码上传不包含或新建 Release 安装包。

## 历史记录

随源码保留的 [发布清单](RemoteDesk-release-manifest.json) 只记录历史 1.0.0
制品的工具链和 SHA-256，不对应当前源码快照。最新 Linux 优化和 Android 1.0.1
不能用该清单冒充共同版本的完整发布包。

旧文档中的 `artifacts/...` 是本地历史证据路径，不是公开下载链接。
原始测试证据和旧开发历史仍保留在本地，未将含个人设备信息的目录上传。
当前源码的实测范围与尚未覆盖项见
[最新复核报告](../docs/OptimizationRecheck-20260908.md)。
