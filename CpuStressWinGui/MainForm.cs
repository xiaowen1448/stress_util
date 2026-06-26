using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CpuStressCore;

namespace CpuStressWinGui
{
    public sealed class MainForm : Form
    {
        // 性能监控与图表
        private PerfMonitor _perfMonitor;
        private System.Windows.Forms.Timer _perfTimer;
        private ChartPanel _chartCpu, _chartMemory, _chartDiskRw, _chartDiskBusy;
        private ComboBox _comboPerfDisk;

        // 顶部操作区（仅按钮）
        private MenuStrip _menuStrip;
        private Button _btnStart, _btnStop;
        private CheckBox _chkCpu, _chkMem, _chkDisk;
        private CancellationTokenSource _ctsCpu, _ctsMem, _ctsDisk;

        // 全部来自设置菜单
        private int _durationSec = 3600; // 默认 60 分钟（以秒存储）
        private int _threads = 1;
        private int _cpuPeakPercent = 100;
        private int _memPeakPercent = 100;
        private int _diskPeakPercent = 100;

        // 磁盘测试：多选的磁盘编号 + 读写同时(混合)开关
        private List<int> _testDiskNumbers = new List<int>();
        private bool _rwParallel = false;
        private Button _btnDiskSelect;
        private TextBox _txtLog;

        // 硬件信息（图表副标题与日志表头）
        private string _cpuName = "";
        private string _memInfo = "";
        private string _diskModel = "";

        public MainForm()
        {
            Text = "Stress Util";
            MinimumSize = new Size(760, 640);
            Size = new Size(980, 820);
            StartPosition = FormStartPosition.CenterScreen;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            ConfigHelper.Load(out _durationSec, out _cpuPeakPercent, out _memPeakPercent, out _diskPeakPercent);
            BuildUi();
            PopulateDefaults();
            SetRunning(false, false, false);
            Shown += MainForm_Shown;
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            Shown -= MainForm_Shown;
            Task.Run(() =>
            {
                try
                {
                    var instances = PerfMonitor.GetPhysicalDiskInstances();
                    string first = instances.FirstOrDefault(n => n != "_Total");
                    if (string.IsNullOrEmpty(first)) first = "_Total";
                    var monitor = new PerfMonitor(first);
                    var diskList = PerfMonitor.GetPhysicalDiskDisplayList();
                    string cpuName = StressRunner.GetProcessorName();
                    BeginInvoke(new Action(() =>
                    {
                        _perfMonitor = monitor;
                        if (!_perfMonitor.IsAvailable)
                        {
                            UpdateStatus("性能计数器不可用（请尝试以管理员运行）");
                            return;
                        }
                        _cpuName = string.IsNullOrEmpty(cpuName) ? "CPU" : cpuName;
                        _memInfo = _perfMonitor.TotalRamMb > 0 ? string.Format("总内存 {0:F0} GB", _perfMonitor.TotalRamMb / 1024.0) : "内存";
                        _chartCpu.SubTitle = _cpuName;
                        _chartMemory.SubTitle = _memInfo;
                        _chartMemory.MaxScale = 100f;
                        if (_comboPerfDisk != null)
                        {
                            _comboPerfDisk.Items.Clear();
                            foreach (var item in diskList)
                                _comboPerfDisk.Items.Add(item);
                            var sel = diskList.FirstOrDefault(d => d.Instance == first) ?? diskList.FirstOrDefault();
                            if (sel != null)
                                _comboPerfDisk.SelectedItem = sel;
                            else if (diskList.Count > 0)
                                _comboPerfDisk.SelectedIndex = 0;
                        }
                        UpdateDiskChartSubTitle();
                        _perfTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                        _perfTimer.Tick += PerfTimer_Tick;
                        _perfTimer.Start();
                        UpdateStatus("就绪");
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() => UpdateStatus("性能监控启动失败: " + ex.Message)));
                }
            });
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(8, 4, 8, 8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // menu 紧凑
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // top buttons
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));  // spacer
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // charts
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170)); // 实时日志

