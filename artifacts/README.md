# 打包与发布

本目录只跟踪本说明和 `RemoteDesk-release-manifest.json`。缓存、签名材料、
截图、设备配置和私有诊断报告保留在本地。供用户下载的成品放在根目录
[release](../release/)，不把整个 artifacts 目录上传。

## 获取与构建

当前源码包含 Windows、Linux 和 Android，以及自托管中继服务。
构建环境和脚本参数见 [项目 README](../README.md#构建)；
Linux 系统 Python/ARM64 安装见 [Linux-SystemPackage](../docs/Linux-SystemPackage.md)。
正式 Android 构建需要自己的签名密钥，仓库只提供配置模板。

`scripts/Publish-LocalRelease.ps1` 是本地正式发布辅助入口；请先阅读其参数、
版本及输出目录，避免把旧版目录或调试 APK 当作新版本安装包。
需要发布时，先在最终源码上重新构建、验证，再将 Windows EXE 及必要附带文件、
Android APK 和 Linux 安装包整理到 `release/`。该目录不放日志、中间产物和旧版备份。

## 发布清单

随包更新的 [发布清单](RemoteDesk-release-manifest.json) 记录构建所用的源码提交、
工具链和各成品的 SHA-256。核对版本时以清单中的 `sourceRevision` 和文件哈希为准，
不以文件修改时间判断。发布后的开发提交不代表下载包已经更新。

旧文档中的 `artifacts/...` 是本地历史证据路径，不是公开下载链接。
原始测试证据和旧开发历史保留在本地，不上传含个人设备信息的目录。
