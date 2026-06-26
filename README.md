# StressUtil - Windows 压力测试工具（.NET Framework 4.8）

面向 **Windows** 的 CPU、内存、硬盘压力测试与基准工具，提供命令行与图形界面两种用法，输出格式参考主流工具（stress-ng、MemTest、CrystalDiskMark）。

> **运行环境**：目标框架 **.NET Framework 4.8**，兼容 **Windows Server 2012 / 2012 R2 / 2008 R2 SP1** 及所有更新的 Windows（Win7 SP1+、Win10/11、Server 2016/2019/2022）。
> 旧服务器若未自带 4.8，运行前安装一次免费的「.NET Framework 4.8 运行时」即可（Win10/Server 2019+ 已内置）。

## 功能概览

| 类型 | 功能 | 输出指标 |
|------|------|----------|
| **CPU** | 多线程 CPU 压力测试，可调时长/线程数/目标使用率 | 总操作数、ops/s、平均/峰值 CPU 利用率 |
| **内存** | 分配、模式填充、验证（类似 MemTest） | 写入/验证带宽 MB/s、错误数 |
| **硬盘** | 顺序/随机 读写的压力与基准（类似 CrystalDiskMark/fio） | 顺序与随机 读/写 MB/s、IOPS |

## 项目结构

- **CpuStressCore**：核心库（CPU / 内存 / 硬盘 测试逻辑）
- **StressUtil**：控制台程序（推荐的命令行入口）
- **CpuStressWin**：旧版 CPU 控制台（兼容旧用法）
- **CpuStressWinGui**：图形界面（CPU / 内存 / 硬盘）

## 环境要求

- **构建机**：安装 .NET SDK（6.0 或 8.0 均可）。首次构建会自动还原 `Microsoft.NETFramework.ReferenceAssemblies`（net48 引用程序集），**无需安装 Visual Studio 或 v4.8 Targeting Pack**。
- **目标机**：.NET Framework 4.8 运行时。

## 编译

### 一键构建（推荐）

```powershell
# 在仓库根目录
.\build.ps1                 # Release，输出到 dist\package_<时间戳>\ 并打包 zip
.\build.ps1 -Clean         # 先清理再构建
.\build.ps1 -Configuration Debug
```

或用批处理：

```bat
build.cmd
build.cmd -Clean
```

### 用 dotnet 直接编译

```bash
dotnet build  CpuStressCore\CpuStressCore.csproj   -c Release
dotnet build  StressUtil\StressUtil.csproj         -c Release
dotnet build  CpuStressWin\CpuStressWin.csproj     -c Release
dotnet build  CpuStressWinGui\CpuStressWinGui.csproj -c Release

# 发布（框架依赖，输出 exe+dll 到 out 目录）
dotnet publish StressUtil\StressUtil.csproj -c Release -f net48 -o out\StressUtil
```

> 说明：.NET Framework 是「框架依赖」部署，产物即 exe + 依赖 dll；不使用 RID / 自包含 / 单文件（那是 .NET Core/5+ 的概念）。把输出目录整体拷到目标机即可运行。

## 用法（StressUtil 控制台）

```text
StressUtil cpu [选项]           # CPU 压力测试
StressUtil memory [选项]        # 内存压力测试
StressUtil disk [选项]          # 硬盘压力测试
```

### CPU 选项

- `-d, --duration <秒>`  测试时长（默认 60）
- `-t, --threads <数量>` 线程数（默认 逻辑核心数）
- `-p, --cpu-percent <1-100>` 目标 CPU 使用率（默认 100）

### Memory 选项

- `-d, --duration <秒>`  测试时长（默认 60，0=直到取消）
- `-m, --mb <MB>`        使用内存 MB（默认 可用内存的约 25%，上限 2048）
- `-t, --threads <数量>` 线程数（默认 1）
- `-b, --block-kb <KB>`  块大小（默认 64）
- `--pattern <0-4>`      0=AllZero 1=AllOne 2=Alternating 3=WalkingOne 4=Random

### Disk 选项

- `-p, --path <路径>`    测试目录（默认当前目录）
- `-d, --duration <秒>`  每阶段时长（默认 10）
- `-s, --size-mb <MB>`   测试文件大小（默认 256）
- `-b, --block-kb <KB>`  块大小（默认 1024）
- `-t, --threads <数量>` 并发线程（默认 1）
- `--seq-only`           仅顺序读/写
- `--rnd-only`           仅随机读/写

### 示例

```bat
StressUtil cpu -d 60 -p 100
StressUtil memory -d 120 -m 512 -t 2
StressUtil disk -p C:\Temp -d 15 -s 512
```

## 图形界面

直接运行 `CpuStressWinGui.exe`，在界面中选择 CPU / 内存 / 硬盘进行测试，并查看实时图表（CPU%、内存%、磁盘读写、CPU 温度）。

## 说明

- **CPU 利用率**：通过 `typeperf` / 性能计数器获取（部分环境需要相应权限）。
- **内存测试**：会占用指定或自动计算的内存，请勿在内存紧张的生产环境长时间运行。
- **硬盘测试**：会在指定目录创建临时文件并在结束后删除；请勿指向系统关键分区。

## 与主流工具对应关系

- **CPU**：类似 stress-ng 的负载方式与利用率/吞吐量输出（注：本工具为用户态标量负载，非 AVX 满载烤机）。
- **内存**：类似 stress-ng `--vm` 的写入/验证与错误计数（托管内存，非 MemTest86 那种裸机硬件级诊断）。
- **硬盘**：类似 CrystalDiskMark 的顺序/随机 读写 MB/s、IOPS（Windows 下走无缓冲 I/O 避免读缓存虚高）。
