using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TheCloser
{
    public sealed class SessionLine
    {
        public string Speaker { get; set; }
        public string Text { get; set; }
        public DateTime Time { get; set; }
    }

    /// <summary>One question and its answer.</summary>
    public sealed class QaItem
    {
        public string Question { get; set; }
        public string Answer { get; set; }
        public string Kind { get; set; }
        public string Error { get; set; }
        public string Footer { get; set; }
        public string Model { get; set; }
        public DateTime Time { get; set; }

        /// <summary>Text being streamed right now (null once finished).</summary>
        [ScriptIgnore] public StringBuilder Live;
        [ScriptIgnore] public volatile bool Dirty;

        [ScriptIgnore]
        public bool Streaming { get { return Live != null; } }

        [ScriptIgnore]
        public string CurrentText
        {
            get
            {
                var live = Live;
                if (live == null) return Answer ?? "";
                lock (live) return live.ToString();
            }
        }
    }

    /// <summary>A call: transcript plus every Q&A, saved to disk as it goes.</summary>
    public sealed class Session
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public bool TitleSetByUser { get; set; }
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public string PromptId { get; set; }
        public double ElapsedSeconds { get; set; }
        public List<SessionLine> Lines { get; set; }
        public List<QaItem> Qas { get; set; }

        public Session()
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Title = "New session";
            Created = DateTime.Now;
            Updated = DateTime.Now;
            Lines = new List<SessionLine>();
            Qas = new List<QaItem>();
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(Title).Append("\n\n").Append(Created.ToString("f")).Append("\n\n");
            if (Qas.Count > 0)
            {
                sb.Append("## Questions & answers\n\n");
                foreach (var q in Qas)
                    sb.Append("### ").Append(q.Question).Append("\n\n").Append(q.CurrentText.Trim()).Append("\n\n");
            }
            if (Lines.Count > 0)
            {
                sb.Append("## Transcript\n\n");
                foreach (var l in Lines)
                    sb.Append("**").Append(l.Speaker).Append("** (").Append(l.Time.ToString("HH:mm:ss")).Append("): ").Append(l.Text).Append("\n\n");
            }
            return sb.ToString();
        }

        public string ToPlainText()
        {
            var sb = new StringBuilder();
            sb.Append(Title).Append("\r\n").Append(Created.ToString("f")).Append("\r\n\r\n");
            foreach (var q in Qas)
                sb.Append("Q: ").Append(q.Question).Append("\r\nA: ").Append(q.CurrentText.Trim().Replace("\n", "\r\n")).Append("\r\n\r\n");
            if (Lines.Count > 0)
            {
                sb.Append("TRANSCRIPT\r\n");
                foreach (var l in Lines) sb.Append("[").Append(l.Time.ToString("HH:mm:ss")).Append("] ").Append(l.Speaker).Append(": ").Append(l.Text).Append("\r\n");
            }
            return sb.ToString();
        }
    }

    internal static class SessionStore
    {
        public static string Folder
        {
            get { return Path.Combine(AppSettings.Folder, "sessions"); }
        }

        private static string PathOf(string id)
        {
            return Path.Combine(Folder, id + ".json");
        }

        public static void Save(Session s)
        {
            if (s == null || (s.Lines.Count == 0 && s.Qas.Count == 0)) return; // don't litter history with empty sessions
            try
            {
                Directory.CreateDirectory(Folder);
                s.Updated = DateTime.Now;
                var tmp = PathOf(s.Id) + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(s), Encoding.UTF8);
                if (File.Exists(PathOf(s.Id))) File.Delete(PathOf(s.Id));
                File.Move(tmp, PathOf(s.Id));
            }
            catch { }
        }

        public static List<Session> All()
        {
            var list = new List<Session>();
            try
            {
                if (!Directory.Exists(Folder)) return list;
                foreach (var f in Directory.GetFiles(Folder, "*.json"))
                {
                    try
                    {
                        var s = Json.Deserialize<Session>(File.ReadAllText(f, Encoding.UTF8));
                        if (s == null) continue;
                        if (s.Lines == null) s.Lines = new List<SessionLine>();
                        if (s.Qas == null) s.Qas = new List<QaItem>();
                        list.Add(s);
                    }
                    catch { }
                }
            }
            catch { }
            return list.OrderByDescending(s => s.Updated).ToList();
        }

        public static void Delete(string id)
        {
            try { if (File.Exists(PathOf(id))) File.Delete(PathOf(id)); } catch { }
        }
    }
}
