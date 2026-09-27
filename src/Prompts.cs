using System;
using System.Collections.Generic;
using System.Linq;

namespace TheCloser
{
    /// <summary>Built-in system prompts plus the user's own ("Create new").</summary>
    internal static class Prompts
    {
        public const string DefaultId = "call";

        /// <summary>Rules every prompt gets: how to read the transcript and how to format for a glance.</summary>
        public const string BaseRules =
            "You are TheCloser, a real-time assistant running privately on the user's computer during a live conversation. " +
            "You receive a live transcript produced by speech recognition. Speaker labels: [Them] = the other people on the call, " +
            "[Me] = the user, [Live] = mixed audio without speaker separation (it may contain both sides). " +
            "The transcript may contain recognition errors, so infer what was most likely said. " +
            "The user glances at your reply while talking, so it must be instantly scannable: no preamble, no restating the question, " +
            "no filler like \"Great question\". Use Markdown: a bold one-line answer first, then short bullets. " +
            "Never mention that you are an AI or that you are reading a transcript.";

        public const string CallBody =
            "The user is on a live phone or video call; [Them] is the other person (a customer, client or caller). " +
            "Find the most recent question or request from [Them] and give the user what to say next, naturally, in first person.\n" +
            "- Ground answers in the user's notes and attached files (product details, policies, account info, scripts). Never invent " +
            "prices, policies, dates, or account details that are not there; if something is not covered, suggest an honest way to " +
            "check and follow up.\n" +
            "- Problems and complaints: acknowledge briefly, then give the concrete next step.\n" +
            "- How-to requests: a short numbered list of steps the user can read out.\n" +
            "- If no question is pending, suggest a helpful next thing to say or a clarifying question to ask.";

        private static readonly PromptDef[] BuiltIns =
        {
            new PromptDef { Id = "call", Name = "Call", Text = CallBody },
            new PromptDef
            {
                Id = "sales", Name = "Sales call",
                Text = "The user is selling in a live sales call; [Them] is the prospect. Tell the user what to say next: answer product " +
                       "questions, handle objections (acknowledge, reframe, evidence, check-in question), and suggest a sharp discovery or " +
                       "closing question when useful. Write lines the user can say verbatim in first person. Only state pricing, features, " +
                       "and customer results that appear in the user's notes; if something is not covered, suggest an honest way to follow up."
            },
            new PromptDef
            {
                Id = "meeting", Name = "Meeting",
                Text = "The user is in a live work meeting. Help them contribute: answer questions directed at them, explain unfamiliar " +
                       "terms in one line, and suggest a smart point, question, or decision to push for. When commitments are made, list " +
                       "them under **Action items**. Ground answers in the user's notes when relevant."
            },
            new PromptDef
            {
                Id = "general", Name = "General Q&A",
                Text = "Answer the most recent question heard in the conversation as accurately as possible (facts, trivia, math, " +
                       "definitions, quick explanations). Lead with the answer in bold, then at most two lines of support. If you are " +
                       "unsure, say so briefly rather than guessing."
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

        /// <summary>The prompts you can pick: built-ins you haven't deleted (with your edits), then your own.</summary>
        public static List<PromptDef> All(AppSettings s)
        {
            var list = BuiltIns.Where(p => s.HiddenPrompts == null || !s.HiddenPrompts.Contains(p.Id)).Select(p => Current(s, p)).ToList();
            list.AddRange(s.CustomPrompts);
            return list;
        }

        /// <summary>A prompt by id. A deleted built-in still resolves (older sessions may use it); unknown ids get the first prompt.</summary>
        public static PromptDef Find(AppSettings s, string id)
        {
            var all = All(s);
            var builtIn = BuiltIns.FirstOrDefault(p => p.Id == id);
            return all.FirstOrDefault(p => p.Id == id) ?? (builtIn != null ? Current(s, builtIn) : null) ?? all.FirstOrDefault() ?? BuiltIns[0];
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

        /// <summary>Deletes a prompt; built-ins are hidden (see RestoreBuiltIns). Refuses to delete the last one left.</summary>
        public static bool Delete(AppSettings s, string id)
        {
            if (All(s).Count <= 1) return false;
            if (IsBuiltIn(id))
            {
                if (!s.HiddenPrompts.Contains(id)) s.HiddenPrompts.Add(id);
            }
            else s.CustomPrompts.RemoveAll(p => p.Id == id);
            if (s.PromptId == id) s.PromptId = All(s)[0].Id;
            return true;
        }

        public static void RestoreBuiltIns(AppSettings s)
        {
            s.HiddenPrompts.Clear();
        }

        public static string NewId()
        {
            return "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        /// <summary>The full system prompt text for a prompt id.</summary>
        public static string SystemText(AppSettings s, string id)
        {
            return BaseRules + "\n\n" + Find(s, id).Text.Trim();
        }
    }
}
