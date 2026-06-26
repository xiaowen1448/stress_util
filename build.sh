#!/usr/bin/env bash
# 本项目已切换为 .NET Framework 4.8（兼容 Windows Server 2012+），仅能在 Windows 上构建。
# 请在 Windows 上使用 build.ps1 / build.cmd：
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   build.cmd
# Linux/macOS 无法构建 .NET Framework 目标，此脚本仅作提示。
echo "请在 Windows 上运行 build.ps1 / build.cmd 构建（目标 .NET Framework 4.8，兼容 Windows Server 2012+）。"
exit 1
