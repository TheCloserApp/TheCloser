using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;

namespace TheCloser
{
    /// <summary>The app mark (a cue card with a speech tail), drawn at any size for the tray and exe icons.</summary>
    internal static class Brand
    {
        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Bitmap Mark(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new RectangleF(size * 0.04f, size * 0.04f, size * 0.92f, size * 0.92f);
                using (var path = RoundRect(rect, size * 0.24f))
                using (var brush = new LinearGradientBrush(rect, Color.FromArgb(0x2E, 0xCC, 0x94), Color.FromArgb(0x14, 0x8F, 0x6A), 45f))
                    g.FillPath(brush, path);
                var card = new RectangleF(size * 0.22f, size * 0.27f, size * 0.56f, size * 0.40f);
                using (var path = RoundRect(card, size * 0.08f))
                using (var brush = new SolidBrush(Color.White))
                    g.FillPath(brush, path);
                using (var tail = new GraphicsPath())
                {
                    tail.AddPolygon(new[]
                    {
                        new PointF(size * 0.33f, size * 0.65f),
                        new PointF(size * 0.29f, size * 0.80f),
                        new PointF(size * 0.48f, size * 0.65f)
                    });
                    using (var brush = new SolidBrush(Color.White)) g.FillPath(brush, tail);
                }
                using (var pen = new Pen(Color.FromArgb(0x17, 0x9B, 0x72), Math.Max(1f, size * 0.055f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, size * 0.33f, size * 0.41f, size * 0.67f, size * 0.41f);
                    g.DrawLine(pen, size * 0.33f, size * 0.53f, size * 0.56f, size * 0.53f);
                }
            }
            return bmp;
        }

        public static Icon TrayIcon()
        {
            using (var bmp = Mark(32)) return Icon.FromHandle(bmp.GetHicon());
        }

        /// <summary>Writes a multi-size .ico (PNG-compressed entries) for the build script.</summary>
        public static void WriteIcon(string path)
        {
            var sizes = new[] { 16, 24, 32, 48, 64, 256 };
            var images = sizes.Select(size =>
            {
                using (var bmp = Mark(size))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
            }).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }
    }
}
