using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TheCloser
{
    /// <summary>Streaming client for OpenRouter's OpenAI-compatible chat completions API (GPT, Gemini, Grok, Kimi, ...).</summary>
    internal static class OpenRouterClient
    {
        private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

        public static Task<StreamResult> StreamAsync(string key, Dictionary<string, object> body, Action<string> onText, CancellationToken ct)
        {
            return StreamCoreAsync(Endpoint, key, null, body, onText, ct);
        }

        public static Task<StreamResult> StreamManagedAsync(string pass, string device, Dictionary<string, object> body, Action<string> onText, CancellationToken ct, HttpClient http = null)
        {
            return StreamCoreAsync(SubscriptionClient.ApiBase + "chat", pass, device, body, onText, ct, http);
        }

        private static async Task<StreamResult> StreamCoreAsync(string endpoint, string key, string device, Dictionary<string, object> body, Action<string> onText, CancellationToken ct, HttpClient http = null)
        {
            var payload = new Dictionary<string, object>(body);
            payload["stream"] = true;
            var started = DateTime.UtcNow;
            var result = new StreamResult();

            var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (device != null) req.Headers.TryAddWithoutValidation("X-Device", device);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.TryAddWithoutValidation("X-Title", "TheCloser");
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            req.Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json");

            using (req)
            {
                HttpResponseMessage resp;
                try
                {
                    resp = await (http ?? Http).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new ClaudeApiException(0, "network", "Network error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                }

                using (resp)
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw device == null ? ToException((int)resp.StatusCode, err) : ToSubscriptionException((int)resp.StatusCode, err);
                    }
                    using (ct.Register(delegate { try { resp.Dispose(); } catch { } }))
                    using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var text = new StringBuilder();
                        string line;
                        try
                        {
                            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (line.Length == 0 || line.StartsWith(":")) continue; // keep-alive comments
                                if (!line.StartsWith("data:")) continue;
                                var data = line.Substring(5).Trim();
                                if (data == "[DONE]") break;
                                object ev;
                                try { ev = Json.Parse(data); } catch { continue; }

                                var error = Json.Get(ev, "error");
                                if (error != null)
                                    throw new ClaudeApiException(0, "stream_error", Json.Str(error, "message") ?? "The model returned an error.");

                                if (result.Model == null) result.Model = Json.Str(ev, "model");
                                var choices = Json.Get(ev, "choices") as object[];
                                if (choices != null && choices.Length > 0)
                                {
                                    var delta = Json.Str(choices[0], "delta", "content");
                                    if (!string.IsNullOrEmpty(delta))
                                    {
                                        if (text.Length == 0) result.FirstTokenSeconds = (DateTime.UtcNow - started).TotalSeconds;
                                        text.Append(delta);
                                        onText(delta);
                                    }
                                    var finish = Json.Str(choices[0], "finish_reason");
                                    if (!string.IsNullOrEmpty(finish)) result.StopReason = finish == "length" ? "max_tokens" : finish;
                                }
                                var usage = Json.Get(ev, "usage");
                                if (usage != null)
                                {
                                    result.InputTokens = ToLong(Json.Get(usage, "prompt_tokens"));
                                    result.OutputTokens = ToLong(Json.Get(usage, "completion_tokens"));
                                }
                            }
                        }
                        catch (IOException)
                        {
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                            throw new ClaudeApiException(0, "network", "Connection interrupted.");
                        }
                        catch (ObjectDisposedException)
                        {
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                            throw;
                        }
                        result.Text = text.ToString();
                        result.TotalSeconds = (DateTime.UtcNow - started).TotalSeconds;
                        return result;
                    }
                }
            }
        }

        private static long ToLong(object o)
        {
            try { return o == null ? 0 : Convert.ToInt64(o); } catch { return 0; }
        }

        private static ClaudeApiException ToException(int status, string body)
        {
            string message = null;
            try { message = Json.Str(Json.Parse(body), "error", "message"); } catch { }
            if (string.IsNullOrEmpty(message)) message = body.Length > 300 ? body.Substring(0, 300) : body;
            switch (status)
            {
                case 401: return new ClaudeApiException(status, "auth", "Your OpenRouter key was rejected. Check it in Settings > Models.");
                case 402: return new ClaudeApiException(status, "credits", "Your OpenRouter account is out of credits.");
                case 404: return new ClaudeApiException(status, "not_found", "OpenRouter doesn't have that model any more - pick another in the Model menu.");
                case 429: return new ClaudeApiException(status, "rate_limit", "Rate limited by OpenRouter. Wait a moment and try again.");
                default: return new ClaudeApiException(status, "error", message);
            }
        }

        private static ClaudeApiException ToSubscriptionException(int status, string body)
        {
            string code = null;
            try { code = Json.Str(Json.Parse(body), "error"); } catch { }
            switch (status)
            {
                case 401: return new ClaudeApiException(status, "subscription_auth", "Your subscription needs to be refreshed. Open Settings > Subscription.");
                case 402: return new ClaudeApiException(status, "allowance", code == "no_subscription" ? "Your subscription is no longer active. Open Settings > Subscription." : "Your plan's AI allowance is used up. Check usage or manage your plan in Settings > Subscription.");
                case 403: return new ClaudeApiException(status, "model_not_in_plan", "This model isn't included in your subscription. Choose an included model.");
                case 413: return new ClaudeApiException(status, "request_too_large", "This request is too large. Remove an attachment or use a smaller screenshot.");
                case 429: return new ClaudeApiException(status, "rate_limit", "Too many requests. Wait a moment and try again.");
                default: return new ClaudeApiException(status, "subscription_error", "The subscription AI service couldn't answer. Try again shortly.");
            }
        }

        /// <summary>Cheap key check: GET /api/v1/key returns details for a valid key.</summary>
        public static async Task<string> TestKeyAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "Enter a key first.";
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key"))
                using (var cts = new CancellationTokenSource(20000))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                    using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode) return null;
                        return (int)resp.StatusCode == 401 ? "OpenRouter rejected this key." : "OpenRouter error " + (int)resp.StatusCode;
                    }
                }
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
