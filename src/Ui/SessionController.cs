using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TheCloser.Ui
{
    /// <summary>
    /// Runs an interview session: the transcript source, question detection and auto-answers, answer streaming and
    /// saving. Everything here runs on the UI dispatcher; the UI subscribes to the events.
    /// </summary>
    internal sealed class SessionController
    {
        private readonly AppSettings S;
        private readonly Dispatcher D;
        private readonly SubscriptionClient Billing;
        private ITranscriptSource _src;
        private readonly DispatcherTimer _auto;
        private bool _pendingQuestion;
        private string _pendingText;                 // the question the gate picked out
        private DateTime _pendingSince, _lastHeard;
        private CancellationTokenSource _answerCts, _gateCts;
        private string _lastGated;
        private bool _themHeard;                     // the other side has spoken; until then the user's own questions count
        private bool _saidOtherLanguage;
        private DateTime _resumedAt;

        public Session Current { get; private set; }
        public bool Active { get; private set; }     // a live session (listening or paused)
        public bool Paused { get; private set; }
        public bool Speaking { get; private set; }
        public string SourceName { get; private set; }
        public readonly Dictionary<string, string> Partials = new Dictionary<string, string>();

        public event Action StateChanged;
        public event Action TranscriptChanged;
        public event Action QasChanged;
        public event Action<string, bool> Status;
        public event Action NeedsKey;

        public SessionController(AppSettings settings, Dispatcher dispatcher, SubscriptionClient billing = null)
        {
            S = settings;
            D = dispatcher;
            Billing = billing;
            _auto = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
            _auto.Tick += delegate { AutoTick(); };
        }

        private void Raise(Action a) { if (a != null) a(); }
        private void Say(string text, bool problem) { var h = Status; if (h != null) h(text, problem); }

        public TimeSpan Elapsed
        {
            get
            {
                if (Current == null) return TimeSpan.Zero;
                double s = Current.ElapsedSeconds;
                if (Active && !Paused) s += (DateTime.UtcNow - _resumedAt).TotalSeconds;
                return TimeSpan.FromSeconds(s);
            }
        }

        // --- Lifecycle -------------------------------------------------------------------------

        public void Begin(Session session)
        {
            if (Active) End();
            Current = session;
            if (string.IsNullOrEmpty(Current.PromptId)) Current.PromptId = S.PromptId;
            _themHeard = Current.Lines.Any(l => l.Speaker != "Me");
            _saidOtherLanguage = false;
            ClearPending();
            Active = true;
            Paused = false;
            _resumedAt = DateTime.UtcNow;
            StartSource();
            Raise(StateChanged);
            Raise(QasChanged);
            Raise(TranscriptChanged);
        }

        /// <summary>Shows a saved session without listening.</summary>
        public void Open(Session session)
        {
            if (Active) End();
            Current = session;
            Raise(StateChanged);
            Raise(QasChanged);
            Raise(TranscriptChanged);
        }

        public void Pause()
        {
            if (!Active || Paused) return;
            Current.ElapsedSeconds += (DateTime.UtcNow - _resumedAt).TotalSeconds;
            Paused = true;
            ClearPending();
            StopSource();
            SessionStore.Save(Current);
            Raise(StateChanged);
            Say("Interview paused.", false);
        }

        public void Resume()
        {
            if (!Active || !Paused) return;
            Paused = false;
            _resumedAt = DateTime.UtcNow;
            StartSource();
            Raise(StateChanged);
        }

        public void End()
        {
            if (!Active) return;
            CancelAnswer();
            ClearPending();
            if (!Paused) Current.ElapsedSeconds += (DateTime.UtcNow - _resumedAt).TotalSeconds;
            StopSource();
            Active = false;
            Paused = false;
            Speaking = false;
            SessionStore.Save(Current);
            Raise(StateChanged);
        }

        /// <summary>Re-opens the transcript source after the audio source or engine changed.</summary>
        public void RestartSource()
        {
            if (!Active || Paused) return;
            StopSource();
            StartSource();
            Raise(StateChanged);
        }

        public void Rename(string title)
        {
            if (Current == null) return;
            Current.Title = title;
            Current.TitleSetByUser = true;
            SessionStore.Save(Current);
            Raise(StateChanged);
        }

        public void DeleteCurrent()
        {
            if (Current == null) return;
            if (Active) End();
            SessionStore.Delete(Current.Id);
            Current = null;
            Raise(StateChanged);
            Raise(QasChanged);
            Raise(TranscriptChanged);
        }

        public void Save()
        {
            if (Current != null) SessionStore.Save(Current);
        }

        // --- Transcript source -------------------------------------------------------------------

        private void StartSource()
        {
            if (_src != null) return;
            ITranscriptSource src;
            var engine = S.EffectiveTranscription;
            if (engine == "ElevenLabs") src = new ElevenLabsStreamingSource(S.Clone());
            else if (engine == "Grok") src = new GrokStreamingSource(S.Clone());
            else
            {
                if (!LiveCaptionsSource.IsAvailable) { Say("Windows Live Captions isn't available on this PC. Add an ElevenLabs key in Settings > AI.", true); return; }
                src = new LiveCaptionsSource();
            }
            src.Transcript += ev => D.BeginInvoke((Action)(() => OnTranscript(ev)));
            src.Status += st => D.BeginInvoke((Action)(() => Say(st, LooksLikeProblem(st))));
            src.Activity += (speaker, on) => D.BeginInvoke((Action)(() => OnActivity(speaker, on)));
            try
            {
                src.Start();
                _src = src;
                SourceName = src.Name;
            }
            catch (Exception ex)
            {
                Say("Couldn't start listening: " + ex.Message, true);
            }
        }

        private void StopSource()
        {
            var src = _src;
            _src = null;
            Partials.Clear();
            Speaking = false;
            if (src != null) Task.Run(() => { try { src.Dispose(); } catch { } });
            Raise(TranscriptChanged);
        }

        private static bool LooksLikeProblem(string s)
        {
            var l = (s ?? "").ToLowerInvariant();
            return l.Contains("can't") || l.Contains("error") || l.Contains("failed") || l.Contains("setup") || l.Contains("couldn't") ||
                   l.Contains("rejected") || l.Contains("invalid") || l.Contains("reconnecting");
        }

        private void OnActivity(string speaker, bool on)
        {
            if (speaker == "Me" || _src == null) return;
            Speaking = on;
            Raise(StateChanged);
        }

        /// <summary>
        /// Whose questions get answered: the other side's ([Them], or [Live] from Live Captions). Until they've said anything
        /// - you're testing on your own, in person, or listening to the mic only - your own [Me] questions count too.
        /// </summary>
        private bool IsAsker(string speaker)
        {
            return speaker != "Me" || !_themHeard;
        }

        private void OnTranscript(TranscriptEvent ev)
        {
            if (_src == null || Current == null) return;
            var text = (ev.Text ?? "").Trim();
            if (!SpeechLanguages.Matches(S.SpeechLanguage, text))
            {
                // Speech in another language than the one picked for the call: it isn't transcribed.
                Partials.Remove(ev.Speaker);
                if (ev.IsFinal && !_saidOtherLanguage)
                {
                    _saidOtherLanguage = true;
                    Say("Ignoring speech that isn't " + SpeechLanguages.Name(S.SpeechLanguage) + ".", false);
                }
                Raise(TranscriptChanged);
                return;
            }
            if (!ev.IsFinal)
            {
                Partials[ev.Speaker] = ev.Text ?? "";
                if (IsAsker(ev.Speaker) && text.Length > 0)
                {
                    _lastHeard = DateTime.UtcNow;
                    if (text.EndsWith("?") && text != _lastGated) CheckLine(ev.Speaker, text); // start early on an obvious question
                    KickAutoTimer(); // still talking: hold any pending answer
                }
                Raise(TranscriptChanged);
                return;
            }
            Partials.Remove(ev.Speaker);
            Current.Lines.Add(new SessionLine { Speaker = ev.Speaker, Text = ev.Text, Time = DateTime.Now });
            if (ev.Speaker != "Me") _themHeard = true;
            if (IsAsker(ev.Speaker) && text.Length > 0)
            {
                _lastHeard = DateTime.UtcNow;
                if (text != _lastGated) CheckLine(ev.Speaker, text);
                KickAutoTimer();
            }
            Raise(TranscriptChanged);
        }

        /// <summary>Asks the small model whether the newest line is worth answering; a newer line supersedes an older check.</summary>
        private void CheckLine(string speaker, string text)
        {
            if (!S.AutoGenerate || !Active || Paused) return;
            // "Okay." "Right." - not worth a check, and it shouldn't cancel the check for the question before it.
            if (text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length < 2 && !text.EndsWith("?")) return;
            _lastGated = text;
            if (_gateCts != null) _gateCts.Cancel();

            var cts = new CancellationTokenSource();
            _gateCts = cts;
            var settings = S.Clone();
            var recent = RecentText(8);
            bool mineCount = speaker == "Me";
            Task.Run(async () =>
            {
                GateVerdict v;
                try { v = await QuestionGate.CheckAsync(settings, recent, mineCount, cts.Token, Billing).ConfigureAwait(false); }
                catch (Exception)
                {
                    if (cts.IsCancellationRequested) return;
                    // No key for the check, or it failed: fall back to the keyword heuristic so answers still come.
                    v = new GateVerdict { Answer = QuestionDetector.LooksLikeQuestion(text), Question = text };
                }
                D.BeginInvoke((Action)(() => OnVerdict(v, cts)));
            });
        }

        private void OnVerdict(GateVerdict v, CancellationTokenSource cts)
        {
            if (_gateCts != cts) return; // superseded by newer speech
            _gateCts = null;
            if (!v.Answer || !Active || Paused || !S.AutoGenerate) return;
            _pendingText = v.Question;
            if (!_pendingQuestion) _pendingSince = DateTime.UtcNow;
            _pendingQuestion = true;
            KickAutoTimer();
        }

        private void ClearPending()
        {
            _auto.Stop();
            _pendingQuestion = false;
            _pendingText = null;
            _lastGated = null;
            if (_gateCts != null) _gateCts.Cancel();
            _gateCts = null;
        }

        /// <summary>Answers once the speaker has paused for the auto-answer delay, but never waits more than ~2 s past it.</summary>
        private void KickAutoTimer()
        {
            if (!_pendingQuestion || !S.AutoGenerate) return;
            int delay = Math.Max(250, S.AutoAnswerDelayMs);
            var now = DateTime.UtcNow;
            double quiet = (now - _lastHeard).TotalMilliseconds;
            double waited = (now - _pendingSince).TotalMilliseconds;
            double wait = Math.Min(delay - quiet, delay + 2200 - waited);
            _auto.Stop();
            _auto.Interval = TimeSpan.FromMilliseconds(Math.Max(60, wait));
            _auto.Start();
        }

        private void AutoTick()
        {
            _auto.Stop();
            if (!_pendingQuestion || !Active || Paused || !S.AutoGenerate) return;
            var question = _pendingText;
            _pendingQuestion = false;
            _pendingText = null;
            Answer(AnswerKind.Auto, question, null);
        }

        /// <summary>The last few lines (plus live text) for the question check.</summary>
        private string RecentText(int lines)
        {
            var sb = new StringBuilder();
            var all = Current.Lines;
            for (int i = Math.Max(0, all.Count - lines); i < all.Count; i++)
                sb.Append('[').Append(all[i].Speaker).Append("] ").Append(all[i].Text).Append('\n');
            foreach (var p in OpenPartials())
                sb.Append('[').Append(p.Key).Append("] ").Append(p.Value).Append('\n');
            return sb.ToString();
        }

        /// <summary>Latest transcript (last ~12k characters) in the [Speaker] text format the prompts expect.</summary>
        public string TranscriptText()
        {
            if (Current == null) return "";
            var sb = new StringBuilder();
            var lines = Current.Lines;
            for (int i = Math.Max(0, lines.Count - 80); i < lines.Count; i++)
                sb.Append('[').Append(lines[i].Speaker).Append("] ").Append(lines[i].Text).Append('\n');
            foreach (var p in OpenPartials())
                sb.Append('[').Append(p.Key).Append("] ").Append(p.Value).Append('\n');
            var text = sb.ToString();
            return text.Length > 12000 ? text.Substring(text.Length - 12000) : text;
        }

        public List<KeyValuePair<string, string>> OpenPartials()
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var speaker in new[] { "Them", "Live", "Me" })
            {
                string text;
                if (Partials.TryGetValue(speaker, out text) && !string.IsNullOrWhiteSpace(text))
                    list.Add(new KeyValuePair<string, string>(speaker, text));
            }
            return list;
        }

        /// <summary>The newest thing heard (live text first) - shown in the listening bar.</summary>
        public string LatestHeard()
        {
            var partials = OpenPartials();
            if (partials.Count > 0) return partials[0].Value;
            if (Current != null && Current.Lines.Count > 0)
            {
                var last = Current.Lines[Current.Lines.Count - 1];
                if ((DateTime.Now - last.Time).TotalSeconds < 6) return last.Text;
            }
            return null;
        }

        private string LastQuestion()
        {
            foreach (var p in OpenPartials())
                if (IsAsker(p.Key)) return p.Value;
            if (Current != null)
                for (int i = Current.Lines.Count - 1; i >= 0; i--)
                    if (IsAsker(Current.Lines[i].Speaker)) return Current.Lines[i].Text;
            return null;
        }

        private static string Clip(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";
        }

        // --- Answers ---------------------------------------------------------------------------

        public void Answer(AnswerKind kind, string userText, string screenshot)
        {
            if (Current == null) return;
            if (!S.UseSubscription && ModelCatalog.Resolve(S, S.Model).Provider == null)
            {
                Say(ModelCatalog.Resolve(S, S.Model).Missing.Replace(" Click to open API keys.", ""), true);
                Raise(NeedsKey);
                return;
            }
            CancelAnswer();
            _pendingQuestion = false;
            _pendingText = null;
            _auto.Stop();

            // Auto: the gate's cleaned-up question. Manual: the model finds the question itself, unless only your own
            // lines exist (testing alone / in person) - then it's told which one, or it would wait for the "other side".
            string focus = null;
            if (kind == AnswerKind.Auto) focus = userText;
            else if (kind == AnswerKind.Manual && !_themHeard) focus = LastQuestion();

            string question;
            switch (kind)
            {
                case AnswerKind.Ask: question = Clip(userText, 300); break;
                case AnswerKind.Screen: question = "Screen analysis" + (userText != null ? " · " + Clip(userText, 160) : ""); break;
                default: question = Clip(focus ?? LastQuestion() ?? "Answer the latest question", 300); break;
            }

            var req = new AnswerRequest
            {
                SystemPrompt = Prompts.SystemText(S, Current.PromptId ?? S.PromptId),
                Transcript = TranscriptText(),
                Kind = kind,
                Question = focus,
                UserText = kind == AnswerKind.Ask || kind == AnswerKind.Screen ? userText : null,
                ScreenshotJpegBase64 = screenshot,
                Effort = S.Effort
            };
            AddMemory(req);

            var qa = new QaItem { Question = question, Kind = kind.ToString(), Time = DateTime.Now, Model = S.Model, Live = new StringBuilder() };
            Current.Qas.Add(qa);
            Raise(QasChanged); // the new card shows animated dots until the first words arrive

            var settings = S.Clone();
            var cts = new CancellationTokenSource();
            _answerCts = cts;
            Task.Run(async () =>
            {
                try
                {
                    PromptBuilder.AddContext(req, settings);
                    var result = await AnswerEngine.StreamAsync(settings, req, delegate(string t)
                    {
                        var live = qa.Live;
                        if (live == null) return;
                        lock (live) live.Append(t);
                        qa.Dirty = true;
                    }, cts.Token, Billing).ConfigureAwait(false);
                    D.BeginInvoke((Action)(() => Finish(qa, result, cts)));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    D.BeginInvoke((Action)(() => Fail(qa, ex, cts)));
                }
            });
        }

        /// <summary>
        /// Earlier answers the request replays (Settings > Memory): the last few from this session, or all of them, and
        /// optionally the latest answer from each of the three most recent other sessions. Same rules as the Mac app.
        /// </summary>
        private void AddMemory(AnswerRequest req)
        {
            if (!S.ReplayTurns) return;
            var done = Current.Qas.Where(q => !q.Streaming && q.Error == null).ToList();
            if (!S.ReplayAllTurns && done.Count > S.ReplayTurnCount)
            {
                // Keep the opening exchange: it carries the setup context the whole session leans on.
                var recent = done.Skip(done.Count - S.ReplayTurnCount).ToList();
                recent.Insert(0, done[0]);
                done = recent;
            }
            req.Recent.AddRange(done);
            if (!S.PullPastSessions) return;
            foreach (var other in SessionStore.All().Where(o => o.Id != Current.Id && o.Qas.Count > 0).Take(3))
            {
                var last = other.Qas.LastOrDefault(q => q.Error == null && !string.IsNullOrEmpty(q.Answer));
                if (last != null) req.Past.Add(last);
            }
        }

        private void Close(QaItem qa)
        {
            qa.Answer = qa.CurrentText;
            qa.Live = null;
            qa.Dirty = true;
        }

        private void Finish(QaItem qa, StreamResult r, CancellationTokenSource cts)
        {
            Close(qa);
            if (r.StopReason == "refusal") qa.Footer = "The model declined to answer this one.";
            else if (r.StopReason == "max_tokens") qa.Footer = "(Answer was cut off.)";
            if (_answerCts == cts) _answerCts = null;
            if (Current != null && !Current.TitleSetByUser && Current.Title == "New session" && qa.Kind != "Screen")
                Current.Title = Clip(qa.Question.TrimEnd('?', '.', ' '), 48);
            Save();
            Raise(QasChanged);
            Raise(StateChanged);
        }

        private void Fail(QaItem qa, Exception ex, CancellationTokenSource cts)
        {
            if (cts.IsCancellationRequested) return;
            Close(qa);
            qa.Error = ex.Message;
            if (_answerCts == cts) _answerCts = null;
            Save();
            Raise(QasChanged);
            Say(ex.Message, true);
        }

        public bool Answering { get { return _answerCts != null; } }

        public void CancelAnswer()
        {
            var cts = _answerCts;
            _answerCts = null;
            if (cts == null) return;
            cts.Cancel();
            if (Current != null)
                foreach (var q in Current.Qas.Where(q => q.Streaming))
                {
                    Close(q);
                    if (string.IsNullOrEmpty(q.Answer)) q.Footer = "(stopped)";
                }
            Raise(QasChanged);
        }
    }
}
