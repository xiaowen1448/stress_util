using System;
using System.Collections.Generic;
using System.IO;
using System.Management;

namespace CpuStressWinGui
{
    /// <summary>
    /// 获取 Windows 系统版本、CPU、内存、硬盘分区参数（类似任务管理器“概览”）
    /// </summary>
    public static class WindowsSysInfo
    {
        public sealed class OsInfo
        {
            public string Caption { get; set; }
            public string Version { get; set; }
            public string OsArchitecture { get; set; }
            public string BuildNumber { get; set; }
        }

        public sealed class CpuInfo
        {
            public string Name { get; set; }
            public int NumberOfCores { get; set; }
            public int NumberOfLogicalProcessors { get; set; }
            public string MaxClockSpeedMhz { get; set; }
        }

        public sealed class MemoryInfo
        {
            public ulong TotalPhysicalMb { get; set; }
            public ulong AvailablePhysicalMb { get; set; }
            public ulong UsedPhysicalMb => TotalPhysicalMb - AvailablePhysicalMb;
            public double UsedPercent => TotalPhysicalMb > 0 ? (double)(TotalPhysicalMb - AvailablePhysicalMb) / TotalPhysicalMb * 100 : 0;
        }

        public sealed class DiskPartitionInfo
        {
            public string DriveLetter { get; set; }
            public string VolumeLabel { get; set; }
            public string FileSystem { get; set; }
            public long TotalGb { get; set; }
            public long FreeGb { get; set; }
            public long UsedGb => TotalGb - FreeGb;
        }

        public static OsInfo GetOsInfo()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Caption, Version, OSArchitecture, BuildNumber FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        return new OsInfo
                        {
                            Caption = obj["Caption"]?.ToString()?.Trim() ?? "Windows",
                            Version = obj["Version"]?.ToString()?.Trim() ?? "",
                            OsArchitecture = obj["OSArchitecture"]?.ToString()?.Trim() ?? "",
                            BuildNumber = obj["BuildNumber"]?.ToString()?.Trim() ?? ""
                        };
                    }
                }
            }
            catch { }
            return new OsInfo { Caption = "Windows", Version = Environment.OSVersion.Version.ToString(), OsArchitecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit" };
        }

        public static CpuInfo GetCpuInfo()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        int maxMhz = 0;
                        int.TryParse(obj["MaxClockSpeed"]?.ToString(), out maxMhz);
                        return new CpuInfo
                        {
                            Name = obj["Name"]?.ToString()?.Trim() ?? "Unknown",
                            NumberOfCores = Convert.ToInt32(obj["NumberOfCores"] ?? 0),
                            NumberOfLogicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0),
                            MaxClockSpeedMhz = maxMhz > 0 ? maxMhz + " MHz" : ""
                        };
                    }
                }
            }
            catch { }
            return new CpuInfo
            {
                Name = CpuStressCore.PlatformHelper.GetProcessorName(),
                NumberOfCores = Environment.ProcessorCount,
                NumberOfLogicalProcessors = Environment.ProcessorCount,
                MaxClockSpeedMhz = ""
            };
        }

        public static MemoryInfo GetMemoryInfo()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        ulong totalKb = Convert.ToUInt64(obj["TotalVisibleMemorySize"] ?? 0UL);
                        ulong freeKb = Convert.ToUInt64(obj["FreePhysicalMemory"] ?? 0UL);
                        return new MemoryInfo
                        {
                            TotalPhysicalMb = totalKb / 1024,
                            AvailablePhysicalMb = freeKb / 1024
                        };
                    }
                }
            }
            catch { }
            return new MemoryInfo();
        }

        public static List<DiskPartitionInfo> GetDiskPartitions()
        {
            var list = new List<DiskPartitionInfo>();
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    try
                    {
                        long total = drive.TotalSize;
                        long free = drive.AvailableFreeSpace;
                        list.Add(new DiskPartitionInfo
                        {
                            DriveLetter = drive.Name.TrimEnd('\\'),
                            VolumeLabel = string.IsNullOrEmpty(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel,
                            FileSystem = drive.DriveFormat ?? "",
                            TotalGb = total / (1024 * 1024 * 1024),
                            FreeGb = free / (1024 * 1024 * 1024)
                        });
                    }
                    catch
                    {
                        // skip
                    }
                }
            }
            catch { }
            return list;
        }
    }
}
