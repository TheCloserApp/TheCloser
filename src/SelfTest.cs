using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TheCloser.Ui;

namespace TheCloser
{
    /// <summary>
    /// Headless checks: TheCloser.exe --selftest [log-file] [--audio] [--api] [--ci]
    ///   --audio  plays a short, quiet speech clip and verifies speaker-loopback capture + phrase detection
    ///   --api    calls OpenRouter (needs a key in settings or OPENROUTER_API_KEY) and xAI
    ///   --ci     skips the Live Captions installation check on Windows Server build agents
    /// </summary>
    internal static class SelfTest
    {
        private static StringBuilder _log;
        private static int _failures;

        private static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            _log.Append(ok ? "PASS  " : "FAIL  ").Append(name);
            if (!string.IsNullOrEmpty(detail)) _log.Append("  -- ").Append(detail);
            _log.AppendLine();
        }

        private static void Note(string text)
        {
            _log.Append("      ").AppendLine(text);
        }

        public static int Run(string[] args)
        {
            _log = new StringBuilder();
            _failures = 0;
            string logPath = args.Length > 1 && args[1].Length > 0 && !args[1].StartsWith("--") ? args[1] : Path.Combine(Path.GetTempPath(), "thecloser-selftest.txt");
            bool audio = args.Contains("--audio");
            bool api = args.Contains("--api");
            bool ci = args.Contains("--ci");

            Try("settings + JSON round trip", TestSettings);
            Try("subscription client", delegate
            {
                SubscriptionSelfTest.Run(delegate(bool ok, string name) { Check(name, ok, null); });
            });
            Try("prompts", TestPrompts);
            Try("required keys and transcription engine", TestKeys);
            Try("prompt builder", TestPrompt);
            Try("markdown rendering", TestMarkdown);
            Try("keyword styles", TestKeywordStyles);
            Try("question detector", TestQuestions);
            Try("live captions diffing", TestCaptionDiff);
            Try("segmenter (synthetic audio)", TestSegmenter);
            Try("xAI Grok streaming protocol", TestGrokProtocol);
            Try("ElevenLabs streaming protocol", TestElevenLabsProtocol);
            if (api) Try("xAI Grok live connection", TestGrokLive);
            Try("live captions availability", delegate
            {
                if (ci) Note("skipped: Live Captions is an optional Windows 11 feature, unavailable on Windows Server CI");
                else Check("LiveCaptions.exe present", LiveCaptionsSource.IsAvailable, LiveCaptionsSource.ExePath);
            });
            if (audio) Try("speaker loopback capture", TestLoopback);
            if (audio) Try("microphone capture", TestMic);
            if (api) Try("OpenRouter answers", TestApi);

            _log.AppendLine();
            _log.AppendLine(_failures == 0 ? "ALL CHECKS PASSED" : _failures + " CHECK(S) FAILED");
            File.WriteAllText(logPath, _log.ToString(), Encoding.UTF8);
            return _failures;
        }

