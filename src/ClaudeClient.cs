using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TheCloser
{
    internal sealed class ClaudeApiException : Exception
    {
        public readonly int Status;
        public readonly string ErrorType;

        public ClaudeApiException(int status, string type, string message) : base(message)
        {
            Status = status;
            ErrorType = type;
        }
    }

    internal sealed class StreamResult
    {
        public string Text = "";
        public string StopReason;
        public string Model;
        public long InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens;
        public double FirstTokenSeconds, TotalSeconds;
    }

    /// <summary>
    /// Minimal Claude Messages API client (raw HTTPS + server-sent events). The official C# SDK needs
    /// NuGet/.NET SDK tooling; this app builds with the compiler that ships with Windows, so it speaks HTTP directly.
    /// </summary>
    internal sealed class ClaudeClient
    {
        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private const string FallbackBeta = "server-side-fallback-2026-07-01";

        private static readonly HttpClient Http = CreateHttp();
        private static bool _fallbackUnsupported;

        private static HttpClient CreateHttp()
        {
            if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var h = new HttpClient();
            h.Timeout = Timeout.InfiniteTimeSpan;
            return h;
        }

        /// <summary>Models that support output_config.effort (adaptive thinking).</summary>
        public static bool SupportsEffort(string model)
        {
            model = model ?? "";
            if (model.Contains("haiku")) return false;
            if (model.StartsWith("claude-3") || model.StartsWith("claude-sonnet-4-5") || model.StartsWith("claude-sonnet-4-2") ||
                model.StartsWith("claude-opus-4-1") || model.StartsWith("claude-opus-4-0") || model == "claude-opus-4" || model == "claude-sonnet-4")
                return false;
            return model.StartsWith("claude-");
        }

        /// <summary>Models that take the server-side refusal fallback ("fallbacks": "default").</summary>
        public static bool SupportsFallbacks(string model)
        {
            return model == "claude-opus-5" || model == "claude-fable-5-1" || model == "claude-fable-5";
        }

        /// <summary>Streams a Messages API request. onText receives each text delta as it arrives.</summary>
        public async Task<StreamResult> StreamAsync(string apiKey, Dictionary<string, object> body, Action<string> onText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ClaudeApiException(401, "missing_key", "Add your Anthropic API key in Settings.");

            string model = (string)body["model"];
            bool useFallback = SupportsFallbacks(model) && !_fallbackUnsupported;
            int attempt = 0;
            while (true)
            {
                attempt++;
                var result = new StreamResult();
                bool anyText = false;
                int retryDelayMs = 0; // C# 5 can't await inside catch, so catches set this and we wait below
                try
                {
                    var payload = new Dictionary<string, object>(body);
                    payload["stream"] = true;
                    if (useFallback) payload["fallbacks"] = "default";
                    await StreamOnce(apiKey, payload, useFallback, delegate(string t)
                    {
                        anyText = true;
                        onText(t);
                    }, result, ct).ConfigureAwait(false);
                    return result;
                }
                catch (ClaudeApiException ex)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    // If this account/model rejects the fallback beta, retry once without it and remember.
                    if (useFallback && ex.Status == 400 && ex.Message.IndexOf("fallback", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _fallbackUnsupported = true;
                        useFallback = false;
                        continue;
                    }
                    bool retryable = ex.Status == 429 || ex.Status == 500 || ex.Status == 502 || ex.Status == 503 || ex.Status == 529 || ex.ErrorType == "overloaded_error";
                    if (!retryable || anyText || attempt >= 3) throw;
                    retryDelayMs = attempt * 900;
                }
                catch (HttpRequestException ex)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (anyText || attempt >= 3)
                        throw new ClaudeApiException(0, "network", "Network error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                    retryDelayMs = attempt * 700;
                }
                catch (IOException ex)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    throw new ClaudeApiException(0, "network", "Connection interrupted: " + ex.Message);
                }
                catch (ObjectDisposedException)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    throw;
                }
                if (retryDelayMs > 0) await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
            }
        }

        private static async Task StreamOnce(string apiKey, Dictionary<string, object> payload, bool fallbackBeta,
            Action<string> onText, StreamResult result, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            if (fallbackBeta) req.Headers.TryAddWithoutValidation("anthropic-beta", FallbackBeta);
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            req.Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json");

            using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw ToException((int)resp.StatusCode, err);
                }
                using (ct.Register(delegate { try { resp.Dispose(); } catch { } }))
                using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var text = new StringBuilder();
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!line.StartsWith("data:")) continue;
                        var data = line.Substring(5).Trim();
                        if (data.Length == 0) continue;
                        object ev;
                        try { ev = Json.Parse(data); } catch { continue; }
                        var type = Json.Str(ev, "type");
                        switch (type)
                        {
                            case "message_start":
                                result.Model = Json.Str(ev, "message", "model");
                                result.InputTokens = ToLong(Json.Get(ev, "message", "usage", "input_tokens"));
                                result.CacheReadTokens = ToLong(Json.Get(ev, "message", "usage", "cache_read_input_tokens"));
                                result.CacheWriteTokens = ToLong(Json.Get(ev, "message", "usage", "cache_creation_input_tokens"));
                                break;
                            case "content_block_delta":
                                if (Json.Str(ev, "delta", "type") == "text_delta")
                                {
                                    var t = Json.Str(ev, "delta", "text") ?? "";
                                    if (t.Length > 0)
                                    {
                                        if (text.Length == 0) result.FirstTokenSeconds = (DateTime.UtcNow - started).TotalSeconds;
                                        text.Append(t);
                                        onText(t);
                                    }
                                }
                                break;
                            case "message_delta":
                                var stop = Json.Str(ev, "delta", "stop_reason");
                                if (stop != null) result.StopReason = stop;
                                var outTok = Json.Get(ev, "usage", "output_tokens");
                                if (outTok != null) result.OutputTokens = ToLong(outTok);
                                break;
                            case "error":
                                throw new ClaudeApiException(0, Json.Str(ev, "error", "type") ?? "error",
                                    Json.Str(ev, "error", "message") ?? "The API returned an error.");
                            case "message_stop":
                                break;
                        }
                    }
                    result.Text = text.ToString();
                    result.TotalSeconds = (DateTime.UtcNow - started).TotalSeconds;
                }
            }
        }

        private static long ToLong(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt64(o); } catch { return 0; }
        }

        private static ClaudeApiException ToException(int status, string body)
        {
            string type = null, message = null;
            try
            {
                var o = Json.Parse(body);
                type = Json.Str(o, "error", "type");
                message = Json.Str(o, "error", "message");
            }
            catch { }
            if (string.IsNullOrEmpty(message)) message = body.Length > 300 ? body.Substring(0, 300) : body;
            string friendly;
            switch (status)
            {
                case 401: friendly = "Your Anthropic API key was rejected. Check it in Settings."; break;
                case 403: friendly = "This API key doesn't have access: " + message; break;
                case 404: friendly = "Model not found - pick another model in Settings. (" + message + ")"; break;
                case 413: friendly = "Request too large - remove a large PDF from your context."; break;
                case 429: friendly = "Rate limited by the API. Wait a moment and try again."; break;
                case 529: friendly = "The API is overloaded right now. Try again in a few seconds."; break;
                default: friendly = message; break;
            }
            return new ClaudeApiException(status, type, friendly);
        }
    }
}
