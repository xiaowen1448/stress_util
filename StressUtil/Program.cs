using System;
using System.Threading;
using System.Threading.Tasks;
using CpuStressCore;

namespace StressUtil
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            string mode = args[0].ToLowerInvariant();
            if (mode == "-h" || mode == "--help")
            {
                PrintUsage();
                return 0;
            }

            var progress = new Progress<string>(s => Console.WriteLine(s));
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { cts.Cancel(); e.Cancel = true; };

            try
            {
                if (mode == "cpu" || mode == "-c" || mode == "--cpu")
                {
                    ParseCpuArgs(args, out int duration, out int threads, out int percent);
                    var options = new StressOptions
                    {
                        DurationSec = duration,
                        Threads = threads,
                        CpuPercent = percent
                    };
                    await StressRunner.RunCpuStressAsync(options, progress, cts.Token).ConfigureAwait(false);
                    return 0;
                }

                if (mode == "memory" || mode == "mem" || mode == "-m" || mode == "--memory")
                {
                    ParseMemoryArgs(args, out int duration, out int mb, out int threads, out int blockKb, out int pattern);
                    var options = new MemoryStressOptions
                    {
                        DurationSec = duration,
                        MemoryMb = mb,
                        Threads = threads,
                        BlockSizeKb = blockKb,
                        Pattern = pattern
                    };
                    await MemoryStressRunner.RunMemoryStressAsync(options, progress, cts.Token).ConfigureAwait(false);
                    return 0;
                }

                if (mode == "disk" || mode == "-d" || mode == "--disk")
                {
                    ParseDiskArgs(args, out string path, out int duration, out int fileSizeMb, out int blockKb, out int threads, out bool seqR, out bool seqW, out bool rndR, out bool rndW, out bool mixed);
                    var options = new DiskStressOptions
                    {
                        Path = path,
                        DurationSec = duration,
                        FileSizeMb = fileSizeMb,
                        BlockSizeKb = blockKb,
                        Threads = threads,
                        SequentialRead = seqR,
                        SequentialWrite = seqW,
                        RandomRead = rndR,
                        RandomWrite = rndW,
                        ReadWriteParallel = mixed
                    };
                    await DiskStressRunner.RunDiskStressAsync(options, progress, cts.Token).ConfigureAwait(false);
                    return 0;
                }

                Console.WriteLine("未知模式: " + mode);
                PrintUsage();
                return 1;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("已取消。");
                return 130;
            }
            catch (Exception ex)
            {
                Console.WriteLine("错误: " + ex.Message);
                return 1;
            }
        }

        private static void ParseCpuArgs(string[] args, out int duration, out int threads, out int percent)
        {
            duration = 60;
            threads = Environment.ProcessorCount;
            percent = 100;
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if ((a == "-d" || a == "--duration") && i + 1 < args.Length && int.TryParse(args[++i], out int d)) duration = d;
                else if ((a == "-t" || a == "--threads") && i + 1 < args.Length && int.TryParse(args[++i], out int t)) threads = t;
                else if ((a == "-p" || a == "--cpu-percent") && i + 1 < args.Length && int.TryParse(args[++i], out int p)) percent = Math.Max(1, Math.Min(100, p));
            }
        }

        private static void ParseMemoryArgs(string[] args, out int duration, out int mb, out int threads, out int blockKb, out int pattern)
        {
            duration = 60;
            mb = 0;
            threads = 1;
            blockKb = 64;
            pattern = MemoryStressRunner.PatternAlternating;
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if ((a == "-d" || a == "--duration") && i + 1 < args.Length && int.TryParse(args[++i], out int d)) duration = d;
                else if ((a == "-m" || a == "--mb") && i + 1 < args.Length && int.TryParse(args[++i], out int m)) mb = m;
                else if ((a == "-t" || a == "--threads") && i + 1 < args.Length && int.TryParse(args[++i], out int t)) threads = t;
                else if ((a == "-b" || a == "--block-kb") && i + 1 < args.Length && int.TryParse(args[++i], out int b)) blockKb = b;
                else if ((a == "--pattern") && i + 1 < args.Length && int.TryParse(args[++i], out int p)) pattern = p;
            }
        }

        private static void ParseDiskArgs(string[] args, out string path, out int duration, out int fileSizeMb, out int blockKb, out int threads, out bool seqR, out bool seqW, out bool rndR, out bool rndW, out bool mixed)
        {
            path = ".";
            duration = 10;
            fileSizeMb = 256;
            blockKb = 1024;
            threads = 1;
            seqR = seqW = rndR = rndW = true;
            mixed = false;
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if ((a == "-p" || a == "--path") && i + 1 < args.Length) path = args[++i];
                else if ((a == "-d" || a == "--duration") && i + 1 < args.Length && int.TryParse(args[++i], out int d)) duration = d;
                else if ((a == "-s" || a == "--size-mb") && i + 1 < args.Length && int.TryParse(args[++i], out int s)) fileSizeMb = s;
                else if ((a == "-b" || a == "--block-kb") && i + 1 < args.Length && int.TryParse(args[++i], out int b)) blockKb = b;
                else if ((a == "-t" || a == "--threads") && i + 1 < args.Length && int.TryParse(args[++i], out int t)) threads = t;
                else if (a == "--seq-only") { seqR = seqW = true; rndR = rndW = false; }
                else if (a == "--rnd-only") { seqR = seqW = false; rndR = rndW = true; }
                else if (a == "--mixed" || a == "--rw-parallel") { mixed = true; }
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"
StressUtil - 跨平台压力测试 (Linux / Windows / macOS 10.12+)
用法:
  StressUtil cpu [选项]           CPU 压力测试
  StressUtil memory [选项]        内存压力测试（写入/验证带宽、错误计数）
  StressUtil disk [选项]          硬盘压力测试（顺序/随机 读写的 MB/s、IOPS）

CPU 选项:
  -d, --duration <秒>             测试时长 (默认 60)
  -t, --threads <数量>            线程数 (默认 逻辑核心数)
  -p, --cpu-percent <1-100>       目标 CPU 使用率 (默认 100)

Memory 选项:
  -d, --duration <秒>             测试时长 (默认 60，0=直到取消)
  -m, --mb <MB>                   使用内存 MB (默认 可用内存的 25%，上限 2048)
  -t, --threads <数量>            线程数 (默认 1)
  -b, --block-kb <KB>             块大小 (默认 64)
  --pattern <0-4>                 0=AllZero 1=AllOne 2=Alternating 3=WalkingOne 4=Random

Disk 选项:
  -p, --path <路径>               测试目录或路径 (默认当前目录)
  -d, --duration <秒>             每阶段时长 (默认 10)
  -s, --size-mb <MB>              测试文件大小 (默认 256)
  -b, --block-kb <KB>             块大小 (默认 1024)
  -t, --threads <数量>            并发线程 (默认 1)
  --seq-only                      仅顺序读/写
  --rnd-only                      仅随机读/写
  --mixed, --rw-parallel          读写同时进行(混合负载：读写线程并发，分别统计读/写)

示例:
  StressUtil cpu -d 60 -p 100
  StressUtil memory -d 120 -m 512 -t 2
  StressUtil disk -p /tmp -d 15 -s 512
");
        }
    }
}