        private static void Try(string name, Action test)
        {
            _log.AppendLine("== " + name);
            try { test(); }
            catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>Assembles an answer request the way SessionController does, so the prompt builder can be exercised.</summary>
        private static AnswerRequest MakeRequest(AppSettings s, string transcript, AnswerKind kind, string userText, string screenshot)
        {
            var req = new AnswerRequest
            {
                SystemPrompt = Prompts.SystemText(s, s.PromptId),
                Transcript = transcript,
                Kind = kind,
                UserText = userText,
                ScreenshotJpegBase64 = screenshot,
                Effort = s.Effort
            };
            PromptBuilder.AddContext(req, s);
            return req;
        }

        private static void TestSettings()
        {
            var s = new AppSettings();
            s.OpenRouterKey = "sk-or-test-123";
            s.ElevenLabsKey = "sk_eleven-test-456";
            s.Context = "Line 1\nLine \"2\" with unicode \u00E9\u2013";
            s.Files.Add(@"C:\x\resume.pdf");
            var json = Json.Serialize(s);
            Check("plain keys not in JSON", !json.Contains("sk-or-test-123") && !json.Contains("sk_eleven-test-456"), null);
            var back = Json.Deserialize<AppSettings>(json);
            Check("keys decrypt", back.OpenRouterKey == "sk-or-test-123" && back.ElevenLabsKey == "sk_eleven-test-456", null);
            Check("context survives", back.Context == s.Context, null);
            Check("files survive", back.Files.Count == 1 && back.Files[0] == @"C:\x\resume.pdf", null);
            var d = AppSettings.Defaults();
            Check("defaults match the Mac", d.Model == "anthropic/claude-sonnet-5" && d.PromptId == "" && d.SpeechLanguage == "en" &&
                  d.BackgroundPct == 60 && d.TextSizePct == 100 && d.KeywordStyle == "lightBlue" && d.HideFromCapture && d.AutoGenerate &&
                  d.ReplayTurns && !d.ReplayAllTurns && d.ReplayTurnCount == 6 && !d.PullPastSessions, d.Model);
            Check("the Mac's ten models", ModelCatalog.Curated.Length == 10 && d.EnabledModels.Count == 10, null);

            var old = Json.Deserialize<AppSettings>("{\"Model\":\"anthropic/claude-opus-5\",\"PromptId\":\"sales\",\"Transcription\":\"Whisper\",\"TextSizePct\":190,\"KeywordStyle\":\"x\"}").Clone();
            Check("old settings move to the new defaults", old.Model == "anthropic/claude-sonnet-5" && old.PromptId == "" &&
                  old.Transcription == "Automatic" && old.TextSizePct == 160 && old.KeywordStyle == "lightBlue", old.Model + " " + old.PromptId + " " + old.Transcription);

            var legacy = Json.Deserialize<AppSettings>("{\"Model\":\"claude-haiku-4-5\",\"EnabledModels\":[\"claude-haiku-4-5\",\"anthropic/claude-haiku-4.5\",\"claude-opus-5-5\"]}").Clone();
            Check("bare claude id normalized", legacy.Model == "anthropic/claude-haiku-4.5", legacy.Model);
            Check("normalized models deduped", legacy.EnabledModels.SequenceEqual(new[] { "anthropic/claude-haiku-4.5", "anthropic/claude-opus-5.5" }), string.Join(", ", legacy.EnabledModels));
            Check("no key, no route", ModelCatalog.Resolve(legacy, legacy.Model).Provider == null, null);
            legacy.OpenRouterKey = "sk-or-test";
            Check("every model routes through OpenRouter", ModelCatalog.Resolve(legacy, legacy.Model).Provider == "openrouter", null);
        }

        private static void TestPrompts()
        {
            var s = new AppSettings();
            Check("four built-ins, like the Mac", Prompts.All(s).Select(p => p.Name).SequenceEqual(new[] { "General", "Interview", "Meeting", "Call" }), null);
            Check("default is the interview prompt", Prompts.DisplayName(s, "") == "Default (Interview)" && Prompts.SystemText(s, "").Contains("real-time interview copilot"), null);
            Check("interview prompt keeps its own bold rule", !Prompts.SystemText(s, "interview").Contains(Prompts.KeywordRule), null);
            Check("a prompt without one gets the keyword rule", Prompts.SystemText(s, "meeting").EndsWith(Prompts.KeywordRule), null);
            Check("transcript rules come first", Prompts.SystemText(s, "call").StartsWith(Prompts.BaseRules), null);
            s.KeywordStyle = KeywordStyles.Plain;
            Check("plain text overrides the prompt's formatting", Prompts.SystemText(s, "interview").EndsWith(Prompts.PlainTextRule) &&
                  !Prompts.SystemText(s, "meeting").Contains(Prompts.KeywordRule), null);
            s.KeywordStyle = KeywordStyles.Standard;

            s.PromptId = "call";
            Prompts.Delete(s, "call");
            Check("deleting the picked prompt falls back to the default", s.PromptId == "" && !Prompts.All(s).Any(p => p.Id == "call"), s.PromptId);
            Check("a deleted built-in still resolves for old sessions", Prompts.Find(s, "call") != null && Prompts.Find(s, "call").Name == "Call", null);
            foreach (var p in Prompts.All(s).ToList()) Prompts.Delete(s, p.Id);
            Check("every prompt can go; the default stays", Prompts.All(s).Count == 0 && Prompts.DisplayName(s, s.PromptId) == "Default (Interview)", null);
            var kept = Json.Deserialize<AppSettings>(Json.Serialize(s)).Clone();
            Check("deleted prompts stay deleted", kept.HiddenPrompts.Count == 4, null);
            Prompts.RestoreBuiltIns(s);
            Check("built-ins restore", Prompts.All(s).Count == 4, null);

            var original = Prompts.SystemText(new AppSettings(), "meeting");
            Prompts.EditBuiltIn(s, "meeting", "Meeting (mine)", "Always **bold** the owner of each action item.");
            var edited = Json.Deserialize<AppSettings>(Json.Serialize(s)).Clone();
            Check("built-in prompt edits apply and persist", Prompts.SystemText(edited, "meeting").Contains("owner of each action item") &&
                  Prompts.All(edited).Any(p => p.Name == "Meeting (mine)") && Prompts.IsEdited(edited, "meeting"), null);
            Check("an edit that mentions bold drops the keyword rule", !Prompts.SystemText(edited, "meeting").Contains(Prompts.KeywordRule), null);
            Check("editing doesn't touch the shipped prompt", Prompts.SystemText(new AppSettings(), "meeting") == original, null);
            Prompts.ResetBuiltIn(edited, "meeting");
            Check("reset to original", Prompts.SystemText(edited, "meeting") == original && !Prompts.IsEdited(edited, "meeting"), null);
        }

        private static void TestKeys()
        {
            var s = new AppSettings();
            Check("both keys missing", s.MissingKeysText == "Add your OpenRouter and ElevenLabs keys to start. Click to open API keys.", s.MissingKeysText);
            s.OpenRouterKey = "sk-or-x";
            Check("one key missing", s.MissingKeysText == "Add your ElevenLabs key to start. Click to open API keys.", s.MissingKeysText);
            Check("no ElevenLabs key: Windows transcribes", s.EffectiveTranscription == "LiveCaptions", s.EffectiveTranscription);
            s.ElevenLabsKey = "sk_x";
            Check("ready with both", s.MissingKeys.Count == 0 && s.EffectiveTranscription == "ElevenLabs", s.EffectiveTranscription);
            s.Transcription = "Grok";
            Check("Grok needs an xAI key instead", s.MissingKeys.SequenceEqual(new[] { "xAI" }) && s.EffectiveTranscription == "LiveCaptions", null);
            s.XaiKey = "xai-x";
            Check("Grok with its key", s.MissingKeys.Count == 0 && s.EffectiveTranscription == "Grok", null);
            s.Transcription = "LiveCaptions";
            Check("Windows engine picked", s.EffectiveTranscription == "LiveCaptions", null);
            s.Transcription = "Automatic";
            Check("automatic prefers ElevenLabs", s.EffectiveTranscription == "ElevenLabs", null);
            s.ElevenLabsKey = "";
            Check("an xAI key alone is enough", s.MissingKeys.Count == 0 && s.EffectiveTranscription == "Grok", s.MissingKeysText);
        }

        private static void TestPrompt()
        {
            var s = new AppSettings { Context = "Interviewing for a backend role at Acme Pay." };
            var req = MakeRequest(s, "[Them] Why do you want to work here?", AnswerKind.Auto, null, null);
            req.Recent.Add(new QaItem { Question = "Tell me about yourself.", Answer = "I build payment systems." });
            req.Past.Add(new QaItem { Question = "What's your biggest weakness?", Answer = "I over-document." });
            var json = Json.Serialize(PromptBuilder.ForOpenRouter(req, "anthropic/claude-sonnet-5"));
            Check("openrouter payload", json.Contains("\"reasoning\"") && json.Contains("\"messages\"") && json.Contains("anthropic/claude-sonnet-5"), null);
            Check("transcript in user turn", json.Contains("Why do you want to work here?"), null);
            Check("context in the system prompt", json.Contains("Acme Pay"), null);
            Check("this session's answers replayed", json.Contains("earlier_answers") && json.Contains("I build payment systems."), null);
            Check("past sessions' answers replayed", json.Contains("past_sessions") && json.Contains("I over-document."), null);
            Check("valid JSON", Json.Parse(json) is Dictionary<string, object>, null);
            var img = Json.Serialize(PromptBuilder.ForOpenRouter(MakeRequest(new AppSettings(), "", AnswerKind.Screen, "focus on Q3", "AAAA"), "openai/gpt-5.5"));
            Check("screen request has image block", img.Contains("image_url") && img.Contains("image/jpeg") && img.Contains("focus on Q3"), null);
        }

        private static void TestMarkdown()
        {
            var md = "## Heading\n**Bold line** with `code` and *italic*\n- bullet one\n  - nested\n1. first\n\n```python\nprint('hi {x}')\n```\n> quote\n\u00E9\u4E2D";
            var text = MarkdownView.PlainText(MarkdownView.Render(md, 14));
            Check("renders headings + text", text.Contains("Heading") && text.Contains("Bold line") && text.Contains("print('hi {x}')"), text.Replace("\n", " | "));
            Check("unicode kept", text.Contains("\u00E9\u4E2D"), null);
            Check("markers stripped", !text.Contains("**") && !text.Contains("```"), null);
            Check("bullets and numbers", text.Contains("bullet one") && text.Contains("first"), null);
            Check("inline code kept", text.Contains("code") && text.Contains("italic"), null);
        }

        private static void TestQuestions()
        {
            string[] yes = { "Why do you want to work here?", "Tell me about yourself", "walk me through your last project",
                             "So, how would you design a URL shortener", "Can you explain the CAP theorem" };
            string[] no = { "Thanks for joining today.", "Great.", "I worked on payments for three years.", "Okay let's get started" };
            foreach (var q in yes) Check("question: " + q, QuestionDetector.LooksLikeQuestion(q), null);
            foreach (var q in no) Check("not question: " + q, !QuestionDetector.LooksLikeQuestion(q), null);

            var ans = QuestionGate.Parse("ANSWER: What is JavaScript?");
            Check("gate: answer parsed", ans.Answer && ans.Question == "What is JavaScript?", ans.Question);
            var bold = QuestionGate.Parse("**ANSWER:** \"Can you explain Java?\"\nextra");
            Check("gate: markdown + quotes tolerated", bold.Answer && bold.Question == "Can you explain Java?", bold.Question);
            Check("gate: skip", !QuestionGate.Parse("SKIP").Answer && !QuestionGate.Parse("").Answer && !QuestionGate.Parse(null).Answer, null);
            var req = MakeRequest(new AppSettings(), "[Me] What is JavaScript?", AnswerKind.Auto, null, null);
            req.Question = "What is JavaScript?";
            var body = Json.Serialize(PromptBuilder.ForOpenRouter(req, "anthropic/claude-haiku-4.5"));
            Check("detected question goes to the model", body.Contains("Answer this question") && body.Contains("What is JavaScript?"), null);
        }

        private static void TestKeywordStyles()
        {
            foreach (var style in KeywordStyles.Ids)
            {
                var root = MarkdownView.Render("Use **PostgreSQL** for payments.", 14, style);
                var tb = (System.Windows.Controls.TextBlock)root.Children[0];
                var runs = tb.Inlines.OfType<System.Windows.Documents.Run>().ToList();
                var keyword = runs.First(r => r.Text == "PostgreSQL");
                var plain = runs.First(r => r.Text.StartsWith("Use"));
                var fg = KeywordStyles.Foreground(style);
                var bg = KeywordStyles.Background(style);
                bool plainKeyword = style == KeywordStyles.Off || style == KeywordStyles.Plain;
                bool ok = keyword.FontWeight == (plainKeyword ? System.Windows.FontWeights.Normal : System.Windows.FontWeights.Bold) &&
                          (fg == null ? keyword.ReadLocalValue(System.Windows.Documents.TextElement.ForegroundProperty) == System.Windows.DependencyProperty.UnsetValue : keyword.Foreground == fg) &&
                          (bg == null ? keyword.Background == null : keyword.Background == bg) &&
                          plain.Background == null && plain.ReadLocalValue(System.Windows.Documents.TextElement.ForegroundProperty) == System.Windows.DependencyProperty.UnsetValue;
                Check("keywords in " + KeywordStyles.Name(style) + " style", ok, null);
            }
            Check("ten styles (Plain text, then Off), four highlights", KeywordStyles.Ids.Length == 10 && KeywordStyles.Ids[0] == KeywordStyles.Plain &&
                  KeywordStyles.Ids[1] == KeywordStyles.Off && KeywordStyles.Ids.Count(KeywordStyles.IsHighlight) == 4, null);

            // Plain text: no bold, italics, headings or list markers; code still a code block with its language.
            var plainView = MarkdownView.Render("## Plan\nUse a **token bucket**, *quickly*.\n- one\n1. two\n```python\nprint(1)\n```", 14, KeywordStyles.Plain);
            var text = MarkdownView.PlainText(plainView);
            var plainRuns = new List<System.Windows.Documents.Run>();
            foreach (var tb in plainView.Children.OfType<System.Windows.Controls.TextBlock>())
                plainRuns.AddRange(tb.Inlines.OfType<System.Windows.Documents.Run>());
            Check("plain text has no bold or italics", plainRuns.Count > 0 && plainRuns.All(r => r.FontWeight == System.Windows.FontWeights.Normal && r.FontStyle == System.Windows.FontStyles.Normal) &&
                  plainView.Children.OfType<System.Windows.Controls.TextBlock>().All(t => t.FontWeight == System.Windows.FontWeights.Normal), text);
            Check("plain text drops list markers", !text.Contains("\u2022") && text.Contains("one|") && text.Contains("1. two|"), text);
            Check("code keeps its block and language", text.Contains("PYTHON|") && text.Contains("print(1)|"), text);
        }

        private static void TestCaptionDiff()
        {
            long now = 0;
            var src = new LiveCaptionsSource();
            src.Clock = () => now;
            var finals = new List<string>();
            string partial = null;
            src.Transcript += e => { if (e.IsFinal) finals.Add(e.Text); else partial = e.Text; };

            Action<string, long> feed = (t, advance) => { now += advance; src.OnCaptionText(t); };
            feed("Old caption from before we started.", 100);
            feed("Old caption from before we started. Tell me about", 200);
            feed("Old caption from before we started. Tell me about yourself.", 200);
            feed("Old caption from before we started. Tell me about yourself. Why do", 200);
            feed("Old caption from before we started. Tell me about yourself. Why do you want this job?", 200);
            feed("Old caption from before we started. Tell me about yourself. Why do you want this job?", 1700);
            feed("before we started. Tell me about yourself. Why do you want this job? I have", 200);
            feed("Tell me about yourself. Why do you want this job? I have five years of experience.", 200);
            feed("you want this job? I have five years of experience. And", 200);

            Note("finals: " + string.Join(" | ", finals));
            Check("old captions ignored", !finals.Any(f => f.Contains("Old caption")), null);
            Check("sentences committed once", finals.Count == 3 && finals[0] == "Tell me about yourself." &&
                  finals[1] == "Why do you want this job?" && finals[2] == "I have five years of experience.", null);
            Check("partial tracks tail", partial == "And", partial);
        }

        private static void TestSegmenter()
        {
            var seg = new Segmenter { SilenceMs = 600, MinThreshold = 0.004f };
            var segments = new List<short[]>();
            seg.Segment += pcm => segments.Add(pcm);
            var rnd = new Random(1);
            Func<double, float, float[]> tone = (seconds, amp) =>
            {
                int n = (int)(seconds * 16000);
                var a = new float[n];
                for (int i = 0; i < n; i++) a[i] = (float)(amp * Math.Sin(i * 2 * Math.PI * 220 / 16000) + (rnd.NextDouble() - 0.5) * 0.002);
                return a;
            };
            foreach (var chunk in new[] { tone(1.0, 0f), tone(1.5, 0.2f), tone(1.0, 0f), tone(0.1, 0.3f), tone(1.0, 0f), tone(2.0, 0.15f), tone(1.0, 0f) })
                seg.Feed(chunk, chunk.Length);
            Note("segments: " + string.Join(", ", segments.Select(s => (s.Length / 16000.0).ToString("0.00") + "s")));
            Check("two speech phrases, click ignored", segments.Count == 2, segments.Count.ToString());
            var wav = Wav.Encode(segments.Count > 0 ? segments[0] : new short[0], 16000);
            Check("wav header", wav.Length > 44 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wav, 8, 4) == "WAVE", null);
        }

