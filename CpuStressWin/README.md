# CpuStressWin - Windows CPU 压力测试

基于 C# 的 Windows CPU 压力测试工具，兼容 **Windows 10** 与 **Windows Server 2016 及以上**（.NET Framework 4.6.2）。

## 环境要求

- Windows 10 或 Windows Server 2016+
- .NET Framework 4.6.2 或更高（Win10/Server 通常已预装）

## 编译

项目支持双目标：**net462**（.NET Framework 4.6.2）、**net6.0**（.NET 6），便于在仅装 .NET Framework 的服务器或仅装 .NET SDK 的机器上编译运行。

### 使用 .NET SDK（推荐）

```cmd
cd CpuStressWin
dotnet build -c Release -f net6.0
```

输出在 `bin\Release\net6.0\CpuStressWin.exe`。运行需本机已安装 [.NET 6 运行时](https://dotnet.microsoft.com/download/dotnet/6.0)（Windows 10/Server 可选安装）。

若需生成可在未安装 .NET 6 的机器上直接运行的 exe，可发布单文件：

```cmd
dotnet publish -c Release -f net6.0 -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### 使用 .NET Framework 4.6.2 目标

在已安装 .NET Framework 4.6.2 开发包或 Visual Studio 的机器上：

```cmd
dotnet build -c Release -f net462
```

或使用 Visual Studio 打开 `CpuStressWin.csproj`，选择 Release 生成。输出在 `bin\Release\net462\CpuStressWin.exe`，可在仅安装 .NET Framework 4.6.2 的 Win10/Server 上直接运行，无需安装 .NET 6。

## 用法

```text
CpuStressWin.exe [选项]
```

| 选项 | 简写 | 说明 | 默认值 |
|------|------|------|--------|
| `--cpu` | `-c` | 执行 CPU 压力测试（必选） | - |
| `--duration` | `-d` | 测试时长（秒） | 60 |
| `--threads` | `-t` | 工作线程数 | 逻辑处理器数 |
| `--cpu-percent` | `-p` | 目标 CPU 使用率 1–100 | 100 |
| `--help` | `-h` | 显示帮助 | - |

## 示例

```cmd
# 默认 60 秒、100% 使用率、使用全部逻辑核心
CpuStressWin.exe --cpu

# 运行 100 秒，目标 CPU 95%
CpuStressWin.exe --cpu --duration 100 --cpu-percent 95

# 使用 4 个线程，80% 使用率，30 秒
CpuStressWin.exe -c -d 30 -t 4 -p 80
```


## 图形界面版本（下拉选择 + 控制台输出）

仓库中同时提供 WinForms 图形界面项目：`CpuStressWinGui`。

### 编译与运行（net6.0-windows）

```cmd
cd CpuStressWinGui
dotnet build -c Release -f net6.0-windows
bin\Release\net6.0-windows\CpuStressWinGui.exe
```

界面说明：

- 上方 **下拉框** 选择：时长(秒)、线程数、CPU%
- 点击 **“测试”** 开始；点击 **“停止”** 可提前结束
- 下方“任务控制台”会实时输出日志与最终结果

## 说明

- **CPU 利用率监控**：依赖 Windows 性能计数器 `Processor\% Processor Time`。若以普通用户运行且无权限，结果中“平均/峰值 CPU 利用率”可能显示为“未获取”；以管理员运行或授予性能计数器权限后可正常显示。
- **线程数**：建议不超过逻辑处理器数；超过时主要增加上下文切换，压力效果不一定更大。
- **cpu-percent**：通过计算与睡眠时间比例逼近目标使用率，实际曲线会有波动。

## 与 Linux 版本对应关系

本工具与仓库中的 `cpu_stress.py`（Linux）在功能上对应：均为可配置时长、线程数和目标 CPU 使用率的 CPU 压力测试，便于在 Windows 与 Linux 上做一致的压力测试。
