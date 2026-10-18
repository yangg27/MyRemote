using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MyRemote.Services
{
    /// <summary>
    /// 全局界面尺寸自适应：把「按 96 DPI 设计的像素值」换算到当前机器真实 DPI，
    /// 并把窗口限制在屏幕可用区域内。目的是解决两类「显示不全」：
    ///   ① 高 DPI（125%/150%/200%）下字体被放大、而控件框与窗口没跟着放大 → 文字被截断、按钮被挤出可视区；
    ///   ② 小分辨率屏幕上窗口比屏幕还大 → 底部/右侧内容点不到、看不全。
    ///
    /// 约定（新增/修改界面代码必须遵守，否则又会退回"显示不全"）：
    ///   • 尺寸一律写「设计像素」并包一层 <see cref="S(int)"/>，例如 <c>Width = UiScale.S(80);</c>
    ///   • 字体一律写 pt，例如 <c>new Font(family, 10f)</c> —— 点本身是物理单位，GDI+ 会随 DPI 自动放大，请不要乘系数；
    ///   • 列表列宽用 <see cref="SetColumns"/> / <see cref="AutoFillLastColumn"/>；
    ///   • 窗体尺寸用 <see cref="SetFormSize"/>（内部完成缩放 + 屏幕裁剪）。
    ///
    /// 之所以用"显式换算"而不是 WinForms 的 AutoScaleMode.Dpi：本项目全部界面都是纯代码构建、
    /// 没有设计器基准信息，显式换算行为完全确定、可被探针逐项测量验证，不会出现双重缩放。
    /// 因此所有窗体都设 AutoScaleMode = None。
    /// </summary>
    public static class UiScale
    {
        private static float? _factor;
        private static int _systemDpi = -1;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        private const int LOGPIXELSX = 88;

        /// <summary>系统 DPI（96=100%，120=125%，144=150%，192=200%）。</summary>
        public static int SystemDpi
        {
            get
            {
                if (_systemDpi < 0) _systemDpi = DetectSystemDpi();
                return _systemDpi;
            }
        }

        /// <summary>缩放系数 = 系统 DPI / 96。</summary>
        public static float Factor
        {
            get
            {
                if (_factor == null)
                {
                    var f = SystemDpi / 96f;
                    // 排障兜底：可用环境变量 MYREMOTE_UI_SCALE 强制指定缩放（例如界面仍偏小/偏大时手动微调）
                    try
                    {
                        var env = Environment.GetEnvironmentVariable("MYREMOTE_UI_SCALE");
                        if (!string.IsNullOrWhiteSpace(env) &&
                            float.TryParse(env.Trim(), System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out var ov) &&
                            ov >= 0.5f && ov <= 4f)
                            f = ov;
                    }
                    catch { }
                    _factor = f;
                }
                return _factor.Value;
            }
        }

        /// <summary>是否处于非 100% 缩放环境。</summary>
        public static bool IsScaled => Math.Abs(Factor - 1f) > 0.01f;

        /// <summary>供界面显示与日志使用的描述文本，例如「150%（系统 DPI 144）」。</summary>
        public static string Describe()
        {
            var f = Factor;
            var pct = (int)Math.Round(f * 100);
            return pct == 100 ? "100%（系统 DPI 96）" : $"{pct}%（系统 DPI {SystemDpi}）";
        }

        private static int DetectSystemDpi()
        {
            // Windows 10 1607+ 有 GetDpiForSystem；老系统上用 GetDeviceCaps 兜底
            try
            {
                var d = (int)GetDpiForSystem();
                if (d >= 48 && d <= 480) return d;
            }
            catch { }
            try
            {
                var hdc = GetDC(IntPtr.Zero);
                try
                {
                    var d = GetDeviceCaps(hdc, LOGPIXELSX);
                    if (d >= 48 && d <= 480) return d;
                }
                finally { ReleaseDC(IntPtr.Zero, hdc); }
            }
            catch { }
            return 96;
        }

        // ---------------- 尺寸换算 ----------------

        /// <summary>把设计像素换算为当前 DPI 下的实际像素。</summary>
        public static int S(int designPx) => (int)Math.Round(designPx * Factor, MidpointRounding.AwayFromZero);

        /// <summary>把设计尺寸换算为当前 DPI 下的实际尺寸。</summary>
        public static Size S(Size designSize) => new(S(designSize.Width), S(designSize.Height));

        /// <summary>把设计内边距换算为当前 DPI 下的实际内边距。</summary>
        public static Padding S(Padding designPadding) =>
            new(S(designPadding.Left), S(designPadding.Top), S(designPadding.Right), S(designPadding.Bottom));

        // ---------------- 窗体尺寸 ----------------

        /// <summary>
        /// 设置窗体尺寸与最小尺寸：先按 DPI 放大，再裁剪到屏幕可用区域之内。
        /// 最小尺寸同样要裁剪 —— 否则小屏 + 高 DPI 时"最小尺寸"本身就会超出屏幕，窗口必然显示不全。
        /// </summary>
        /// <param name="form">目标窗体</param>
        /// <param name="designWidth">设计宽度（96 DPI 下的像素）</param>
        /// <param name="designHeight">设计高度</param>
        /// <param name="minWidth">设计最小宽度，0 表示不设置</param>
        /// <param name="minHeight">设计最小高度，0 表示不设置</param>
        public static void SetFormSize(Form form, int designWidth, int designHeight, int minWidth = 0, int minHeight = 0)
        {
            var wa = WorkingArea();
            int maxW = Math.Max(320, wa.Width - S(40));
            int maxH = Math.Max(240, wa.Height - S(60));

            int mw = minWidth > 0 ? Math.Min(S(minWidth), maxW) : 0;
            int mh = minHeight > 0 ? Math.Min(S(minHeight), maxH) : 0;
            if (mw > 0 || mh > 0) form.MinimumSize = new Size(mw, mh);

            form.Width = Math.Min(Math.Max(S(designWidth), mw), maxW);
            form.Height = Math.Min(Math.Max(S(designHeight), mh), maxH);
        }

        /// <summary>
        /// 屏幕可用区域（不含任务栏）。
        /// 注意：本方法可能被后台线程调用（如诊断服务），
        /// 因此优先走 Win32 系统参数，避免 Screen 组件对 UI 线程的依赖。
        /// </summary>
        public static Rectangle WorkingArea()
        {
            var wa = new Rectangle(0, 0, 1024, 768);
            // 优先：非交互式查询，线程安全
            try
            {
                var rc = new RECT();
                if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref rc, 0))
                {
                    var w = rc.Right - rc.Left;
                    var h = rc.Bottom - rc.Top;
                    if (w >= 320 && h >= 240) wa = new Rectangle(rc.Left, rc.Top, w, h);
                }
            }
            catch { }
            // 兜底：Screen 组件（提供多显示器主屏语义）
            try { wa = Screen.PrimaryScreen?.WorkingArea ?? wa; } catch { }
            // 排障：可用环境变量模拟小屏（如 MYREMOTE_UI_SCREEN=1366x768），便于验证裁剪逻辑
            try
            {
                var env = Environment.GetEnvironmentVariable("MYREMOTE_UI_SCREEN");
                if (!string.IsNullOrWhiteSpace(env))
                {
                    var parts = env.Trim().Split(new[] { 'x', 'X' });
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0].Trim(), out var sw) && int.TryParse(parts[1].Trim(), out var sh) &&
                        sw >= 640 && sh >= 480)
                        wa = new Rectangle(0, 0, sw, sh);
                }
            }
            catch { }
            return wa;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint SPI_GETWORKAREA = 0x0030;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        /// <summary>
        /// 让按钮按文字自动撑开宽度（同时保留一个设计最小宽度）。
        /// 高 DPI 下中文字符明显变宽，固定宽度会把文字切掉 —— 这是"字显示不全"最常见的成因，
        /// 因此本项目所有按钮一律使用本方法，不要写死 Width。
        /// </summary>
        /// <param name="button">目标按钮</param>
        /// <param name="designMinWidth">设计最小宽度（96 DPI 下的像素），0 表示不设下限</param>
        public static Button AutoFit(this Button button, int designMinWidth = 0)
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            if (designMinWidth > 0)
                button.MinimumSize = new Size(S(designMinWidth), 0);
            return button;
        }

        /// <summary>
        /// 计算"按钮行容器"（FlowLayoutPanel 之类）应有的高度：
        /// 以当前字体下按钮的实际高度为准，再加上内边距。
        /// 用于替代写死的 40/32 等数值 —— 高 DPI 下按钮会变高，写死高度会把按钮切掉。
        /// </summary>
        /// <param name="verticalPadding">设计内边距（上下合计），默认 8</param>
        public static int ButtonRowHeight(int verticalPadding = 8)
        {
            // 标准 WinForms 按钮在 96 DPI + 默认字体下约 25px 高。
            // 取「设计值 × 缩放」与「按钮实测高度」两者中的较大者：
            //   • 设计值：随 DPI 稳定缩放，是下限保证；
            //   • 实测值：若系统使用较大的辅助功能字体，按钮会更高，此时以实测为准。
            // 只用一个来源都可能在特定环境下偏小，取大值可确保按钮不被切掉。
            int byDesign = S(26 + verticalPadding);
            try
            {
                using var probe = new Button
                {
                    Text = "确定",
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink
                };
                var measured = probe.PreferredSize.Height + S(verticalPadding);
                if (measured >= 20 && measured <= 600) return Math.Max(byDesign, measured);
            }
            catch { }
            return byDesign;
        }

        // ---------------- 列表视图 ----------------

        /// <summary>
        /// 按设计像素批量设置列表列，并让最后一列自动补满剩余宽度。
        /// 直接写绝对列宽会在高 DPI 下"字大列窄"→ 内容被截断；由最后一列吸收剩余空间可显著缓解。
        /// </summary>
        public static void SetColumns(ListView list, params (string text, int designWidth)[] columns)
        {
            list.Columns.Clear();
            foreach (var (text, w) in columns) list.Columns.Add(text, S(w));
            AutoFillLastColumn(list);
        }

        /// <summary>让最后一列随窗口变宽自动补满剩余空间（只增不减，窗口过窄时由横向滚动条兜底，不会丢内容）。</summary>
        public static void AutoFillLastColumn(ListView list)
        {
            list.Resize += (_, _) => FillLastColumnNow(list);
            FillLastColumnNow(list);
        }

        private static void FillLastColumnNow(ListView list)
        {
            try
            {
                if (list.Columns.Count == 0) return;
                int used = 0;
                for (int i = 0; i < list.Columns.Count - 1; i++) used += list.Columns[i].Width;
                // 预留：竖直滚动条 + 边界余量，避免列宽顶到滚动条而误触发横向滚动条
                int reserved = SystemInformation.VerticalScrollBarWidth + S(8);
                int avail = list.ClientSize.Width - used - reserved;
                var last = list.Columns[list.Columns.Count - 1];
                var want = Math.Max(S(80), avail);
                if (want > last.Width) last.Width = want;
            }
            catch { }
        }
    }
}