        private static void TestGrokProtocol()
        {
            var s = new AppSettings { SpeechLanguage = "en", KeyTerms = "Kubernetes, Acme Pay,  ,kubernetes" };
            var url = GrokStreamingSource.BuildUrl(s);
            Note(url);
            Check("websocket url", url.StartsWith("wss://api.x.ai/v1/stt?model=grok-voice-transcribe-2.0&sample_rate=16000&encoding=pcm&interim_results=true"), null);
            Check("endpointing + language", url.Contains("&endpointing=700") && url.Contains("&language=en"), null);
            Check("keyterms deduped", url.Contains("keyterm=Kubernetes") && url.Contains("keyterm=Acme%20Pay") &&
                  url.IndexOf("keyterm=Kubernetes") == url.LastIndexOf("keyterm=Kubernetes"), null);
            Check("blank + case-duplicate terms dropped", !url.ToLowerInvariant().Contains("keyterm=kubernetes&keyterm=kubernetes"), null);
            var te = GrokStreamingSource.BuildUrl(new AppSettings { SpeechLanguage = "te" });
            var enUs = GrokStreamingSource.BuildUrl(new AppSettings { SpeechLanguage = "en-US" });
            Check("language only sent where xAI formats it", !te.Contains("language=") && enUs.Contains("&language=en") && !enUs.Contains("en-US"), te);

            // The language lock drops text in other scripts (xAI transcribes any language it hears).
            const string telugu = "నాకింత బాగా, ఓకే, అంటే ఇది";
            const string hindi = "आप कैसे हैं?";
            Check("English lock keeps English", SpeechLanguages.Matches("en", "What is JavaScript? Explain closures."), null);
            Check("English lock drops Telugu", !SpeechLanguages.Matches("en", telugu), null);
            Check("English lock keeps a stray foreign word", SpeechLanguages.Matches("en", "I studied in Hyderabad, నమస్తే everyone, nice to meet you"), null);
            Check("Telugu lock keeps Telugu and English mixed in", SpeechLanguages.Matches("te", telugu) && SpeechLanguages.Matches("te", "Tell me about yourself"), null);
            Check("Telugu lock drops Hindi", !SpeechLanguages.Matches("te", hindi), null);
            Check("auto keeps everything; numbers-only is neutral", SpeechLanguages.Matches("", telugu) && SpeechLanguages.Matches("ja", "2024?"), null);

            var src = new GrokStreamingSource(s);
            var ch = GrokStreamingSource.TestChannel("Them");
            var finals = new List<string>();
            var partials = new List<string>();
            src.Transcript += e => { if (e.IsFinal) finals.Add(e.Text); else partials.Add(e.Text); };
            var ready = new TaskCompletionSource<bool>();
            src.OnServerEvent(ch, "{\"type\":\"transcript.created\"}", ready);
            src.OnServerEvent(ch, "{\"type\":\"transcript.partial\",\"text\":\"Tell me\",\"is_final\":false,\"speech_final\":false}", ready);
            src.OnServerEvent(ch, "{\"type\":\"transcript.partial\",\"text\":\"Tell me about a time\",\"is_final\":true,\"speech_final\":false}", ready);
            src.OnServerEvent(ch, "{\"type\":\"transcript.partial\",\"text\":\"you failed\",\"is_final\":false,\"speech_final\":false}", ready);
            src.OnServerEvent(ch, "{\"type\":\"transcript.partial\",\"text\":\"Tell me about a time you failed?\",\"is_final\":true,\"speech_final\":true,\"end_of_turn_confidence\":0.97}", ready);
            src.OnServerEvent(ch, "{\"type\":\"transcript.partial\",\"text\":\"\",\"is_final\":true,\"speech_final\":true}", ready);
            src.OnServerEvent(ch, "{\"type\":\"transcript.partial\",\"text\":\"Take your time\",\"is_final\":true,\"speech_final\":false}", ready);
            src.OnServerEvent(ch, "{\"type\":\"transcript.done\",\"text\":\"...\"}", ready);
            Note("partials: " + string.Join(" | ", partials));
            Note("finals: " + string.Join(" | ", finals));
            Check("ready on transcript.created", ready.Task.IsCompleted, null);
            Check("interim shows locked + live text", partials.Contains("Tell me about a time you failed"), null);
            Check("utterance final emitted once", finals.Count == 2 && finals[0] == "Tell me about a time you failed?", null);
            Check("locked text flushed on done", finals.Count == 2 && finals[1] == "Take your time", null);
        }

