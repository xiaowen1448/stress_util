using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CpuStressWinGui
{
    /// <summary>
    /// 磁盘多选对话框：勾选要测试的物理磁盘，显示型号/容量/介质/接口/健康/分区状态；
    /// 提供「读写同时进行(混合负载)」开关。未分区盘会标注，测试时自动初始化并在测后恢复。
    /// </summary>
    public sealed class DiskSelectForm : Form
    {
        private readonly ListView _list = new ListView();
        private readonly CheckBox _chkRw = new CheckBox();
        private readonly List<DiskManager.DiskInfo> _disks;

        private static readonly Font UiFont = new Font("微软雅黑", 9f);
        private static readonly Color BgColor = Color.FromArgb(240, 240, 240);
        private static readonly Color TextColor = Color.FromArgb(64, 64, 64);

        public List<int> SelectedDiskNumbers { get; private set; } = new List<int>();
        public bool ReadWriteParallel { get; private set; }

        public DiskSelectForm(List<DiskManager.DiskInfo> disks, IEnumerable<int> selected, bool rwParallel)
        {
            _disks = disks ?? new List<DiskManager.DiskInfo>();
            var preSel = new HashSet<int>(selected ?? new List<int>());

            Text = "选择要测试的磁盘";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = BgColor;
            ForeColor = TextColor;
            Size = new Size(860, 460);
            MinimumSize = new Size(720, 360);
            Font = UiFont;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

            layout.Controls.Add(new Label
            {
                Text = "勾选要压测的磁盘（可多选，多盘将并行测试）。标注“未分区”的新盘需管理员权限，测试时自动初始化、测后恢复为未初始化状态。",
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(90, 90, 90)
            }, 0, 0);

            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.Dock = DockStyle.Fill;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.Columns.Add("磁盘", 60);
            _list.Columns.Add("型号", 220);
            _list.Columns.Add("容量", 90);
            _list.Columns.Add("介质", 70);
            _list.Columns.Add("接口", 70);
            _list.Columns.Add("健康", 80);
            _list.Columns.Add("分区/盘符", 120);
            _list.Columns.Add("状态", 90);
            // 单击行任意处即可切换勾选
            _list.ItemActivate += (s, e) => { };
            _list.MouseClick += (s, e) =>
            {
                var hit = _list.HitTest(e.Location);
                if (hit.Item != null && hit.Location != ListViewHitTestLocations.StateImage)
                    hit.Item.Checked = !hit.Item.Checked;
            };

            foreach (var d in _disks)
            {
                var it = new ListViewItem(d.Number.ToString());
                it.SubItems.Add(string.IsNullOrEmpty(d.Model) ? "未知型号" : d.Model);
                it.SubItems.Add(d.SizeText);
                it.SubItems.Add(string.IsNullOrEmpty(d.MediaType) ? "-" : d.MediaType);
                it.SubItems.Add(string.IsNullOrEmpty(d.BusType) ? "-" : d.BusType);
                it.SubItems.Add(string.IsNullOrEmpty(d.Health) ? "-" : d.Health);
                it.SubItems.Add(string.IsNullOrEmpty(d.DriveLetters) ? "未分区" : LettersText(d.DriveLetters));
                string state = d.IsSystem ? "系统盘" : (d.IsOffline ? "脱机" : (d.IsRawCandidate ? "可初始化" : "就绪"));
                it.SubItems.Add(state);
                it.Tag = d.Number;
                if (preSel.Contains(d.Number)) it.Checked = true;
                if (d.IsSystem) it.ForeColor = Color.FromArgb(150, 80, 0);
                _list.Items.Add(it);
            }

            layout.Controls.Add(_list, 0, 1);

            _chkRw.Text = "读写同时进行（混合负载：读线程与写线程并发，而非顺序四阶段）";
            _chkRw.Checked = rwParallel;
            _chkRw.AutoSize = true;
            _chkRw.Dock = DockStyle.Fill;
            layout.Controls.Add(_chkRw, 0, 2);

            var btnOk = new Button
            {
                Text = "开始测试",
                DialogResult = DialogResult.OK,
                Size = new Size(100, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White
            };
            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Size = new Size(90, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(200, 200, 200),
                ForeColor = TextColor
            };
            var btnPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Padding = new Padding(0, 6, 0, 0)
            };
            btnPanel.Controls.Add(btnOk);
            btnPanel.Controls.Add(btnCancel);
            layout.Controls.Add(btnPanel, 0, 3);

            Controls.Add(layout);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            btnOk.Click += (s, e) =>
            {
                SelectedDiskNumbers = new List<int>();
                foreach (ListViewItem it in _list.Items)
                    if (it.Checked && it.Tag is int n) SelectedDiskNumbers.Add(n);
                ReadWriteParallel = _chkRw.Checked;
                if (SelectedDiskNumbers.Count == 0)
                {
                    MessageBox.Show(this, "请至少勾选一块磁盘。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    DialogResult = DialogResult.None;
                }
            };
        }

        private static string LettersText(string letters)
        {
            var parts = new List<string>();
            foreach (var c in letters) parts.Add(c + ":");
            return string.Join(" ", parts);
        }
    }
}
