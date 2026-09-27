using System;
using System.Linq;

namespace TheCloser
{
    internal sealed class SpeechLanguage
    {
        public readonly string Code;       // ISO 639-1, "" = detect automatically
        public readonly string Name;
        public readonly string[] Scripts;  // writing systems a transcript in this language uses

        public SpeechLanguage(string code, string name, params string[] scripts)
        {
            Code = code;
            Name = name;
            Scripts = scripts;
        }
    }

    /// <summary>
    /// Languages you can lock transcription to (the "Language" picker). Whisper takes the code as a hard constraint.
    /// xAI Grok transcribes whatever it hears (its language parameter only formats numbers and currency), so the
    /// session also drops transcript text written in a script the chosen language doesn't use - Telugu or Devanagari
    /// text while English is picked, say. Indian languages also allow Latin, since they're routinely mixed with English.
    /// </summary>
    internal static class SpeechLanguages
    {
        public static readonly SpeechLanguage[] All =
        {
            new SpeechLanguage("", "Detect automatically"),
            new SpeechLanguage("en", "English", "Latin"),
            new SpeechLanguage("hi", "Hindi", "Devanagari", "Latin"),
            new SpeechLanguage("te", "Telugu", "Telugu", "Latin"),
            new SpeechLanguage("ta", "Tamil", "Tamil", "Latin"),
            new SpeechLanguage("kn", "Kannada", "Kannada", "Latin"),
            new SpeechLanguage("ml", "Malayalam", "Malayalam", "Latin"),
            new SpeechLanguage("mr", "Marathi", "Devanagari", "Latin"),
            new SpeechLanguage("bn", "Bengali", "Bengali", "Latin"),
            new SpeechLanguage("gu", "Gujarati", "Gujarati", "Latin"),
            new SpeechLanguage("pa", "Punjabi", "Gurmukhi", "Latin"),
            new SpeechLanguage("ur", "Urdu", "Arabic", "Latin"),
            new SpeechLanguage("es", "Spanish", "Latin"),
            new SpeechLanguage("fr", "French", "Latin"),
            new SpeechLanguage("de", "German", "Latin"),
            new SpeechLanguage("it", "Italian", "Latin"),
            new SpeechLanguage("pt", "Portuguese", "Latin"),
            new SpeechLanguage("nl", "Dutch", "Latin"),
            new SpeechLanguage("pl", "Polish", "Latin"),
            new SpeechLanguage("ru", "Russian", "Cyrillic"),
            new SpeechLanguage("uk", "Ukrainian", "Cyrillic"),
            new SpeechLanguage("tr", "Turkish", "Latin"),
            new SpeechLanguage("ar", "Arabic", "Arabic"),
            new SpeechLanguage("zh", "Chinese", "Han"),
            new SpeechLanguage("ja", "Japanese", "Kana", "Han"),
            new SpeechLanguage("ko", "Korean", "Hangul"),
            new SpeechLanguage("id", "Indonesian", "Latin"),
            new SpeechLanguage("vi", "Vietnamese", "Latin"),
            new SpeechLanguage("th", "Thai", "Thai")
        };

        /// <summary>Codes xAI's speech API accepts for its number/currency formatting.</summary>
        private static readonly string[] XaiFormatting =
        {
            "ar", "cs", "da", "nl", "en", "fil", "fr", "de", "hi", "id", "it", "ja", "ko", "mk", "ms", "fa", "pl", "pt", "ro",
            "ru", "es", "sv", "th", "tr", "vi"
        };

        /// <summary>The picker entry for a code ("en", "en-US"), or null for one that isn't listed.</summary>
        public static SpeechLanguage Find(string code)
        {
            var c = (code ?? "").Trim().ToLowerInvariant();
            int dash = c.IndexOf('-');
            if (dash > 0) c = c.Substring(0, dash);
            return All.FirstOrDefault(l => l.Code == c);
        }

        public static string Name(string code)
        {
            var l = Find(code);
            return l != null ? l.Name : code.Trim();
        }

        public static bool XaiFormats(string code)
        {
            var l = Find(code);
            return l != null && l.Code.Length > 0 && XaiFormatting.Contains(l.Code);
        }

        /// <summary>True when the text is (mostly) written in the chosen language's script(s), or no language is chosen.</summary>
        public static bool Matches(string code, string text)
        {
            var lang = Find(code);
            if (lang == null || lang.Scripts.Length == 0 || string.IsNullOrEmpty(text)) return true;
            int total = 0, ok = 0;
            foreach (var c in text)
            {
                var script = Script(c);
                if (script == null) continue;
                total++;
                if (Array.IndexOf(lang.Scripts, script) >= 0) ok++;
            }
            return total == 0 || ok * 2 >= total;
        }

        /// <summary>The writing system of a character, or null for digits, punctuation and spaces.</summary>
        internal static string Script(char c)
        {
            if (c < 0x0250) return char.IsLetter(c) ? "Latin" : null;
            if (c >= 0x1E00 && c <= 0x1EFF) return "Latin"; // Vietnamese and other accented Latin
            if (c >= 0x0400 && c <= 0x052F) return "Cyrillic";
            if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F) || (c >= 0xFB50 && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF)) return "Arabic";
            if (c >= 0x0900 && c <= 0x097F) return "Devanagari";
            if (c >= 0x0980 && c <= 0x09FF) return "Bengali";
            if (c >= 0x0A00 && c <= 0x0A7F) return "Gurmukhi";
            if (c >= 0x0A80 && c <= 0x0AFF) return "Gujarati";
            if (c >= 0x0B80 && c <= 0x0BFF) return "Tamil";
            if (c >= 0x0C00 && c <= 0x0C7F) return "Telugu";
            if (c >= 0x0C80 && c <= 0x0CFF) return "Kannada";
            if (c >= 0x0D00 && c <= 0x0D7F) return "Malayalam";
            if (c >= 0x0E00 && c <= 0x0E7F) return "Thai";
            if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x31F0 && c <= 0x31FF) || (c >= 0xFF66 && c <= 0xFF9F)) return "Kana";
            if ((c >= 0x1100 && c <= 0x11FF) || (c >= 0x3130 && c <= 0x318F) || (c >= 0xAC00 && c <= 0xD7AF)) return "Hangul";
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF)) return "Han";
            return char.IsLetter(c) ? "Other" : null;
        }
    }
}