        private static void TestElevenLabsProtocol()
        {
            var url = ElevenLabsStreamingSource.BuildUrl(new AppSettings { SpeechLanguage = "en" });
            Note(url);
            Check("websocket url", url.StartsWith("wss://api.elevenlabs.io/v1/speech-to-text/realtime?model_id=scribe_v2_realtime&audio_format=pcm_16000&commit_strategy=vad"), null);
            Check("language sent", url.Contains("&language_code=en") && url.Contains("vad_silence_threshold_secs=0.6"), null);
            Check("detect: no language", !ElevenLabsStreamingSource.BuildUrl(new AppSettings { SpeechLanguage = "" }).Contains("language_code"), null);
            var chunk = Json.Parse(ElevenLabsStreamingSource.AudioMessage(new byte[] { 1, 2, 3, 4 }, false));
            Check("audio chunk message", Json.Str(chunk, "message_type") == "input_audio_chunk" && Json.Str(chunk, "audio_base_64") == "AQIDBA==" &&
                  object.Equals(Json.Get(chunk, "commit"), false) && Json.Str(chunk, "sample_rate") == "16000", null);

            var src = new ElevenLabsStreamingSource(new AppSettings());
            var ch = StreamingSpeechSource.TestChannel("Them");
            var finals = new List<string>();
            var partials = new List<string>();
            string status = null;
            src.Transcript += e => { if (e.IsFinal) finals.Add(e.Text); else partials.Add(e.Text); };
            src.Status += st => status = st;
            var ready = new TaskCompletionSource<bool>();
            src.OnServerEvent(ch, "{\"message_type\":\"session_started\",\"session_id\":\"x\"}", ready);
            src.OnServerEvent(ch, "{\"message_type\":\"partial_transcript\",\"text\":\"Tell me about\"}", ready);
            src.OnServerEvent(ch, "{\"message_type\":\"partial_transcript\",\"text\":\"Tell me about a time you failed\"}", ready);
            src.OnServerEvent(ch, "{\"message_type\":\"committed_transcript\",\"text\":\"Tell me about a time you failed?\"}", ready);
            src.OnServerEvent(ch, "{\"message_type\":\"committed_transcript\",\"text\":\"\"}", ready);
            src.OnServerEvent(ch, "{\"message_type\":\"auth_error\",\"message\":\"Invalid API key\"}", ready);
            Check("ready on session_started", ready.Task.IsCompleted, null);
            Check("live text", partials.Contains("Tell me about a time you failed"), string.Join(" | ", partials));
            Check("committed text is final once", finals.Count == 1 && finals[0] == "Tell me about a time you failed?", string.Join(" | ", finals));
            Check("errors reported", status == "ElevenLabs: Invalid API key", status);
        }

