using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AccessFix
{
    public static class TrayIconHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon CreateStatusIcon(bool active, int size)
        {
            using (Bitmap bmp = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float pad = size * 0.06f;
                float w = size - pad * 2f;
                float h = size - pad * 2f;

                using (GraphicsPath bgPath = CreateRoundedRect(pad, pad, w, h, size * 0.22f))
                using (LinearGradientBrush bgBrush = new LinearGradientBrush(
                    new PointF(0, 0),
                    new PointF(0, size),
                    Color.FromArgb(24, 30, 44),
                    Color.FromArgb(15, 19, 28)))
                {
                    g.FillPath(bgBrush, bgPath);
                    using (Pen borderPen = new Pen(active ? Color.FromArgb(140, 16, 185, 129) : Color.FromArgb(100, 100, 116, 139), Math.Max(1f, size * 0.04f)))
                    {
                        g.DrawPath(borderPen, bgPath);
                    }
                }

                // Vẽ hình khiên ở giữa
                using (GraphicsPath shield = new GraphicsPath())
                {
                    float cx = size / 2f;
                    float top = size * 0.20f;
                    float midY = size * 0.50f;
                    float bot = size * 0.82f;
                    float halfW = size * 0.28f;

                    shield.AddBezier(
                        new PointF(cx, top),
                        new PointF(cx + halfW * 0.5f, top + size * 0.03f),
                        new PointF(cx + halfW, top + size * 0.04f),
                        new PointF(cx + halfW, top + size * 0.14f));
                    shield.AddBezier(
                        new PointF(cx + halfW, top + size * 0.14f),
                        new PointF(cx + halfW, midY + size * 0.06f),
                        new PointF(cx + halfW * 0.55f, bot - size * 0.08f),
                        new PointF(cx, bot));
                    shield.AddBezier(
                        new PointF(cx, bot),
                        new PointF(cx - halfW * 0.55f, bot - size * 0.08f),
                        new PointF(cx - halfW, midY + size * 0.06f),
                        new PointF(cx - halfW, top + size * 0.14f));
                    shield.AddBezier(
                        new PointF(cx - halfW, top + size * 0.14f),
                        new PointF(cx - halfW, top + size * 0.04f),
                        new PointF(cx - halfW * 0.5f, top + size * 0.03f),
                        new PointF(cx, top));
                    shield.CloseFigure();

                    Color cTop = active ? Color.FromArgb(16, 195, 135) : Color.FromArgb(100, 116, 139);
                    Color cBot = active ? Color.FromArgb(5, 145, 100) : Color.FromArgb(71, 85, 105);

                    using (LinearGradientBrush sb = new LinearGradientBrush(new PointF(0, top), new PointF(0, bot), cTop, cBot))
                    {
                        g.FillPath(sb, shield);
                    }
                }

                // Dấu tích (khi đang bật) hoặc dấu gạch tạm dừng (khi đang tắt)
                using (Pen markPen = new Pen(Color.White, Math.Max(1.6f, size * 0.085f)))
                {
                    markPen.StartCap = LineCap.Round;
                    markPen.EndCap = LineCap.Round;
                    markPen.LineJoin = LineJoin.Round;

                    if (active)
                    {
                        PointF p1 = new PointF(size * 0.39f, size * 0.51f);
                        PointF p2 = new PointF(size * 0.47f, size * 0.60f);
                        PointF p3 = new PointF(size * 0.62f, size * 0.41f);
                        g.DrawLines(markPen, new PointF[] { p1, p2, p3 });
                    }
                    else
                    {
                        g.DrawLine(markPen, size * 0.40f, size * 0.50f, size * 0.60f, size * 0.50f);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(hIcon).Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private static GraphicsPath CreateRoundedRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath path = new GraphicsPath();
            float d = r * 2f;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
