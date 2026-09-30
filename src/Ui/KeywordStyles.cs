using System;
using System.Windows.Media;

namespace TheCloser.Ui
{
    /// <summary>
    /// How the keywords the model bolds in answers stand out: coloured text or a highlighter background, or "Off" for plain
    /// text. Picked in Settings > General > Keywords or the ... menu; the same styles as the Mac app.
    /// </summary>
    internal static class KeywordStyles
    {
        public const string Standard = "lightBlue";
        public const string Off = "off";
        public static readonly string[] Ids = { "off", "bold", "blue", "lightBlue", "yellow", "blueHighlight", "yellowHighlight", "greenHighlight", "pinkHighlight" };

        /// <summary>Shown under the picker in Settings.</summary>
        public const string Sample = "Use **PostgreSQL** here, since payments need **strong consistency**.";

        private static readonly Brush Brand = U.B(0xFF3B8EFF);
        private static readonly Brush LightBlue = U.B(0xFF7AB4FF);
        private static readonly Brush Yellow = U.B(0xFFFFD479);
        private static readonly Brush Ink = U.B(0xFF0A0A0A);
        private static readonly Brush BlueWash = U.B(0x523B8EFF);
        private static readonly Brush GreenWash = U.B(0x5234C759);
        private static readonly Brush PinkWash = U.B(0x52FF375F);

        public static bool IsKnown(string id)
        {
            return id != null && Array.IndexOf(Ids, id) >= 0;
        }

        public static string Name(string id)
        {
            switch (id)
            {
                case "off": return "Off";
                case "bold": return "Bold only";
                case "blue": return "Blue";
                case "yellow": return "Yellow";
                case "blueHighlight": return "Blue highlight";
                case "yellowHighlight": return "Yellow highlight";
                case "greenHighlight": return "Green highlight";
                case "pinkHighlight": return "Pink highlight";
                default: return "Light blue";
            }
        }

        /// <summary>The keyword's text colour; null keeps the body colour.</summary>
        public static Brush Foreground(string id)
        {
            switch (id)
            {
                case "blue": return Brand;
                case "yellow": return Yellow;
                case "yellowHighlight": return Ink;
                case "off":
                case "bold":
                case "blueHighlight":
                case "greenHighlight":
                case "pinkHighlight": return null;
                default: return LightBlue;
            }
        }

        /// <summary>The colour behind the keyword, like a highlighter pen; null for the text-colour styles.</summary>
        public static Brush Background(string id)
        {
            switch (id)
            {
                case "blueHighlight": return BlueWash;
                case "yellowHighlight": return Yellow;
                case "greenHighlight": return GreenWash;
                case "pinkHighlight": return PinkWash;
                default: return null;
            }
        }

        public static bool IsHighlight(string id)
        {
            return Background(id) != null;
        }
    }
}