        private static void TestGrokLive()
        {
            // With a bad key this proves the WebSocket handshake reaches xAI and the error is readable;
            // with a real key (Settings or XAI_API_KEY) it proves the stream is accepted.
            var s = AppSettings.Load();
            bool realKey = s.EffectiveXaiKey.Length > 0;
            var probe = new AppSettings();
            probe.XaiKey = realKey ? s.EffectiveXaiKey : "xai-invalid-key-for-testing";
            var result = GrokStreamingSource.TestConnectionAsync(probe).GetAwaiter().GetResult();
            Note((realKey ? "real key: " : "invalid key: ") + (result ?? "connected"));
            if (realKey) Check("xAI accepted the stream", result == null, result);
            else Check("xAI reached, bad key reported", result != null && result.Contains("rejected"), result);
        }

        private static void TestLoopback()
        {
            var samples = new List<float>();
            var seg = new Segmenter { SilenceMs = 600, MinThreshold = 0.004f };
            int segments = 0;
            string started = null, error = null;
            seg.Segment += pcm => Interlocked.Increment(ref segments);
            using (var cap = new WasapiCapture(true))
            {
                cap.Started += s => started = s;
                cap.Error += e => error = e;
                cap.Samples += (buf, n) =>
                {
                    lock (samples) for (int i = 0; i < n; i++) samples.Add(buf[i]);
                    seg.Feed(buf, n);
                };
                cap.Start();
                Thread.Sleep(600);
                using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
                {
                    synth.Volume = 30;
                    synth.SetOutputToDefaultAudioDevice();
                    synth.Speak("TheCloser audio check. Tell me about a time you solved a hard problem.");
                }
                Thread.Sleep(1000);
                seg.Tick();
            }
            Note("device: " + (started ?? "(not started)") + (error != null ? "  error: " + error : ""));
            double rms = 0;
            lock (samples)
            {
                foreach (var v in samples) rms += v * v;
                rms = samples.Count > 0 ? Math.Sqrt(rms / samples.Count) : 0;
            }
            Note(string.Format("captured {0:0.0}s, rms {1:0.0000}, phrases {2}", samples.Count / 16000.0, rms, segments));
            Check("loopback started", started != null, error);
            Check("loopback heard audio", rms > 0.0005, null);
            Check("phrase detected", segments >= 1, null);
            if (samples.Count > 0)
            {
                var pcm = samples.Select(v => (short)(Math.Max(-1f, Math.Min(1f, v)) * 32767)).ToArray();
                var path = Path.Combine(Path.GetTempPath(), "thecloser-loopback-test.wav");
                File.WriteAllBytes(path, Wav.Encode(pcm, 16000));
                Note("wrote " + path);
            }
        }

