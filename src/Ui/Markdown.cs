using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TheCloser.Ui
{
    /// <summary>
    /// Renders the Markdown subset models use in answers (headings, bullets, numbers, bold, italic, code) as WPF.
    /// Bold spans are the answer's keywords, drawn in the chosen keyword style. "Plain text" draws everything but code as
    /// plain lines.
    /// </summary>
    internal static class MarkdownView
    {
        private static readonly Regex Bullet = new Regex(@"^(\s*)([-*+•])\s+(.*)$");
        private static readonly Regex Numbered = new Regex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$");
        private static readonly Regex Heading = new Regex(@"^(#{1,6})\s+(.*)$");

        public static StackPanel Render(string markdown, double size)
        {
            return Render(markdown, size, "bold");
        }

        public static StackPanel Render(string markdown, double size, string keywordStyle)
        {
            var root = new StackPanel();
            var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
            StringBuilder code = null;
            string lang = "";
            bool lastBlank = true;
            bool plain = keywordStyle == KeywordStyles.Plain;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("```"))
                {
                    if (code == null)
                    {
                        code = new StringBuilder();
                        lang = line.Trim().Substring(3).Trim();
                    }
                    else
                    {
                        root.Children.Add(CodeBlock(code.ToString().TrimEnd('\n'), lang, size));
                        code = null;
                    }
                    continue;
                }
                if (code != null) { code.Append(raw).Append('\n'); continue; }

                if (line.Trim().Length == 0)
                {
                    lastBlank = true;
                    continue;
                }
                double top = lastBlank && root.Children.Count > 0 ? size * 0.55 : size * 0.2;
                lastBlank = false;
                var trimmed = line.Trim();

                if (Regex.IsMatch(trimmed, @"^(-{3,}|\*{3,}|_{3,})$"))
                {
                    if (plain) continue;
                    root.Children.Add(new Border { Height = 1, Background = U.Line, Margin = new Thickness(0, size * 0.6, 0, size * 0.4) });
                    continue;
                }

                // Plain text: headings, list items and quotes read as ordinary lines.
                var m = Heading.Match(trimmed);
                if (m.Success && plain) { root.Children.Add(Para(m.Groups[2].Value, size, top, keywordStyle)); continue; }
                if (m.Success)
                {
                    var tb = Para(m.Groups[2].Value, size * (m.Groups[1].Value.Length <= 2 ? 1.12 : 1.04), top + size * 0.15, keywordStyle);
                    tb.FontWeight = FontWeights.Bold;
                    root.Children.Add(tb);
                    continue;
                }

                m = Bullet.Match(line);
                if (m.Success && plain) { root.Children.Add(Para(m.Groups[3].Value, size, top, keywordStyle)); continue; }
                if (m.Success)
                {
                    root.Children.Add(ListItem("•", m.Groups[3].Value, Depth(m.Groups[1].Value), size, top, keywordStyle));
                    continue;
                }
                m = Numbered.Match(line);
                if (m.Success && plain) { root.Children.Add(Para(m.Groups[2].Value + ". " + m.Groups[3].Value, size, top, keywordStyle)); continue; }
                if (m.Success)
                {
                    root.Children.Add(ListItem(m.Groups[2].Value + ".", m.Groups[3].Value, Depth(m.Groups[1].Value), size, top, keywordStyle));
                    continue;
                }

                if (trimmed.StartsWith(">") && plain) { root.Children.Add(Para(trimmed.TrimStart('>', ' '), size, top, keywordStyle)); continue; }
                if (trimmed.StartsWith(">"))
                {
                    var q = Para(trimmed.TrimStart('>', ' '), size, 0, keywordStyle);
                    q.Foreground = U.Text2;
                    root.Children.Add(new Border
                    {
                        BorderBrush = U.Border,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(12, 0, 0, 0),
                        Margin = new Thickness(0, top, 0, 0),
                        Child = q
                    });
                    continue;
                }

                root.Children.Add(Para(line, size, top, keywordStyle));
            }
            if (code != null) root.Children.Add(CodeBlock(code.ToString().TrimEnd('\n'), lang, size));
            return root;
        }

        private static int Depth(string indent)
        {
            return Math.Min(3, indent.Replace("\t", "  ").Length / 2);
        }

        private static TextBlock Para(string text, double size, double top, string keywordStyle)
        {
            var tb = new TextBlock
            {
                FontFamily = U.Font,
                FontSize = size,
                Foreground = U.Text,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.38,
                Margin = new Thickness(0, top, 0, 0)
            };
            AddInlines(tb.Inlines, text, size, keywordStyle);
            return tb;
        }

        private static Grid ListItem(string marker, string text, int depth, double size, double top, string keywordStyle)
        {
            var g = new Grid { Margin = new Thickness(depth * size * 1.2, top, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size * (marker.Length > 1 ? 1.5 : 1.05)) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new TextBlock { Text = marker, FontFamily = U.Font, FontSize = size, Foreground = marker.Length > 1 ? U.Text2 : U.Text, LineHeight = size * 1.38 };
            var body = Para(text, size, 0, keywordStyle);
            Grid.SetColumn(body, 1);
            g.Children.Add(dot);
            g.Children.Add(body);
            return g;
        }

        /// <summary>A code block like the Mac app's: the language and a Copy button above the code.</summary>
        private static Border CodeBlock(string code, string lang, double size)
        {
            double mono = Math.Max(11, size * 0.78);
            var tb = new TextBlock
            {
                Text = code.Replace("\t", "    "),
                FontFamily = U.Mono,
                FontSize = mono,
                Foreground = U.B(0xFFD6DEEB),
                LineHeight = mono * 1.45,
                Margin = new Thickness(14, 10, 14, 12)
            };
            var label = new TextBlock
            {
                Text = (lang.Length == 0 ? "code" : lang).ToUpperInvariant(),
                FontFamily = U.Mono,
                FontSize = Math.Max(10, size * 0.66),
                Foreground = U.Text2,
                VerticalAlignment = VerticalAlignment.Center
            };
            var copyLabel = new TextBlock { Text = "Copy", FontSize = Math.Max(11, size * 0.74), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 1) };
            var copyContent = new StackPanel { Orientation = Orientation.Horizontal };
            copyContent.Children.Add(U.Icon(U.GCopy, Math.Max(10, size * 0.7), null));
            copyContent.Children.Add(copyLabel);
            var copy = U.Btn("Btn.Ghost", copyContent, () => CopyCode(code, copyLabel), "Copy code");
            copy.Padding = new Thickness(7, 3, 7, 3);

            var header = new Grid { Margin = new Thickness(14, 4, 6, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(label);
            Grid.SetColumn(copy, 1);
            header.Children.Add(copy);

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(new Border { Height = 1, Background = U.Border });
            body.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = tb
            });
            return new Border
            {
                Background = U.CodeBg,
                BorderBrush = U.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, size * 0.5, 0, size * 0.2),
                Child = body
            };
        }

        private static void CopyCode(string code, TextBlock label)
        {
            try { Clipboard.SetText(code); }
            catch { return; } // the clipboard is busy; the button just stays "Copy"
            label.Text = "Copied";
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            reset.Tick += delegate { reset.Stop(); label.Text = "Copy"; };
            reset.Start();
        }

        /// <summary>Inline spans: **bold** (a keyword, in the keyword style), __bold__, *italic*, `code`.</summary>
        private static void AddInlines(InlineCollection inlines, string text, double size, string keywordStyle)
        {
            var keywordFg = KeywordStyles.Foreground(keywordStyle);
            var keywordBg = KeywordStyles.Background(keywordStyle);
            bool bold = false, italic = false;
            var buf = new StringBuilder();
            Action flush = delegate
            {
                if (buf.Length == 0) return;
                var run = new Run(buf.ToString());
                // "Off" and "Plain text": keywords read as plain text. Plain text drops italics too.
                if (bold && keywordStyle != KeywordStyles.Off && keywordStyle != KeywordStyles.Plain)
                {
                    run.FontWeight = FontWeights.Bold;
                    if (keywordFg != null) run.Foreground = keywordFg;
                    if (keywordBg != null) run.Background = keywordBg;
                }
                if (italic && keywordStyle != KeywordStyles.Plain) run.FontStyle = FontStyles.Italic;
                inlines.Add(run);
                buf.Length = 0;
            };
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        flush();
                        inlines.Add(new Run(text.Substring(i + 1, end - i - 1))
                        {
                            FontFamily = U.Mono,
                            FontSize = size * 0.88,
                            Foreground = U.B(0xFFB8E0FF),
                            Background = U.B(0xFF26262B)
                        });
                        i = end + 1;
                        continue;
                    }
                }
                if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
                {
                    flush();
                    bold = !bold;
                    i += 2;
                    continue;
                }
                if (c == '*' && (italic || (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))))
                {
                    flush();
                    italic = !italic;
                    i++;
                    continue;
                }
                buf.Append(c);
                i++;
            }
            flush();
        }

        /// <summary>Plain text of a rendered tree (self-test).</summary>
        internal static string PlainText(DependencyObject root)
        {
            var sb = new StringBuilder();
            Walk(root, sb);
            return sb.ToString();
        }

        private static void Walk(DependencyObject o, StringBuilder sb)
        {
            var tb = o as TextBlock;
            if (tb != null)
            {
                foreach (var inline in tb.Inlines)
                {
                    var r = inline as Run;
                    if (r != null) sb.Append(r.Text);
                }
                if (tb.Inlines.Count == 0) sb.Append(tb.Text);
                sb.Append('|');
                return;
            }
            var panel = o as Panel;
            if (panel != null) { foreach (UIElement c in panel.Children) Walk(c, sb); return; }
            var border = o as Border;
            if (border != null && border.Child != null) { Walk(border.Child, sb); return; }
            var sv = o as ScrollViewer;
            if (sv != null && sv.Content is DependencyObject) Walk((DependencyObject)sv.Content, sb);
        }
    }
}
