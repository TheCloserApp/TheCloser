using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace TheCloser
{
    /// <summary>
    /// Reads Windows 11 Live Captions (free, on-device, captions everything the PC plays and optionally the mic)
    /// through UI Automation and turns its rolling caption text into transcript sentences.
    /// </summary>
    internal sealed class LiveCaptionsSource : ITranscriptSource
    {
        public event Action<TranscriptEvent> Transcript;
        public event Action<string> Status;
        public event Action<string, bool> Activity;

        private const string Speaker = "Live";
        private Thread _thread;
        private volatile bool _running;
        private string _lastStatus;

        // Caption diffing state.
        private string _last = "";
        private long _lastChangeMs;
        private bool _baseline = true;
        private readonly List<string> _committed = new List<string>();
        private string _timeoutCommitted;
        private string _partial = "";
        private bool _partialCommitted = true;
        private string _shownPartial = "";
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>Time source (overridable by the self-test).</summary>
        internal Func<long> Clock;

        private long Now() { return Clock != null ? Clock() : _clock.ElapsedMilliseconds; }

        public string Name { get { return "Windows Live Captions"; } }

        public static string ExePath
        {
            get
            {
                var p = Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe");
                if (File.Exists(p)) return p;
                var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "LiveCaptions.exe");
                return File.Exists(native) ? native : null;
            }
        }

        public static bool IsAvailable { get { return ExePath != null; } }

        public static bool IsRunning { get { return Process.GetProcessesByName("LiveCaptions").Length > 0; } }

        public static void Launch()
        {
            if (!IsRunning && ExePath != null) Process.Start(ExePath);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "live-captions" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(1500);
            _thread = null;
        }

        public void Dispose() { Stop(); }

        private void SetStatus(string s)
        {
            if (s == _lastStatus) return;
            _lastStatus = s;
            var h = Status;
            if (h != null) h(s);
        }

        private static AutomationElement FindWindow()
        {
            foreach (var p in Process.GetProcessesByName("LiveCaptions"))
            {
                var w = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, p.Id));
                if (w != null) return w;
            }
            return AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "LiveCaptionsDesktopWindow"));
        }

        private static AutomationElement FindById(AutomationElement root, string id)
        {
            return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        }

        private void Run()
        {
            try { Launch(); }
            catch (Exception ex)
            {
                SetStatus("Couldn't start Live Captions: " + ex.Message);
            }

            AutomationElement text = null;
            long nextLookup = 0;
            while (_running)
            {
                try
                {
                    if (text == null)
                    {
                        if (Now() < nextLookup) { Thread.Sleep(100); continue; }
                        nextLookup = Now() + 1000;
                        var win = FindWindow();
                        if (win == null)
                        {
                            if (!IsRunning) Launch();
                            SetStatus("Waiting for Live Captions to open...");
                            continue;
                        }
                        text = FindById(win, "CaptionsTextBlock");
                        if (text == null)
                        {
                            if (FindById(win, "PrivacyInfoTitleTextBlock") != null)
                                SetStatus("One-time setup: click \"Yes, continue\" in the Live Captions bar at the top of your screen.");
                            else
                                SetStatus("Live Captions is starting (the first run downloads speech files)...");
                            continue;
                        }
                        _baseline = true;
                        SetStatus("Listening via Windows Live Captions");
                    }
                    OnCaptionText(text.Current.Name);
                }
                catch (ElementNotAvailableException) { text = null; }
                catch (Exception ex)
                {
                    text = null;
                    SetStatus("Live Captions: " + ex.Message);
                }
                Thread.Sleep(200);
            }
        }

        private static readonly Regex Space = new Regex(@"\s+");
        private static readonly Regex SentenceBreak = new Regex(@"(?<=[.?!])\s+");
        private static readonly Regex NonWord = new Regex(@"[^\p{L}\p{N} ]+");

        /// <summary>Visible for self-test: feed successive snapshots of the caption box.</summary>
        internal void OnCaptionText(string raw)
        {
            string t = Space.Replace(raw ?? "", " ").Trim();
            long now = Now();
            if (t == _last)
            {
                // Speech paused: the trailing sentence is probably complete even without punctuation.
                if (!_partialCommitted && _partial.Length > 0 && now - _lastChangeMs > 1500)
                {
                    CommitIfNew(_partial, true);
                    _partialCommitted = true;
                    ShowPartial("");
                    Speaking(false);
                }
                return;
            }
            _last = t;
            _lastChangeMs = now;

            var sentences = new List<string>();
            foreach (var s in SentenceBreak.Split(t))
                if (s.Trim().Length > 0) sentences.Add(s.Trim());

            if (_baseline)
            {
                // Whatever was on screen before we started listening is not part of this session.
                foreach (var s in sentences) Remember(Norm(s));
                _baseline = false;
                _partial = "";
                _partialCommitted = true;
                return;
            }

            for (int i = 0; i < sentences.Count - 1; i++) CommitIfNew(sentences[i], false);
            string p = sentences.Count > 0 ? sentences[sentences.Count - 1] : "";
            if (p.Length == 0 || IsKnown(Norm(p)))
            {
                _partial = "";
                _partialCommitted = true;
                ShowPartial("");
            }
            else
            {
                _partial = p;
                _partialCommitted = false;
                ShowPartial(StripTimeoutPrefix(p));
                Speaking(true);
            }
        }

        private bool _speaking;

        private void Speaking(bool on)
        {
            if (on == _speaking) return;
            _speaking = on;
            var h = Activity;
            if (h != null) h(Speaker, on);
        }

        private void ShowPartial(string p)
        {
            if (p == _shownPartial) return;
            _shownPartial = p;
            var h = Transcript;
            if (h != null) h(new TranscriptEvent(Speaker, p, false));
        }

        private string StripTimeoutPrefix(string s)
        {
            if (_timeoutCommitted == null) return s;
            var n = Norm(s);
            if (!n.StartsWith(_timeoutCommitted)) return s;
            return DropWords(s, _timeoutCommitted.Split(' ').Length);
        }

        private void CommitIfNew(string s, bool byTimeout)
        {
            var n = Norm(s);
            if (IsKnown(n)) return;
            string emit = s;
            if (_timeoutCommitted != null && n.StartsWith(_timeoutCommitted))
                emit = DropWords(s, _timeoutCommitted.Split(' ').Length);
            Remember(n);
            _timeoutCommitted = byTimeout ? n : null;
            emit = emit.Trim();
            if (emit.Length == 0) return;
            var h = Transcript;
            if (h != null) h(new TranscriptEvent(Speaker, emit, true));
        }

        private bool IsKnown(string n)
        {
            if (n.Length == 0) return true;
            if (_timeoutCommitted != null && (_timeoutCommitted == n || _timeoutCommitted.StartsWith(n))) return true;
            for (int i = _committed.Count - 1, seen = 0; i >= 0 && seen < 30; i--, seen++)
            {
                var c = _committed[i];
                if (c == n || (n.Length >= 12 && c.Contains(n))) return true;
                if (seen < 8 && Similarity(c, n) >= 0.85) return true;
            }
            return false;
        }

        private void Remember(string n)
        {
            if (n.Length == 0) return;
            _committed.Add(n);
            if (_committed.Count > 120) _committed.RemoveRange(0, 40);
        }

        internal static string Norm(string s)
        {
            var lower = NonWord.Replace((s ?? "").ToLowerInvariant(), " ");
            return Space.Replace(lower, " ").Trim();
        }

        private static string DropWords(string s, int count)
        {
            var words = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (count >= words.Length) return "";
            var sb = new StringBuilder();
            for (int i = count; i < words.Length; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(words[i]);
            }
            return sb.ToString();
        }

        /// <summary>Word-level LCS similarity in [0, 1].</summary>
        private static double Similarity(string a, string b)
        {
            var x = a.Split(' ');
            var y = b.Split(' ');
            if (x.Length == 0 || y.Length == 0) return 0;
            if (x.Length > 80 || y.Length > 80) return 0;
            var dp = new int[x.Length + 1, y.Length + 1];
            for (int i = 1; i <= x.Length; i++)
                for (int j = 1; j <= y.Length; j++)
                    dp[i, j] = x[i - 1] == y[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            return 2.0 * dp[x.Length, y.Length] / (x.Length + y.Length);
        }
    }
}
