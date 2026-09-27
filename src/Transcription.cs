using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TheCloser
{
    internal sealed class TranscriptEvent
    {
        public readonly string Speaker;
        public readonly string Text;
        public readonly bool IsFinal;

        public TranscriptEvent(string speaker, string text, bool isFinal)
        {
            Speaker = speaker;
            Text = text;
            IsFinal = isFinal;
        }
    }

    internal interface ITranscriptSource : IDisposable
    {
        event Action<TranscriptEvent> Transcript;
        event Action<string> Status;
        event Action<string, bool> Activity; // speaker, is speaking
        string Name { get; }
        void Start();
        void Stop();
    }

    /// <summary>OpenAI-compatible /audio/transcriptions client (works with OpenAI, Groq, and compatible servers).</summary>
    internal sealed class SpeechToTextClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        private readonly string _baseUrl, _model, _key, _language;

        public SpeechToTextClient(string baseUrl, string model, string key, string language)
        {
            _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
            _model = (model ?? "").Trim();
            _key = (key ?? "").Trim();
            _language = (language ?? "").Trim();
        }

        private static void AddField(MultipartFormDataContent form, string name, string value)
        {
            var part = new StringContent(value, Encoding.UTF8);
            part.Headers.ContentType = null;
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + name + "\"" };
            form.Add(part);
        }

        public async Task<string> TranscribeAsync(byte[] wav, string prompt, CancellationToken ct)
        {
            using (var form = new MultipartFormDataContent("----thecloser" + Guid.NewGuid().ToString("N")))
            {
                var file = new ByteArrayContent(wav);
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = "\"audio.wav\"" };
                form.Add(file);
                AddField(form, "model", _model);
                AddField(form, "response_format", "json");
                if (_language.Length > 0) AddField(form, "language", _language);
                if (!string.IsNullOrWhiteSpace(prompt)) AddField(form, "prompt", prompt);

                using (var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/audio/transcriptions"))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                    req.Content = form;
                    using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            string msg = null;
                            try { msg = Json.Str(Json.Parse(body), "error", "message"); } catch { }
                            if (string.IsNullOrEmpty(msg)) msg = body.Length > 200 ? body.Substring(0, 200) : body;
                            throw new Exception("Speech-to-text " + (int)resp.StatusCode + ": " + msg);
                        }
                        return (Json.Str(Json.Parse(body), "text") ?? "").Trim();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Captures speaker audio (the other side, "Them") and optionally the microphone ("Me") with WASAPI,
    /// cuts phrases with a VAD and transcribes each phrase with a cloud speech-to-text API.
    /// </summary>
    internal sealed class CloudSpeechSource : ITranscriptSource
    {
        public event Action<TranscriptEvent> Transcript;
        public event Action<string> Status;
        public event Action<string, bool> Activity;

        private sealed class Channel
        {
            public string Speaker;
            public WasapiCapture Capture;
            public Segmenter Segmenter;
            public BlockingCollection<short[]> Queue;
            public Thread Worker;
            public string Recent = "";
        }

        private readonly AppSettings _settings;
        private readonly List<Channel> _channels = new List<Channel>();
        private SpeechToTextClient _stt;
        private CancellationTokenSource _cts;
        private Timer _tick;

        public CloudSpeechSource(AppSettings settings)
        {
            _settings = settings;
        }

        public string Name { get { return _settings.WhisperPreset + " Whisper"; } }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _stt = new SpeechToTextClient(_settings.WhisperBaseUrl, _settings.WhisperModel, _settings.EffectiveWhisperKey, _settings.SpeechLanguage);
            if (_settings.CaptureSystem) AddChannel("Them", true, 0.004f);
            if (_settings.CaptureMic) AddChannel("Me", false, 0.012f);
            _tick = new Timer(delegate { foreach (var c in _channels) c.Segmenter.Tick(); }, null, 200, 200);
            RaiseStatus("Listening to " + (_settings.CaptureMic && _settings.CaptureSystem ? "speakers + microphone" : _settings.CaptureMic ? "your microphone" : "speaker audio"));
        }

        private void AddChannel(string speaker, bool loopback, float minThreshold)
        {
            var ch = new Channel
            {
                Speaker = speaker,
                Capture = new WasapiCapture(loopback),
                Segmenter = new Segmenter { SilenceMs = Math.Max(300, _settings.SilenceMs), MinThreshold = minThreshold },
                Queue = new BlockingCollection<short[]>(64)
            };
            ch.Capture.Samples += ch.Segmenter.Feed;
            ch.Capture.Error += RaiseStatus;
            ch.Segmenter.Segment += delegate(short[] pcm) { ch.Queue.TryAdd(pcm); };
            ch.Segmenter.SpeechChanged += delegate(bool on)
            {
                var h = Activity;
                if (h != null) h(speaker, on);
            };
            ch.Worker = new Thread(delegate() { Work(ch); }) { IsBackground = true, Name = "stt-" + speaker };
            _channels.Add(ch);
            ch.Worker.Start();
            ch.Capture.Start();
        }

        private static readonly HashSet<string> Hallucinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "you", "thank you", "thank you.", "thanks.", "thanks for watching!", "thanks for watching.", "thank you for watching.",
            "bye.", "bye", ".", "okay.", "um", "uh", "subtitles by the amara.org community", "please subscribe."
        };

        private void Work(Channel ch)
        {
            var token = _cts.Token;
            try
            {
                foreach (var pcm in ch.Queue.GetConsumingEnumerable(token))
                {
                    try
                    {
                        var wav = Wav.Encode(pcm, WasapiCapture.OutRate);
                        var prompt = ch.Recent.Length > 200 ? ch.Recent.Substring(ch.Recent.Length - 200) : ch.Recent;
                        var text = _stt.TranscribeAsync(wav, prompt, token).GetAwaiter().GetResult();
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        double seconds = pcm.Length / (double)WasapiCapture.OutRate;
                        if (seconds < 2.5 && Hallucinations.Contains(text.Trim())) continue;
                        ch.Recent = (ch.Recent + " " + text).Trim();
                        if (ch.Recent.Length > 1000) ch.Recent = ch.Recent.Substring(ch.Recent.Length - 600);
                        var h = Transcript;
                        if (h != null) h(new TranscriptEvent(ch.Speaker, text, true));
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested) return;
                        var inner = ex.InnerException != null && ex is AggregateException ? ex.InnerException : ex;
                        RaiseStatus(inner.Message);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void RaiseStatus(string s)
        {
            var h = Status;
            if (h != null) h(s);
        }

        public void Stop()
        {
            if (_tick != null) { _tick.Dispose(); _tick = null; }
            foreach (var c in _channels) c.Capture.Stop();
            if (_cts != null) _cts.Cancel();
            foreach (var c in _channels) c.Queue.CompleteAdding();
            _channels.Clear();
        }

        public void Dispose() { Stop(); }
    }
}
