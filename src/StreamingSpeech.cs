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
    /// Live transcription over a WebSocket: speaker audio ("Them") and optionally the microphone ("Me") each stream as
    /// 16 kHz PCM on their own connection, and the provider sends back live and final text. Handles capture,
    /// silence padding and reconnects; subclasses speak one provider's protocol (ElevenLabs, xAI Grok).
    /// </summary>
    internal abstract class StreamingSpeechSource : ITranscriptSource
    {
        protected const int Rate = 16000;

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

        protected readonly AppSettings _s;
        private readonly List<Channel> _channels = new List<Channel>();
        private CancellationTokenSource _cts;
        private int _connected;

        protected StreamingSpeechSource(AppSettings settings)
        {
            _s = settings;
        }

        public abstract string Name { get; }

        /// <summary>The provider's name in status messages ("ElevenLabs", "xAI").</summary>
        protected abstract string Provider { get; }
        protected abstract string ApiKey { get; }
        protected abstract Uri BuildUri();
        protected abstract void Authorize(ClientWebSocket ws, string key);

        /// <summary>The credential for the next connection: the API key, or (Pro) a fresh short-lived token.</summary>
        protected virtual Task<string> ConnectKeyAsync()
        {
            return Task.FromResult(ApiKey);
        }

        /// <summary>Pro listens to the interviewer only (one stream), unless you picked the microphone alone.</summary>
        internal bool InterviewerOnly;
        protected abstract Task SendAudioAsync(ClientWebSocket ws, byte[] pcm, CancellationToken ct);
        /// <summary>Asks for the final transcript of whatever was said before closing.</summary>
        protected abstract Task FinishAsync(ClientWebSocket ws, CancellationToken ct);
        /// <summary>Handles one server message; completes `ready` when the provider accepts the stream.</summary>
        internal abstract void OnServerEvent(Channel ch, string json, TaskCompletionSource<bool> ready);
        /// <summary>A readable message for a failed connection (a bad key, a rate limit, a dropped network).</summary>
        protected abstract string Friendly(Exception ex, string speaker);

        public void Start()
        {
            _cts = new CancellationTokenSource();
            RaiseStatus("Connecting to " + Name + "...");
            bool system = _s.CaptureSystem, mic = _s.CaptureMic;
            if (InterviewerOnly && system) mic = false;
            if (system) AddChannel("Them", true);
            if (mic) AddChannel("Me", false);
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

        protected void RaiseStatus(string s)
        {
            var h = Status;
            if (h != null) h(s);
        }

        protected void Raise(TranscriptEvent e)
        {
            var h = Transcript;
            if (h != null) h(e);
        }

        protected void SetSpeaking(Channel ch, bool on)
        {
            if (ch.Speaking == on) return;
            ch.Speaking = on;
            var h = Activity;
            if (h != null) h(ch.Speaker, on);
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
                    // A bad key won't fix itself; wait longer between attempts.
                    if (IsAuthError(ex)) failures = 10;
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
            var key = await ConnectKeyAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Add your " + Provider + " API key in Settings > AI.");

            using (var ws = new ClientWebSocket())
            {
                Authorize(ws, key);
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectTimeout.CancelAfter(15000);
                    await ws.ConnectAsync(BuildUri(), connectTimeout.Token).ConfigureAwait(false);
                }

                var ready = new TaskCompletionSource<bool>();
                var receive = ReceiveLoop(ws, ch, ready);
                var first = await Task.WhenAny(ready.Task, receive, Task.Delay(10000, ct)).ConfigureAwait(false);
                if (first != ready.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    if (receive.IsFaulted) throw receive.Exception.InnerException;
                    throw new IOException(Provider + " didn't accept the audio stream.");
                }

                lock (ch.Lock) ch.Pending.SetLength(0); // drop audio captured while connecting
                if (Interlocked.Increment(ref _connected) == 1 || ch.Speaker == "Them")
                    RaiseStatus("Listening via " + Name + (_s.CaptureMic && _s.CaptureSystem && !InterviewerOnly ? " (you + them)" : ""));

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
                    // Speaker loopback delivers nothing while the PC is silent; send silence so the stream stays
                    // real-time and the provider can tell the speaker has stopped.
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
                    await SendAudioAsync(ws, chunk, ct).ConfigureAwait(false);
                    sent += chunk.Length / 2;
                }

                if (ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    // Graceful stop: ask for the final transcript, wait briefly, then close.
                    try
                    {
                        using (var t = new CancellationTokenSource(2000)) await FinishAsync(ws, t.Token).ConfigureAwait(false);
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
                        RaiseStatus(Provider + " closed the stream: " + r.CloseStatusDescription);
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

        /// <summary>Finalizes text that was locked in but never closed by the provider (the stream ended).</summary>
        protected void FlushLocked(Channel ch)
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
        protected static int HandshakeStatus(Exception ex)
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

        /// <summary>Opens a stream, waits for the provider to accept it, then closes. Null on success, else a message.</summary>
        protected static async Task<string> TestAsync(StreamingSpeechSource source)
        {
            if (string.IsNullOrEmpty(source.ApiKey)) return "Enter an API key first.";
            try
            {
                using (var ws = new ClientWebSocket())
                using (var cts = new CancellationTokenSource(15000))
                {
                    source.Authorize(ws, source.ApiKey);
                    await ws.ConnectAsync(source.BuildUri(), cts.Token).ConfigureAwait(false);
                    var ready = new TaskCompletionSource<bool>();
                    string error = null;
                    source.Status += delegate(string s) { if (error == null) error = s; };
                    var buffer = new byte[8192];
                    while (!ready.Task.IsCompleted && error == null)
                    {
                        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) { error = "The connection was closed: " + r.CloseStatusDescription; break; }
                        source.OnServerEvent(TestChannel("test"), Encoding.UTF8.GetString(buffer, 0, r.Count), ready);
                    }
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", cts.Token).ConfigureAwait(false); } catch { }
                    return ready.Task.IsCompleted ? null : error;
                }
            }
            catch (OperationCanceledException) { return "Timed out connecting to " + source.Provider + "."; }
            catch (Exception ex) { return source.Friendly(ex, "test").Replace(" - reconnecting...", ""); }
        }
    }

    /// <summary>ElevenLabs Scribe realtime (wss://api.elevenlabs.io/v1/speech-to-text/realtime), the Mac app's default engine.</summary>
    internal sealed class ElevenLabsStreamingSource : StreamingSpeechSource
    {
        public ElevenLabsStreamingSource(AppSettings settings) : base(settings) { }

        public override string Name { get { return "ElevenLabs"; } }
        protected override string Provider { get { return "ElevenLabs"; } }
        protected override string ApiKey { get { return _s.EffectiveElevenLabsKey; } }

        /// <summary>The server commits an utterance after 0.6 s of silence, like the Mac app.</summary>
        internal static string BuildUrl(AppSettings s)
        {
            var sb = new StringBuilder("wss://api.elevenlabs.io/v1/speech-to-text/realtime");
            sb.Append("?model_id=scribe_v2_realtime&audio_format=pcm_16000&commit_strategy=vad&vad_silence_threshold_secs=0.6");
            var lang = SpeechLanguages.Find(s.SpeechLanguage);
            if (lang != null && lang.Code.Length > 0) sb.Append("&language_code=").Append(lang.Code);
            return sb.ToString();
        }

        protected override Uri BuildUri() { return new Uri(BuildUrl(_s)); }

        protected override void Authorize(ClientWebSocket ws, string key)
        {
            ws.Options.SetRequestHeader("xi-api-key", key);
        }

        internal static string AudioMessage(byte[] pcm, bool commit)
        {
            return Json.Serialize(Json.Obj("message_type", "input_audio_chunk", "audio_base_64", Convert.ToBase64String(pcm), "commit", commit, "sample_rate", Rate));
        }

        protected override Task SendAudioAsync(ClientWebSocket ws, byte[] pcm, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(AudioMessage(pcm, false));
            return ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        protected override Task FinishAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(AudioMessage(new byte[0], true));
            return ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        internal override void OnServerEvent(Channel ch, string json, TaskCompletionSource<bool> ready)
        {
            object ev;
            try { ev = Json.Parse(json); } catch { return; }
            var type = Json.Str(ev, "message_type") ?? "";
            switch (type)
            {
                case "session_started":
                    if (ready != null) ready.TrySetResult(true);
                    break;
                case "partial_transcript":
                    var partial = (Json.Str(ev, "text") ?? "").Trim();
                    if (partial.Length == 0) break;
                    Raise(new TranscriptEvent(ch.Speaker, partial, false));
                    SetSpeaking(ch, true);
                    break;
                case "committed_transcript":
                case "committed_transcript_with_timestamps":
                    var text = (Json.Str(ev, "text") ?? "").Trim();
                    Raise(new TranscriptEvent(ch.Speaker, "", false));
                    if (text.Length > 0) Raise(new TranscriptEvent(ch.Speaker, text, true));
                    SetSpeaking(ch, false);
                    break;
                default:
                    if (type.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                        RaiseStatus("ElevenLabs: " + (Json.Str(ev, "message") ?? Json.Str(ev, "error") ?? type));
                    break;
            }
        }

        protected override string Friendly(Exception ex, string speaker)
        {
            switch (HandshakeStatus(ex))
            {
                case 401:
                case 403: return "ElevenLabs rejected the API key. Check it in Settings > AI.";
                case 429: return "ElevenLabs is rate limited - retrying...";
            }
            if (ex is InvalidOperationException) return ex.Message;
            var inner = ex.InnerException != null ? ex.InnerException.Message : "";
            return "ElevenLabs (" + speaker + "): " + (inner.Length > 0 ? inner : ex.Message) + " - reconnecting...";
        }

        public static Task<string> TestConnectionAsync(AppSettings s)
        {
            return TestAsync(new ElevenLabsStreamingSource(s));
        }
    }
}
