using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CpuStressWinGui
{
    /// <summary>
    /// 实时折线图控件（类似任务管理器性能曲线），支持一条或多条曲线、固定点数滚动
    /// </summary>
    public sealed class ChartPanel : Panel
    {
        private const int MaxPoints = 90;
        private readonly List<float> _values = new List<float>();
        private readonly List<float> _values2 = new List<float>();
        private readonly object _lock = new object();
        private string _title = "";
        private string _unit = "%";
        private float _maxScale = 100f;
        private Color _lineColor = Color.FromArgb(0, 120, 215);
        private Color _lineColor2 = Color.FromArgb(232, 80, 80);
        private Color _gridColor = Color.FromArgb(240, 240, 240);
        private bool _scaleFromData = true;
        private bool _showSeries2 = false;
        private string _series1Name = "";
        private string _series2Name = "";
        private string _series2Unit = "";
        private string _subTitle = "";

        public ChartPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            Padding = new Padding(8);
        }

        /// <summary>图表标题（如 "CPU"、"内存"）</summary>
        public string ChartTitle
        {
            get => _title;
            set { _title = value ?? ""; Invalidate(); }
        }

        /// <summary>数值单位（如 "%"、"MB/s"）</summary>
        public string Unit
        {
            get => _unit;
            set { _unit = value ?? ""; Invalidate(); }
        }

        /// <summary>Y 轴最大值（当 ScaleFromData 为 false 时使用）</summary>
        public float MaxScale
        {
            get => _maxScale;
            set { _maxScale = value > 0 ? value : 100f; Invalidate(); }
        }

        /// <summary>是否根据当前数据自动计算 Y 轴最大值</summary>
        public bool ScaleFromData
        {
            get => _scaleFromData;
            set { _scaleFromData = value; Invalidate(); }
        }

        /// <summary>曲线颜色</summary>
        public Color LineColor
        {
            get => _lineColor;
            set { _lineColor = value; Invalidate(); }
        }

        /// <summary>第二条曲线颜色（用于读/写合并展示）</summary>
        public Color LineColor2
        {
            get => _lineColor2;
            set { _lineColor2 = value; Invalidate(); }
        }

        /// <summary>是否启用第二条曲线</summary>
        public bool ShowSeries2
        {
            get => _showSeries2;
            set { _showSeries2 = value; Invalidate(); }
        }

        /// <summary>曲线1名称（标注用）</summary>
        public string Series1Name
        {
            get => _series1Name;
            set { _series1Name = value ?? ""; Invalidate(); }
        }

        /// <summary>曲线2名称（标注用）</summary>
        public string Series2Name
        {
            get => _series2Name;
            set { _series2Name = value ?? ""; Invalidate(); }
        }

        /// <summary>曲线2单位（如 "℃"；为空时使用 Unit）</summary>
        public string Series2Unit
        {
            get => _series2Unit;
            set { _series2Unit = value ?? ""; Invalidate(); }
        }

        /// <summary>副标题/型号等小字说明（如 CPU/内存/磁盘型号）</summary>
        public string SubTitle
        {
            get => _subTitle;
            set { _subTitle = value ?? ""; Invalidate(); }
        }

        /// <summary>追加一个采样点，超出 MaxPoints 时丢弃最旧的点</summary>
        public void AddValue(float value)
        {
            lock (_lock)
            {
                _values.Add(value);
                while (_values.Count > MaxPoints)
                    _values.RemoveAt(0);
            }
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(Invalidate));
        }

        /// <summary>追加第二条曲线采样点（需 ShowSeries2=true）</summary>
        public void AddValue2(float value)
        {
            lock (_lock)
            {
                _values2.Add(value);
                while (_values2.Count > MaxPoints)
                    _values2.RemoveAt(0);
            }
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(Invalidate));
        }

        /// <summary>清空历史数据（两条曲线都会清空）</summary>
        public void ClearValues()
        {
            lock (_lock)
            {
                _values.Clear();
                _values2.Clear();
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rc = ClientRectangle;
            if (rc.Width < 20 || rc.Height < 20) return;

            // Left axis labels (tick values)
            const int axisLabelWidth = 44;
            int left = Padding.Left;
            int top = Padding.Top;
            int right = rc.Width - Padding.Right;
            int bottom = rc.Height - Padding.Bottom;
            int chartLeft = left + axisLabelWidth;
            int chartW = right - chartLeft;
            int chartH = bottom - top;
            if (chartW <= 0 || chartH <= 0) return;

            float maxVal;
            float last1 = 0, last2 = 0;
            lock (_lock)
            {
                maxVal = _maxScale;
                if (_values.Count > 0) last1 = _values[_values.Count - 1];
                if (_showSeries2 && _values2.Count > 0) last2 = _values2[_values2.Count - 1];

                if (_scaleFromData && (_values.Count > 0 || (_showSeries2 && _values2.Count > 0)))
                {
                    float m = 0;
                    foreach (var v in _values)
                        if (v > m) m = v;
                    if (_showSeries2)
                    {
                        foreach (var v in _values2)
                            if (v > m) m = v;
                    }
                    if (m > 0) maxVal = Math.Max(maxVal, (float)Math.Ceiling(m * 1.1));
                }
            }

            // 网格线（水平约 5 条）+ 左侧纵坐标刻度
            using (var penGrid = new Pen(_gridColor, 1f))
            {
                using (var fontTicks = new Font(Font.FontFamily, 8.5f))
                using (var brushTicks = new SolidBrush(Color.Gray))
                using (var penAxis = new Pen(Color.FromArgb(220, 220, 220), 1f))
                {
                    // Y axis line
                    g.DrawLine(penAxis, chartLeft, top, chartLeft, bottom);

                    for (int i = 0; i <= 5; i++)
                    {
                        int y = top + (chartH * i) / 5;
                        g.DrawLine(penGrid, chartLeft, y, right, y);

                        float tickVal = maxVal * (1f - i / 5f);
                        string s = tickVal >= 100 ? tickVal.ToString("F0") : tickVal.ToString("F1");
                        var size = g.MeasureString(s, fontTicks);
                        g.DrawString(s, fontTicks, brushTicks, chartLeft - 6 - size.Width, y - size.Height / 2);
                    }
                }

                for (int i = 0; i <= 5; i++)
                {
                    int y = top + (chartH * i) / 5;
                    // already drawn above with chartLeft
                }
                for (int i = 0; i <= 10; i++)
                {
                    int x = chartLeft + (chartW * i) / 10;
                    g.DrawLine(penGrid, x, top, x, bottom);
                }
            }

            // 标题（双曲线时显示双单位）
            using (var font = new Font(Font.FontFamily, 9f))
            using (var brush = new SolidBrush(Color.Gray))
            {
                string titleUnit = _unit;
                if (_showSeries2 && !string.IsNullOrEmpty(_series2Unit))
                    titleUnit = _unit + " / " + _series2Unit;
                g.DrawString(_title + " (" + titleUnit + ")", Font, brush, chartLeft, 2);
                if (!string.IsNullOrEmpty(_subTitle))
                {
                    using (var fontSub = new Font(Font.FontFamily, 7.5f))
                    using (var brushSub = new SolidBrush(Color.FromArgb(140, 140, 140)))
                        g.DrawString(_subTitle, fontSub, brushSub, chartLeft, 14);
                }
            }

            // 标注（当前值）：曲线1 用 Unit，曲线2 用 Series2Unit
            using (var fontLegend = new Font(Font.FontFamily, 8.5f))
            {
                string s1 = (string.IsNullOrEmpty(_series1Name) ? "" : (_series1Name + ": ")) + FormatValue(last1, _unit);
                SizeF sz1 = g.MeasureString(s1, fontLegend);
                float xLegend = right - sz1.Width - 4;
                float yLegend = 2;
                using (var b1 = new SolidBrush(_lineColor))
                    g.DrawString(s1, fontLegend, b1, xLegend, yLegend);

                if (_showSeries2)
                {
                    string unit2 = string.IsNullOrEmpty(_series2Unit) ? _unit : _series2Unit;
                    string s2 = (string.IsNullOrEmpty(_series2Name) ? "" : (_series2Name + ": ")) + FormatValue(last2, unit2);
                    SizeF sz2 = g.MeasureString(s2, fontLegend);
                    using (var b2 = new SolidBrush(_lineColor2))
                        g.DrawString(s2, fontLegend, b2, right - sz2.Width - 4, yLegend + sz1.Height - 2);
                }
            }

            // 折线（曲线1/曲线2）
            float[] arr;
            float[] arr2 = null;
            lock (_lock)
            {
                if (_values.Count < 2) return;
                arr = _values.ToArray();
                if (_showSeries2 && _values2.Count >= 2)
                    arr2 = _values2.ToArray();
            }

            float stepX = chartW / (float)(MaxPoints - 1);
            var pts = new PointF[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                float x = right - (arr.Length - 1 - i) * stepX;
                float y = bottom - (arr[i] / maxVal) * chartH;
                if (y < top) y = top;
                if (y > bottom) y = bottom;
                pts[i] = new PointF(x, y);
            }

            using (var pen = new Pen(_lineColor, 2f))
                g.DrawLines(pen, pts);

            if (arr2 != null)
            {
                var pts2 = new PointF[arr2.Length];
                for (int i = 0; i < arr2.Length; i++)
                {
                    float x = right - (arr2.Length - 1 - i) * stepX;
                    float y = bottom - (arr2[i] / maxVal) * chartH;
                    if (y < top) y = top;
                    if (y > bottom) y = bottom;
                    pts2[i] = new PointF(x, y);
                }
                using (var pen2 = new Pen(_lineColor2, 2f))
                    g.DrawLines(pen2, pts2);
            }
        }

        private string FormatValue(float v, string unit)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "-";
            if (string.IsNullOrEmpty(unit)) unit = _unit;
            if (unit == "%") return v.ToString("F0") + "%";
            if (unit == "℃" || unit == "°C") return v.ToString("F0") + " ℃";
            if (Math.Abs(v) >= 100) return v.ToString("F0") + (unit.Length > 0 ? (" " + unit) : "");
            return v.ToString("F1") + (unit.Length > 0 ? (" " + unit) : "");
        }
    }
}
