using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace CpuStressWinGui
{
    /// <summary>
    /// 磁盘下拉项：Instance 用于性能计数器，Display 为显示文本（含型号）
    /// </summary>
    public sealed class DiskItem
    {
        public string Instance { get; set; }
        public string Display { get; set; }
        public override string ToString() => Display ?? Instance ?? "";
    }
    /// <summary>
    /// 使用 Windows 性能计数器采集 CPU%、内存%、磁盘 读/写 字节/秒，供图表实时更新
    /// </summary>
    public sealed class PerfMonitor : IDisposable
    {
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _ramAvailCounter;
        private PerformanceCounter _diskReadCounter;
        private PerformanceCounter _diskWriteCounter;
        private PerformanceCounter _diskBusyCounter;
        private ulong _totalRamMb;
        private bool _disposed;
        private string _diskInstance = "_Total";

        public float CpuPercent { get; private set; }
        public float MemoryUsedPercent { get; private set; }
        public float MemoryUsedMb { get; private set; }
        public float DiskReadMbPerSec { get; private set; }
        public float DiskWriteMbPerSec { get; private set; }
        public float DiskBusyPercent { get; private set; }
        /// <summary>CPU 温度（摄氏度），不可用时为 float.NaN</summary>
        public float CpuTemperatureCelsius { get; private set; } = float.NaN;
        public float TotalRamMb => _totalRamMb;
        public string DiskInstance => _diskInstance;

        public bool IsAvailable { get; private set; }

        /// <summary>
        /// 创建「全机 CPU 总使用率」计数器。优先用 "Processor Information(_Total)"——它跨所有
        /// 处理器组统计；多核服务器(>64 逻辑CPU)上 "Processor(_Total)" 只统计单个组会偏低。
        /// 旧系统不支持时回退到 "Processor(_Total)"。
        /// </summary>
        private static PerformanceCounter CreateCpuTotalCounter()
        {
            try
            {
                var c = new PerformanceCounter("Processor Information", "% Processor Time", "_Total", true);
                c.NextValue();
                return c;
            }
            catch
            {
                return new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            }
        }

        public static string[] GetPhysicalDiskInstances()
        {
            try
            {
                var cat = new PerformanceCounterCategory("PhysicalDisk");
                var names = cat.GetInstanceNames();
                if (names == null || names.Length == 0) return new[] { "_Total" };
                var list = names
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct()
                    .OrderBy(n => n == "_Total" ? 1 : 0)
                    .ThenBy(n => n)
                    .ToArray();
                return list.Length > 0 ? list : new[] { "_Total" };
            }
            catch
            {
                return new[] { "_Total" };
            }
        }

        /// <summary>
        /// 获取磁盘列表（含型号），用于下拉显示。Display 格式如 "0 C: - Samsung SSD 870"
        /// </summary>
        public static List<DiskItem> GetPhysicalDiskDisplayList()
        {
            var result = new List<DiskItem>();
            var models = GetDiskModelsByIndex();
            try
            {
                var instances = GetPhysicalDiskInstances();
                foreach (var inst in instances)
                {
                    if (string.Equals(inst, "_Total", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string model = null;
                    var firstPart = inst.Trim().Split(' ')[0];
                    if (int.TryParse(firstPart, out int idx) && models.TryGetValue(idx, out string m))
                        model = m;
                    result.Add(new DiskItem
                    {
                        Instance = inst,
                        Display = string.IsNullOrEmpty(model) ? inst : inst + " - " + model.Trim()
                    });
                }
            }
            catch { }
            return result;
        }

        private static Dictionary<int, string> GetDiskModelsByIndex()
        {
            var dict = new Dictionary<int, string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive"))
                using (var results = searcher.Get())
                {
                    foreach (var obj in results.Cast<ManagementObject>())
                    {
                        var idx = obj["Index"];
                        var model = obj["Model"]?.ToString();
                        if (idx != null && model != null && int.TryParse(idx.ToString(), out int i))
                            dict[i] = model;
                    }
                }
            }
            catch { }
            return dict;
        }

        public PerfMonitor(string diskInstance = "_Total")
        {
            try
            {
                _cpuCounter = CreateCpuTotalCounter();
                _cpuCounter.NextValue();

                _ramAvailCounter = new PerformanceCounter("Memory", "Available MBytes", true);
                var mem = WindowsSysInfo.GetMemoryInfo();
                _totalRamMb = mem.TotalPhysicalMb;

                SetDiskInstance(diskInstance);
                IsAvailable = true;
            }
            catch
            {
                IsAvailable = false;
            }
        }

        public void SetDiskInstance(string diskInstance)
        {
            if (_disposed) return;
            if (string.IsNullOrWhiteSpace(diskInstance)) diskInstance = "_Total";
            _diskInstance = diskInstance;

            try { _diskReadCounter?.Dispose(); } catch { }
            try { _diskWriteCounter?.Dispose(); } catch { }
            try { _diskBusyCounter?.Dispose(); } catch { }

            _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", _diskInstance, true);
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", _diskInstance, true);
            _diskBusyCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", _diskInstance, true);
            _diskReadCounter.NextValue();
            _diskWriteCounter.NextValue();
            _diskBusyCounter.NextValue();
        }

        /// <summary>
        /// 采样一次并更新属性（需间隔约 1 秒调用一次，首次 NextValue 返回 0 属正常）
        /// </summary>
        public void Sample()
        {
            if (!IsAvailable || _disposed) return;
            try
            {
                CpuPercent = _cpuCounter.NextValue();
                float availMb = _ramAvailCounter.NextValue();
                if (_totalRamMb > 0)
                {
                    MemoryUsedPercent = (float)((_totalRamMb - availMb) / (double)_totalRamMb * 100.0);
                    MemoryUsedMb = (float)(_totalRamMb - (ulong)Math.Max(0, availMb));
                }
                DiskReadMbPerSec = _diskReadCounter.NextValue() / (1024 * 1024);
                DiskWriteMbPerSec = _diskWriteCounter.NextValue() / (1024 * 1024);
                float busy = _diskBusyCounter.NextValue();
                DiskBusyPercent = Math.Min(100f, Math.Max(0f, busy));
                CpuTemperatureCelsius = GetCpuTemperatureCelsius();
            }
            catch
            {
                // ignore
            }
        }

        private static float GetCpuTemperatureCelsius()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature"))
                using (var results = searcher.Get())
                {
                    foreach (var obj in results.Cast<ManagementObject>())
                    {
                        var v = obj["CurrentTemperature"];
                        if (v != null && long.TryParse(v.ToString(), out long tenthsKelvin) && tenthsKelvin > 0)
                        {
                            double celsius = tenthsKelvin / 10.0 - 273.15;
                            if (celsius >= -20 && celsius <= 150)
                                return (float)celsius;
                        }
                    }
                }
            }
            catch { }
            return float.NaN;
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                _cpuCounter?.Dispose();
                _ramAvailCounter?.Dispose();
                _diskReadCounter?.Dispose();
                _diskWriteCounter?.Dispose();
                _diskBusyCounter?.Dispose();
            }
            catch { }
            _disposed = true;
        }
    }
}
