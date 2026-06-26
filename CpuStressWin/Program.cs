using System;
using System.Threading.Tasks;
using CpuStressCore;

namespace CpuStressWin
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            int duration = 60, threads = Environment.ProcessorCount, percent = 100;
            for (int i = 0; i < args?.Length; i++)
            {
                var a = args[i];
                if ((a == "-c" || a == "--cpu") && i + 1 >= args.Length) { }
                else if ((a == "-d" || a == "--duration") && i + 1 < args.Length && int.TryParse(args[++i], out int d)) duration = d;
                else if ((a == "-t" || a == "--threads") && i + 1 < args.Length && int.TryParse(args[++i], out int t)) threads = t;
                else if ((a == "-p" || a == "--cpu-percent") && i + 1 < args.Length && int.TryParse(args[++i], out int p)) percent = Math.Max(1, Math.Min(100, p));
                else if (a == "-h" || a == "--help") { PrintHelp(); return 0; }
            }

            var progress = new Progress<string>(s => Console.WriteLine(s));
            var cts = new System.Threading.CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { cts.Cancel(); e.Cancel = true; };

            try
            {
                var options = new StressOptions { DurationSec = duration, Threads = threads, CpuPercent = percent };
                await StressRunner.RunCpuStressAsync(options, progress, cts.Token).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) { Console.WriteLine("已取消。"); return 130; }
            catch (Exception ex) { Console.WriteLine("错误: " + ex.Message); return 1; }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("CpuStressWin --cpu [--duration 60] [--threads N] [--cpu-percent 100]");
        }
    }
}
