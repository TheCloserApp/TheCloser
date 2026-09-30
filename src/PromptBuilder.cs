using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TheCloser
{
    internal enum AnswerKind { Auto, Manual, Ask, Screen }

    /// <summary>Everything an answer needs, independent of which API serves it.</summary>
    internal sealed class AnswerRequest
    {
        public string SystemPrompt;
        public string ContextText;                       // reference file + context + text attachments
        public List<Attachment> Documents = new List<Attachment>(); // PDFs and images
        public string Transcript;
        public List<QaItem> Recent = new List<QaItem>(); // earlier answers in this session (Settings > Memory)
        public List<QaItem> Past = new List<QaItem>();   // one recent answer from each recent other session (Settings > Memory)
        public AnswerKind Kind;
        public string Question;                          // Auto/Manual: the question to answer, when known
        public string UserText;
        public string ScreenshotJpegBase64;
        public string Effort = "low";
    }

    /// <summary>Builds the OpenRouter payload. Stable content (system prompt, context, documents) comes first, so
    /// providers that cache prompts can reuse it; the transcript and question come last.</summary>
    internal static class PromptBuilder
    {
        /// <summary>Collects the user's context (reference file, notes, attached files) for a request.</summary>
        public static void AddContext(AnswerRequest r, AppSettings s)
        {
            var ctx = new StringBuilder();
            var files = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.ResumeFile)) files.Add(s.ResumeFile);
            files.AddRange(s.Files);
            foreach (var path in files)
            {
                var a = Attachments.Load(path);
                if (a == null) continue;
                if (a.Kind == "text")
                {
                    var tag = path == s.ResumeFile ? "reference" : "file";
                    ctx.Append("<").Append(tag).Append(" name=\"").Append(a.Name).Append("\">\n").Append(a.Text.Trim()).Append("\n</").Append(tag).Append(">\n");
                }
                else r.Documents.Add(a);
            }
            if (!string.IsNullOrWhiteSpace(s.Context))
                ctx.Append("<context>\n").Append(s.Context.Trim()).Append("\n</context>\n");
            r.ContextText = ctx.ToString();
        }

        private static string TaskText(AnswerRequest r)
        {
            var task = new StringBuilder();
            if (r.Past.Count > 0)
            {
                task.Append("<past_sessions>\n");
                foreach (var qa in r.Past)
                    task.Append("Q: ").Append(qa.Question).Append("\nA: ").Append(qa.CurrentText).Append("\n\n");
                task.Append("</past_sessions>\n\n");
            }
            if (r.Recent.Count > 0)
            {
                task.Append("<earlier_answers>\n");
                foreach (var qa in r.Recent)
                    task.Append("Q: ").Append(qa.Question).Append("\nA: ").Append(qa.CurrentText).Append("\n\n");
                task.Append("</earlier_answers>\n\n");
            }
            task.Append("<transcript>\n").Append(string.IsNullOrWhiteSpace(r.Transcript) ? "(nothing transcribed yet)" : r.Transcript.Trim()).Append("\n</transcript>\n\n");
            switch (r.Kind)
            {
                case AnswerKind.Ask:
                    task.Append("The user typed this request to you (use the transcript and earlier answers as context): ").Append(r.UserText);
                    break;
                case AnswerKind.Screen:
                    task.Append("The image is a screenshot of the user's screen right now. Identify the question, problem, or task it shows " +
                                "(for example a coding challenge, a multiple-choice question, a slide, a document, or a chat message) and give the best answer. " +
                                "For coding problems give the approach, complete working code, and complexity. If the screen contains nothing to answer, " +
                                "say in one line what you see and how you can help.");
                    if (!string.IsNullOrWhiteSpace(r.UserText)) task.Append("\nThe user added: ").Append(r.UserText);
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(r.Question))
                        task.Append("Answer this question, just asked in the conversation (use the transcript for context): ").Append(r.Question);
                    else
                        task.Append("Respond to the most recent question or prompt from the other side of the conversation.");
                    break;
            }
            return task.ToString();
        }

        /// <summary>OpenRouter chat-completions payload (OpenAI message format).</summary>
        public static Dictionary<string, object> ForOpenRouter(AnswerRequest r, string model)
        {
            var system = r.SystemPrompt;
            if (!string.IsNullOrWhiteSpace(r.ContextText)) system += "\n\nThe user's own background material:\n" + r.ContextText;

            var content = new List<object>();
            foreach (var d in r.Documents)
            {
                if (d.Kind == "pdf")
                    content.Add(Json.Obj("type", "file", "file", Json.Obj("filename", d.Name, "file_data", "data:application/pdf;base64," + d.Base64)));
                else
                    content.Add(Json.Obj("type", "image_url", "image_url", Json.Obj("url", "data:" + d.MediaType + ";base64," + d.Base64)));
            }
            if (!string.IsNullOrEmpty(r.ScreenshotJpegBase64))
                content.Add(Json.Obj("type", "image_url", "image_url", Json.Obj("url", "data:image/jpeg;base64," + r.ScreenshotJpegBase64)));
            content.Add(Json.Obj("type", "text", "text", TaskText(r)));

            return Json.Obj(
                "model", model,
                "max_tokens", 8000,
                "reasoning", Json.Obj("effort", string.IsNullOrEmpty(r.Effort) ? "low" : r.Effort, "exclude", true),
                "messages", new List<object>
                {
                    Json.Obj("role", "system", "content", system),
                    Json.Obj("role", "user", "content", content)
                });
        }
    }
}
