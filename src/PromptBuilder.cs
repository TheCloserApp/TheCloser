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
        public List<QaItem> Recent = new List<QaItem>(); // earlier answers, for follow-ups like "shorter"
        public AnswerKind Kind;
        public string Question;                          // Auto/Manual: the question to answer, when known
        public string UserText;
        public string ScreenshotJpegBase64;
        public string Length = "Short";
        public string Effort = "low";
    }

    /// <summary>Builds provider payloads. Stable content (system prompt, context, documents) comes first and is marked
    /// for prompt caching on the Anthropic path; the transcript and question come after the cache breakpoints.</summary>
    internal static class PromptBuilder
    {
        private static string LengthRule(string length)
        {
            switch (length)
            {
                case "Detailed": return "Length: up to about 250 words (code blocks do not count).";
                case "Medium": return "Length: up to about 130 words (code blocks do not count).";
                default: return "Length: up to about 70 words (code blocks do not count). Be ruthless about brevity.";
            }
        }

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

        /// <summary>Claude Messages API payload (direct Anthropic).</summary>
        public static Dictionary<string, object> ForAnthropic(AnswerRequest r, string model)
        {
            var system = new List<object> { Json.Obj("type", "text", "text", r.SystemPrompt + "\n\n" + LengthRule(r.Length)) };
            if (!string.IsNullOrWhiteSpace(r.ContextText))
                system.Add(Json.Obj("type", "text", "text", "The user's own background material:\n" + r.ContextText));
            ((Dictionary<string, object>)system[system.Count - 1])["cache_control"] = Json.Obj("type", "ephemeral");

            var content = new List<object>();
            Dictionary<string, object> lastDoc = null;
            foreach (var d in r.Documents)
            {
                lastDoc = d.Kind == "pdf"
                    ? Json.Obj("type", "document", "title", d.Name, "source", Json.Obj("type", "base64", "media_type", "application/pdf", "data", d.Base64))
                    : Json.Obj("type", "image", "source", Json.Obj("type", "base64", "media_type", d.MediaType, "data", d.Base64));
                content.Add(lastDoc);
            }
            if (lastDoc != null) lastDoc["cache_control"] = Json.Obj("type", "ephemeral");
            if (!string.IsNullOrEmpty(r.ScreenshotJpegBase64))
                content.Add(Json.Obj("type", "image", "source", Json.Obj("type", "base64", "media_type", "image/jpeg", "data", r.ScreenshotJpegBase64)));
            content.Add(Json.Obj("type", "text", "text", TaskText(r)));

            var body = Json.Obj(
                "model", model,
                "max_tokens", 16000,
                "system", system,
                "messages", new List<object> { Json.Obj("role", "user", "content", content) });
            if (ClaudeClient.SupportsEffort(model) && !string.IsNullOrEmpty(r.Effort))
                body["output_config"] = Json.Obj("effort", r.Effort);
            return body;
        }

        /// <summary>OpenRouter chat-completions payload (OpenAI message format).</summary>
        public static Dictionary<string, object> ForOpenRouter(AnswerRequest r, string model)
        {
            var system = r.SystemPrompt + "\n\n" + LengthRule(r.Length);
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

        public static Dictionary<string, object> BuildPing(string model)
        {
            var body = Json.Obj(
                "model", model,
                "max_tokens", 1024,
                "messages", new List<object> { Json.Obj("role", "user", "content", "Reply with exactly: OK") });
            if (ClaudeClient.SupportsEffort(model)) body["output_config"] = Json.Obj("effort", "low");
            return body;
        }
    }
}
