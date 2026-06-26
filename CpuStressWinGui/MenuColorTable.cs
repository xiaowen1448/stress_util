using System.Drawing;
using System.Windows.Forms;

namespace CpuStressWinGui
{
    /// <summary>
    /// 自定义菜单颜色表（与 macos_vm_c# ExcelStyleForm 菜单背景一致：浅灰 240,240,240）
    /// </summary>
    public sealed class MenuColorTable : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(240, 240, 240);
        private static readonly Color Sel = Color.FromArgb(200, 230, 255);
        private static readonly Color SelEnd = Color.FromArgb(150, 200, 255);
        private static readonly Color ItemBorder = Color.FromArgb(100, 150, 200);
        private static readonly Color MenuBorderColor = Color.FromArgb(200, 200, 200);
        private static readonly Color PressBegin = Color.FromArgb(180, 210, 240);
        private static readonly Color PressEnd = Color.FromArgb(130, 180, 230);

        public override Color MenuStripGradientBegin => Bg;
        public override Color MenuStripGradientEnd => Bg;
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => MenuBorderColor;
        public override Color MenuItemBorder => ItemBorder;

        public override Color MenuItemSelected => Sel;
        public override Color MenuItemSelectedGradientBegin => Sel;
        public override Color MenuItemSelectedGradientEnd => SelEnd;

        public override Color MenuItemPressedGradientBegin => PressBegin;
        public override Color MenuItemPressedGradientEnd => PressEnd;

        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
    }

    /// <summary>
    /// 扁平化菜单渲染（与 macos_vm_c# 菜单背景一致：浅灰 240,240,240）
    /// </summary>
    public sealed class MenuStripRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Bg = Color.FromArgb(240, 240, 240);
        private static readonly Color TextColor = Color.FromArgb(64, 64, 64);

        public MenuStripRenderer() : base(new MenuColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = TextColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Bg);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
                ControlPaint.DrawBorder(e.Graphics, e.AffectedBounds, Color.FromArgb(200, 200, 200), ButtonBorderStyle.Solid);
        }
    }
}
