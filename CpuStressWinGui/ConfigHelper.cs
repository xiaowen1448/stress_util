using System.IO;
using System.Windows.Forms;

namespace CpuStressWinGui
{
    /// <summary>
    /// 压力测试参数保存/加载到程序目录 config.ini
    /// </summary>
    public static class ConfigHelper
    {
        private static string ConfigPath => Path.Combine(Application.StartupPath, "config.ini");

        public static void Load(out int durationSec, out int cpuPeak, out int memPeak, out int diskPeak)
        {
            durationSec = 3600; // 默认 60 分钟
            cpuPeak = 100;
            memPeak = 100;
            diskPeak = 100;
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var lines = File.ReadAllLines(ConfigPath);
                foreach (var line in lines)
                {
                    var s = line?.Trim();
                    if (string.IsNullOrEmpty(s) || s.StartsWith(";") || s.StartsWith("#")) continue;
                    var idx = s.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = s.Substring(0, idx).Trim();
                    var val = s.Substring(idx + 1).Trim();
                    if (int.TryParse(val, out int v))
                    {
                        if (key.Equals("DurationSec", System.StringComparison.OrdinalIgnoreCase)) durationSec = v;
                        else if (key.Equals("CpuPeakPercent", System.StringComparison.OrdinalIgnoreCase)) cpuPeak = v;
                        else if (key.Equals("MemoryPeakPercent", System.StringComparison.OrdinalIgnoreCase)) memPeak = v;
                        else if (key.Equals("DiskPeakPercent", System.StringComparison.OrdinalIgnoreCase)) diskPeak = v;
                    }
                }
            }
            catch { }
        }

        public static void Save(int durationSec, int cpuPeak, int memPeak, int diskPeak)
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var content = "[Stress]\r\n" +
                    "DurationSec=" + durationSec + "\r\n" +
                    "CpuPeakPercent=" + cpuPeak + "\r\n" +
                    "MemoryPeakPercent=" + memPeak + "\r\n" +
                    "DiskPeakPercent=" + diskPeak + "\r\n";
                File.WriteAllText(ConfigPath, content, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
