using System;
using System.Collections.Generic;
using System.Text;
using CpuStressCore;

namespace CpuStressWinGui
{
    /// <summary>
    /// 生成磁盘测试的详细文本报告：被测硬件的型号/状态 + 每块盘的详细测试结果。
    /// </summary>
    public static class DiskReport
    {
        public sealed class Entry
        {
            public DiskManager.DiskInfo Disk { get; set; }
            public DiskStressResult Result { get; set; }
            public bool AutoInitialized { get; set; }
            public bool RestoredRaw { get; set; }
            public string TestPath { get; set; }
            public string Error { get; set; }
        }

        public sealed class Config
        {
            public int ThreadsPerDisk { get; set; }
            public int BlockSizeKb { get; set; }
            public int FileSizeMb { get; set; }
            public int PhaseSec { get; set; }
            public bool ReadWriteParallel { get; set; }
            public bool MultiDiskParallel { get; set; }
        }

        public static string Build(DateTime start, DateTime end, string cpuName, int cores, double totalRamGb,
            Config cfg, IList<Entry> entries)
        {
            var sb = new StringBuilder();
            string sep = new string('=', 60);
            string sub = new string('-', 60);

            sb.AppendLine(sep);
            sb.AppendLine("                  磁盘压力测试报告");
            sb.AppendLine(sep);
            sb.AppendLine("生成时间:   " + end.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("开始时间:   " + start.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("总耗时:     " + (end - start).TotalSeconds.ToString("F1") + " 秒");
            sb.AppendLine("主机名:     " + SafeMachineName());
            sb.AppendLine("操作系统:   " + GetOsText());
            sb.AppendLine("CPU:        " + (string.IsNullOrEmpty(cpuName) ? "未知" : cpuName) + "  (" + cores + " 逻辑核心)");
            sb.AppendLine("内存:       " + totalRamGb.ToString("F1") + " GB");
            sb.AppendLine("管理员权限: " + (DiskManager.IsAdministrator() ? "是" : "否"));
            sb.AppendLine();

            sb.AppendLine("测试配置:");
            sb.AppendLine("  每盘并发线程: " + cfg.ThreadsPerDisk);
            sb.AppendLine("  块大小:       " + cfg.BlockSizeKb + " KB");
            sb.AppendLine("  测试文件:     " + cfg.FileSizeMb + " MB");
            sb.AppendLine("  每阶段时长:   " + cfg.PhaseSec + " 秒");
            sb.AppendLine("  读写模式:     " + (cfg.ReadWriteParallel ? "读写同时进行(混合负载)" : "顺序四阶段(顺写/顺读/随写/随读)"));
            sb.AppendLine("  多盘模式:     " + (cfg.MultiDiskParallel ? "多块盘同时并行测试" : "单盘"));
            sb.AppendLine("  被测磁盘数:   " + entries.Count);
            sb.AppendLine();

            int idx = 0;
            foreach (var e in entries)
            {
                idx++;
                var d = e.Disk;
                sb.AppendLine(sub);
                sb.AppendLine("磁盘 " + d.Number + "  [" + idx + "/" + entries.Count + "]");
                sb.AppendLine(sub);
                sb.AppendLine("  型号:       " + Dash(d.Model));
                sb.AppendLine("  序列号:     " + Dash(d.Serial));
                sb.AppendLine("  容量:       " + d.SizeText);
                sb.AppendLine("  介质类型:   " + Dash(d.MediaType) + (IsHdd(d) && !string.IsNullOrEmpty(d.SpindleSpeed) && d.SpindleSpeed != "0" ? "  (" + d.SpindleSpeed + " RPM)" : ""));
                sb.AppendLine("  接口总线:   " + Dash(d.BusType));
                sb.AppendLine("  分区形式:   " + Dash(d.PartitionStyle));
                sb.AppendLine("  盘符:       " + (string.IsNullOrEmpty(d.DriveLetters) ? "无(未分区)" : LettersText(d.DriveLetters)));
                sb.AppendLine("  系统盘:     " + YesNo(d.IsSystem) + "    启动盘: " + YesNo(d.IsBoot));
                sb.AppendLine("  健康状态:   " + Dash(d.Health) + "    运行状态: " + Dash(d.OperationalStatus));
                sb.AppendLine("  只读/脱机:  " + YesNo(d.IsReadOnly) + " / " + YesNo(d.IsOffline));
                sb.AppendLine("  测试路径:   " + Dash(e.TestPath));
                if (e.AutoInitialized)
                    sb.AppendLine("  自动初始化: 是（测试前自动分区格式化" + (e.RestoredRaw ? "，测试后已恢复为未初始化(RAW)" : "") + "）");
                else
                    sb.AppendLine("  自动初始化: 否（使用已有分区）");

                if (!string.IsNullOrEmpty(e.Error))
                {
                    sb.AppendLine("  结果:       未完成 —— " + e.Error);
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine("  测试结果:");
                var r = e.Result;
                if (r != null)
                {
                    if (cfg.ReadWriteParallel)
                    {
                        AppendPhase(sb, "    混合读(并发)", r.MixedReadResult);
                        AppendPhase(sb, "    混合写(并发)", r.MixedWriteResult);
                    }
                    else
                    {
                        AppendPhase(sb, "    顺序读取", r.SequentialReadResult);
                        AppendPhase(sb, "    顺序写入", r.SequentialWriteResult);
                        AppendPhase(sb, "    随机读取", r.RandomReadResult);
                        AppendPhase(sb, "    随机写入", r.RandomWriteResult);
                    }
                    string assess = Assess(d, r, cfg);
                    if (!string.IsNullOrEmpty(assess))
                        sb.AppendLine("  评估:       " + assess);
                }
                else
                {
                    sb.AppendLine("    (无结果)");
                }
                sb.AppendLine();
            }

            sb.AppendLine(sep);
            sb.AppendLine("                  报告结束");
            sb.AppendLine(sep);
            return sb.ToString();
        }

        private static void AppendPhase(StringBuilder sb, string name, DiskPhaseResult p)
        {
            if (p == null) { sb.AppendLine(name + ": 未测"); return; }
            sb.AppendLine(name + ": " + p.MbPerSec.ToString("F2") + " MB/s, " + p.Iops.ToString("F0") + " IOPS");
        }

        // 轻量评估：仅按介质给出顺序读吞吐的常识性区间提示，不做硬性结论。
        private static string Assess(DiskManager.DiskInfo d, DiskStressResult r, Config cfg)
        {
            double seqRead = cfg.ReadWriteParallel
                ? (r.MixedReadResult != null ? r.MixedReadResult.MbPerSec : 0)
                : (r.SequentialReadResult != null ? r.SequentialReadResult.MbPerSec : 0);
            if (seqRead <= 0) return "";
            string media = (d.MediaType ?? "").ToUpperInvariant();
            string bus = (d.BusType ?? "").ToUpperInvariant();
            if (bus.Contains("NVME"))
                return seqRead >= 1500 ? "顺序读吞吐符合 NVMe SSD 预期" : "顺序读偏低于常见 NVMe SSD，留意队列深度/盘体状态";
            if (media.Contains("SSD"))
                return seqRead >= 300 ? "顺序读吞吐符合 SATA SSD 预期" : "顺序读偏低于常见 SATA SSD";
            if (media.Contains("HDD") || bus.Contains("USB"))
                return seqRead >= 80 ? "顺序读吞吐符合机械盘/移动盘预期" : "顺序读偏低，可能受接口或盘体限制";
            return "";
        }

        private static bool IsHdd(DiskManager.DiskInfo d) => (d.MediaType ?? "").ToUpperInvariant().Contains("HDD");
        private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();
        private static string YesNo(bool b) => b ? "是" : "否";

        private static string LettersText(string letters)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < letters.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(letters[i]).Append(':');
            }
            return sb.ToString();
        }

        private static string SafeMachineName()
        {
            try { return Environment.MachineName; } catch { return "-"; }
        }

        private static string GetOsText()
        {
            try
            {
                var os = WindowsSysInfo.GetOsInfo();
                return (os.Caption ?? "Windows").Trim() + " " + (os.Version ?? "").Trim() + " " + (os.OsArchitecture ?? "").Trim();
            }
            catch { return Environment.OSVersion.ToString(); }
        }
    }
}
