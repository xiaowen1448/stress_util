using System;
using System.Drawing;
using System.Windows.Forms;

namespace CpuStressWinGui
{
    /// <summary>
    /// 压力测试设置（时长、峰值百分比），背景与主程序一致
    /// </summary>
    public sealed class PeakSettingsForm : Form
    {
        private readonly NumericUpDown _numDuration = new NumericUpDown();
        private readonly NumericUpDown _numCpu = new NumericUpDown();
        private readonly NumericUpDown _numMem = new NumericUpDown();
        private readonly NumericUpDown _numDisk = new NumericUpDown();

        private static readonly Font LabelFont = new Font("微软雅黑", 9f);
        private static readonly Font DescFont = new Font("微软雅黑", 8f);
        private static readonly Color BgColor = Color.FromArgb(240, 240, 240);
        private static readonly Color TextColor = Color.FromArgb(64, 64, 64);
        private static readonly Color MutedColor = Color.FromArgb(100, 100, 100);

        // 对话框以「分钟」为单位输入；对外仍返回秒，供测试引擎使用。
        public int DurationSec => (int)_numDuration.Value * 60;
        public int CpuPeakPercent => (int)_numCpu.Value;
        public int MemoryPeakPercent => (int)_numMem.Value;
        public int DiskPeakPercent => (int)_numDisk.Value;

        public PeakSettingsForm(int durationSec, int cpuPeak, int memPeak, int diskPeak)
        {
            Text = "压力测试设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = BgColor;
            ForeColor = TextColor;
            Size = new Size(420, 280);
            MinimumSize = new Size(400, 260);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Padding = new Padding(16, 12, 16, 12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++)
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // 时长以分钟为单位（传入为秒，转换为分钟显示）；范围 1 分钟 ~ 7 天。
            ConfigureNum(_numDuration, Math.Max(1, durationSec / 60), 1, 10080);
            ConfigureNum(_numCpu, cpuPeak, 1, 100);
            ConfigureNum(_numMem, memPeak, 1, 100);
            ConfigureNum(_numDisk, diskPeak, 1, 100);
            // 内存峰值步进调大：大内存机上 1% ≈ 1GB，每次 +1% 调整太慢，改为每步 5%（仍可手动输入精确值）。
            _numMem.Increment = 5;

            int row = 0;
            layout.Controls.Add(NewLabel("测试时长(分钟):"), 0, row);
            layout.Controls.Add(_numDuration, 1, row++);
            layout.Controls.Add(NewLabel("CPU 峰值(%):"), 0, row);
            layout.Controls.Add(_numCpu, 1, row++);
            layout.Controls.Add(NewLabel("内存 峰值(%):"), 0, row);
            layout.Controls.Add(_numMem, 1, row++);
            layout.Controls.Add(NewLabel("磁盘 峰值(%):"), 0, row);
            layout.Controls.Add(_numDisk, 1, row++);

            var desc = new Label
            {
                Text = "所有压力测试均使用上述数值。线程数由程序根据 CPU 核心数自动设置。",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Font = DescFont,
                ForeColor = MutedColor,
                Padding = new Padding(0, 8, 0, 0)
            };
            layout.Controls.Add(desc, 0, row);
            layout.SetColumnSpan(desc, 2);
            row++;

            var btnOk = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Font = LabelFont
            };
            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(200, 200, 200),
                ForeColor = TextColor,
                Font = LabelFont
            };
            var btnPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0)
            };
            btnPanel.Controls.Add(btnOk);
            btnPanel.Controls.Add(btnCancel);
            layout.Controls.Add(btnPanel, 0, row);
            layout.SetColumnSpan(btnPanel, 2);

            Controls.Add(layout);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private static Label NewLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Font = LabelFont, ForeColor = TextColor };
        }

        private static void ConfigureNum(NumericUpDown num, int val, int min, int max)
        {
            num.Minimum = min;
            num.Maximum = max;
            num.Value = Math.Max(min, Math.Min(max, val));
            num.Increment = 1;
            num.Width = 120;
            num.Font = LabelFont;
            num.BackColor = Color.White;
            num.ForeColor = TextColor;
        }
    }
}