            // ===== MenuStrip（扁平、紧凑、与主程序一致） =====
            _menuStrip = new MenuStrip
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(240, 240, 240),
                ForeColor = Color.FromArgb(64, 64, 64),
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new MenuStripRenderer(),
                Font = new Font("Segoe UI", 9f),
                Padding = new Padding(4, 0, 4, 0),
                ImageScalingSize = new Size(0, 0)
            };
            var mSettings = new ToolStripMenuItem("设置(&S)");
            var mPeak = new ToolStripMenuItem("压力测试设置…", null, (s, e) => OpenPeakSettings());
            mSettings.DropDownItems.Add(mPeak);
            _menuStrip.Items.Add(mSettings);

            var mHelp = new ToolStripMenuItem("帮助(&H)");
            var mHelpDoc = new ToolStripMenuItem("帮助文档", null, (s, e) => ShowHelp());
            var mAbout = new ToolStripMenuItem("关于", null, (s, e) => ShowAbout());
            mHelp.DropDownItems.AddRange(new ToolStripItem[] { mHelpDoc, mAbout });
            _menuStrip.Items.Add(mHelp);

            // ===== Top buttons row（去掉时长、线程下拉框） =====
            var topBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 6, 0, 0)
            };

            // 勾选要测试的项目（可多选，一次性同时启动）
            var lblItems = new Label { Text = "测试项:", AutoSize = true, Padding = new Padding(0, 7, 4, 0) };
            _chkCpu = new CheckBox { Text = "CPU", AutoSize = true, Checked = true, Padding = new Padding(0, 5, 6, 0) };
            _chkMem = new CheckBox { Text = "内存", AutoSize = true, Checked = false, Padding = new Padding(0, 5, 6, 0) };
            _chkDisk = new CheckBox { Text = "磁盘", AutoSize = true, Checked = false, Padding = new Padding(0, 5, 10, 0) };

            _btnStart = new Button { Text = "开始测试", AutoSize = true, Height = 28, FlatStyle = FlatStyle.Standard };
            _btnDiskSelect = new Button { Text = "磁盘选择…", AutoSize = true, Height = 28, FlatStyle = FlatStyle.Standard };
            _btnStop = new Button { Text = "停止", AutoSize = true, Height = 28, FlatStyle = FlatStyle.Standard };

            _btnStart.Click += (s, e) => StartSelectedTests();
            _btnDiskSelect.Click += (s, e) => OpenDiskSelect();
            _btnStop.Click += (s, e) => StopRun();

            topBar.Controls.Add(lblItems);
            topBar.Controls.Add(_chkCpu);
            topBar.Controls.Add(_chkMem);
            topBar.Controls.Add(_chkDisk);
            topBar.Controls.Add(_btnStart);
            topBar.Controls.Add(_btnDiskSelect);
            topBar.Controls.Add(_btnStop);

            _chartCpu = new ChartPanel
            {
                ChartTitle = "CPU",
                Unit = "%",
                MaxScale = 100f,
                ScaleFromData = false,
                LineColor = Color.FromArgb(0, 120, 215),
                ShowSeries2 = true,
                Series1Name = "使用率",
                Series2Name = "温度",
                Series2Unit = "℃",
                LineColor2 = Color.FromArgb(230, 126, 34),
                Dock = DockStyle.Fill
            };
            // 内存：改为 0~最大内存(MB)，不再显示 0~100%
            _chartMemory = new ChartPanel { ChartTitle = "内存(已用%)", Unit = "%", MaxScale = 100f, ScaleFromData = false, LineColor = Color.FromArgb(0, 164, 0), Dock = DockStyle.Fill };
            // 磁盘：读写合并到一个图表（蓝=读，红=写），刻度随数据动态变化
            _chartDiskRw = new ChartPanel
            {
                ChartTitle = "磁盘 读/写",
                Unit = "MB/s",
                ScaleFromData = true,
                LineColor = Color.FromArgb(0, 120, 215),
                LineColor2 = Color.FromArgb(232, 80, 80),
                ShowSeries2 = true,
                Series1Name = "读",
                Series2Name = "写",
                Dock = DockStyle.Fill
            };
            // 新增：磁盘繁忙%
            _chartDiskBusy = new ChartPanel { ChartTitle = "磁盘繁忙", Unit = "%", MaxScale = 100f, ScaleFromData = false, LineColor = Color.FromArgb(160, 90, 255), Dock = DockStyle.Fill };

            var chartContainer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(0)
            };
            chartContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            chartContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); // disk selector
            chartContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            chartContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            chartContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            chartContainer.Controls.Add(_chartCpu, 0, 0);
            chartContainer.Controls.Add(_chartMemory, 1, 0);
            chartContainer.Controls.Add(_chartDiskRw, 0, 2);
            chartContainer.Controls.Add(_chartDiskBusy, 1, 2);

            // 磁盘选择下拉（位于磁盘图表上方，切换后读写/繁忙度一起改变）
            var diskSel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Padding = new Padding(0, 4, 0, 0)
            };
            diskSel.Controls.Add(new Label { Text = "磁盘", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
            _comboPerfDisk = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
            _comboPerfDisk.SelectedIndexChanged += (s, ev) => ChangePerfDisk();
            diskSel.Controls.Add(_comboPerfDisk);
            chartContainer.Controls.Add(diskSel, 0, 1);
            chartContainer.SetColumnSpan(diskSel, 2);

            // ===== 实时日志面板（动态打印详细结果，同步写入崩溃安全的 log.txt） =====
            var logBox = new GroupBox { Text = "实时测试日志（同时写入 log.txt，每行立即落盘）", Dock = DockStyle.Fill, Padding = new Padding(6, 2, 6, 6) };
            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
                HideSelection = false
            };
            logBox.Controls.Add(_txtLog);

            root.Controls.Add(_menuStrip, 0, 0);
            root.Controls.Add(topBar, 0, 1);
            root.Controls.Add(new Panel { Dock = DockStyle.Fill, Height = 6 }, 0, 2);
            root.Controls.Add(chartContainer, 0, 3);
            root.Controls.Add(logBox, 0, 4);

            Controls.Add(root);
            MainMenuStrip = _menuStrip;
        }

        private void StartPerfTimer()
        {
            // 已移至 MainForm_Shown 异步初始化，此处保留空方法避免误删引用
        }

        private void UpdateDiskChartSubTitle()
        {
            _diskModel = GetSelectedDiskDisplay();
            if (_chartDiskRw != null) _chartDiskRw.SubTitle = _diskModel;
            if (_chartDiskBusy != null) _chartDiskBusy.SubTitle = _diskModel;
        }

        private string GetSelectedDiskDisplay()
        {
            var item = _comboPerfDisk?.SelectedItem as DiskItem;
            return item != null ? item.Display : (_perfMonitor?.DiskInstance ?? "");
        }

        private string GetSelectedDiskInstance()
        {
            var item = _comboPerfDisk?.SelectedItem as DiskItem;
            return item != null ? item.Instance : (_perfMonitor?.DiskInstance ?? "_Total");
        }

        private void ChangePerfDisk()
        {
            if (_perfMonitor == null || !_perfMonitor.IsAvailable) return;
            try
            {
                string inst = GetSelectedDiskInstance();
                if (string.IsNullOrWhiteSpace(inst)) return;
                _perfMonitor.SetDiskInstance(inst);
                _chartDiskRw.ClearValues();
                _chartDiskBusy.ClearValues();
                UpdateDiskChartSubTitle();
            }
            catch (Exception ex)
            {
                UpdateStatus("切换磁盘失败: " + ex.Message);
            }
        }

        private void PerfTimer_Tick(object sender, EventArgs e)
        {
            if (_perfMonitor == null || !_perfMonitor.IsAvailable) return;
            try
            {
                _perfMonitor.Sample();
                _chartCpu.AddValue(_perfMonitor.CpuPercent);
                float temp = float.IsNaN(_perfMonitor.CpuTemperatureCelsius) ? 0f : _perfMonitor.CpuTemperatureCelsius;
                _chartCpu.AddValue2(temp);
                _chartMemory.AddValue(_perfMonitor.MemoryUsedPercent);
                _chartDiskRw.AddValue(_perfMonitor.DiskReadMbPerSec);
                _chartDiskRw.AddValue2(_perfMonitor.DiskWriteMbPerSec);
                _chartDiskBusy.AddValue(_perfMonitor.DiskBusyPercent);
            }
            catch
            {
                // ignore
            }
        }

        private void PopulateDefaults()
        {
            _threads = Math.Max(1, Environment.ProcessorCount);
            UpdateStatus("就绪");
        }

        private void SetRunning(bool cpuRunning, bool memRunning, bool diskRunning)
        {
            // 运行中的项目禁止重复勾选切换；其余项目仍可勾选并通过“开始测试”加入同时运行。
            if (_chkCpu != null) _chkCpu.Enabled = !cpuRunning;
            if (_chkMem != null) _chkMem.Enabled = !memRunning;
            if (_chkDisk != null) _chkDisk.Enabled = !diskRunning;
            if (_btnStop != null) _btnStop.Enabled = cpuRunning || memRunning || diskRunning;
            if (_comboPerfDisk != null) _comboPerfDisk.Enabled = true;
        }

        private void UpdateStatus(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) text = "就绪";
            Text = "Stress Util - " + text;
        }

        /// <summary>按勾选并发启动 CPU/内存/磁盘测试（已在运行的项目会被各自的守卫跳过）。</summary>
        private void StartSelectedTests()
        {
            if ((_chkCpu == null || !_chkCpu.Checked) &&
                (_chkMem == null || !_chkMem.Checked) &&
                (_chkDisk == null || !_chkDisk.Checked))
            {
                UpdateStatus("请先勾选要测试的项目(CPU/内存/磁盘)");
                return;
            }
            // 磁盘可能先弹出选择/确认框，先触发；随后启动 CPU/内存，三者并发运行。
            if (_chkDisk != null && _chkDisk.Checked) _ = StartDiskAsync();
            if (_chkCpu != null && _chkCpu.Checked) _ = StartCpuAsync();
            if (_chkMem != null && _chkMem.Checked) _ = StartMemoryAsync();
        }

        private async Task StartCpuAsync()
        {
            if (_ctsCpu != null) return;

            _ctsCpu = new CancellationTokenSource();
            var token = _ctsCpu.Token;
            SetRunning(true, _ctsMem != null, _ctsDisk != null);
            UpdateStatus("CPU 压力测试运行中…");

            string resultDir = null;
            IProgress<string> logProgress = null;
            try
            {
                resultDir = PrepareResultDir("cpu");
                logProgress = CreateLogProgress(resultDir);
                logProgress?.Report("CPU: " + _cpuName + " | 内存: " + _memInfo + " | 磁盘: " + GetSelectedDiskDisplay());
            }
            catch (Exception ex) { UpdateStatus("创建结果目录失败: " + ex.Message); }

            try
            {
                await StressRunner.RunCpuStressAsync(new StressOptions
                {
                    DurationSec = _durationSec,
                    Threads = Math.Max(1, _threads),
                    CpuPercent = Math.Max(1, Math.Min(100, _cpuPeakPercent))
                }, log: logProgress, cancellationToken: token);
                UpdateStatus("CPU 压力测试完成");
                if (!string.IsNullOrEmpty(resultDir))
                    SaveChartImages(resultDir, "cpu");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("已停止");
            }
            catch (Exception ex)
            {
                UpdateStatus("异常: " + ex.Message);
                logProgress?.Report("异常: " + ex.Message);
            }
            finally
            {
                FlushLog(logProgress);
                CloseLog(logProgress);
                _ctsCpu?.Dispose();
                _ctsCpu = null;
                SetRunning(false, _ctsMem != null, _ctsDisk != null);
            }
        }

        private async Task StartMemoryAsync()
        {
            if (_ctsMem != null) return;

            long totalMb = _perfMonitor != null && _perfMonitor.TotalRamMb > 0
                ? (long)_perfMonitor.TotalRamMb
                : MemoryStressRunner.GetAvailableMemoryMb();
            if (totalMb <= 0) totalMb = 1024;
            int memMb = (int)Math.Max(64, Math.Min(totalMb - 256, totalMb * Math.Max(1, _memPeakPercent) / 100));

            _ctsMem = new CancellationTokenSource();
            var token = _ctsMem.Token;
            SetRunning(_ctsCpu != null, true, _ctsDisk != null);
            UpdateStatus("内存压力测试运行中…");

            string resultDir = null;
            IProgress<string> logProgress = null;
            try
            {
                resultDir = PrepareResultDir("memory");
                logProgress = CreateLogProgress(resultDir);
                logProgress?.Report("CPU: " + _cpuName + " | 内存: " + _memInfo + " | 磁盘: " + GetSelectedDiskDisplay());
            }
            catch (Exception ex) { UpdateStatus("创建结果目录失败: " + ex.Message); }

            try
            {
                await MemoryStressRunner.RunMemoryStressAsync(new MemoryStressOptions
                {
                    DurationSec = _durationSec,
                    MemoryMb = memMb,
                    Threads = Math.Max(1, _threads),
                    BlockSizeKb = 64,
                    Pattern = MemoryStressRunner.PatternAlternating
                }, log: logProgress, cancellationToken: token);
                UpdateStatus("内存压力测试完成");
                if (!string.IsNullOrEmpty(resultDir))
                    SaveChartImages(resultDir, "memory");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("已停止");
            }
            catch (Exception ex)
            {
                UpdateStatus("异常: " + ex.Message);
                logProgress?.Report("异常: " + ex.Message);
            }
            finally
            {
                FlushLog(logProgress);
                CloseLog(logProgress);
                _ctsMem?.Dispose();
                _ctsMem = null;
                SetRunning(_ctsCpu != null, false, _ctsDisk != null);
            }
        }

        private async Task StartDiskAsync()
        {
            if (_ctsDisk != null) return;

            // 1) 选择磁盘（无则先弹选择框）
            if (_testDiskNumbers == null || _testDiskNumbers.Count == 0)
            {
                if (!OpenDiskSelect()) return;
            }
            var disks = DiskManager.ListDisks();
            var selected = new List<DiskManager.DiskInfo>();
            foreach (var n in _testDiskNumbers)
            {
                var d = disks.Find(x => x.Number == n);
                if (d != null) selected.Add(d);
            }
            if (selected.Count == 0)
            {
                UpdateStatus("未找到所选磁盘，请重新选择");
                _testDiskNumbers.Clear();
                return;
            }

            // 2) 未分区盘：管理员校验 + 二次确认（会改写磁盘）
            var rawToInit = selected.FindAll(d => d.IsRawCandidate && string.IsNullOrEmpty(d.FirstDriveLetter));
            if (rawToInit.Count > 0)
            {
                if (!DiskManager.IsAdministrator())
                {
                    MessageBox.Show(this,
                        "检测到未分区的新盘需要自动初始化，但当前不是管理员权限。\r\n请右键以“管理员身份运行”本程序后重试（本次未分区盘将被跳过）。",
                        "需要管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    selected.RemoveAll(d => rawToInit.Contains(d));
                    if (selected.Count == 0) return;
                }
                else
                {
                    var names = new StringBuilder();
                    foreach (var d in rawToInit)
                        names.AppendLine("  • 磁盘 " + d.Number + "  " + (string.IsNullOrEmpty(d.Model) ? "未知型号" : d.Model) + "  " + d.SizeText);
                    var ans = MessageBox.Show(this,
                        "以下未分区磁盘将被【自动初始化（GPT + 快速格式化 NTFS）】用于测试，测试完成后会【删除分区并恢复为未初始化(RAW)】：\r\n\r\n" +
                        names.ToString() + "\r\n请确认这些是空的新盘（无重要数据）。是否继续？",
                        "确认初始化未分区磁盘", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (ans != DialogResult.OK) return;
                }
            }

            _ctsDisk = new CancellationTokenSource();
            var token = _ctsDisk.Token;
            SetRunning(_ctsCpu != null, _ctsMem != null, true);
            UpdateStatus("磁盘压力测试运行中…");

            int threadsPerDisk = Math.Max(8, Math.Max(1, _threads));
            int fileSizeMb = 1024;
            int phaseSec = Math.Max(15, _durationSec / 4);
            int blockKb = 4096;

            if (selected.Count > 0) SelectChartDiskByNumber(selected[0].Number);

            DateTime start = DateTime.Now;
            string resultDir = null;
            IProgress<string> logProgress = null;
            try
            {
                resultDir = PrepareResultDir("disk");
                logProgress = CreateLogProgress(resultDir);
                logProgress?.Report("磁盘压力测试开始：被测 " + selected.Count + " 块盘，读写模式=" +
                    (_rwParallel ? "读写同时(混合)" : "顺序四阶段") + (selected.Count > 1 ? "，多盘并行" : ""));
                // 先把被测硬件信息写进日志（每行立即落盘，即使中途崩溃也已记录）
                foreach (var d in selected)
                {
                    logProgress?.Report(string.Format("[磁盘{0}] 型号={1} 容量={2} 介质={3} 接口={4} 健康={5} 运行={6} 分区={7} 盘符={8}{9}",
                        d.Number, string.IsNullOrEmpty(d.Model) ? "未知" : d.Model, d.SizeText,
                        string.IsNullOrEmpty(d.MediaType) ? "-" : d.MediaType,
                        string.IsNullOrEmpty(d.BusType) ? "-" : d.BusType,
                        string.IsNullOrEmpty(d.Health) ? "-" : d.Health,
                        string.IsNullOrEmpty(d.OperationalStatus) ? "-" : d.OperationalStatus,
                        string.IsNullOrEmpty(d.PartitionStyle) ? "-" : d.PartitionStyle,
                        string.IsNullOrEmpty(d.DriveLetters) ? "未分区" : d.DriveLetters,
                        d.IsSystem ? " [系统盘]" : ""));
                }
                FlushLog(logProgress);
            }
            catch (Exception ex) { UpdateStatus("创建结果目录失败: " + ex.Message); }

            // 3) 初始化未分区盘（串行，避免并发存储操作冲突），并解析每盘测试路径
            var entries = new List<DiskReport.Entry>();
            foreach (var d in selected)
            {
                if (token.IsCancellationRequested) break;
                var entry = new DiskReport.Entry { Disk = d };
                string letter = d.FirstDriveLetter;
                if (string.IsNullOrEmpty(letter) && d.IsRawCandidate && DiskManager.IsAdministrator())
                {
                    logProgress?.Report("[磁盘" + d.Number + "] 未分区，正在自动初始化…");
                    FlushLog(logProgress);
                    string err;
                    if (DiskManager.InitializeRawDisk(d.Number, out letter, out err))
                    {
                        entry.AutoInitialized = true;
                        logProgress?.Report("[磁盘" + d.Number + "] 已初始化为 " + letter + ": 盘");
                    }
                    else
                    {
                        entry.Error = "自动初始化失败：" + err;
                        logProgress?.Report("[磁盘" + d.Number + "] 初始化失败：" + err);
                        entries.Add(entry);
                        continue;
                    }
                }
                if (string.IsNullOrEmpty(letter))
                {
                    entry.Error = "无可用盘符（该盘有分区但无盘符，已跳过以确保安全）";
                    entries.Add(entry);
                    continue;
                }
                entry.TestPath = TestPathForLetter(letter);
                entries.Add(entry);
            }

            // 4) 各盘并行测试
            try
            {
                var tasks = new List<Task>();
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrEmpty(entry.Error) || string.IsNullOrEmpty(entry.TestPath)) continue;
                    var e = entry;
                    var diskLog = new PrefixProgress(logProgress, "[磁盘" + e.Disk.Number + "] ");
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            e.Result = await DiskStressRunner.RunDiskStressAsync(new DiskStressOptions
                            {
                                Path = e.TestPath,
                                DurationSec = phaseSec,
                                FileSizeMb = fileSizeMb,
                                BlockSizeKb = blockKb,
                                Threads = threadsPerDisk,
                                SequentialRead = true,
                                SequentialWrite = true,
                                RandomRead = true,
                                RandomWrite = true,
                                ReadWriteParallel = _rwParallel
                            }, log: diskLog, cancellationToken: token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { e.Error = "已取消"; }
                        catch (Exception ex) { e.Error = ex.Message; }
                    }, token));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
                UpdateStatus(token.IsCancellationRequested ? "已停止" : "磁盘压力测试完成");
            }
            catch (OperationCanceledException) { UpdateStatus("已停止"); }
            catch (Exception ex) { UpdateStatus("异常: " + ex.Message); }
            finally
            {
                // 5) 恢复自动初始化过的盘为未初始化(RAW)
                foreach (var e in entries)
                {
                    if (!e.AutoInitialized) continue;
                    string err;
                    logProgress?.Report("[磁盘" + e.Disk.Number + "] 正在删除分区、恢复为未初始化(RAW)…");
                    FlushLog(logProgress);
                    if (DiskManager.RestoreRawDisk(e.Disk.Number, out err)) e.RestoredRaw = true;
                    else logProgress?.Report("[磁盘" + e.Disk.Number + "] 恢复失败：" + err + "（请手动用磁盘管理删除分区）");
                }

                // 6) 生成并保存详细报告
                try
                {
                    var cfg = new DiskReport.Config
                    {
                        ThreadsPerDisk = threadsPerDisk,
                        BlockSizeKb = blockKb,
                        FileSizeMb = fileSizeMb,
                        PhaseSec = phaseSec,
                        ReadWriteParallel = _rwParallel,
                        MultiDiskParallel = entries.Count > 1
                    };
                    double ramGb = (_perfMonitor != null && _perfMonitor.TotalRamMb > 0) ? _perfMonitor.TotalRamMb / 1024.0 : 0;
                    string report = DiskReport.Build(start, DateTime.Now, _cpuName, Environment.ProcessorCount, ramGb, cfg, entries);
                    logProgress?.Report("");
                    logProgress?.Report(report);
                    FlushLog(logProgress);
                    if (!string.IsNullOrEmpty(resultDir))
                    {
                        File.WriteAllText(Path.Combine(resultDir, "report.txt"), report, Encoding.UTF8);
                        SaveChartImages(resultDir, "disk");
                        UpdateStatus("磁盘测试完成，报告已保存到 " + resultDir);
                    }
                }
                catch (Exception ex) { UpdateStatus("生成报告失败: " + ex.Message); }

                FlushLog(logProgress);
                CloseLog(logProgress);
                _ctsDisk?.Dispose();
                _ctsDisk = null;
                SetRunning(_ctsCpu != null, _ctsMem != null, false);
            }
        }

        /// <summary>打开磁盘多选对话框，记录选择的磁盘编号与读写模式。返回是否已选中至少一块盘。</summary>
        private bool OpenDiskSelect()
        {
            try
            {
                var disks = DiskManager.ListDisks();
                if (disks.Count == 0)
                {
                    MessageBox.Show(this, "未枚举到磁盘（请尝试以管理员运行，或确认系统 PowerShell 存储模块可用）。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                using (var dlg = new DiskSelectForm(disks, _testDiskNumbers, _rwParallel))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _testDiskNumbers = dlg.SelectedDiskNumbers ?? new List<int>();
                        _rwParallel = dlg.ReadWriteParallel;
                        if (_testDiskNumbers.Count > 0)
                            UpdateStatus("已选择 " + _testDiskNumbers.Count + " 块盘，点“磁盘测试”开始");
                        return _testDiskNumbers.Count > 0;
                    }
                }
            }
            catch (Exception ex) { UpdateStatus("打开磁盘选择失败: " + ex.Message); }
            return false;
        }

        /// <summary>在指定盘符上创建测试用临时目录并返回路径（不破坏原有文件）。</summary>
        private static string TestPathForLetter(string letter)
        {
            string drive = char.ToUpperInvariant(letter[0]) + ":\\";
            string dir = Path.Combine(drive, "StressUtilTemp");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        /// <summary>把实时图表切到指定物理磁盘编号对应的性能计数器实例（如 "0 C:"）。</summary>
        private void SelectChartDiskByNumber(int number)
        {
            try
            {
                if (_comboPerfDisk == null) return;
                string prefix = number.ToString() + " ";
                for (int i = 0; i < _comboPerfDisk.Items.Count; i++)
                {
                    var di = _comboPerfDisk.Items[i] as DiskItem;
                    if (di == null || di.Instance == null) continue;
                    if (di.Instance == number.ToString() || di.Instance.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        _comboPerfDisk.SelectedIndex = i;
                        return;
                    }
                }
            }
            catch { }
        }

        /// <summary>根据当前选中的磁盘获取测试路径（在该盘写入临时文件，不破坏原有文件）</summary>
        private string GetTestPathForSelectedDisk()
        {
            var item = _comboPerfDisk?.SelectedItem as DiskItem;
            if (item == null || string.IsNullOrEmpty(item.Instance) || item.Instance == "_Total")
                return Path.GetTempPath();
            try
            {
                string inst = item.Instance;
                for (int i = 0; i < inst.Length; i++)
                {
                    if (inst[i] >= 'A' && inst[i] <= 'Z' && i + 1 < inst.Length && inst[i + 1] == ':')
                    {
                        string drive = inst.Substring(i, 2) + "\\";
                        string dir = Path.Combine(drive, "StressUtilTemp");
                        try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
                        return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    }
                    if (inst[i] >= 'a' && inst[i] <= 'z' && i + 1 < inst.Length && inst[i + 1] == ':')
                    {
                        string drive = char.ToUpperInvariant(inst[i]) + ":\\";
                        string dir = Path.Combine(drive, "StressUtilTemp");
                        try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
                        return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    }
                }
            }
            catch { }
            return Path.GetTempPath();
        }

        /// <summary>根据测试路径所在盘符切换到对应磁盘实例，使图表显示该盘 I/O</summary>
        private void SelectDiskInstanceForPath(string testPath)
        {
            if (_perfMonitor == null || !_perfMonitor.IsAvailable || _comboPerfDisk == null || _comboPerfDisk.Items.Count == 0) return;
            try
            {
                string root = Path.GetPathRoot(testPath);
                if (string.IsNullOrEmpty(root) || root.Length < 2) return;
                char driveLetter = char.ToUpperInvariant(root[0]);
                string volumePattern = " " + driveLetter + ":";
                for (int i = 0; i < _comboPerfDisk.Items.Count; i++)
                {
                    var di = _comboPerfDisk.Items[i] as DiskItem;
                    if (di == null) continue;
                    if (di.Instance == "_Total") continue;
                    if (di.Instance.IndexOf(volumePattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _comboPerfDisk.SelectedIndex = i;
                        return;
                    }
                }
                for (int i = 0; i < _comboPerfDisk.Items.Count; i++)
                {
                    var di = _comboPerfDisk.Items[i] as DiskItem;
                    if (di == null) continue;
                    if (di.Instance == "_Total") continue;
                    if (di.Instance.IndexOf(driveLetter) >= 0)
                    {
                        _comboPerfDisk.SelectedIndex = i;
                        return;
                    }
                }
            }
            catch { }
        }

        private void StopRun()
        {
            try { _ctsCpu?.Cancel(); } catch { }
            try { _ctsMem?.Cancel(); } catch { }
            try { _ctsDisk?.Cancel(); } catch { }
        }

        private static string PrepareResultDir(string testName)
        {
            string baseDir = Path.Combine(Application.StartupPath, "StressTestResults");
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string resultDir = Path.Combine(baseDir, testName + "_" + stamp);
            Directory.CreateDirectory(resultDir);
            return resultDir;
        }

        // 创建测试日志：同时写入崩溃安全的 log.txt（每行立即落盘）并回调到 GUI 实时面板。
        private IProgress<string> CreateLogProgress(string resultDir)
        {
            string logPath = string.IsNullOrEmpty(resultDir) ? null : Path.Combine(resultDir, "log.txt");
            var fileLog = new LogToFileProgress(logPath);
            return new TestLogProgress(fileLog, AppendLogLine);
        }

        private static void FlushLog(IProgress<string> logProgress)
        {
            (logProgress as TestLogProgress)?.Flush();
            (logProgress as LogToFileProgress)?.Flush();
        }

        private static void CloseLog(IProgress<string> logProgress)
        {
            (logProgress as IDisposable)?.Dispose();
        }

        // 把一行日志追加到 GUI 实时日志面板（线程安全，自动滚到底，限制长度防止内存膨胀）。
        private void AppendLogLine(string line)
        {
            if (_txtLog == null) return;
            try
            {
                if (_txtLog.IsHandleCreated && _txtLog.InvokeRequired)
                    _txtLog.BeginInvoke(new Action(() => AppendLogCore(line)));
                else
                    AppendLogCore(line);
            }
            catch { }
        }

        private void AppendLogCore(string line)
        {
            try
            {
                if (_txtLog == null || _txtLog.IsDisposed) return;
                if (_txtLog.TextLength > 300000)
                    _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 150000);
                _txtLog.AppendText((line ?? "") + "\r\n");
            }
            catch { }
        }

        private void SaveChartImages(string resultDir, string testName)
        {
            if (string.IsNullOrEmpty(resultDir)) return;
            try
            {
                void SaveChart(ChartPanel chart, string fileName)
                {
                    if (chart == null || !chart.IsHandleCreated) return;
                    string path = Path.Combine(resultDir, fileName + ".png");
                    using (var bmp = new Bitmap(Math.Max(1, chart.Width), Math.Max(1, chart.Height)))
                    {
                        chart.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(path, ImageFormat.Png);
                    }
                }
                SaveChart(_chartCpu, "cpu");
                SaveChart(_chartMemory, "memory");
                SaveChart(_chartDiskRw, "disk_rw");
                SaveChart(_chartDiskBusy, "disk_busy");
            }
            catch (Exception ex)
            {
                UpdateStatus("保存图表图片失败: " + ex.Message);
            }
        }

        private void OpenPeakSettings()
        {
            using (var dlg = new PeakSettingsForm(_durationSec, _cpuPeakPercent, _memPeakPercent, _diskPeakPercent))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _durationSec = dlg.DurationSec;
                    _cpuPeakPercent = dlg.CpuPeakPercent;
                    _memPeakPercent = dlg.MemoryPeakPercent;
                    _diskPeakPercent = dlg.DiskPeakPercent;
                    ConfigHelper.Save(_durationSec, _cpuPeakPercent, _memPeakPercent, _diskPeakPercent);
                    UpdateStatus("设置已更新");
                }
            }
        }

        private void ShowHelp()
        {
            var msg = "压力测试 - 使用说明\n\n" +
                      "· 所有测试参数（时长、线程数、峰值百分比）均在「设置」菜单中配置。\n" +
                      "· 点击「开始CPU/内存/磁盘压力测试」运行对应测试，上方图表会实时更新。\n" +
                      "· 可通过磁盘下拉框切换监控的物理磁盘。\n" +
                      "· 测试运行中可点击「停止」提前结束。";
            MessageBox.Show(msg, "帮助文档", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                "Stress Util\n\n" +
                "CPU / 内存 / 磁盘 压力测试与实时监控。\n" +
                "支持 Windows 性能计数器与多磁盘选择。",
                "关于",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _perfTimer?.Stop();
            _perfTimer = null;
            _perfMonitor?.Dispose();
            _perfMonitor = null;
            StopRun();
            try { _ctsCpu?.Dispose(); } catch { }
            try { _ctsMem?.Dispose(); } catch { }
            try { _ctsDisk?.Dispose(); } catch { }
            base.OnFormClosing(e);
        }
    }

    /// <summary>
    /// 崩溃安全的日志写入：每行【立即落盘】(append + FileStream.Flush(true) 强制写入物理磁盘)。
    /// 即使测试中机器死机/蓝屏/断电，崩溃前已输出的日志也都在磁盘上，不会丢。
    /// FileShare.Read 允许测试期间用记事本/Get-Content -Wait 实时查看。
    /// </summary>
    internal sealed class LogToFileProgress : IProgress<string>, IDisposable
    {
        private readonly object _lock = new object();
        private FileStream _fs;
        private StreamWriter _writer;

        public LogToFileProgress(string logPath)
        {
            if (string.IsNullOrEmpty(logPath)) return;
            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(_fs, new System.Text.UTF8Encoding(false));
            }
            catch { _fs = null; _writer = null; }
        }

        public void Report(string value)
        {
            if (_writer == null) return;
            lock (_lock)
            {
                try
                {
                    _writer.WriteLine(value ?? "");
                    _writer.Flush();      // StreamWriter 缓冲 -> FileStream
                    _fs.Flush(true);      // FileStream -> 物理磁盘（崩溃也不丢）
                }
                catch { }
            }
        }

        public void Flush()
        {
            if (_writer == null) return;
            lock (_lock) { try { _writer.Flush(); _fs.Flush(true); } catch { } }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _writer?.Flush(); } catch { }
                try { _writer?.Dispose(); } catch { }
                _writer = null; _fs = null;
            }
        }
    }

    /// <summary>同时把日志写入文件(崩溃安全)并回调到 GUI 实时显示。</summary>
    internal sealed class TestLogProgress : IProgress<string>, IDisposable
    {
        private readonly LogToFileProgress _file;
        private readonly Action<string> _ui;
        public TestLogProgress(LogToFileProgress file, Action<string> ui) { _file = file; _ui = ui; }
        public void Report(string value)
        {
            _file?.Report(value);
            try { _ui?.Invoke(value ?? ""); } catch { }
        }
        public void Flush() { _file?.Flush(); }
        public void Dispose() { _file?.Dispose(); }
    }

    /// <summary>给日志每行加前缀（如 "[磁盘1] "），用于多盘并行时区分来源。线程安全取决于内层。</summary>
    internal sealed class PrefixProgress : IProgress<string>
    {
        private readonly IProgress<string> _inner;
        private readonly string _prefix;
        public PrefixProgress(IProgress<string> inner, string prefix)
        {
            _inner = inner;
            _prefix = prefix ?? "";
        }
        public void Report(string value)
        {
            if (_inner == null) return;
            _inner.Report(_prefix + (value ?? ""));
        }
    }
}
