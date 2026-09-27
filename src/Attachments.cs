using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace TheCloser
{
    /// <summary>A file the user attached: sent as a PDF/image block, or inlined as text.</summary>
    internal sealed class Attachment
    {
        public string Name;
        public string Kind;       // pdf | image | text
        public string MediaType;
        public string Base64;
        public string Text;
    }

    internal static class Attachments
    {
        private static readonly Dictionary<string, KeyValuePair<DateTime, Attachment>> Cache =
            new Dictionary<string, KeyValuePair<DateTime, Attachment>>(StringComparer.OrdinalIgnoreCase);

        public const string DialogFilter =
            "Documents (*.pdf;*.docx;*.txt;*.md)|*.pdf;*.docx;*.txt;*.md;*.markdown;*.rtf;*.csv;*.json|" +
            "Images (*.png;*.jpg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp|All files (*.*)|*.*";

        /// <summary>Loads (and caches) a file; returns null if it's missing, too large or unreadable.</summary>
        public static Attachment Load(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                var info = new FileInfo(path);
                lock (Cache)
                {
                    KeyValuePair<DateTime, Attachment> hit;
                    if (Cache.TryGetValue(path, out hit) && hit.Key == info.LastWriteTimeUtc) return hit.Value;
                }
                var a = Read(path, info);
                if (a != null) lock (Cache) Cache[path] = new KeyValuePair<DateTime, Attachment>(info.LastWriteTimeUtc, a);
                return a;
            }
            catch { return null; }
        }

        /// <summary>Why a file can't be used, or null if it's fine.</summary>
        public static string Problem(string path)
        {
            if (!File.Exists(path)) return "File not found.";
            long len = new FileInfo(path).Length;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pdf" && len > 24L * 1024 * 1024) return "PDFs must be under 24 MB.";
            if (IsImage(ext) && len > 5L * 1024 * 1024) return "Images must be under 5 MB.";
            if (ext == ".doc") return "Old .doc files aren't supported - save it as .docx or PDF.";
            if (Load(path) == null) return "TheCloser couldn't read this file.";
            return null;
        }

        private static bool IsImage(string ext)
        {
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp";
        }

        private static Attachment Read(string path, FileInfo info)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var name = Path.GetFileName(path);
            if (ext == ".pdf")
            {
                if (info.Length > 24L * 1024 * 1024) return null;
                return new Attachment { Name = name, Kind = "pdf", MediaType = "application/pdf", Base64 = Convert.ToBase64String(File.ReadAllBytes(path)) };
            }
            if (IsImage(ext))
            {
                if (info.Length > 5L * 1024 * 1024) return null;
                string mt = ext == ".png" ? "image/png" : ext == ".gif" ? "image/gif" : ext == ".webp" ? "image/webp" : "image/jpeg";
                return new Attachment { Name = name, Kind = "image", MediaType = mt, Base64 = Convert.ToBase64String(File.ReadAllBytes(path)) };
            }
            if (ext == ".docx") return new Attachment { Name = name, Kind = "text", Text = DocxText(path) };
            if (ext == ".doc") return null;
            if (info.Length > 1024 * 1024) return null;
            return new Attachment { Name = name, Kind = "text", Text = File.ReadAllText(path) };
        }

        /// <summary>Plain text of a Word document (paragraphs from word/document.xml).</summary>
        private static string DocxText(string path)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) return "";
                var doc = new XmlDocument();
                using (var s = entry.Open()) doc.Load(s);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
                var sb = new StringBuilder();
                foreach (XmlNode p in doc.SelectNodes("//w:p", ns))
                {
                    foreach (XmlNode t in p.SelectNodes(".//w:t|.//w:tab", ns))
                        sb.Append(t.LocalName == "tab" ? "\t" : t.InnerText);
                    sb.Append('\n');
                }
                return sb.ToString();
            }
        }
    }
}
