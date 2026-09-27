using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TheCloser.Ui
{
    /// <summary>The live call card: recording pill + what's being heard, optional transcript, and the answers.</summary>
    internal sealed class SessionView : Grid
    {
        private readonly OverlayWindow W;
        private SessionController Ctl { get { return W.Ctl; } }

        private readonly Border _pill;
        private readonly Ellipse _dot;
        private readonly TextBlock _timer, _heard;
        private readonly Button _collapseBtn, _transcriptBtn, _stealthBtn, _continueBtn;
        private readonly Border _transcriptPanel;
        private readonly StackPanel _transcriptLines = new StackPanel();
        private readonly ScrollViewer _transcriptScroll;
        private readonly Border _headerLine;
        private readonly Grid _body;
        private readonly FrameworkElement _empty;
        private readonly TextBlock _emptyTitle, _emptySub;
        private readonly ScrollViewer _qaScroll;
        private readonly StackPanel _qaList = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        private readonly Dictionary<QaItem, ContentControl> _answerHosts = new Dictionary<QaItem, ContentControl>();
        private bool _collapsed;
        private int _renderedTextSize;

        public bool IsCollapsed { get { return _collapsed; } }

        public SessionView(OverlayWindow w)
        {
            W = w;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // divider
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // transcript
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body

            // Header: [● 00:13] Listening... speak anytime.            [v] [≡] [eye]
            var header = new Grid { Margin = new Thickness(20, 16, 16, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _dot = new Ellipse { Width = 8, Height = 8, Fill = U.Red, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _timer = new TextBlock { FontFamily = U.Mono, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = U.RecText, VerticalAlignment = VerticalAlignment.Center, Text = "00:00" };
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
            pillRow.Children.Add(_dot);
            pillRow.Children.Add(_timer);
            _pill = new Border { Background = U.RecBg, CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 4, 12, 5), Child = pillRow, VerticalAlignment = VerticalAlignment.Center };
            U.Pulse(_dot);
            header.Children.Add(_pill);

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
            header.Children.Add(_heard);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _continueBtn = U.Btn("Btn.White", U.IconText(U.GPlay, "Continue", 8), () => W.ContinueSession());
            _continueBtn.Padding = new Thickness(14, 7, 16, 7);
            _continueBtn.FontSize = 14;
            _continueBtn.Margin = new Thickness(0, 0, 8, 0);
            _collapseBtn = U.Circle(U.GDown, ToggleCollapsed, "Collapse", 13);
            _transcriptBtn = U.Circle(U.GList, () => W.SetShowTranscript(!W.S.ShowTranscript), "Show live transcript", 15);
            _stealthBtn = U.Circle(U.GHide, () => W.ToggleStealth(), "", 15);
            foreach (var b in new FrameworkElement[] { _continueBtn, _collapseBtn, _transcriptBtn, _stealthBtn })
            {
                if (b != _continueBtn) b.Margin = new Thickness(8, 0, 0, 0);
                buttons.Children.Add(b);
            }
            Grid.SetColumn(buttons, 2);
            header.Children.Add(buttons);
            Children.Add(header);

            _headerLine = new Border { Height = 1, Background = U.Line };
            SetRow(_headerLine, 1);
            Children.Add(_headerLine);

            // Live transcript (toggle)
            _transcriptScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 150, Content = _transcriptLines };
            _transcriptPanel = new Border
            {
                Background = U.B(0xFF111113),
                Padding = new Thickness(20, 10, 14, 10),
                Child = _transcriptScroll,
                BorderBrush = U.Line,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            SetRow(_transcriptPanel, 2);
            Children.Add(_transcriptPanel);

            // Body: empty state or answers
            _body = new Grid();
            SetRow(_body, 3);
            Children.Add(_body);

            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 40, 24, 44) };
            empty.Children.Add(U.Waveform(30, U.Text3, true));
            _emptyTitle = U.T("Waiting for the first question", 17, U.Text2, FontWeights.SemiBold);
            _emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyTitle.Margin = new Thickness(0, 16, 0, 0);
            _emptySub = U.T("The answer appears here the moment the caller finishes asking.", 14, U.Text3);
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

        private void ToggleCollapsed()
        {
            _collapsed = !_collapsed;
            ((TextBlock)_collapseBtn.Content).Text = _collapsed ? U.GUp : U.GDown;
            _collapseBtn.ToolTip = _collapsed ? "Expand" : "Collapse";
            RefreshLayout();
        }

        private void RefreshLayout()
        {
            _body.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
            _headerLine.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
            _transcriptPanel.Visibility = W.S.ShowTranscript && !_collapsed ? Visibility.Visible : Visibility.Collapsed;
            W.UpdateCardLayout();
        }

        /// <summary>Header state: timer, what's being heard, buttons.</summary>
        public void RefreshState()
        {
            var s = Ctl.Current;
            bool live = Ctl.Active && !Ctl.Paused;
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
            _continueBtn.Visibility = !Ctl.Active && s != null ? Visibility.Visible : Visibility.Collapsed;
            _collapseBtn.Visibility = s != null && s.Qas.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _stealthBtn.Content = U.Icon(W.S.HideFromCapture ? U.GHide : U.GView, 15, W.S.HideFromCapture ? null : U.Amber);
            _stealthBtn.ToolTip = W.S.HideFromCapture ? "Hidden from screen sharing (click to show)" : "Visible to screen sharing (click to hide)";
            _transcriptBtn.Content = U.Icon(U.GList, 15, W.S.ShowTranscript ? U.Blue : null);

            if (!Ctl.Active) _emptyTitle.Text = s == null ? "No session" : "No questions in this session";
            else if (Ctl.Paused) _emptyTitle.Text = "Call paused";
            else _emptyTitle.Text = "Waiting for the first question";
            _emptySub.Text = Ctl.Active && !Ctl.Paused ? "The answer appears here the moment the caller finishes asking." :
                Ctl.Active ? "Resume from the … menu when you're ready." : "Press Continue to pick this call back up.";
            Tick();
            RefreshLayout();
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

        public void RefreshTranscript()
        {
            _transcriptLines.Children.Clear();
            var s = Ctl.Current;
            if (s == null) return;
            bool atBottom = _transcriptScroll.VerticalOffset >= _transcriptScroll.ScrollableHeight - 4;
            foreach (var l in s.Lines.Skip(Math.Max(0, s.Lines.Count - 60)))
                _transcriptLines.Children.Add(Line(l.Speaker, l.Text, false));
            foreach (var p in Ctl.OpenPartials())
                _transcriptLines.Children.Add(Line(p.Key, p.Value, true));
            if (_transcriptLines.Children.Count == 0)
                _transcriptLines.Children.Add(U.T(Ctl.Active ? "Speech will appear here as it's transcribed." : "No transcript.", 13.5, U.Text3));
            if (atBottom) _transcriptScroll.ScrollToEnd();
        }

        private static TextBlock Line(string speaker, string text, bool partial)
        {
            var tb = new TextBlock { FontFamily = U.Font, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            var color = speaker == "Me" ? U.Green : speaker == "Them" ? U.B(0xFF7DB4FF) : U.B(0xFFC9A7FF);
            tb.Inlines.Add(new System.Windows.Documents.Run(speaker + "  ") { Foreground = color, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = partial ? U.Text3 : U.Text2, FontStyle = partial ? FontStyles.Italic : FontStyles.Normal });
            return tb;
        }

        /// <summary>Rebuilds the Q&A list (new question, focus mode, text size, or a different session).</summary>
        public void RefreshQas()
        {
            var s = Ctl.Current;
            _qaList.Children.Clear();
            _answerHosts.Clear();
            _renderedTextSize = W.S.TextSizePct;
            var qas = s == null ? new List<QaItem>() : (W.S.FocusMode ? s.Qas.Skip(Math.Max(0, s.Qas.Count - 1)).ToList() : s.Qas);
            _empty.Visibility = qas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _qaScroll.Visibility = qas.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            for (int i = 0; i < qas.Count; i++)
            {
                var block = QaBlock(qas[i], i == qas.Count - 1);
                if (i > 0) _qaList.Children.Add(new Border { Height = 1, Background = U.Line, Margin = new Thickness(24, 6, 24, 6) });
                _qaList.Children.Add(block);
                if (i == qas.Count - 1 && qas[i].Streaming) U.Enter(block, 8, 200);
            }
            RefreshState();
            Dispatcher.BeginInvoke((Action)(() => _qaScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private FrameworkElement QaBlock(QaItem qa, bool last)
        {
            var sp = new StackPanel { Margin = new Thickness(26, 14, 22, last ? 18 : 10) };
            var q = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var qi = U.Icon("", 14, U.Text3);
            qi.Margin = new Thickness(0, 1, 10, 0);
            qi.VerticalAlignment = VerticalAlignment.Top;
            q.Children.Add(qi);
            var qt = U.T(qa.Question, 14.5, U.Text2, FontWeights.SemiBold);
            qt.MaxWidth = 900;
            q.Children.Add(qt);
            sp.Children.Add(q);

            var host = new ContentControl();
            _answerHosts[qa] = host;
            RenderAnswer(qa, host, false);
            sp.Children.Add(host);

            var menu = new ContextMenu();
            var copy = new MenuItem { Header = "Copy answer" };
            copy.Click += delegate { try { Clipboard.SetText(qa.CurrentText); W.Toast("Answer copied.", false); } catch { } };
            var copyQ = new MenuItem { Header = "Copy question" };
            copyQ.Click += delegate { try { Clipboard.SetText(qa.Question); } catch { } };
            menu.Items.Add(copy);
            menu.Items.Add(copyQ);
            sp.ContextMenu = menu;
            sp.Background = Brushes.Transparent;
            return sp;
        }

        private void RenderAnswer(QaItem qa, ContentControl host, bool streamingUpdate)
        {
            double size = 14.5 * W.S.TextSizePct / 100.0;
            var text = qa.CurrentText;
            bool stick = streamingUpdate && _qaScroll.VerticalOffset >= _qaScroll.ScrollableHeight - 30;
            var panel = new StackPanel();
            if (text.Length > 0) panel.Children.Add(MarkdownView.Render(text, size));
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
            if (_renderedTextSize != W.S.TextSizePct) RefreshQas();
            RefreshState();
        }
    }
}
