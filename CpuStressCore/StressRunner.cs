using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CpuStressCore
{
    public sealed class StressOptions
    {
        public int DurationSec { get; set; } = 60;
        public int Threads { get; set; } = Environment.ProcessorCount;
        public int CpuPercent { get; set; } = 100;
        public int MonitorIntervalMs { get; set; } = 500;
        // 控制窗口 500ms：空闲段用 Stopwatch 精确界定，远大于 Thread.Sleep 计时器粒度，
        // 使实际占用精确贴近目标百分比；同时窗口够小，图表(每秒采样)看起来平滑。
        // 忙阶段仍频繁检查取消，停止依然灵敏。
        public int ControlWindowMs { get; set; } = 500;
    }

    public sealed class StressResult
    {
        public string CpuName { get; set; }
        public int LogicalCores { get; set; }
        public int Threads { get; set; }
        public int CpuPercent { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double ElapsedSeconds { get; set; }
        public long TotalOps { get; set; }
        public float AvgCpuUtil { get; set; }
        public float MaxCpuUtil { get; set; }
        public int UtilSamples { get; set; }
    }

    public static class StressRunner
    {
        // 提升系统计时器分辨率到 1ms：否则 Thread.Sleep 受默认 ~15.6ms 粒度影响，
        // 高占比(如 98%)对应的 ~2ms 休眠基本不生效，导致实际仍跑满 100%、无法严格按峰值。
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uMilliseconds);

        public static string GetProcessorName()
        {
            return PlatformHelper.GetProcessorName();
        }

        public static async Task<StressResult> RunCpuStressAsync(
            StressOptions options,
            IProgress<string> log,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.DurationSec <= 0) throw new ArgumentOutOfRangeException(nameof(options.DurationSec));
            if (options.Threads <= 0) throw new ArgumentOutOfRangeException(nameof(options.Threads));
            if (options.CpuPercent < 1 || options.CpuPercent > 100) throw new ArgumentOutOfRangeException(nameof(options.CpuPercent));
            if (options.ControlWindowMs < 10) options.ControlWindowMs = 10;
            if (options.MonitorIntervalMs < 100) options.MonitorIntervalMs = 100;

            var result = new StressResult
            {
                CpuName = GetProcessorName(),
                LogicalCores = Environment.ProcessorCount,
                Threads = options.Threads,
                CpuPercent = options.CpuPercent,
                StartTime = DateTime.Now
            };

            log?.Report("");
            log?.Report("========== CPU 压力测试 ==========");
            log?.Report($"CPU 型号:       {result.CpuName}");
            log?.Report($"逻辑核心数:     {result.LogicalCores}");
            log?.Report($"测试线程数:     {result.Threads}");
            log?.Report($"目标 CPU 使用率: {result.CpuPercent}%");
            log?.Report($"开始时间:       {result.StartTime:yyyy-MM-dd HH:mm:ss}");

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(options.DurationSec));
                var token = cts.Token;

                var monitorTask = Task.Run(() => PlatformHelper.MonitorCpuUtil(options.MonitorIntervalMs, token), token);

                // 关键：用专用线程而非线程池(Task.Run)。CPU 满载的忙循环若占满线程池线程，会饿死
                // 同时运行的内存/磁盘测试(它们的 Task.Run 工作项排不到线程)——这正是「同时跑 CPU+内存
                // 时内存占用不增长」的根因。专用线程由 OS 抢占式调度，各测试公平分到 CPU。
                timeBeginPeriod(1); // 让 Thread.Sleep 精确到 ~1ms，使占空比节流严格贴近目标百分比
                var threads = new Thread[options.Threads];
                var ops = new long[options.Threads];
                try
                {
                    for (int i = 0; i < options.Threads; i++)
                    {
                        int idx = i;
                        threads[i] = new Thread(() => ops[idx] = StressWorker(options, token))
                        {
                            IsBackground = true,
                            // 低于普通优先级：满载时仍吃满空闲 CPU，但 UI 线程(普通优先级)一需要运行
                            // 即被 OS 抢占调度，避免高占比把界面饿死、点不动“停止”。
                            Priority = ThreadPriority.BelowNormal,
                            Name = "cpu-stress-" + idx
                        };
                        threads[i].Start();
                    }
                    await Task.Run(() => { foreach (var t in threads) t.Join(); }).ConfigureAwait(false);
                }
                finally
                {
                    timeEndPeriod(1);
                }
                long totalOps = 0;
                foreach (var v in ops) totalOps += v;
                result.TotalOps = totalOps;

                var mon = await SafeAwaitMonitor(monitorTask).ConfigureAwait(false);
                result.AvgCpuUtil = mon.Item1;
                result.MaxCpuUtil = mon.Item2;
                result.UtilSamples = mon.Item3;
            }

            result.EndTime = DateTime.Now;
            result.ElapsedSeconds = (result.EndTime - result.StartTime).TotalSeconds;

            log?.Report("");
            log?.Report("========== 测试结果 ==========");
            log?.Report($"结束时间:       {result.EndTime:yyyy-MM-dd HH:mm:ss}");
            log?.Report($"持续时长:       {result.ElapsedSeconds:F2} 秒");
            log?.Report("");
            log?.Report("【CPU 性能】");
            log?.Report($"总操作数:       {result.TotalOps:N0}");
            if (result.ElapsedSeconds > 0)
                log?.Report($"平均吞吐量:     {(result.TotalOps / result.ElapsedSeconds):N0} ops/s");
            if (result.UtilSamples > 0 && (result.AvgCpuUtil > 0 || result.MaxCpuUtil > 0))
            {
                log?.Report($"平均 CPU 利用率: {result.AvgCpuUtil:F1}%");
                log?.Report($"峰值 CPU 利用率: {result.MaxCpuUtil:F1}%");
            }
            else
            {
                log?.Report("CPU 利用率:     未获取（当前平台或权限限制）");
            }

            return result;
        }

        private static async Task<(float, float, int)> SafeAwaitMonitor(Task<(float, float, int)> monitorTask)
        {
            try
            {
                return await monitorTask.ConfigureAwait(false);
            }
            catch
            {
                return (0f, 0f, 0);
            }
        }

        private static long StressWorker(StressOptions options, CancellationToken token)
        {
            long ops = 0;
            int windowMs = options.ControlWindowMs;
            double frac = options.CpuPercent / 100.0;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;

            double freq = Stopwatch.Frequency;
            long windowTicks = (long)(windowMs / 1000.0 * freq);
            long workTicks = (long)(windowMs / 1000.0 * frac * freq);
            if (windowTicks <= 0) windowTicks = 1;

            var sw = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                long start = sw.ElapsedTicks;

                // ── 忙阶段：占空比的工作部分（Stopwatch 精确界定时长，必满载该核）──
                long workEnd = start + workTicks;
                while (!token.IsCancellationRequested && sw.ElapsedTicks < workEnd)
                {
                    for (int i = 0; i < 200; i++)
                    {
                        _ = Math.Sqrt(i * i + 1);
                        ops++;
                    }
                }

                // ── 空闲阶段：用 Stopwatch 精确界定到窗口结束，循环短 Sleep(1) 让出 CPU。
                //    无论单次 Thread.Sleep 受系统计时器粒度影响多不准，整窗的占空比都精确贴近目标，
                //    解决「高占比(如98%)因 ~2ms 休眠被放大而严重欠压、或低占比被放大而过压」的问题。──
                long windowEnd = start + windowTicks;
                while (!token.IsCancellationRequested && sw.ElapsedTicks < windowEnd)
                {
                    Thread.Sleep(1);
                }
            }

            return ops;
        }
    }
}