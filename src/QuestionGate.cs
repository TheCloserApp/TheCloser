using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TheCloser
{
    internal sealed class GateVerdict
    {
        public bool Answer;
        public string Question;   // the question, cleaned up by the model (null if it didn't give one)
    }

    /// <summary>
    /// Before an auto-answer, asks a small fast model (Claude Haiku, through OpenRouter) whether the newest line of the conversation is
    /// worth answering - a real question or request - or just greetings, filler, or half a sentence.
    /// </summary>
    internal static class QuestionGate
    {
        private const string OpenRouterModel = "anthropic/claude-haiku-4.5";

        private const string Instructions =
            "You are the trigger for a live-conversation copilot. You get the last few lines of a speech-recognition transcript " +
            "([Them] = the other people, [Me] = the user, [Live] = mixed audio) and decide whether the copilot should write an " +
            "answer for the user right now.\n" +
            "Reply ANSWER when the newest line, read together with the lines just before it, asks a question or makes a request " +
            "the user would want help responding to: a direct question, \"tell me about...\", \"explain...\", \"walk me through...\", " +
            "a technical or interview question, an objection, or a request for information.\n" +
            "Reply SKIP for greetings, small talk, \"can you hear me\", thanks, acknowledgements, filler, statements that need no " +
            "reply, a sentence that is obviously cut off mid-way, or garbled text with no clear question.\n" +
            "Output exactly one line: either SKIP, or ANSWER: followed by the question rewritten as one clear sentence " +
            "(fix speech-recognition errors, at most 25 words).";

        /// <summary>Which provider and model the check uses, or null without a subscription or OpenRouter key.</summary>
        public static ModelRoute Route(AppSettings s)
        {
            if (s.UseSubscription) return new ModelRoute { Provider = "subscription", ApiModel = OpenRouterModel };
            if (s.EffectiveOpenRouterKey.Length > 0)
                return new ModelRoute { Provider = "openrouter", ApiModel = OpenRouterModel, Key = s.EffectiveOpenRouterKey };
            return null;
        }

        /// <param name="recent">The last few lines in [Speaker] text form, newest last.</param>
        /// <param name="mineCount">True when the other side hasn't spoken yet, so the user's own questions count.</param>
        public static async Task<GateVerdict> CheckAsync(AppSettings s, string recent, bool mineCount, CancellationToken ct, SubscriptionClient billing = null)
        {
            var route = Route(s);
            if (route == null) throw new InvalidOperationException("No API key for the question check.");

            var user = "<transcript>\n" + recent.Trim() + "\n</transcript>\n\n" +
                       (mineCount
                           ? "The other side hasn't spoken yet (the user may be practising or asking out loud), so questions in [Me] lines count."
                           : "Only questions from [Them] or [Live] count; the user's own [Me] questions to the other side do not.") +
                       "\nShould the copilot answer the newest line?";

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(8000);
                var body = Json.Obj(
                    "model", route.ApiModel,
                    "max_tokens", 80,
                    "temperature", 0,
                    "messages", new List<object>
                    {
                        Json.Obj("role", "system", "content", Instructions),
                        Json.Obj("role", "user", "content", user)
                    });
                StreamResult r;
                if (route.Provider == "subscription")
                {
                    if (billing == null) throw new SubscriptionException("subscription_unavailable", "Refresh your subscription in Settings.");
                    r = await billing.StreamAsync(body, delegate { }, timeout.Token).ConfigureAwait(false);
                }
                else r = await OpenRouterClient.StreamAsync(route.Key, body, delegate { }, timeout.Token).ConfigureAwait(false);
                return Parse(r.Text);
            }
        }

        /// <summary>"ANSWER: What is JavaScript?" -> answer it; anything else -> skip.</summary>
        internal static GateVerdict Parse(string reply)
        {
            var line = (reply ?? "").Trim();
            int nl = line.IndexOf('\n');
            if (nl >= 0) line = line.Substring(0, nl).Trim();
            line = line.Trim('*', '`', ' ');
            if (!line.StartsWith("ANSWER", StringComparison.OrdinalIgnoreCase)) return new GateVerdict();
            var q = line.Substring("ANSWER".Length).TrimStart(':', '-', ' ', '*').Trim().Trim('"');
            return new GateVerdict { Answer = true, Question = q.Length > 0 ? q : null };
        }
    }
}
