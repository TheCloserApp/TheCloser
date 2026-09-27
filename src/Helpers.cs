using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TheCloser
{
    /// <summary>Cheap heuristic for "the other person just asked something", used to trigger auto-answers.</summary>
    internal static class QuestionDetector
    {
        private static readonly string[] Starters =
        {
            "what", "what's", "whats", "why", "how", "how's", "when", "where", "who", "whom", "whose", "which",
            "can you", "could you", "would you", "will you", "do you", "did you", "does", "have you", "are you",
            "were you", "is there", "is it", "is that", "should", "shall", "may i ask", "tell me", "tell us", "describe",
            "explain", "walk me through", "walk us through", "give me", "give us", "talk about", "talk me through",
            "share", "let's talk about", "imagine", "suppose", "say you", "design", "write a", "write me", "implement",
            "compare", "define", "any questions", "do we", "can we", "could we", "is this", "are there"
        };

        public static bool LooksLikeQuestion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim().ToLowerInvariant();
            if (s.EndsWith("?")) return true;
            if (s.Split(' ').Length < 3) return false;
            foreach (var st in Starters)
            {
                if (s.StartsWith(st + " ") || s.Contains(", " + st + " ") || s.Contains(". " + st + " ") || s.Contains(" so " + st + " "))
                    return true;
            }
            return false;
        }
    }

    internal static class ScreenGrab
    {
        /// <summary>Captures the monitor the user is working on, downscaled to at most 1568px, as base64 JPEG.</summary>
        public static string CaptureJpegBase64(IntPtr ownWindow)
        {
            Screen screen;
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != ownWindow) screen = Screen.FromHandle(fg);
            else screen = Screen.FromPoint(Cursor.Position);
            var b = screen.Bounds;

            using (var full = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(full))
                    g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);

                const int maxEdge = 1568;
                double scale = Math.Min(1.0, maxEdge / (double)Math.Max(b.Width, b.Height));
                int w = Math.Max(1, (int)(b.Width * scale)), h = Math.Max(1, (int)(b.Height * scale));
                using (var small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(full, 0, 0, w, h);
                    }
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (var ps = new EncoderParameters(1))
                    using (var ms = new MemoryStream())
                    {
                        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                        small.Save(ms, codec, ps);
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
        }
    }
}
