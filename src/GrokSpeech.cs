using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TheCloser
{
    /// <summary>
    /// Real-time transcription with xAI Grok Voice Transcribe over WebSocket (wss://api.x.ai/v1/stt).
    /// Speaker audio ("Them") and optionally the microphone ("Me") each stream as 16 kHz PCM on their own
    /// connection; Grok returns live partial text and a stitched final transcript per utterance.
    /// </summary>
    internal sealed class GrokStreamingSource : ITranscriptSource
    {
        public const string PresetName = "xAI Grok";
        public const string DefaultModel = "grok-voice-transcribe-2.0";
        private const int Rate = 16000;

        public event Action<TranscriptEvent> Transcript;
        public event Action<string> Status;
        public event Action<string, bool> Activity;

        internal sealed class Channel
        {
            public string Speaker;
            public WasapiCapture Capture;
            public readonly object Lock = new object();
            public readonly MemoryStream Pending = new MemoryStream();
            public string Locked = "";
            public bool Speaking;
            public Task Loop;
        }

        private readonly AppSettings _s;
        private readonly List<Channel> _channels = new List<Channel>();
        private CancellationTokenSource _cts;
        private int _connected;

        public GrokStreamingSource(AppSettings settings)
        {
            _s = settings;
        }

        public string Name { get { return "xAI Grok"; } }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            RaiseStatus("Connecting to xAI Grok...");
            if (_s.CaptureSystem) AddChannel("Them", true);
            if (_s.CaptureMic) AddChannel("Me", false);
        }

        private void AddChannel(string speaker, bool loopback)
        {
            var ch = new Channel { Speaker = speaker, Capture = new WasapiCapture(loopback) };
            ch.Capture.Samples += delegate(float[] buf, int n)
            {
                var bytes = new byte[n * 2];
                for (int i = 0; i < n; i++)
                {
                    short v = (short)(Math.Max(-1f, Math.Min(1f, buf[i])) * 32767);
                    bytes[2 * i] = (byte)v;
                    bytes[2 * i + 1] = (byte)(v >> 8);
                }
                lock (ch.Lock)
                {
                    // Never buffer more than ~5 s (e.g. while reconnecting).
                    if (ch.Pending.Length > Rate * 2 * 5) ch.Pending.SetLength(0);
                    ch.Pending.Write(bytes, 0, bytes.Length);
                }
            };
            ch.Capture.Error += RaiseStatus;
            _channels.Add(ch);
            ch.Capture.Start();
            var token = _cts.Token;
            ch.Loop = Task.Run(() => RunChannel(ch, token));
        }

        public void Stop()
        {
            if (_cts == null) return;
            _cts.Cancel();
            foreach (var c in _channels) c.Capture.Stop();
            try { Task.WaitAll(_channels.ConvertAll(c => c.Loop).ToArray(), 4000); } catch { }
            _channels.Clear();
        }

        public void Dispose() { Stop(); }

        private void RaiseStatus(string s)
        {
            var h = Status;
            if (h != null) h(s);
        }

        private void Raise(TranscriptEvent e)
        {
            var h = Transcript;
            if (h != null) h(e);
        }

        private void SetSpeaking(Channel ch, bool on)
        {
            if (ch.Speaking == on) return;
            ch.Speaking = on;
            var h = Activity;
            if (h != null) h(ch.Speaker, on);
        }

        internal static string BuildUrl(AppSettings s)
        {
            var baseUrl = "https://api.x.ai/v1";
            if (baseUrl.StartsWith("https://")) baseUrl = "wss://" + baseUrl.Substring(8);
            else if (baseUrl.StartsWith("http://")) baseUrl = "ws://" + baseUrl.Substring(7);
            var sb = new StringBuilder(baseUrl).Append("/stt");
            sb.Append("?model=").Append(Uri.EscapeDataString(string.IsNullOrWhiteSpace(s.GrokModel) ? DefaultModel : s.GrokModel.Trim()));
            sb.Append("&sample_rate=").Append(Rate).Append("&encoding=pcm&interim_results=true");
            sb.Append("&endpointing=").Append(Math.Max(100, Math.Min(5000, s.SilenceMs)));
            // xAI transcribes any language regardless; "language" only formats numbers and currency, for the codes it knows.
            if (SpeechLanguages.XaiFormats(s.SpeechLanguage)) sb.Append("&language=").Append(SpeechLanguages.Find(s.SpeechLanguage).Code);
            foreach (var term in KeyTerms(s)) sb.Append("&keyterm=").Append(Uri.EscapeDataString(term));
            return sb.ToString();
        }

        /// <summary>User-supplied key terms plus the user's name (max 100, 50 chars each, per the API limits).</summary>
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

        private async Task RunChannel(Channel ch, CancellationToken ct)
        {
            int failures = 0;
            while (!ct.IsCancellationRequested)
            {
                bool hadSession = false;
                try
                {
                    hadSession = await Session(ch, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) return;
                    RaiseStatus(Friendly(ex, ch.Speaker));
                    if (IsAuthError(ex))
                    {
                        // A bad key won't fix itself; wait longer between attempts.
                        failures = 10;
                    }
                }
                if (ct.IsCancellationRequested) return;
                failures = hadSession ? 0 : failures + 1;
                int delay = hadSession ? 300 : Math.Min(15000, 1000 * failures * failures);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>One WebSocket session. Returns true if it connected and streamed (so a reconnect is routine).</summary>
        private async Task<bool> Session(Channel ch, CancellationToken ct)
        {
            var key = _s.EffectiveXaiKey;
            if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Add your xAI API key in Settings > Models.");

            using (var ws = new ClientWebSocket())
            {
                ws.Options.SetRequestHeader("Authorization", "Bearer " + key);
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectTimeout.CancelAfter(15000);
                    await ws.ConnectAsync(new Uri(BuildUrl(_s)), connectTimeout.Token).ConfigureAwait(false);
                }

                var ready = new TaskCompletionSource<bool>();
                var receive = ReceiveLoop(ws, ch, ready);
                var first = await Task.WhenAny(ready.Task, receive, Task.Delay(10000, ct)).ConfigureAwait(false);
                if (first != ready.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    if (receive.IsFaulted) throw receive.Exception.InnerException;
                    throw new IOException("xAI didn't accept the audio stream (no transcript.created).");
                }

                lock (ch.Lock) ch.Pending.SetLength(0); // drop audio captured while connecting
                if (Interlocked.Increment(ref _connected) == 1 || ch.Speaker == "Them")
                    RaiseStatus("Listening via xAI Grok" + (_s.CaptureMic && _s.CaptureSystem ? " (you + them)" : ""));

                var clock = Stopwatch.StartNew();
                long sent = 0; // samples
                while (ws.State == WebSocketState.Open && !receive.IsCompleted)
                {
                    try { await Task.Delay(100, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    byte[] chunk;
                    lock (ch.Lock)
                    {
                        chunk = ch.Pending.ToArray();
                        ch.Pending.SetLength(0);
                    }
                    // Speaker loopback delivers nothing while the PC is silent; send silence so the
                    // stream stays real-time and Grok's endpointing can close the utterance.
                    long expected = clock.ElapsedMilliseconds * Rate / 1000;
                    long have = sent + chunk.Length / 2;
                    if (have < expected - Rate / 5)
                    {
                        int pad = (int)Math.Min(expected - have - Rate / 10, Rate);
                        var padded = new byte[chunk.Length + pad * 2];
                        Buffer.BlockCopy(chunk, 0, padded, 0, chunk.Length);
                        chunk = padded;
                    }
                    if (chunk.Length == 0) continue;
                    await ws.SendAsync(new ArraySegment<byte>(chunk), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    sent += chunk.Length / 2;
                }

                if (ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    // Graceful stop: ask for the final transcript, wait briefly, then close.
                    try
                    {
                        using (var t = new CancellationTokenSource(2000))
                        {
                            var done = Encoding.UTF8.GetBytes("{\"type\":\"audio.done\"}");
                            await ws.SendAsync(new ArraySegment<byte>(done), WebSocketMessageType.Text, true, t.Token).ConfigureAwait(false);
                        }
                        await Task.WhenAny(receive, Task.Delay(2500)).ConfigureAwait(false);
                        using (var t = new CancellationTokenSource(1500))
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", t.Token).ConfigureAwait(false);
                    }
                    catch { }
                    throw new OperationCanceledException(ct);
                }
                if (receive.IsFaulted) throw receive.Exception.InnerException;
                return true;
            }
        }

        private async Task ReceiveLoop(ClientWebSocket ws, Channel ch, TaskCompletionSource<bool> ready)
        {
            var buffer = new byte[16384];
            var message = new MemoryStream();
            while (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseSent)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (r.CloseStatus.HasValue && r.CloseStatus.Value != WebSocketCloseStatus.NormalClosure && !string.IsNullOrEmpty(r.CloseStatusDescription))
                        RaiseStatus("xAI closed the stream: " + r.CloseStatusDescription);
                    break;
                }
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                if (r.MessageType == WebSocketMessageType.Text)
                    OnServerEvent(ch, Encoding.UTF8.GetString(message.ToArray()), ready);
                message.SetLength(0);
            }
            FlushLocked(ch);
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

        /// <summary>Handles one server event (visible for the self-test).</summary>
        internal void OnServerEvent(Channel ch, string json, TaskCompletionSource<bool> ready)
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

        private void FlushLocked(Channel ch)
        {
            if (ch.Locked.Length == 0) return;
            var text = ch.Locked;
            ch.Locked = "";
            Raise(new TranscriptEvent(ch.Speaker, "", false));
            Raise(new TranscriptEvent(ch.Speaker, text, true));
            SetSpeaking(ch, false);
        }

        internal static Channel TestChannel(string speaker)
        {
            return new Channel { Speaker = speaker };
        }

        /// <summary>HTTP status of a failed WebSocket handshake, parsed from the exception text (0 if none).</summary>
        private static int HandshakeStatus(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? "";
                foreach (var code in new[] { 400, 401, 403, 404, 429, 500, 502, 503 })
                    if (m.Contains("(" + code + ")") || m.Contains("'" + code + "'")) return code;
            }
            return 0;
        }

        private static bool IsAuthError(Exception ex)
        {
            int status = HandshakeStatus(ex);
            return status == 400 || status == 401 || status == 403 || (ex.Message ?? "").Contains("API key");
        }

        private static string Friendly(Exception ex, string speaker)
        {
            // xAI answers a wrong key with 400 "Incorrect API key provided" and a missing one with 401.
            switch (HandshakeStatus(ex))
            {
                case 400:
                case 401: return "xAI rejected the API key. Check it in Settings > Models.";
                case 403: return "This xAI key doesn't have access to speech-to-text.";
                case 404: return "xAI speech endpoint not found.";
                case 429: return "xAI speech is rate limited - retrying...";
            }
            if (ex is InvalidOperationException) return ex.Message;
            var m = ex.Message ?? "";
            var inner = ex.InnerException != null ? ex.InnerException.Message : "";
            return "xAI speech (" + speaker + "): " + (inner.Length > 0 ? inner : m) + " - reconnecting...";
        }

        /// <summary>Opens a stream, waits for the server to accept it, then closes. Returns null on success, else a message.</summary>
        public static async Task<string> TestConnectionAsync(AppSettings s)
        {
            var key = s.EffectiveXaiKey;
            if (string.IsNullOrEmpty(key)) return "Enter an xAI API key first.";
            try
            {
                using (var ws = new ClientWebSocket())
                using (var cts = new CancellationTokenSource(15000))
                {
                    ws.Options.SetRequestHeader("Authorization", "Bearer " + key);
                    await ws.ConnectAsync(new Uri(BuildUrl(s)), cts.Token).ConfigureAwait(false);
                    var buffer = new byte[8192];
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                    var msg = Encoding.UTF8.GetString(buffer, 0, r.Count);
                    var type = Json.Str(Json.Parse(msg), "type");
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", cts.Token).ConfigureAwait(false); } catch { }
                    if (type == "transcript.created") return null;
                    if (type == "error") return Json.Str(Json.Parse(msg), "message");
                    return "Unexpected reply: " + msg;
                }
            }
            catch (OperationCanceledException) { return "Timed out connecting to xAI."; }
            catch (Exception ex) { return Friendly(ex, "test").Replace(" - reconnecting...", ""); }
        }
    }
}
