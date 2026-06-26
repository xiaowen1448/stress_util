using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace CpuStressWinGui
{
    /// <summary>
    /// 物理磁盘枚举 / 状态型号采集 / 未分区(RAW)盘的自动初始化与测后恢复。
    /// 底层调用 Windows Server 2012+ 自带的 PowerShell Storage 模块
    /// (Get-Disk / Get-PhysicalDisk / Initialize-Disk / New-Partition / Format-Volume / Clear-Disk)，
    /// 不引入额外依赖。涉及初始化/恢复的操作需要管理员权限。
    /// </summary>
    public static class DiskManager
    {
        public sealed class DiskInfo
        {
            public int Number { get; set; }
            public string Model { get; set; } = "";
            public string Serial { get; set; } = "";
            public long SizeBytes { get; set; }
            public string PartitionStyle { get; set; } = "";   // RAW / MBR / GPT
            public string BusType { get; set; } = "";          // SATA / NVMe / USB / RAID ...
            public string Health { get; set; } = "";           // Healthy / Warning / Unhealthy
            public string OperationalStatus { get; set; } = "";
            public bool IsBoot { get; set; }
            public bool IsSystem { get; set; }
            public bool IsOffline { get; set; }
            public bool IsReadOnly { get; set; }
            public string MediaType { get; set; } = "";        // SSD / HDD / Unspecified
            public string SpindleSpeed { get; set; } = "";
            public int PartitionCount { get; set; }
            public string DriveLetters { get; set; } = "";     // 如 "CD"

            /// <summary>无任何分区（RAW 或已初始化但空），可作为自动分区候选。</summary>
            public bool IsRawCandidate =>
                !IsSystem && !IsBoot &&
                (string.Equals(PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase) || PartitionCount == 0);

            public string SizeText => SizeBytes >= (1L << 30)
                ? string.Format("{0:F1} GB", SizeBytes / (1024.0 * 1024 * 1024))
                : string.Format("{0:F0} MB", SizeBytes / (1024.0 * 1024));

            public string FirstDriveLetter =>
                string.IsNullOrEmpty(DriveLetters) ? "" : DriveLetters.Substring(0, 1);

            /// <summary>列表显示文本。</summary>
            public string Display
            {
                get
                {
                    var sb = new StringBuilder();
                    sb.Append("磁盘 ").Append(Number).Append(" | ");
                    sb.Append(string.IsNullOrEmpty(Model) ? "未知型号" : Model).Append(" | ");
                    sb.Append(SizeText);
                    if (!string.IsNullOrEmpty(MediaType) && !MediaType.Equals("Unspecified", StringComparison.OrdinalIgnoreCase))
                        sb.Append(" | ").Append(MediaType);
                    if (!string.IsNullOrEmpty(BusType)) sb.Append(" | ").Append(BusType);
                    if (!string.IsNullOrEmpty(DriveLetters))
                        sb.Append(" | ").Append(InsertColons(DriveLetters));
                    else
                        sb.Append(" | 未分区");
                    if (IsSystem) sb.Append(" | 系统盘");
                    if (IsOffline) sb.Append(" | 脱机");
                    return sb.ToString();
                }
            }

            private static string InsertColons(string letters)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < letters.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(letters[i]).Append(':');
                }
                return sb.ToString();
            }
        }

        /// <summary>当前进程是否以管理员身份运行。</summary>
        public static bool IsAdministrator()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    var p = new WindowsPrincipal(id);
                    return p.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        /// <summary>枚举所有物理磁盘及其状态/型号/分区情况。失败返回空列表。</summary>
        public static List<DiskInfo> ListDisks()
        {
            var result = new List<DiskInfo>();
            // 用 '|' 连接字段，避免 CSV 引号转义；字段内的 '|' 先替换为空格。
            const string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "$pd=@{};Get-PhysicalDisk|ForEach-Object{$pd[[string]$_.DeviceId]=$_};" +
                "Get-Disk|Sort-Object Number|ForEach-Object{" +
                "$p=$pd[[string]$_.Number];" +
                "$media=$(if($p){$p.MediaType}else{''});" +
                "$spin=$(if($p){$p.SpindleSpeed}else{''});" +
                "$pc=(Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue|Measure-Object).Count;" +
                "$lt=((Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue|Where-Object{$_.DriveLetter}|ForEach-Object{[string]$_.DriveLetter}) -join '');" +
                "$vals=@($_.Number,$_.FriendlyName,$_.SerialNumber,$_.Size,$_.PartitionStyle,$_.BusType,$_.HealthStatus,($_.OperationalStatus -join '/'),$_.IsBoot,$_.IsSystem,$_.IsOffline,$_.IsReadOnly,$media,$spin,$pc,$lt);" +
                "($vals|ForEach-Object{([string]$_).Replace('|',' ').Trim()}) -join '|'}";

            string outText, errText;
            int code = RunPowerShell(script, 60000, out outText, out errText);
            if (code != 0 || string.IsNullOrWhiteSpace(outText)) return result;

            foreach (var raw in outText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = raw.Split('|');
                if (f.Length < 16) continue;
                try
                {
                    var di = new DiskInfo
                    {
                        Number = ParseInt(f[0]),
                        Model = f[1],
                        Serial = f[2],
                        SizeBytes = ParseLong(f[3]),
                        PartitionStyle = f[4],
                        BusType = f[5],
                        Health = f[6],
                        OperationalStatus = f[7],
                        IsBoot = ParseBool(f[8]),
                        IsSystem = ParseBool(f[9]),
                        IsOffline = ParseBool(f[10]),
                        IsReadOnly = ParseBool(f[11]),
                        MediaType = f[12],
                        SpindleSpeed = f[13],
                        PartitionCount = ParseInt(f[14]),
                        DriveLetters = f[15]
                    };
                    result.Add(di);
                }
                catch { }
            }
            return result;
        }

        /// <summary>取某物理磁盘上第一个带盘符的分区盘符（无则返回空）。</summary>
        public static string GetFirstDriveLetter(int diskNumber)
        {
            string script = "(Get-Partition -DiskNumber " + diskNumber +
                " -ErrorAction SilentlyContinue|Where-Object{$_.DriveLetter}|Select-Object -First 1 -ExpandProperty DriveLetter)";
            string outText, errText;
            if (RunPowerShell(script, 30000, out outText, out errText) == 0)
            {
                var s = (outText ?? "").Trim();
                if (s.Length >= 1 && char.IsLetter(s[0])) return s.Substring(0, 1);
            }
            return "";
        }

        /// <summary>
        /// 把未分区磁盘自动初始化：联机→GPT 初始化→建最大分区并分配盘符→快速格式化 NTFS。
        /// 成功时 driveLetter 返回分配到的盘符（单字母）。需要管理员权限。
        /// </summary>
        public static bool InitializeRawDisk(int diskNumber, out string driveLetter, out string error)
        {
            driveLetter = "";
            error = "";
            string script =
                "$ErrorActionPreference='Stop';" +
                "Set-Disk -Number " + diskNumber + " -IsOffline $false -ErrorAction SilentlyContinue;" +
                "Set-Disk -Number " + diskNumber + " -IsReadOnly $false -ErrorAction SilentlyContinue;" +
                "$d=Get-Disk -Number " + diskNumber + ";" +
                "if($d.PartitionStyle -eq 'RAW'){Initialize-Disk -Number " + diskNumber + " -PartitionStyle GPT};" +
                "$p=New-Partition -DiskNumber " + diskNumber + " -UseMaximumSize -AssignDriveLetter;" +
                "Start-Sleep -Milliseconds 800;" +
                "Format-Volume -DriveLetter $p.DriveLetter -FileSystem NTFS -NewFileSystemLabel 'STRESSTMP' -Confirm:$false -Force | Out-Null;" +
                "'OK:'+[string]$p.DriveLetter";
            string outText, errText;
            int code = RunPowerShell(script, 180000, out outText, out errText);
            if (code == 0 && outText != null)
            {
                foreach (var line in outText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = line.Trim();
                    if (t.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
                    {
                        var letter = t.Substring(3).Trim();
                        if (letter.Length >= 1 && char.IsLetter(letter[0]))
                        {
                            driveLetter = letter.Substring(0, 1);
                            return true;
                        }
                    }
                }
            }
            error = string.IsNullOrWhiteSpace(errText) ? ("初始化失败(退出码 " + code + ")") : errText.Trim();
            return false;
        }

        /// <summary>
        /// 测后恢复：清除磁盘上所有分区，把盘恢复为「未初始化(RAW)」状态。需要管理员权限。
        /// </summary>
        public static bool RestoreRawDisk(int diskNumber, out string error)
        {
            error = "";
            string script =
                "$ErrorActionPreference='Stop';" +
                "Clear-Disk -Number " + diskNumber + " -RemoveData -RemoveOEM -Confirm:$false";
            string outText, errText;
            int code = RunPowerShell(script, 120000, out outText, out errText);
            if (code == 0) return true;
            error = string.IsNullOrWhiteSpace(errText) ? ("恢复失败(退出码 " + code + ")") : errText.Trim();
            return false;
        }

        // ── PowerShell 调用 ──
        private static int RunPowerShell(string command, int timeoutMs, out string stdout, out string stderr)
        {
            stdout = "";
            stderr = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) { stderr = "无法启动 powershell.exe"; return -1; }
                    var so = p.StandardOutput.ReadToEnd();
                    var se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        stderr = "PowerShell 执行超时";
                        return -2;
                    }
                    stdout = so;
                    stderr = se;
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return -3;
            }
        }

        private static int ParseInt(string s) { int v; return int.TryParse((s ?? "").Trim(), out v) ? v : 0; }
        private static long ParseLong(string s) { long v; return long.TryParse((s ?? "").Trim(), out v) ? v : 0; }
        private static bool ParseBool(string s) { bool v; return bool.TryParse((s ?? "").Trim(), out v) && v; }
    }
}
