using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TheCloser
{
    internal sealed class ModelInfo
    {
        public string Slug;   // OpenRouter id, e.g. "anthropic/claude-sonnet-5"
        public string Name;

        public ModelInfo(string slug, string name)
        {
            Slug = slug;
            Name = name;
        }
    }

    /// <summary>Where a request goes: straight to Anthropic, or through OpenRouter.</summary>
    internal sealed class ModelRoute
    {
        public string Provider;   // "anthropic" | "openrouter" | null when no key is available
        public string ApiModel;
        public string Key;
        public string Missing;    // what to tell the user when Provider is null
    }

    /// <summary>
    /// The model picker. Claude models go directly to the Claude API when an Anthropic key is set (prompt caching,
    /// native PDFs); everything else - and Claude without an Anthropic key - goes through OpenRouter.
    /// </summary>
    internal static class ModelCatalog
    {
        public static readonly ModelInfo[] Curated =
        {
            new ModelInfo("anthropic/claude-opus-5", "Claude Opus 5"),
            new ModelInfo("anthropic/claude-sonnet-5", "Claude Sonnet 5"),
            new ModelInfo("anthropic/claude-haiku-4.5", "Claude Haiku 4.5"),
            new ModelInfo("openai/gpt-5.4-mini", "GPT-5.4 mini"),
            new ModelInfo("google/gemini-3.8-flash", "Gemini 3.8 Flash"),
            new ModelInfo("google/gemini-3.5-flash-lite", "Gemini 3.5 Flash Lite"),
            new ModelInfo("x-ai/grok-4.7", "Grok 4.7"),
            new ModelInfo("moonshotai/kimi-k2.6", "Kimi K2.6")
        };

        public static IEnumerable<string> DefaultEnabled
        {
            get { return Curated.Select(m => m.Slug); }
        }

        private static List<ModelInfo> _remote;
        public static List<ModelInfo> Remote { get { return _remote; } }

        public static string Group(string slug)
        {
            var p = (slug ?? "").Split('/')[0].ToLowerInvariant();
            switch (p)
            {
                case "anthropic": return "Anthropic";
                case "openai": return "OpenAI";
                case "google": return "Google";
                case "x-ai": return "xAI";
                case "moonshotai": return "Kimi";
                case "meta-llama": return "Meta";
                case "mistralai": return "Mistral";
                case "deepseek": return "DeepSeek";
                case "qwen": return "Qwen";
                case "z-ai": return "Z.ai";
                default: return p.Length == 0 ? "Other" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p);
            }
        }

        public static string Name(string slug)
        {
            var m = Curated.FirstOrDefault(x => x.Slug == slug);
            if (m != null) return m.Name;
            if (_remote != null)
            {
                m = _remote.FirstOrDefault(x => x.Slug == slug);
                if (m != null) return m.Name;
            }
            var tail = (slug ?? "").Contains("/") ? slug.Substring(slug.IndexOf('/') + 1) : slug;
            return tail;
        }

        public static bool IsAnthropic(string slug)
        {
            return (slug ?? "").StartsWith("anthropic/");
        }

        /// <summary>"anthropic/claude-haiku-4.5" -> "claude-haiku-4-5" (Claude API model id).</summary>
        public static string AnthropicApiId(string slug)
        {
            return slug.Substring("anthropic/".Length).Replace('.', '-');
        }

        /// <summary>
        /// Bare Claude API ids ("claude-haiku-4-5", saved by older versions or typed by hand) -> "anthropic/claude-haiku-4.5",
        /// so they route to the Anthropic key instead of asking for an OpenRouter one.
        /// </summary>
        public static string NormalizeSlug(string slug)
        {
            slug = (slug ?? "").Trim();
            if (!slug.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return slug;
            var id = slug.ToLowerInvariant();
            var known = Curated.FirstOrDefault(m => IsAnthropic(m.Slug) && AnthropicApiId(m.Slug) == id);
            return known != null ? known.Slug : "anthropic/" + id;
        }

        public static ModelRoute Resolve(AppSettings s, string slug)
        {
            var r = new ModelRoute();
            if (IsAnthropic(slug) && s.EffectiveAnthropicKey.Length > 0)
            {
                r.Provider = "anthropic";
                r.ApiModel = AnthropicApiId(slug);
                r.Key = s.EffectiveAnthropicKey;
                return r;
            }
            if (s.EffectiveOpenRouterKey.Length > 0)
            {
                r.Provider = "openrouter";
                r.ApiModel = slug;
                r.Key = s.EffectiveOpenRouterKey;
                return r;
            }
            r.Missing = IsAnthropic(slug)
                ? "Add your Anthropic or OpenRouter key to start. Click to open API keys."
                : "Add your OpenRouter key to use " + Name(slug) + ". Click to open API keys.";
            return r;
        }

        /// <summary>Fetches OpenRouter's public model list (no key needed) for "Show all".</summary>
        public static async Task<List<ModelInfo>> FetchRemoteAsync()
        {
            if (_remote != null) return _remote;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                var json = await http.GetStringAsync("https://openrouter.ai/api/v1/models").ConfigureAwait(false);
                var list = new List<ModelInfo>();
                var data = Json.Get(Json.Parse(json), "data") as object[];
                if (data != null)
                {
                    foreach (var item in data)
                    {
                        var id = Json.Str(item, "id");
                        if (string.IsNullOrEmpty(id) || id.Contains(":") || id.StartsWith("~")) continue;
                        var name = Json.Str(item, "name") ?? id;
                        int colon = name.IndexOf(": ");
                        if (colon > 0) name = name.Substring(colon + 2);
                        list.Add(new ModelInfo(id, name));
                    }
                }
                list.Sort((a, b) => string.Compare(Group(a.Slug) + a.Name, Group(b.Slug) + b.Name, StringComparison.OrdinalIgnoreCase));
                _remote = list;
                return list;
            }
        }
    }

    /// <summary>Streams an answer from whichever provider the selected model routes to.</summary>
    internal static class AnswerEngine
    {
        private static readonly ClaudeClient Claude = new ClaudeClient();

        public static Task<StreamResult> StreamAsync(AppSettings s, AnswerRequest req, Action<string> onText, CancellationToken ct)
        {
            var route = ModelCatalog.Resolve(s, s.Model);
            if (route.Provider == null) throw new ClaudeApiException(401, "missing_key", route.Missing.Replace(" Click to open API keys.", ""));
            if (route.Provider == "anthropic")
                return Claude.StreamAsync(route.Key, PromptBuilder.ForAnthropic(req, route.ApiModel), onText, ct);
            return OpenRouterClient.StreamAsync(route.Key, PromptBuilder.ForOpenRouter(req, route.ApiModel), onText, ct);
        }
    }
}
