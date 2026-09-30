using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TheCloser
{
    /// <summary>
    /// Real-time transcription with xAI Grok Voice Transcribe over WebSocket (wss://api.x.ai/v1/stt). Grok returns
    /// live partial text and a stitched final transcript per utterance.
    /// </summary>
    internal sealed class GrokStreamingSource : StreamingSpeechSource
    {
        public const string PresetName = "xAI Grok";
        public const string DefaultModel = "grok-voice-transcribe-2.0";

        public GrokStreamingSource(AppSettings settings) : base(settings) { }

        public override string Name { get { return "Grok Transcribe 2"; } }
        protected override string Provider { get { return "xAI"; } }
        protected override string ApiKey { get { return _s.EffectiveXaiKey; } }

        internal static string BuildUrl(AppSettings s)
        {
            var sb = new StringBuilder("wss://api.x.ai/v1/stt");
            sb.Append("?model=").Append(Uri.EscapeDataString(string.IsNullOrWhiteSpace(s.GrokModel) ? DefaultModel : s.GrokModel.Trim()));
            sb.Append("&sample_rate=").Append(Rate).Append("&encoding=pcm&interim_results=true");
            sb.Append("&endpointing=").Append(Math.Max(100, Math.Min(5000, s.SilenceMs)));
            // xAI transcribes any language regardless; "language" only formats numbers and currency, for the codes it knows.
            if (SpeechLanguages.XaiFormats(s.SpeechLanguage)) sb.Append("&language=").Append(SpeechLanguages.Find(s.SpeechLanguage).Code);
            foreach (var term in KeyTerms(s)) sb.Append("&keyterm=").Append(Uri.EscapeDataString(term));
            return sb.ToString();
        }

        /// <summary>User-supplied key terms (max 100, 50 chars each, per the API limits).</summary>
        internal static List<string> KeyTerms(AppSettings s)
        {
            var terms = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = s.KeyTerms ?? "";
            foreach (var part in raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim();
                if (t.Length == 0 || t.Length > 50 || !seen.Add(t)) continue;
                terms.Add(t);
                if (terms.Count == 100) break;
            }
            return terms;
        }

        protected override Uri BuildUri() { return new Uri(BuildUrl(_s)); }

        protected override void Authorize(ClientWebSocket ws, string key)
        {
            ws.Options.SetRequestHeader("Authorization", "Bearer " + key);
        }

        /// <summary>Pro: fetches a short-lived token from TheCloser's server before each connection (each lasts minutes).</summary>
        internal Func<Task<string>> TokenProvider;

        protected override Task<string> ConnectKeyAsync()
        {
            return TokenProvider != null ? TokenProvider() : base.ConnectKeyAsync();
        }

        protected override Task SendAudioAsync(ClientWebSocket ws, byte[] pcm, CancellationToken ct)
        {
            return ws.SendAsync(new ArraySegment<byte>(pcm), WebSocketMessageType.Binary, true, ct);
        }

        protected override Task FinishAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var done = Encoding.UTF8.GetBytes("{\"type\":\"audio.done\"}");
            return ws.SendAsync(new ArraySegment<byte>(done), WebSocketMessageType.Text, true, ct);
        }

        private static bool Bool(object o)
        {
            return o is bool && (bool)o;
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + " " + b;
        }

        internal override void OnServerEvent(Channel ch, string json, TaskCompletionSource<bool> ready)
        {
            object ev;
            try { ev = Json.Parse(json); } catch { return; }
            switch (Json.Str(ev, "type"))
            {
                case "transcript.created":
                    if (ready != null) ready.TrySetResult(true);
                    break;
                case "transcript.partial":
                    var text = (Json.Str(ev, "text") ?? "").Trim();
                    bool isFinal = Bool(Json.Get(ev, "is_final"));
                    bool speechFinal = Bool(Json.Get(ev, "speech_final"));
                    if (speechFinal)
                    {
                        // Utterance final: the complete, stitched utterance.
                        var full = text.Length > 0 ? text : ch.Locked;
                        ch.Locked = "";
                        Raise(new TranscriptEvent(ch.Speaker, "", false));
                        if (full.Length > 0) Raise(new TranscriptEvent(ch.Speaker, full, true));
                        SetSpeaking(ch, false);
                    }
                    else if (isFinal)
                    {
                        // Chunk final: ~3 s of speech locked in, utterance continues.
                        ch.Locked = Join(ch.Locked, text);
                        Raise(new TranscriptEvent(ch.Speaker, ch.Locked, false));
                        SetSpeaking(ch, true);
                    }
                    else
                    {
                        var shown = ch.Locked.Length > 0 && text.StartsWith(ch.Locked) ? text : Join(ch.Locked, text);
                        Raise(new TranscriptEvent(ch.Speaker, shown, false));
                        if (shown.Length > 0) SetSpeaking(ch, true);
                    }
                    break;
                case "transcript.done":
                    FlushLocked(ch);
                    break;
                case "error":
                    RaiseStatus("xAI speech error: " + (Json.Str(ev, "message") ?? "unknown"));
                    break;
            }
        }

        protected override string Friendly(Exception ex, string speaker)
        {
            // xAI answers a wrong key with 400 "Incorrect API key provided" and a missing one with 401.
            switch (HandshakeStatus(ex))
            {
                case 400:
                case 401: return "xAI rejected the API key. Check it in Settings > AI.";
                case 403: return "This xAI key doesn't have access to speech-to-text.";
                case 404: return "xAI speech endpoint not found.";
                case 429: return "xAI speech is rate limited - retrying...";
            }
            if (ex is InvalidOperationException) return ex.Message;
            var m = ex.Message ?? "";
            var inner = ex.InnerException != null ? ex.InnerException.Message : "";
            return "xAI speech (" + speaker + "): " + (inner.Length > 0 ? inner : m) + " - reconnecting...";
        }

        public static Task<string> TestConnectionAsync(AppSettings s)
        {
            return TestAsync(new GrokStreamingSource(s));
        }
    }
}