        private static void TestMic()
        {
            long count = 0;
            string started = null, error = null;
            using (var cap = new WasapiCapture(false))
            {
                cap.Started += s => started = s;
                cap.Error += e => error = e;
                cap.Samples += (buf, n) => Interlocked.Add(ref count, n);
                cap.Start();
                Thread.Sleep(1500);
            }
            Note("device: " + (started ?? "(not started)") + (error != null ? "  error: " + error : "") + string.Format("  samples {0}", count));
            Check("microphone delivers audio", started != null && count > 8000, error);
        }

        private static void TestApi()
        {
            var s = AppSettings.Load();
            var key = s.EffectiveOpenRouterKey;
            if (string.IsNullOrEmpty(key)) { Note("skipped: no OpenRouter key configured"); return; }
            var sw = Stopwatch.StartNew();
            var text = new StringBuilder();
            var body = PromptBuilder.ForOpenRouter(MakeRequest(s, "[Them] Thanks for joining.\n[Them] So, why are you interested in this role?", AnswerKind.Auto, null, null), s.Model);
            var r = OpenRouterClient.StreamAsync(key, body, t => text.Append(t), CancellationToken.None).GetAwaiter().GetResult();
            Note(string.Format("model {0}, stop {1}, first token {2:0.00}s, total {3:0.00}s", r.Model, r.StopReason, r.FirstTokenSeconds, sw.Elapsed.TotalSeconds));
            Note("answer: " + text.ToString().Replace("\n", " / "));
            Check("got an answer", text.Length > 20, r.StopReason);

            // The question gate (small model) on lines that should and shouldn't be answered.
            Func<string, bool, GateVerdict> gate = (transcript, mine) =>
            {
                var gsw = Stopwatch.StartNew();
                var v = QuestionGate.CheckAsync(s, transcript, mine, CancellationToken.None).GetAwaiter().GetResult();
                Note(string.Format("gate {0:0.00}s: {1} -> {2}", gsw.Elapsed.TotalSeconds, transcript.Replace("\n", " / "), v.Answer ? "ANSWER " + v.Question : "SKIP"));
                return v;
            };
            Check("gate answers a question from them", gate("[Them] Hi, thanks for joining.\n[Them] So can you explain what a closure is in JavaScript?", false).Answer, null);
            Check("gate skips small talk", !gate("[Them] Hey, can you hear me okay?\n[Them] Great, give me a second to pull up my notes.", false).Answer, null);
            Check("gate answers your own question when testing alone", gate("[Me] Hello\n[Me] What is JavaScript?", true).Answer, null);
        }
    }
}
