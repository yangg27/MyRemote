using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MyRemote
{
    /// <summary>
    /// 运行时生成一个简单但清晰的应用图标（蓝底白字“R”），避免依赖外部 .ico 文件，
    /// 同时让主窗体与托盘图标不再是系统默认的程序图标。
    /// 任何一步失败都返回 null，调用方会安全地回退到默认图标。
    /// </summary>
    public static class AppIcon
    {
        public static Icon? Create(int size = 64)
        {
            try
            {
                using var bmp = new Bitmap(size, size);
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // 圆角背景
                using var brush = new SolidBrush(Color.FromArgb(45, 125, 210));
                using var path = RoundedRect(new Rectangle(2, 2, size - 4, size - 4), size / 6);
                g.FillPath(brush, path);

                // 白字 “R”
                using var font = new Font(FontFamily.GenericSansSerif, size * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("R", font, Brushes.White, new RectangleF(0, 0, size, size), sf);

                // GetHicon 返回的句柄必须显式销毁，否则反复创建会泄漏 GDI/用户句柄。
                // Icon.FromHandle 不接管所有权，因此克隆一份后立刻释放原句柄。
                IntPtr h = bmp.GetHicon();
                try
                {
                    using var tmp = Icon.FromHandle(h);
                    return (Icon)tmp.Clone();
                }
                finally
                {
                    DestroyIcon(h);
                }
            }
            catch
            {
                return null;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var r = Math.Max(1, radius);
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, r, r, 180, 90);
            path.AddArc(bounds.Right - r, bounds.Y, r, r, 270, 90);
            path.AddArc(bounds.Right - r, bounds.Bottom - r, r, r, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - r, r, r, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
