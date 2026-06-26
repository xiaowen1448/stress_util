using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CpuStressCore
{
    /// <summary>
    /// Disk stress/benchmark options (similar to CrystalDiskMark / fio).
    /// </summary>
    public sealed class DiskStressOptions
    {
        /// <summary>Target path (file or directory). If directory, a temp file is created inside.</summary>
        public string Path { get; set; } = ".";
        /// <summary>Test duration per phase in seconds.</summary>
        public int DurationSec { get; set; } = 10;
        /// <summary>File size for test file in MB.</summary>
        public int FileSizeMb { get; set; } = 256;
        /// <summary>Block size in KB (e.g. 64, 1024).</summary>
        public int BlockSizeKb { get; set; } = 1024;
        /// <summary>Number of concurrent I/O threads.</summary>
        public int Threads { get; set; } = 1;
        /// <summary>Run sequential read test.</summary>
        public bool SequentialRead { get; set; } = true;
        /// <summary>Run sequential write test.</summary>
        public bool SequentialWrite { get; set; } = true;
        /// <summary>Run random read test.</summary>
        public bool RandomRead { get; set; } = true;
        /// <summary>Run random write test.</summary>
        public bool RandomWrite { get; set; } = true;
        /// <summary>读写同时进行（混合负载）：开启后不再分四个顺序阶段，而是读线程与写线程并发跑同一时长。</summary>
        public bool ReadWriteParallel { get; set; } = false;
    }

    /// <summary>
    /// Single phase result (MB/s and IOPS).
    /// </summary>
    public sealed class DiskPhaseResult
    {
        public string Phase { get; set; }
        public double MbPerSec { get; set; }
        public double Iops { get; set; }
        public double ElapsedSeconds { get; set; }
        public long BytesTransferred { get; set; }
    }

    /// <summary>
    /// Full disk stress result (like CrystalDiskMark summary).
    /// </summary>
    public sealed class DiskStressResult
    {
        public string TestPath { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double ElapsedSeconds { get; set; }
        public DiskPhaseResult SequentialReadResult { get; set; }
        public DiskPhaseResult SequentialWriteResult { get; set; }
        public DiskPhaseResult RandomReadResult { get; set; }
        public DiskPhaseResult RandomWriteResult { get; set; }
        /// <summary>读写同时（混合负载）阶段的读结果（仅 ReadWriteParallel=true 时有值）。</summary>
        public DiskPhaseResult MixedReadResult { get; set; }
        /// <summary>读写同时（混合负载）阶段的写结果（仅 ReadWriteParallel=true 时有值）。</summary>
        public DiskPhaseResult MixedWriteResult { get; set; }
    }

    /// <summary>Windows 无缓冲 I/O，避免读缓存导致测速虚高（顺序/随机读取从磁盘实测）。</summary>
    internal static class UnbufferedDiskIo
    {
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const int SECTOR_SIZE = 512;
        private const int INVALID_HANDLE = -1;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(IntPtr hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>对齐到扇区（512 字节），用于无缓冲 I/O。</summary>
        public static long AlignToSector(long value) => (value / SECTOR_SIZE) * SECTOR_SIZE;

        public static IntPtr OpenForRead(string path)
        {
            var h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
            return h != (IntPtr)INVALID_HANDLE ? h : IntPtr.Zero;
        }

        public static IntPtr OpenForWrite(string path)
        {
            var h = CreateFile(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
            return h != (IntPtr)INVALID_HANDLE ? h : IntPtr.Zero;
        }

        public static bool Seek(IntPtr handle, long offset)
        {
            return SetFilePointerEx(handle, offset, out _, 0); // FILE_BEGIN = 0
        }

        public static int Read(IntPtr handle, IntPtr buffer, int count)
        {
            if (count <= 0) return 0;
            return ReadFile(handle, buffer, (uint)count, out uint read, IntPtr.Zero) ? (int)read : 0;
        }

        public static int Write(IntPtr handle, IntPtr buffer, int count)
        {
            if (count <= 0) return 0;
            return WriteFile(handle, buffer, (uint)count, out uint written, IntPtr.Zero) ? (int)written : 0;
        }

        public static void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }
    }

    public static class DiskStressRunner
    {
        /// <summary>检测路径是否在可移动盘（如 U 盘）上，此类盘不宜使用无缓冲 I/O 以免卡住。</summary>
        private static bool IsRemovableDrive(string path)
        {
            try
            {
                string root = System.IO.Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || root.Length < 2) return false;
                var drive = new DriveInfo(root.TrimEnd('\\', '/'));
                return drive.DriveType == DriveType.Removable;
            }
            catch { return false; }
        }

        public static async Task<DiskStressResult> RunDiskStressAsync(
            DiskStressOptions options,
            IProgress<string> log,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string basePath = string.IsNullOrEmpty(options.Path) ? "." : options.Path;
            bool isDir = Directory.Exists(basePath) || (!File.Exists(basePath) && !basePath.Contains("."));
            string testDir = isDir ? basePath : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(basePath));
            if (string.IsNullOrEmpty(testDir)) testDir = ".";
            string testFile = System.IO.Path.Combine(testDir, "stress_util_disk_test_" + Guid.NewGuid().ToString("N") + ".tmp");

            var result = new DiskStressResult { TestPath = testFile, StartTime = DateTime.Now };
            int blockSize = options.BlockSizeKb * 1024;
            long fileSize = (long)options.FileSizeMb * 1024 * 1024;

            log?.Report("");
            log?.Report("========== 硬盘压力测试 ==========");
            log?.Report($"测试路径:       {testFile}");
            log?.Report($"文件大小:       {options.FileSizeMb} MB");
            log?.Report($"块大小:         {options.BlockSizeKb} KB");
            int phaseCount = (options.SequentialWrite ? 1 : 0) + (options.SequentialRead ? 1 : 0) + (options.RandomWrite ? 1 : 0) + (options.RandomRead ? 1 : 0);
            if (phaseCount <= 0) phaseCount = 1;
            log?.Report($"每阶段时长:     {options.DurationSec} 秒");
            log?.Report($"总时长约:       {options.DurationSec * phaseCount} 秒");

            try
            {
                log?.Report("准备测试文件...");
                await PrepareTestFileAsync(testFile, fileSize, blockSize, log, cancellationToken).ConfigureAwait(false);

                if (options.ReadWriteParallel)
                {
                    var mixed = await RunMixedAsync(testFile, fileSize, blockSize, options.Threads, options.DurationSec, log, cancellationToken).ConfigureAwait(false);
                    result.MixedReadResult = mixed.read;
                    result.MixedWriteResult = mixed.write;
                }
                else
                {
                if (options.SequentialWrite)
                {
                    var r = await RunSequentialWriteAsync(testFile, fileSize, blockSize, options.Threads, options.DurationSec, log, cancellationToken).ConfigureAwait(false);
                    result.SequentialWriteResult = r;
                }
                if (options.SequentialRead)
                {
                    var r = await RunSequentialReadAsync(testFile, fileSize, blockSize, options.Threads, options.DurationSec, log, cancellationToken).ConfigureAwait(false);
                    result.SequentialReadResult = r;
                }
                if (options.RandomWrite)
                {
                    var r = await RunRandomWriteAsync(testFile, fileSize, blockSize, options.Threads, options.DurationSec, log, cancellationToken).ConfigureAwait(false);
                    result.RandomWriteResult = r;
                }
                if (options.RandomRead)
                {
                    var r = await RunRandomReadAsync(testFile, fileSize, blockSize, options.Threads, options.DurationSec, log, cancellationToken).ConfigureAwait(false);
                    result.RandomReadResult = r;
                }
                }
            }
            finally
            {
                try { if (File.Exists(testFile)) File.Delete(testFile); } catch { }
            }

            result.EndTime = DateTime.Now;
            result.ElapsedSeconds = (result.EndTime - result.StartTime).TotalSeconds;

            log?.Report("");
            log?.Report("========== 硬盘测试结果 ==========");
            log?.Report($"结束时间:       {result.EndTime:yyyy-MM-dd HH:mm:ss}");
            LogPhase(log, "顺序写入", result.SequentialWriteResult);
            LogPhase(log, "顺序读取", result.SequentialReadResult);
            LogPhase(log, "随机写入", result.RandomWriteResult);
            LogPhase(log, "随机读取", result.RandomReadResult);
            LogPhase(log, "混合读(并发)", result.MixedReadResult);
            LogPhase(log, "混合写(并发)", result.MixedWriteResult);
            return result;
        }

        private static void LogPhase(IProgress<string> log, string name, DiskPhaseResult r)
        {
            if (r == null) return;
            log?.Report($"  {name}:        {r.MbPerSec:F2} MB/s, {r.Iops:F0} IOPS");
        }

        private static async Task PrepareTestFileAsync(string path, long size, int blockSize, IProgress<string> log, CancellationToken token)
        {
            var buffer = new byte[blockSize];
            new Random(0).NextBytes(buffer);
            long written = 0;
            int streamBufferSize = Math.Min(blockSize, 1024 * 1024);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, streamBufferSize, FileOptions.Asynchronous))
            {
                while (written < size && !token.IsCancellationRequested)
                {
                    int toWrite = (int)Math.Min(blockSize, size - written);
                    await fs.WriteAsync(buffer, 0, toWrite, token).ConfigureAwait(false);
                    written += toWrite;
                }
                await fs.FlushAsync(token).ConfigureAwait(false);
            }
            log?.Report($"  已创建 {written / (1024 * 1024)} MB");
        }

        private static async Task<DiskPhaseResult> RunSequentialWriteAsync(string path, long fileSize, int blockSize, int threads, int durationSec, IProgress<string> log, CancellationToken token)
        {
            log?.Report("阶段: 顺序写入...");
            return await RunSequentialIoAsync(path, fileSize, blockSize, threads, durationSec, write: true, log, token).ConfigureAwait(false);
        }

        private static async Task<DiskPhaseResult> RunSequentialReadAsync(string path, long fileSize, int blockSize, int threads, int durationSec, IProgress<string> log, CancellationToken token)
        {
            log?.Report("阶段: 顺序读取...");
            return await RunSequentialIoAsync(path, fileSize, blockSize, threads, durationSec, write: false, log, token).ConfigureAwait(false);
        }

        private static async Task<DiskPhaseResult> RunSequentialIoAsync(string path, long fileSize, int blockSize, int threads, int durationSec, bool write, IProgress<string> log, CancellationToken token)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(durationSec));
                var sw = Stopwatch.StartNew();
                long totalBytes = 0;
                long perThread = fileSize / Math.Max(1, threads);
                bool useUnbuffered = UnbufferedDiskIo.IsSupported && (blockSize % 512 == 0) && !IsRemovableDrive(path);

                var tasks = new Task<long>[threads];
                if (useUnbuffered)
                {
                    for (int i = 0; i < threads; i++)
                    {
                        long start = i * perThread;
                        long len = (i == threads - 1) ? (fileSize - start) : perThread;
                        long startAligned = UnbufferedDiskIo.AlignToSector(start);
                        long endAligned = UnbufferedDiskIo.AlignToSector(start + len);
                        long lenAligned = Math.Max(0, endAligned - startAligned);
                        tasks[i] = Task.Run(() => SequentialIoWorkerUnbuffered(path, startAligned, lenAligned, blockSize, write, cts.Token), cts.Token);
                    }
                }
                else
                {
                    var buffer = new byte[blockSize];
                    if (write) new Random(1).NextBytes(buffer);
                    for (int i = 0; i < threads; i++)
                    {
                        long start = i * perThread;
                        long len = (i == threads - 1) ? (fileSize - start) : perThread;
                        tasks[i] = SequentialIoWorker(path, start, len, blockSize, write, buffer, cts.Token);
                    }
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
                sw.Stop();
                foreach (var t in tasks) totalBytes += t.Result;
                double sec = sw.Elapsed.TotalSeconds;
                double mbps = sec > 0 ? totalBytes / sec / (1024 * 1024) : 0;
                double iops = sec > 0 ? (totalBytes / blockSize) / sec : 0;
                return new DiskPhaseResult
                {
                    Phase = write ? "SequentialWrite" : "SequentialRead",
                    BytesTransferred = totalBytes,
                    ElapsedSeconds = sec,
                    MbPerSec = mbps,
                    Iops = iops
                };
            }
        }

        private static long SequentialIoWorkerUnbuffered(string path, long start, long length, int blockSize, bool write, CancellationToken token)
        {
            if (length <= 0) return 0;
            IntPtr handle = write ? UnbufferedDiskIo.OpenForWrite(path) : UnbufferedDiskIo.OpenForRead(path);
            if (handle == IntPtr.Zero) return 0;
            try
            {
                int alignedSize = (blockSize / 512) * 512;
                if (alignedSize <= 0) return 0;
                IntPtr buf = Marshal.AllocHGlobal(alignedSize + 512);
                try
                {
                    long addr = (buf.ToInt64() + 511) & ~511L;
                    IntPtr alignedBuf = (IntPtr)addr;
                    if (write)
                    {
                        var r = new Random(1);
                        var temp = new byte[alignedSize];
                        r.NextBytes(temp);
                        Marshal.Copy(temp, 0, alignedBuf, alignedSize);
                    }
                    if (!UnbufferedDiskIo.Seek(handle, start))
                        return 0;
                    long total = 0;
                    long pos = 0;
                    while (pos < length && !token.IsCancellationRequested)
                    {
                        int toDo = (int)Math.Min(alignedSize, length - pos);
                        if (toDo <= 0) break;
                        int n = write ? UnbufferedDiskIo.Write(handle, alignedBuf, toDo) : UnbufferedDiskIo.Read(handle, alignedBuf, toDo);
                        if (n <= 0) break;
                        total += n;
                        pos += n;
                    }
                    return total;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            finally
            {
                UnbufferedDiskIo.Close(handle);
            }
        }

        private static async Task<long> SequentialIoWorker(string path, long start, long length, int blockSize, bool write, byte[] buffer, CancellationToken token)
        {
            long total = 0;
            var mode = write ? FileMode.Open : FileMode.Open;
            var access = write ? FileAccess.Write : FileAccess.Read;
            var share = FileShare.ReadWrite;
            int streamBufferSize = Math.Min(blockSize, 1024 * 1024);
            using (var fs = new FileStream(path, mode, access, share, streamBufferSize, FileOptions.Asynchronous))
            {
                fs.Seek(start, SeekOrigin.Begin);
                long pos = 0;
                while (pos < length && !token.IsCancellationRequested)
                {
                    int toDo = (int)Math.Min(blockSize, length - pos);
                    if (write)
                        await fs.WriteAsync(buffer, 0, toDo, token).ConfigureAwait(false);
                    else
                        await fs.ReadAsync(buffer, 0, toDo, token).ConfigureAwait(false);
                    total += toDo;
                    pos += toDo;
                }
                if (write) await fs.FlushAsync(token).ConfigureAwait(false);
            }
            return total;
        }

        /// <summary>读写同时（混合负载）：读线程与写线程并发跑同一时长，分别统计读/写吞吐与 IOPS。</summary>
        private static async Task<(DiskPhaseResult read, DiskPhaseResult write)> RunMixedAsync(string path, long fileSize, int blockSize, int threads, int durationSec, IProgress<string> log, CancellationToken token)
        {
            log?.Report("阶段: 读写同时进行(混合负载)...");
            var empty = new DiskPhaseResult { Phase = "Mixed", MbPerSec = 0, Iops = 0 };
            long maxOffset = Math.Max(0, fileSize - blockSize);
            long maxOffsetAligned = UnbufferedDiskIo.IsSupported ? UnbufferedDiskIo.AlignToSector(maxOffset) : maxOffset;
            if (maxOffsetAligned <= 0) return (empty, empty);

            if (threads < 2) threads = 2; // 至少一读一写
            int readThreads = Math.Max(1, threads / 2);
            int writeThreads = Math.Max(1, threads - readThreads);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(durationSec));
                var sw = Stopwatch.StartNew();
                bool useUnbuffered = UnbufferedDiskIo.IsSupported && (blockSize % 512 == 0) && !IsRemovableDrive(path);

                var rTasks = new Task<long>[readThreads];
                var wTasks = new Task<long>[writeThreads];
                for (int i = 0; i < readThreads; i++)
                {
                    rTasks[i] = useUnbuffered
                        ? Task.Run(() => RandomIoWorkerUnbuffered(path, maxOffsetAligned, blockSize, false, cts.Token), cts.Token)
                        : RandomIoWorker(path, maxOffset, blockSize, false, cts.Token);
                }
                for (int i = 0; i < writeThreads; i++)
                {
                    wTasks[i] = useUnbuffered
                        ? Task.Run(() => RandomIoWorkerUnbuffered(path, maxOffsetAligned, blockSize, true, cts.Token), cts.Token)
                        : RandomIoWorker(path, maxOffset, blockSize, true, cts.Token);
                }
                await Task.WhenAll(rTasks).ConfigureAwait(false);
                await Task.WhenAll(wTasks).ConfigureAwait(false);
                sw.Stop();

                long readBytes = 0; foreach (var t in rTasks) readBytes += t.Result;
                long writeBytes = 0; foreach (var t in wTasks) writeBytes += t.Result;
                double sec = sw.Elapsed.TotalSeconds;
                var read = new DiskPhaseResult
                {
                    Phase = "MixedRead",
                    BytesTransferred = readBytes,
                    ElapsedSeconds = sec,
                    MbPerSec = sec > 0 ? readBytes / sec / (1024 * 1024) : 0,
                    Iops = sec > 0 ? (readBytes / blockSize) / sec : 0
                };
                var write = new DiskPhaseResult
                {
                    Phase = "MixedWrite",
                    BytesTransferred = writeBytes,
                    ElapsedSeconds = sec,
                    MbPerSec = sec > 0 ? writeBytes / sec / (1024 * 1024) : 0,
                    Iops = sec > 0 ? (writeBytes / blockSize) / sec : 0
                };
                return (read, write);
            }
        }

        private static async Task<DiskPhaseResult> RunRandomWriteAsync(string path, long fileSize, int blockSize, int threads, int durationSec, IProgress<string> log, CancellationToken token)
        {
            log?.Report("阶段: 随机写入...");
            return await RunRandomIoAsync(path, fileSize, blockSize, threads, durationSec, write: true, log, token).ConfigureAwait(false);
        }

        private static async Task<DiskPhaseResult> RunRandomReadAsync(string path, long fileSize, int blockSize, int threads, int durationSec, IProgress<string> log, CancellationToken token)
        {
            log?.Report("阶段: 随机读取...");
            return await RunRandomIoAsync(path, fileSize, blockSize, threads, durationSec, write: false, log, token).ConfigureAwait(false);
        }

        private static async Task<DiskPhaseResult> RunRandomIoAsync(string path, long fileSize, int blockSize, int threads, int durationSec, bool write, IProgress<string> log, CancellationToken token)
        {
            long maxOffset = Math.Max(0, fileSize - blockSize);
            long maxOffsetAligned = UnbufferedDiskIo.IsSupported ? UnbufferedDiskIo.AlignToSector(maxOffset) : maxOffset;
            if (maxOffsetAligned <= 0) return new DiskPhaseResult { Phase = write ? "RandomWrite" : "RandomRead", MbPerSec = 0, Iops = 0 };

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(durationSec));
                var sw = Stopwatch.StartNew();
                bool useUnbuffered = UnbufferedDiskIo.IsSupported && (blockSize % 512 == 0) && !IsRemovableDrive(path);
                var tasks = new Task<long>[threads];
                for (int i = 0; i < threads; i++)
                {
                    if (useUnbuffered)
                        tasks[i] = Task.Run(() => RandomIoWorkerUnbuffered(path, maxOffsetAligned, blockSize, write, cts.Token), cts.Token);
                    else
                        tasks[i] = RandomIoWorker(path, maxOffset, blockSize, write, cts.Token);
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
                sw.Stop();
                long totalBytes = 0;
                foreach (var t in tasks) totalBytes += t.Result;
                double sec = sw.Elapsed.TotalSeconds;
                double mbps = sec > 0 ? totalBytes / sec / (1024 * 1024) : 0;
                double iops = sec > 0 ? (totalBytes / blockSize) / sec : 0;
                return new DiskPhaseResult
                {
                    Phase = write ? "RandomWrite" : "RandomRead",
                    BytesTransferred = totalBytes,
                    ElapsedSeconds = sec,
                    MbPerSec = mbps,
                    Iops = iops
                };
            }
        }

        private static long RandomIoWorkerUnbuffered(string path, long maxOffsetAligned, int blockSize, bool write, CancellationToken token)
        {
            if (maxOffsetAligned <= 0) return 0;
            IntPtr handle = write ? UnbufferedDiskIo.OpenForWrite(path) : UnbufferedDiskIo.OpenForRead(path);
            if (handle == IntPtr.Zero) return 0;
            try
            {
                int alignedSize = (blockSize / 512) * 512;
                if (alignedSize <= 0) return 0;
                IntPtr buf = Marshal.AllocHGlobal(alignedSize + 512);
                try
                {
                    long addr = (buf.ToInt64() + 511) & ~511L;
                    IntPtr alignedBuf = (IntPtr)addr;
                    if (write)
                    {
                        var temp = new byte[alignedSize];
                        new Random(Environment.TickCount + path.GetHashCode()).NextBytes(temp);
                        Marshal.Copy(temp, 0, alignedBuf, alignedSize);
                    }
                    var rnd = new Random(Environment.TickCount + path.GetHashCode());
                    long total = 0;
                    while (!token.IsCancellationRequested)
                    {
                        long offset = (long)(rnd.NextDouble() * maxOffsetAligned);
                        offset = UnbufferedDiskIo.AlignToSector(offset);
                        if (!UnbufferedDiskIo.Seek(handle, offset))
                            break;
                        int n = write ? UnbufferedDiskIo.Write(handle, alignedBuf, alignedSize) : UnbufferedDiskIo.Read(handle, alignedBuf, alignedSize);
                        if (n <= 0) break;
                        total += n;
                    }
                    return total;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            finally
            {
                UnbufferedDiskIo.Close(handle);
            }
        }

        private static async Task<long> RandomIoWorker(string path, long maxOffset, int blockSize, bool write, CancellationToken token)
        {
            long total = 0;
            var rnd = new Random(Environment.TickCount + path.GetHashCode());
            var buffer = new byte[blockSize];
            if (write) rnd.NextBytes(buffer);
            int streamBufferSize = Math.Min(blockSize, 1024 * 1024);
            using (var fs = new FileStream(path, FileMode.Open, write ? FileAccess.Write : FileAccess.Read, FileShare.ReadWrite, streamBufferSize, FileOptions.Asynchronous))
            {
                while (!token.IsCancellationRequested)
                {
                    long offset = (long)(rnd.NextDouble() * maxOffset);
                    fs.Seek(offset, SeekOrigin.Begin);
                    if (write)
                        await fs.WriteAsync(buffer, 0, blockSize, token).ConfigureAwait(false);
                    else
                        await fs.ReadAsync(buffer, 0, blockSize, token).ConfigureAwait(false);
                    total += blockSize;
                }
            }
            return total;
        }
    }
}
