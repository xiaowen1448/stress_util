using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CpuStressCore
{
    /// <summary>
    /// Cross-platform helpers for CPU name and CPU utilization (Linux/Windows/macOS 10.12+).
    /// </summary>
    public static class PlatformHelper
    {
        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public static string GetProcessorName()
        {
            if (IsWindows) return GetProcessorNameWindows();
            if (IsLinux) return GetProcessorNameLinux();
            if (IsMacOS) return GetProcessorNameMacOS();
            return "Unknown";
        }

        private static string GetProcessorNameWindows()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = "cpu get name",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "Unknown";
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    var lines = output?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines != null && lines.Length >= 2)
                    {
                        var name = lines[1].Trim();
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                }
            }
            catch
            {
                // fallback: try PowerShell
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = "-NoProfile -Command \"Get-WmiObject Win32_Processor | Select-Object -ExpandProperty Name\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            var name = p.StandardOutput?.ReadToEnd()?.Trim();
                            p.WaitForExit(3000);
                            if (!string.IsNullOrEmpty(name)) return name;
                        }
                    }
                }
                catch { }
            }
            return "Unknown";
        }

        private static string GetProcessorNameLinux()
        {
            try
            {
                const string path = "/proc/cpuinfo";
                if (!File.Exists(path)) return "Unknown";
                var lines = File.ReadAllLines(path);
                foreach (var line in lines)
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        var colon = line.IndexOf(':');
                        if (colon >= 0)
                        {
                            var name = line.Substring(colon + 1).Trim();
                            if (!string.IsNullOrEmpty(name)) return name;
                        }
                        break;
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        private static string GetProcessorNameMacOS()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sysctl",
                    Arguments = "-n machdep.cpu.brand_string",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "Unknown";
                    var name = p.StandardOutput?.ReadToEnd()?.Trim();
                    p.WaitForExit(3000);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
            return "Unknown";
        }

        /// <summary>
        /// Try to get CPU utilization (average, max, sample count). Returns (0,0,0) if not available.
        /// </summary>
        public static (float avgPercent, float maxPercent, int samples) MonitorCpuUtil(int intervalMs, CancellationToken token)
        {
            if (IsLinux) return MonitorCpuUtilLinux(intervalMs, token);
            if (IsWindows) return MonitorCpuUtilWindowsTypeperf(intervalMs, token);
            return (0f, 0f, 0);
        }

        private static (float avg, float max, int count) MonitorCpuUtilLinux(int intervalMs, CancellationToken token)
        {
            var samples = new System.Collections.Generic.List<float>();
            try
            {
                ulong[] prev = null;
                while (!token.IsCancellationRequested)
                {
                    Thread.Sleep(intervalMs);
                    var line = ReadProcStatCpuLine();
                    if (string.IsNullOrEmpty(line)) continue;
                    var curr = ParseProcStatCpu(line);
                    if (curr == null) continue;
                    if (prev != null && curr.Length == prev.Length)
                    {
                        ulong totalDelta = 0, idleDelta = 0;
                        for (int i = 0; i < curr.Length; i++)
                        {
                            totalDelta += (curr[i] - prev[i]);
                            if (i == 3) idleDelta = curr[i] - prev[i];
                        }
                        if (totalDelta > 0)
                        {
                            var used = (float)(totalDelta - idleDelta) / totalDelta * 100f;
                            samples.Add(used);
                        }
                    }
                    prev = curr;
                }
            }
            catch { }

            if (samples.Count == 0) return (0f, 0f, 0);
            float sum = 0, max = 0;
            foreach (var s in samples)
            {
                sum += s;
                if (s > max) max = s;
            }
            return (sum / samples.Count, max, samples.Count);
        }

        private static string ReadProcStatCpuLine()
        {
            try
            {
                var lines = File.ReadAllLines("/proc/stat");
                foreach (var l in lines)
                {
                    if (l.StartsWith("cpu ", StringComparison.Ordinal))
                        return l;
                }
            }
            catch { }
            return null;
        }

        private static ulong[] ParseProcStatCpu(string line)
        {
            var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return null;
            var arr = new ulong[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
                if (!ulong.TryParse(parts[i], out arr[i - 1])) return null;
            return arr;
        }

        private static (float avg, float max, int count) MonitorCpuUtilWindowsTypeperf(int intervalMs, CancellationToken token)
        {
            var samples = new System.Collections.Generic.List<float>();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Thread.Sleep(intervalMs);
                    var psi = new ProcessStartInfo
                    {
                        FileName = "typeperf",
                        // 用 "Processor Information(_Total)"：跨所有处理器组统计；多核服务器(>64 逻辑CPU)
                        // 上旧的 "Processor(_Total)" 只统计单个组会偏低。WS2012+ 均支持该计数器。
                        Arguments = "\"\\Processor Information(_Total)\\% Processor Time\" -sc 1",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) continue;
                        var output = p.StandardOutput?.ReadToEnd();
                        p.WaitForExit(2000);
                        if (string.IsNullOrEmpty(output)) continue;
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("(") || line.TrimStart().StartsWith("(")) continue;
                            var parts = line.Split(',');
                            if (parts.Length >= 2 && float.TryParse(parts[1].Trim().Replace("\"", ""), out float val))
                            {
                                samples.Add(val);
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            if (samples.Count == 0) return (0f, 0f, 0);
            float sum = 0, maxVal = 0;
            foreach (var s in samples) { sum += s; if (s > maxVal) maxVal = s; }
            return (sum / samples.Count, maxVal, samples.Count);
        }
    }
}
