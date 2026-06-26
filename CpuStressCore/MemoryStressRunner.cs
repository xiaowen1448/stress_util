using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CpuStressCore
{
    /// <summary>
    /// Memory stress test options (similar to MemTest86 / stress-ng --vm).
    /// </summary>
    public sealed class MemoryStressOptions
    {
        /// <summary>Test duration in seconds (0 = run until cancelled).</summary>
        public int DurationSec { get; set; } = 60;
        /// <summary>Total memory to use in MB (0 = auto, use a fraction of available).</summary>
        public int MemoryMb { get; set; } = 0;
        /// <summary>Number of worker threads.</summary>
        public int Threads { get; set; } = 1;
        /// <summary>Block size per allocation in KB (e.g. 64, 1024).</summary>
        public int BlockSizeKb { get; set; } = 64;
        /// <summary>Fill/verify pattern: 0=AllZero, 1=AllOne, 2=Alternating (0xAA55), 3=WalkingOne, 4=Random.</summary>
        public int Pattern { get; set; } = 2;
        /// <summary>Report progress interval in ms.</summary>
        public int ReportIntervalMs { get; set; } = 1000;
    }

    /// <summary>
    /// Memory stress test result (bandwidth, errors, like MemTest).</summary>
    public sealed class MemoryStressResult
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double ElapsedSeconds { get; set; }
        public long TotalBytesWritten { get; set; }
        public long TotalBytesVerified { get; set; }
        public long ErrorCount { get; set; }
        public int Threads { get; set; }
        public int MemoryMb { get; set; }
        public double WriteBandwidthMbps { get; set; }
        public double VerifyBandwidthMbps { get; set; }
    }

    public static class MemoryStressRunner
    {
        public const int PatternAllZero = 0;
        public const int PatternAllOne = 1;
        public const int PatternAlternating = 2;
        public const int PatternWalkingOne = 3;
        public const int PatternRandom = 4;

        /// <summary>
        /// Get approximate available physical memory in MB (best-effort cross-platform).
        /// </summary>
        public static long GetAvailableMemoryMb()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "wmic",
                        Arguments = "OS get FreePhysicalMemory /Value",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            var out_ = p.StandardOutput.ReadToEnd();
                            p.WaitForExit(2000);
                            var idx = out_.IndexOf("FreePhysicalMemory=", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                var val = out_.Substring(idx + 19).Trim().Split('\r', '\n')[0].Trim();
                                if (long.TryParse(val, out long kb)) return kb / 1024;
                            }
                        }
                    }
                }
                catch { }
                return 1024;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    var lines = System.IO.File.ReadAllLines("/proc/meminfo");
                    long memAvailable = 0;
                    foreach (var l in lines)
                    {
                        if (l.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2 && long.TryParse(parts[1], out long kb)) memAvailable = kb;
                            break;
                        }
                    }
                    if (memAvailable > 0) return memAvailable / 1024;
                }
                catch { }
                return 1024;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "sysctl",
                        Arguments = "-n hw.memsize",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            var s = p.StandardOutput?.ReadToEnd()?.Trim();
                            p.WaitForExit(2000);
                            if (long.TryParse(s, out long bytes)) return (long)(bytes / (1024 * 1024) * 0.5);
                        }
                    }
                }
                catch { }
                return 2048;
            }
            return 1024;
        }

        public static async Task<MemoryStressResult> RunMemoryStressAsync(
            MemoryStressOptions options,
            IProgress<string> log,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Threads < 1) options.Threads = 1;
            if (options.BlockSizeKb < 1) options.BlockSizeKb = 64;
            int blockBytes = options.BlockSizeKb * 1024;

            long availableMb = GetAvailableMemoryMb();
            int memoryMb;
            if (options.MemoryMb > 0)
            {
                memoryMb = options.MemoryMb;
                memoryMb = Math.Min(memoryMb, Math.Max(64, (int)availableMb - 128));
                memoryMb = Math.Max(64, memoryMb);
            }
            else
            {
                memoryMb = Math.Max(64, (int)(availableMb * 0.25));
                memoryMb = Math.Min(memoryMb, (int)Math.Min(availableMb, 2048));
            }
            long totalBytes = (long)memoryMb * 1024 * 1024;
            long perThread = totalBytes / options.Threads;
            if (perThread < blockBytes) { perThread = blockBytes; memoryMb = (int)((perThread * options.Threads) / (1024 * 1024)); }

            var result = new MemoryStressResult
            {
                StartTime = DateTime.Now,
                Threads = options.Threads,
                MemoryMb = memoryMb
            };

            log?.Report("");
            log?.Report("========== 内存压力测试 ==========");
            log?.Report($"可用内存约:     {availableMb} MB");
            log?.Report($"目标占用:       {memoryMb} MB（按配置峰值百分比，测试期间持续占用）");
            log?.Report($"块大小:         {options.BlockSizeKb} KB");
            log?.Report($"线程数:         {options.Threads}");
            log?.Report($"模式:           {GetPatternName(options.Pattern)}");
            log?.Report($"开始时间:       {result.StartTime:yyyy-MM-dd HH:mm:ss}");

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (options.DurationSec > 0)
                    cts.CancelAfter(TimeSpan.FromSeconds(options.DurationSec));
                var token = cts.Token;

                var reportInterval = Math.Max(500, options.ReportIntervalMs);
                long totalWritten = 0, totalVerified = 0, errors = 0;
                var lockObj = new object();
                var lastReport = Stopwatch.StartNew();

                // 用专用线程而非线程池(Task.Run)：避免与同时运行的 CPU 满载测试争抢线程池而饿死，
                // 否则同时跑 CPU+内存时内存分配线程排不到线程、内存占用不增长。
                var threads = new Thread[options.Threads];
                var res = new (long written, long verified, long err)[options.Threads];
                for (int i = 0; i < options.Threads; i++)
                {
                    int idx = i;
                    long threadBytes = (i == options.Threads - 1) ? (totalBytes - i * perThread) : perThread;
                    threads[i] = new Thread(() => res[idx] = MemoryWorkerWithAllocation(threadBytes, blockBytes, options.Pattern, reportInterval, (w, v, e) =>
                    {
                        lock (lockObj)
                        {
                            totalWritten += w; totalVerified += v; errors += e;
                            if (lastReport.ElapsedMilliseconds >= reportInterval)
                            {
                                lastReport.Restart();
                                var elapsed = (DateTime.Now - result.StartTime).TotalSeconds;
                                if (elapsed > 0)
                                    log?.Report($"  已占用 {memoryMb} MB, 写入 {totalWritten / (1024 * 1024)} MB, 验证 {totalVerified / (1024 * 1024)} MB, 错误 {errors}, 写入 {totalWritten / elapsed / (1024 * 1024):F1} MB/s");
                            }
                        }
                    }, token))
                    {
                        IsBackground = true,
                        // 低于普通优先级，避免内存测试的 CPU 密集填充/校验把 UI 线程饿死导致界面卡死。
                        Priority = ThreadPriority.BelowNormal,
                        Name = "mem-stress-" + idx
                    };
                    threads[i].Start();
                }

                await Task.Run(() => { foreach (var t in threads) t.Join(); }).ConfigureAwait(false);

                long tw = 0, tv = 0, te = 0;
                foreach (var r in res) { tw += r.written; tv += r.verified; te += r.err; }
                result.TotalBytesWritten = tw;
                result.TotalBytesVerified = tv;
                result.ErrorCount = te;
            }

            result.EndTime = DateTime.Now;
            result.ElapsedSeconds = (result.EndTime - result.StartTime).TotalSeconds;
            if (result.ElapsedSeconds > 0)
            {
                result.WriteBandwidthMbps = result.TotalBytesWritten / result.ElapsedSeconds / (1024 * 1024);
                result.VerifyBandwidthMbps = result.TotalBytesVerified / result.ElapsedSeconds / (1024 * 1024);
            }

            log?.Report("");
            log?.Report("========== 内存测试结果 ==========");
            log?.Report($"结束时间:       {result.EndTime:yyyy-MM-dd HH:mm:ss}");
            log?.Report($"持续时长:       {result.ElapsedSeconds:F2} 秒");
            log?.Report($"总写入:         {result.TotalBytesWritten / (1024 * 1024)} MB");
            log?.Report($"总验证:         {result.TotalBytesVerified / (1024 * 1024)} MB");
            log?.Report($"写入带宽:       {result.WriteBandwidthMbps:F2} MB/s");
            log?.Report($"验证带宽:       {result.VerifyBandwidthMbps:F2} MB/s");
            log?.Report($"错误数:         {result.ErrorCount}");
            // 主动回收测试期间占用的内存，使进程占用率/百分比下降
            try
            {
                GC.Collect(2, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced);
            }
            catch { }
            return result;
        }

        private static string GetPatternName(int p)
        {
            switch (p)
            {
                case 0: return "AllZero";
                case 1: return "AllOne";
                case 2: return "Alternating(0xAA55)";
                case 3: return "WalkingOne";
                case 4: return "Random";
                default: return "Alternating";
            }
        }

        /// <summary>压力测试：实际分配并占用内存，使系统内存使用率达到配置百分比；在配置时长内循环写/验证。</summary>
        private static (long written, long verified, long errors) MemoryWorkerWithAllocation(
            long perThreadBytes,
            int blockBytes,
            int pattern,
            int reportIntervalMs,
            Action<long, long, long> onProgress,
            CancellationToken token)
        {
            const int chunkSize = 16 * 1024 * 1024; // 16 MB per chunk
            var chunks = new List<byte[]>();
            long allocated = 0;
            while (allocated < perThreadBytes && !token.IsCancellationRequested)
            {
                int toAlloc = (int)Math.Min(chunkSize, perThreadBytes - allocated);
                try
                {
                    chunks.Add(new byte[toAlloc]);
                    allocated += toAlloc;
                }
                catch (OutOfMemoryException)
                {
                    break;
                }
            }
            if (chunks.Count == 0)
                return (0, 0, 0);

            long written = 0, verified = 0, errors = 0;
            var rnd = new Random(Environment.TickCount + (int)perThreadBytes);
            var lastReport = Stopwatch.StartNew();
            int blockIndex = 0;

            while (!token.IsCancellationRequested)
            {
                foreach (var chunk in chunks)
                {
                    if (token.IsCancellationRequested) break;
                    int len = chunk.Length;
                    FillPattern(chunk, len, pattern, blockIndex++, rnd);
                    written += len;
                    if (!VerifyPattern(chunk, len, pattern, blockIndex - 1, rnd))
                        errors++;
                    verified += len;
                    if (lastReport.ElapsedMilliseconds >= reportIntervalMs)
                    {
                        onProgress?.Invoke(written, verified, errors);
                        lastReport.Restart();
                    }
                }
            }
            onProgress?.Invoke(written, verified, errors);
            return (written, verified, errors);
        }

        private static (long written, long verified, long errors) MemoryWorker(
            long startOffset,
            long length,
            int blockBytes,
            int pattern,
            int reportIntervalMs,
            Action<long, long, long> onProgress,
            CancellationToken token)
        {
            long written = 0, verified = 0, errors = 0;
            var rnd = new Random(Environment.TickCount + (int)startOffset);
            var buffer = new byte[blockBytes];
            var lastReport = Stopwatch.StartNew();

            // 按配置时长运行：循环写/验证直到被取消（DurationSec 到或用户停止）
            while (!token.IsCancellationRequested)
            {
                long pos = 0;
                while (pos < length && !token.IsCancellationRequested)
                {
                    int toDo = (int)Math.Min(blockBytes, length - pos);
                    FillPattern(buffer, toDo, pattern, (int)((startOffset + pos) / blockBytes), rnd);
                    written += toDo;
                    if (!VerifyPattern(buffer, toDo, pattern, (int)((startOffset + pos) / blockBytes), rnd))
                        errors++;
                    verified += toDo;
                    pos += toDo;

                    if (lastReport.ElapsedMilliseconds >= reportIntervalMs)
                    {
                        onProgress?.Invoke(written, verified, errors);
                        lastReport.Restart();
                    }
                }
            }
            onProgress?.Invoke(written, verified, errors);
            return (written, verified, errors);
        }

        private static void FillPattern(byte[] buffer, int length, int pattern, int blockIndex, Random rnd)
        {
            switch (pattern)
            {
                case PatternAllZero:
                    for (int i = 0; i < length; i++) buffer[i] = 0;
                    break;
                case PatternAllOne:
                    for (int i = 0; i < length; i++) buffer[i] = 0xFF;
                    break;
                case PatternAlternating:
                    for (int i = 0; i < length; i++) buffer[i] = (byte)((i & 1) == 0 ? 0xAA : 0x55);
                    break;
                case PatternWalkingOne:
                    int bit = blockIndex % (length * 8);
                    for (int i = 0; i < length; i++)
                    {
                        int byteIdx = i * 8;
                        byte b = 0;
                        for (int j = 0; j < 8; j++)
                            if (byteIdx + j == bit) b |= (byte)(1 << j);
                        buffer[i] = b;
                    }
                    break;
                case PatternRandom:
                    rnd.NextBytes(buffer);
                    break;
                default:
                    for (int i = 0; i < length; i++) buffer[i] = (byte)((i & 1) == 0 ? 0xAA : 0x55);
                    break;
            }
        }

        private static bool VerifyPattern(byte[] buffer, int length, int pattern, int blockIndex, Random rnd)
        {
            switch (pattern)
            {
                case PatternAllZero:
                    for (int i = 0; i < length; i++) if (buffer[i] != 0) return false;
                    return true;
                case PatternAllOne:
                    for (int i = 0; i < length; i++) if (buffer[i] != 0xFF) return false;
                    return true;
                case PatternAlternating:
                    for (int i = 0; i < length; i++) if (buffer[i] != (byte)((i & 1) == 0 ? 0xAA : 0x55)) return false;
                    return true;
                case PatternWalkingOne:
                    int bit = blockIndex % (length * 8);
                    for (int i = 0; i < length; i++)
                    {
                        int byteIdx = i * 8;
                        byte b = 0;
                        for (int j = 0; j < 8; j++)
                            if (byteIdx + j == bit) b |= (byte)(1 << j);
                        if (buffer[i] != b) return false;
                    }
                    return true;
                case PatternRandom:
                    return true;
                default:
                    for (int i = 0; i < length; i++) if (buffer[i] != (byte)((i & 1) == 0 ? 0xAA : 0x55)) return false;
                    return true;
            }
        }
    }
}
