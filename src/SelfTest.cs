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
    /// Headless checks: TheCloser.exe --selftest [log-file] [--audio] [--api]
    ///   --audio  plays a short, quiet speech clip and verifies speaker-loopback capture + phrase detection
    ///   --api    calls the Claude API (needs a key in settings or ANTHROPIC_API_KEY)
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

            Try("settings + JSON round trip", TestSettings);
            Try("prompt builder", TestPrompt);
            Try("markdown rendering", TestMarkdown);
            Try("question detector", TestQuestions);
            Try("live captions diffing", TestCaptionDiff);
            Try("segmenter (synthetic audio)", TestSegmenter);
            Try("xAI Grok streaming protocol", TestGrokProtocol);
            if (api) Try("xAI Grok live connection", TestGrokLive);
            Try("live captions availability", delegate
            {
                Check("LiveCaptions.exe present", LiveCaptionsSource.IsAvailable, LiveCaptionsSource.ExePath);
            });
            if (audio) Try("speaker loopback capture", TestLoopback);
            if (audio) Try("microphone capture", TestMic);
            if (api) Try("Claude API", TestApi);

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
                Length = s.Length,
                Effort = s.Effort
            };
            PromptBuilder.AddContext(req, s);
            return req;
        }

        private static void TestSettings()
        {
            var s = new AppSettings();
            s.AnthropicKey = "sk-ant-test-123";
            s.Context = "Line 1\nLine \"2\" with unicode \u00E9\u2013";
            s.Files.Add(@"C:\x\product-sheet.pdf");
            var json = Json.Serialize(s);
            Check("plain key not in JSON", !json.Contains("sk-ant-test-123"), null);
            var back = Json.Deserialize<AppSettings>(json);
            Check("key decrypts", back.AnthropicKey == "sk-ant-test-123", null);
            Check("context survives", back.Context == s.Context, null);
            Check("files survive", back.Files.Count == 1 && back.Files[0] == @"C:\x\product-sheet.pdf", null);
            Check("defaults", back.Model == "anthropic/claude-opus-5" && back.Effort == "low" && back.HideFromCapture, back.Model);

            var ps = new AppSettings();
            Check("built-in prompt deletes", Prompts.Delete(ps, "call") && !Prompts.All(ps).Any(p => p.Id == "call") && ps.PromptId != "call", ps.PromptId);
            Check("deleted built-in still resolves for old sessions", Prompts.Find(ps, "call").Id == "call", null);
            var kept = Json.Deserialize<AppSettings>(Json.Serialize(ps)).Clone();
            Check("deleted prompts stay deleted", kept.HiddenPrompts.Contains("call") && kept.PromptId != "call", kept.PromptId);
            Prompts.Delete(ps, "sales");
            Prompts.Delete(ps, "meeting");
            Check("the last prompt can't be deleted", !Prompts.Delete(ps, "general") && Prompts.All(ps).Count == 1, null);
            Prompts.RestoreBuiltIns(ps);
            Check("built-ins restore", Prompts.All(ps).Count == 4, null);

            var original = Prompts.SystemText(new AppSettings(), "sales");
            Prompts.EditBuiltIn(ps, "sales", "Sales (mine)", "Always mention the free trial.");
            var edited = Json.Deserialize<AppSettings>(Json.Serialize(ps)).Clone();
            Check("built-in prompt edits apply and persist", Prompts.SystemText(edited, "sales").Contains("free trial") &&
                  Prompts.All(edited).Any(p => p.Name == "Sales (mine)") && Prompts.IsEdited(edited, "sales"), null);
            Check("editing doesn't touch the shipped prompt", Prompts.SystemText(new AppSettings(), "sales") == original, null);
            Prompts.ResetBuiltIn(edited, "sales");
            Check("reset to original", Prompts.SystemText(edited, "sales") == original && !Prompts.IsEdited(edited, "sales"), null);

            var legacy = Json.Deserialize<AppSettings>("{\"Model\":\"claude-haiku-4-5\",\"EnabledModels\":[\"claude-haiku-4-5\",\"anthropic/claude-haiku-4.5\",\"claude-opus-5-5\"]}").Clone();
            Check("bare claude id normalized", legacy.Model == "anthropic/claude-haiku-4.5", legacy.Model);
            Check("normalized models deduped", legacy.EnabledModels.SequenceEqual(new[] { "anthropic/claude-haiku-4.5", "anthropic/claude-opus-5-5" }), string.Join(", ", legacy.EnabledModels));
            legacy.AnthropicKey = "sk-ant-test-123";
            Check("bare claude id routes to anthropic", ModelCatalog.Resolve(legacy, legacy.Model).Provider == "anthropic", null);
        }

        private static void TestPrompt()
        {
            var s = new AppSettings { Context = "Selling Acme Pay to a mid-market prospect.", PromptId = "sales" };
            var body = PromptBuilder.ForAnthropic(MakeRequest(s, "[Them] Why should we switch from our current provider?", AnswerKind.Auto, null, null), "claude-opus-5");
            var json = Json.Serialize(body);
            Check("has cache_control", json.Contains("\"cache_control\":{\"type\":\"ephemeral\"}"), null);
            Check("has effort for opus", json.Contains("\"output_config\":{\"effort\":\"low\"}"), null);
            Check("transcript in user turn", json.Contains("Why should we switch from our current provider?"), null);
            Check("context is prompt-cached", json.Contains("Acme Pay"), null);
            Check("no stream flag yet", !json.Contains("\"stream\""), null);
            Check("valid JSON", Json.Parse(json) is Dictionary<string, object>, null);

            var haiku = Json.Serialize(PromptBuilder.ForAnthropic(MakeRequest(new AppSettings(), "", AnswerKind.Manual, null, null), "claude-haiku-4-5"));
            Check("no effort for haiku", !haiku.Contains("output_config"), null);
            Check("fallbacks only on supported models", ClaudeClient.SupportsFallbacks("claude-opus-5") && !ClaudeClient.SupportsFallbacks("claude-haiku-4-5"), null);

            var img = Json.Serialize(PromptBuilder.ForAnthropic(MakeRequest(new AppSettings(), "", AnswerKind.Screen, "focus on Q3", "AAAA"), "claude-opus-5"));
            Check("screen request has image block", img.Contains("\"type\":\"image\"") && img.Contains("image/jpeg") && img.Contains("focus on Q3"), null);

            var or = Json.Serialize(PromptBuilder.ForOpenRouter(MakeRequest(new AppSettings(), "[Them] hi", AnswerKind.Auto, null, null), "openai/gpt-5.4-mini"));
            Check("openrouter payload", or.Contains("\"reasoning\"") && or.Contains("\"messages\"") && or.Contains("openai/gpt-5.4-mini"), null);
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
            var body = Json.Serialize(PromptBuilder.ForAnthropic(req, "claude-haiku-4-5"));
            Check("detected question goes to the model", body.Contains("Answer this question") && body.Contains("What is JavaScript?"), null);
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
            var key = s.EffectiveAnthropicKey;
            if (string.IsNullOrEmpty(key)) { Note("skipped: no API key configured"); return; }
            var client = new ClaudeClient();
            var sw = Stopwatch.StartNew();
            var text = new StringBuilder();
            var model = ModelCatalog.IsAnthropic(s.Model) ? ModelCatalog.AnthropicApiId(s.Model) : "claude-haiku-4-5";
            var body = PromptBuilder.ForAnthropic(MakeRequest(s, "[Them] Thanks for joining.\n[Them] So, why are you interested in this role?", AnswerKind.Auto, null, null), model);
            var r = client.StreamAsync(key, body, t => text.Append(t), CancellationToken.None).GetAwaiter().GetResult();
            Note(string.Format("model {0}, stop {1}, first token {2:0.00}s, total {3:0.00}s, in {4} / out {5} tokens, cache read {6}",
                r.Model, r.StopReason, r.FirstTokenSeconds, sw.Elapsed.TotalSeconds, r.InputTokens, r.OutputTokens, r.CacheReadTokens));
            Note("answer: " + text.ToString().Replace("\n", " / "));
            Check("got an answer", text.Length > 20 && r.StopReason == "end_turn", r.StopReason);

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
