using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TheCloser.Ui
{
    /// <summary>
    /// The live interview card, as on the Mac:
    ///   the strip: [● 00:13] what's being heard           [⌄ full transcript] [≡ one Q&amp;A / all] [eye: hide the strip]
    ///   the full transcript (the ⌄ drop-down): every line with a copy button, and Copy all
    ///   the answers: one question and answer at a time with ‹ 2/5 › (focus mode), or the whole conversation.
    /// The strip and focus mode are also in the … menu.
    /// </summary>
    internal sealed class SessionView : Grid
    {
        private readonly OverlayWindow W;
        private SessionController Ctl { get { return W.Ctl; } }
        private AppSettings S { get { return W.S; } }

        private readonly Grid _strip;
        private readonly Border _pill;
        private readonly Ellipse _dot;
        private readonly TextBlock _timer, _heard;
        private readonly Button _continueBtn, _transcriptBtn, _focusBtn, _hideBtn;
        private readonly Border _transcriptPanel;
        private readonly StackPanel _transcriptLines = new StackPanel();
        private readonly ScrollViewer _transcriptScroll;
        private readonly Border _stripLine;
        private readonly Grid _body;
        private readonly FrameworkElement _empty;
        private readonly TextBlock _emptyTitle, _emptySub;
        private readonly ScrollViewer _qaScroll;
        private readonly StackPanel _qaList = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        private readonly Dictionary<QaItem, ContentControl> _answerHosts = new Dictionary<QaItem, ContentControl>();
        private bool _transcriptOpen;
        private int _focusOffset;          // 0 = the newest question; 1 = the one before, ...
        private int _renderedQaCount = -1;
        private int _renderedTextSize;
        private string _renderedKeywordStyle;

        public SessionView(OverlayWindow w)
        {
            W = w;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // strip
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // full transcript (drop-down)
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // divider
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // answers

            _strip = new Grid { Margin = new Thickness(20, 16, 16, 14) };
            _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _strip.ColumnDefinitions.Add(new ColumnDefinition());
            _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _dot = new Ellipse { Width = 8, Height = 8, Fill = U.Red, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _timer = new TextBlock { FontFamily = U.Mono, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = U.RecText, VerticalAlignment = VerticalAlignment.Center, Text = "00:00" };
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
            pillRow.Children.Add(_dot);
            pillRow.Children.Add(_timer);
            _pill = new Border { Background = U.RecBg, CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 4, 12, 5), Child = pillRow, VerticalAlignment = VerticalAlignment.Center };
            U.Pulse(_dot);
            _strip.Children.Add(_pill);

            _heard = new TextBlock
            {
                FontFamily = U.Font,
                FontSize = 17,
                Foreground = U.Text3,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 12, 2)
            };
            Grid.SetColumn(_heard, 1);
            _strip.Children.Add(_heard);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _continueBtn = U.Btn("Btn.White", U.IconText(U.GPlay, "Continue", 8), () => W.ContinueSession());
            _continueBtn.Padding = new Thickness(14, 7, 16, 7);
            _continueBtn.FontSize = 14;
            _transcriptBtn = U.Circle(U.GDown, ToggleTranscript, "", 13);
            _focusBtn = U.Circle(U.GList, () => W.SetFocusMode(!S.FocusMode), "", 14);
            _hideBtn = U.Btn("Btn.Circle", U.EyeSlash(14, null), () => W.SetShowTranscript(false), "Hide the transcript — bring it back from the … menu");
            foreach (var b in new FrameworkElement[] { _continueBtn, _transcriptBtn, _focusBtn, _hideBtn })
            {
                b.Margin = new Thickness(8, 0, 0, 0);
                buttons.Children.Add(b);
            }
            Grid.SetColumn(buttons, 2);
            _strip.Children.Add(buttons);
            Children.Add(_strip);

            // Full transcript: every line, each with a copy button; Copy all in its header.
            _transcriptScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 240, Content = _transcriptLines };
            var transcriptHead = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            transcriptHead.ColumnDefinitions.Add(new ColumnDefinition());
            transcriptHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = U.T("Full transcript", 13, U.Text3, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            transcriptHead.Children.Add(title);
            var copyAll = U.Btn("Btn.Ghost", U.IconText(U.Icon(U.GCopy, 12, null), "Copy all", 6), CopyAll, "Copy the full transcript");
            copyAll.FontSize = 13;
            copyAll.Padding = new Thickness(8, 4, 8, 4);
            Grid.SetColumn(copyAll, 1);
            transcriptHead.Children.Add(copyAll);
            var transcriptStack = new StackPanel();
            transcriptStack.Children.Add(transcriptHead);
            transcriptStack.Children.Add(_transcriptScroll);
            _transcriptPanel = new Border
            {
                Background = U.B(0xFF0A0A0A),
                Padding = new Thickness(20, 10, 14, 10),
                Child = transcriptStack,
                BorderBrush = U.Line,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Visibility = Visibility.Collapsed
            };
            SetRow(_transcriptPanel, 1);
            Children.Add(_transcriptPanel);

            _stripLine = new Border { Height = 1, Background = U.Line };
            SetRow(_stripLine, 2);
            Children.Add(_stripLine);

            // Answers: empty state, or the Q&A
            _body = new Grid();
            SetRow(_body, 3);
            Children.Add(_body);

            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 40, 24, 44) };
            empty.Children.Add(U.Waveform(30, U.Text3, true));
            _emptyTitle = U.T("Waiting for the first question", 17, U.Text2, FontWeights.SemiBold);
            _emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyTitle.Margin = new Thickness(0, 16, 0, 0);
            _emptySub = U.T("The answer appears here the moment the interviewer finishes asking.", 14, U.Text3);
            _emptySub.HorizontalAlignment = HorizontalAlignment.Center;
            _emptySub.TextAlignment = TextAlignment.Center;
            _emptySub.Margin = new Thickness(0, 6, 0, 0);
            empty.Children.Add(_emptyTitle);
            empty.Children.Add(_emptySub);
            _empty = empty;
            _body.Children.Add(_empty);

            _qaScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _qaList, Padding = new Thickness(0, 0, 4, 0) };
            _body.Children.Add(_qaScroll);
        }

        /// <summary>Focus mode (one Q&amp;A with arrows) applies during a live interview; a saved one shows everything.</summary>
        private bool Focused { get { return S.FocusMode && Ctl.Active; } }

        private void ToggleTranscript()
        {
            _transcriptOpen = !_transcriptOpen;
            if (_transcriptOpen) RefreshTranscript();
            RefreshState();
            if (_transcriptOpen) _transcriptScroll.ScrollToEnd();
        }

        /// <summary>Strip state: timer, what's being heard, buttons.</summary>
        public void RefreshState()
        {
            var s = Ctl.Current;
            if (Ctl.Active)
            {
                _pill.Background = Ctl.Paused ? U.Chip : U.RecBg;
                _dot.Fill = Ctl.Paused ? U.Text3 : U.Red;
                _timer.Foreground = Ctl.Paused ? U.Text2 : U.RecText;
            }
            else
            {
                _pill.Background = U.Chip;
                _dot.Fill = U.Text3;
                _timer.Foreground = U.Text2;
            }
            // The strip hides only during a live interview (the eye); a saved session always shows it, with Continue.
            bool strip = !Ctl.Active || S.ShowTranscript;
            _strip.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
            _stripLine.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
            _transcriptPanel.Visibility = strip && _transcriptOpen ? Visibility.Visible : Visibility.Collapsed;

            _continueBtn.Visibility = !Ctl.Active && s != null ? Visibility.Visible : Visibility.Collapsed;
            bool anything = s != null && (s.Lines.Count > 0 || Ctl.OpenPartials().Count > 0);
            _transcriptBtn.Visibility = anything || _transcriptOpen ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)_transcriptBtn.Content).Text = _transcriptOpen ? U.GUp : U.GDown;
            _transcriptBtn.ToolTip = _transcriptOpen ? "Hide the full transcript" : "Show the full transcript — every line, each with a copy button";
            _focusBtn.Visibility = Ctl.Active ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)_focusBtn.Content).Text = S.FocusMode ? U.GList : U.GCompress;
            _focusBtn.ToolTip = S.FocusMode ? "Show the full conversation" : "Focus on the current answer";
            _hideBtn.Visibility = Ctl.Active ? Visibility.Visible : Visibility.Collapsed;

            if (!Ctl.Active) _emptyTitle.Text = s == null ? "No session" : "No questions in this session";
            else if (Ctl.Paused) _emptyTitle.Text = "Interview paused";
            else _emptyTitle.Text = "Waiting for the first question";
            _emptySub.Text = Ctl.Active && !Ctl.Paused ? "The answer appears here the moment the interviewer finishes asking." :
                Ctl.Active ? "Resume from the … menu when you're ready." : "Press Continue to pick this interview back up.";
            Tick();
        }

        /// <summary>Called ~20x a second: timer, live heard text, streaming answer.</summary>
        public void Tick()
        {
            var s = Ctl.Current;
            var t = Ctl.Elapsed;
            if (Ctl.Active) _timer.Text = t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.ToString(@"mm\:ss") : t.ToString(@"mm\:ss");
            else _timer.Text = s != null ? s.Created.ToString("MMM d") : "--:--";

            string heard;
            if (!Ctl.Active) heard = s != null ? s.Title : "";
            else if (Ctl.Paused) heard = "Paused.";
            else heard = Ctl.LatestHeard() ?? "Listening... speak anytime.";
            if (_heard.Text != heard)
            {
                _heard.Text = heard;
                _heard.Foreground = Ctl.Active && !Ctl.Paused && Ctl.LatestHeard() != null ? U.Text2 : U.Text3;
            }

            if (s == null) return;
            foreach (var qa in s.Qas)
            {
                if (!qa.Dirty) continue;
                qa.Dirty = false;
                ContentControl host;
                if (_answerHosts.TryGetValue(qa, out host)) RenderAnswer(qa, host, true);
            }
        }

        // --- Full transcript ----------------------------------------------------------------------

        public void RefreshTranscript()
        {
            if (!_transcriptOpen) { RefreshState(); return; }
            _transcriptLines.Children.Clear();
            var s = Ctl.Current;
            if (s == null) return;
            bool atBottom = _transcriptScroll.VerticalOffset >= _transcriptScroll.ScrollableHeight - 4;
            foreach (var l in s.Lines.Skip(Math.Max(0, s.Lines.Count - 200)))
                _transcriptLines.Children.Add(Line(Label(l.Speaker), l.Text, false));
            foreach (var p in Ctl.OpenPartials())
                _transcriptLines.Children.Add(Line("LIVE", p.Value, true));
            if (_transcriptLines.Children.Count == 0)
                _transcriptLines.Children.Add(U.T(Ctl.Active ? "Speech will appear here as it's transcribed." : "No transcript.", 13.5, U.Text3));
            if (atBottom) _transcriptScroll.ScrollToEnd();
        }

        private static string Label(string speaker)
        {
            return speaker == "Me" ? "ME" : speaker == "Them" ? "THEM" : "HEARD";
        }

        /// <summary>One transcript line: who, what, and a copy button.</summary>
        private FrameworkElement Line(string label, string text, bool live)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tag = new TextBlock
            {
                Text = label,
                FontFamily = U.Mono,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = live ? U.Red : label == "ME" ? U.Green : U.Text3,
                Margin = new Thickness(0, 3, 0, 0)
            };
            g.Children.Add(tag);
            var body = U.T(text, 14 * S.TextSizePct / 100.0, live ? U.Text3 : U.Text2);
            if (live) body.FontStyle = FontStyles.Italic;
            Grid.SetColumn(body, 1);
            g.Children.Add(body);
            if (!live)
            {
                var copy = U.Btn("Btn.Ghost", U.Icon(U.GCopy, 12, null), () => CopyText(text, "Copied."), "Copy");
                copy.Padding = new Thickness(6, 3, 6, 3);
                copy.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(copy, 2);
                g.Children.Add(copy);
            }
            return g;
        }

        private void CopyAll()
        {
            var s = Ctl.Current;
            if (s == null) return;
            var sb = new StringBuilder();
            foreach (var l in s.Lines) sb.Append(l.Speaker).Append(": ").Append(l.Text).Append('\n');
            CopyText(sb.ToString().TrimEnd(), "Transcript copied.");
        }

        private void CopyText(string text, string toast)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); W.Toast(toast, false); }
            catch (Exception ex) { W.Toast("Couldn't copy: " + ex.Message, true); }
        }

        // --- Answers ------------------------------------------------------------------------------

        /// <summary>Rebuilds the answers (a new question, focus mode, text size, or a different session).</summary>
        public void RefreshQas()
        {
            var s = Ctl.Current;
            _qaList.Children.Clear();
            _answerHosts.Clear();
            _renderedTextSize = S.TextSizePct;
            _renderedKeywordStyle = S.KeywordStyle;
            var all = s == null ? new List<QaItem>() : s.Qas;
            // A new question arrived: back to the newest.
            if (all.Count != _renderedQaCount) _focusOffset = 0;
            _renderedQaCount = all.Count;
            _empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _qaScroll.Visibility = all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            if (Focused && all.Count > 0)
            {
                _focusOffset = Math.Max(0, Math.Min(_focusOffset, all.Count - 1));
                var qa = all[all.Count - 1 - _focusOffset];
                var block = QaBlock(qa, true, all.Count - _focusOffset, all.Count);
                _qaList.Children.Add(block);
                if (_focusOffset == 0 && qa.Streaming) U.Enter(block, 8, 200);
            }
            else
            {
                for (int i = 0; i < all.Count; i++)
                {
                    var block = QaBlock(all[i], i == all.Count - 1, 0, 0);
                    if (i > 0) _qaList.Children.Add(new Border { Height = 1, Background = U.Line, Margin = new Thickness(24, 6, 24, 6) });
                    _qaList.Children.Add(block);
                    if (i == all.Count - 1 && all[i].Streaming) U.Enter(block, 8, 200);
                }
            }
            RefreshState();
            if (Focused) _qaScroll.ScrollToTop();
            else Dispatcher.BeginInvoke((Action)(() => _qaScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <param name="position">In focus mode, which question this is (1-based) of `total`; 0 outside it.</param>
        private FrameworkElement QaBlock(QaItem qa, bool last, int position, int total)
        {
            var sp = new StackPanel { Margin = new Thickness(26, 14, 22, last ? 18 : 10) };
            var q = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            q.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            q.ColumnDefinitions.Add(new ColumnDefinition());
            q.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var qi = U.Icon(U.GQuestion, 14, U.Text3);
            qi.Margin = new Thickness(0, 2, 10, 0);
            qi.VerticalAlignment = VerticalAlignment.Top;
            q.Children.Add(qi);
            var qt = U.T(qa.Question, 14.5 * S.TextSizePct / 100.0, U.Text2);
            Grid.SetColumn(qt, 1);
            q.Children.Add(qt);
            if (total > 1) q.Children.Add(Arrows(position, total));
            sp.Children.Add(q);

            var host = new ContentControl();
            _answerHosts[qa] = host;
            RenderAnswer(qa, host, false);
            sp.Children.Add(host);

            var menu = new ContextMenu();
            menu.Items.Add(OverlayWindow.Item("Copy answer", () => CopyText(qa.CurrentText, "Answer copied.")));
            menu.Items.Add(OverlayWindow.Item("Copy question", () => CopyText(qa.Question, "Question copied.")));
            sp.ContextMenu = menu;
            sp.Background = Brushes.Transparent;
            return sp;
        }

        /// <summary>‹ 2/5 ›: flip through earlier questions in focus mode.</summary>
        private FrameworkElement Arrows(int position, int total)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            var prev = U.Circle(U.GLeft, () => { _focusOffset++; RefreshQas(); }, "Previous question", 10);
            prev.Width = prev.Height = 26;
            prev.IsEnabled = position > 1;
            var count = new TextBlock { Text = position + "/" + total, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = U.Text3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 7, 0) };
            var next = U.Circle(U.GRight, () => { _focusOffset--; RefreshQas(); }, "Next question", 10);
            next.Width = next.Height = 26;
            next.IsEnabled = position < total;
            row.Children.Add(prev);
            row.Children.Add(count);
            row.Children.Add(next);
            Grid.SetColumn(row, 2);
            return row;
        }

        private void RenderAnswer(QaItem qa, ContentControl host, bool streamingUpdate)
        {
            double size = OverlayWindow.AnswerSize(S);
            var text = qa.CurrentText;
            bool stick = streamingUpdate && !Focused && _qaScroll.VerticalOffset >= _qaScroll.ScrollableHeight - 30;
            var panel = new StackPanel();
            if (text.Length > 0) panel.Children.Add(MarkdownView.Render(text, size, S.KeywordStyle));
            if (qa.Streaming && text.Length == 0)
            {
                var thinking = new StackPanel { Orientation = Orientation.Horizontal };
                for (int i = 0; i < 3; i++)
                {
                    var dot = new Ellipse { Width = 7, Height = 7, Fill = U.Text2, Margin = new Thickness(0, 0, 6, 0) };
                    var a = new System.Windows.Media.Animation.DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(520))
                    {
                        AutoReverse = true,
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                        BeginTime = TimeSpan.FromMilliseconds(i * 170)
                    };
                    dot.BeginAnimation(OpacityProperty, a);
                    thinking.Children.Add(dot);
                }
                thinking.Margin = new Thickness(0, 6, 0, 4);
                panel.Children.Add(thinking);
            }
            var foot = qa.Error ?? qa.Footer;
            if (!string.IsNullOrEmpty(foot))
            {
                var f = U.T(foot, 13.5, qa.Error != null ? U.Amber : U.Text3);
                f.FontStyle = FontStyles.Italic;
                f.Margin = new Thickness(0, 8, 0, 0);
                panel.Children.Add(f);
            }
            host.Content = panel;
            if (stick) _qaScroll.ScrollToEnd();
        }

        public void OnSettingsChanged()
        {
            if (_renderedTextSize != S.TextSizePct || _renderedKeywordStyle != S.KeywordStyle) RefreshQas();
            RefreshState();
        }

        /// <summary>Opens the full transcript (`--render`).</summary>
        internal void PreviewTranscript()
        {
            _transcriptOpen = false;
            ToggleTranscript();
        }
    }
}
