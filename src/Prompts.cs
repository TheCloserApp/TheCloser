using System;
using System.Collections.Generic;
using System.Linq;

namespace TheCloser
{
    /// <summary>
    /// System prompts: "Default (Interview)" plus a library you can edit - the built-ins (General, Interview, Meeting,
    /// Call, the same as the Mac app) and your own ("Create new").
    /// </summary>
    internal static class Prompts
    {
        /// <summary>No prompt picked: the shipped interview prompt, shown as "Default (Interview)".</summary>
        public const string DefaultId = "";
        public const string DefaultName = "Default (Interview)";

        /// <summary>How to read the transcript. Every prompt gets this; the prompt itself says how to answer.</summary>
        public const string BaseRules =
            "You are TheCloser, running privately on the user's computer during a live conversation. " +
            "You receive a live transcript produced by speech recognition. Speaker labels: [Them] = the other people on the call, " +
            "[Me] = the user, [Live] = mixed audio without speaker separation (it may contain both sides). " +
            "The transcript may contain recognition errors, so infer what was most likely said. " +
            "Never mention that you are an AI or that you are reading a transcript.";

        /// <summary>Added to a prompt that says nothing about bold: answers highlight the words the model bolds.</summary>
        public const string KeywordRule = "Bold the 2–3 key terms of each answer (**like this**) so they stand out at a glance.";

        public const string InterviewBody =
            "You are a real-time interview copilot. The user is IN a live job interview right now; " +
            "the interviewer's words arrive as transcript messages and the user reads your reply " +
            "while speaking. Every second counts, so format for instant scanning:\n\n" +
            "- FIRST LINE: the direct opening sentence the user can say verbatim, immediately. " +
            "No preamble, no \"Great question\", no headings, never restate the question.\n" +
            "- Then at most 3 short bullets expanding the answer — a concrete example, a metric, " +
            "a closing point. Bold the 2–3 keywords that matter so they pop while skimming.\n" +
            "- Technical questions: lead with the key idea/answer, then the minimal steps or a " +
            "short snippet. Behavioral questions: structure as situation → action → result " +
            "without labelling the framework.\n" +
            "- Ground every answer in the attached resume/JD/context when present — use the " +
            "user's real projects, employers, and stack, never invented ones.\n" +
            "- If the transcript is a statement rather than a question, reply with one line the " +
            "user could naturally say next.";

        private static readonly PromptDef[] BuiltIns =
        {
            new PromptDef
            {
                Id = "general", Name = "General",
                Text = "You are a concise personal assistant running as a floating overlay on the user's PC. " +
                       "The user may send you live transcription from a meeting or call, manual notes, or a screenshot. " +
                       "Respond helpfully and concisely. Prefer bullet points for structured answers. " +
                       "Do not repeat the user's text back to them unless quoting for clarity."
            },
            new PromptDef { Id = "interview", Name = "Interview", Text = InterviewBody },
            new PromptDef
            {
                Id = "meeting", Name = "Meeting",
                Text = "You are a real-time meeting assistant running as a floating overlay. The user is in a work meeting. Your job is to:\n" +
                       "1. Summarise what has been said clearly in bullet form when asked.\n" +
                       "2. Identify action items, owners, and deadlines from transcription.\n" +
                       "3. When asked what to say next, suggest clarifying or probing questions relevant to the discussion.\n" +
                       "4. Flag any decisions made or commitments given.\n" +
                       "5. Keep responses short — the user is in a live meeting. Max 5 bullets."
            },
            new PromptDef
            {
                Id = "call", Name = "Call",
                Text = "You are a real-time call assistant running as a floating overlay. The user is on a phone or video call. Your job is to:\n" +
                       "1. Summarise the key points of the conversation so far.\n" +
                       "2. Suggest concise, professional responses to what the other party has said.\n" +
                       "3. When asked to rephrase, make the user's intended response clearer and more natural.\n" +
                       "4. Flag any commitments or follow-up items mentioned.\n" +
                       "5. Keep all responses brief — max 3 bullet points or 2 sentences."
            }
        };

        public static bool IsBuiltIn(string id)
        {
            return BuiltIns.Any(p => p.Id == id);
        }

        /// <summary>A built-in as you see it: your edited version if you've changed it, else the original.</summary>
        private static PromptDef Current(AppSettings s, PromptDef builtIn)
        {
            return (s.EditedPrompts == null ? null : s.EditedPrompts.FirstOrDefault(p => p.Id == builtIn.Id)) ?? builtIn;
        }

        /// <summary>The prompt library: built-ins you haven't deleted (with your edits), then your own.</summary>
        public static List<PromptDef> All(AppSettings s)
        {
            var list = BuiltIns.Where(p => s.HiddenPrompts == null || !s.HiddenPrompts.Contains(p.Id)).Select(p => Current(s, p)).ToList();
            list.AddRange(s.CustomPrompts);
            return list;
        }

        /// <summary>A prompt by id; null for the default. A deleted built-in still resolves (older sessions may use it).</summary>
        public static PromptDef Find(AppSettings s, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var builtIn = BuiltIns.FirstOrDefault(p => p.Id == id);
            return All(s).FirstOrDefault(p => p.Id == id) ?? (builtIn != null ? Current(s, builtIn) : null);
        }

        /// <summary>What the setup screen's picker shows for a prompt id.</summary>
        public static string DisplayName(AppSettings s, string id)
        {
            var p = Find(s, id);
            return p != null ? p.Name : DefaultName;
        }

        public static bool IsEdited(AppSettings s, string id)
        {
            return IsBuiltIn(id) && s.EditedPrompts.Any(p => p.Id == id);
        }

        /// <summary>Saves your version of a built-in prompt; the original stays in the app for "Reset to original".</summary>
        public static void EditBuiltIn(AppSettings s, string id, string name, string text)
        {
            var edited = s.EditedPrompts.FirstOrDefault(p => p.Id == id);
            if (edited == null)
            {
                edited = new PromptDef { Id = id };
                s.EditedPrompts.Add(edited);
            }
            edited.Name = name;
            edited.Text = text;
        }

        public static void ResetBuiltIn(AppSettings s, string id)
        {
            s.EditedPrompts.RemoveAll(p => p.Id == id);
        }

        /// <summary>Deletes a prompt; built-ins are hidden (see RestoreBuiltIns). The default can't be deleted, so any prompt can.</summary>
        public static void Delete(AppSettings s, string id)
        {
            if (IsBuiltIn(id))
            {
                if (!s.HiddenPrompts.Contains(id)) s.HiddenPrompts.Add(id);
            }
            else s.CustomPrompts.RemoveAll(p => p.Id == id);
            if (s.PromptId == id) s.PromptId = DefaultId;
        }

        public static void RestoreBuiltIns(AppSettings s)
        {
            s.HiddenPrompts.Clear();
        }

        public static string NewId()
        {
            return "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        /// <summary>The instructions of a prompt id: the shipped interview prompt for the default.</summary>
        public static string Instructions(AppSettings s, string id)
        {
            var p = Find(s, id);
            return p != null ? p.Text.Trim() : InterviewBody;
        }

        /// <summary>
        /// The full system prompt: how to read the transcript, the prompt, and - when the prompt says nothing about bold -
        /// the rule that makes answers bold their key terms, which the answer view highlights.
        /// </summary>
        public static string SystemText(AppSettings s, string id)
        {
            var text = Instructions(s, id);
            if (text.IndexOf("bold", StringComparison.OrdinalIgnoreCase) < 0) text += "\n\n" + KeywordRule;
            return BaseRules + "\n\n" + text;
        }
    }
}
