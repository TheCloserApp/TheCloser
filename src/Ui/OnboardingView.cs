using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TheCloser.Ui
{
    /// <summary>
    /// The welcome tour, in the panel on first run and from Settings > General: what TheCloser does, how you want to
    /// use it (your own keys, or Pro), then your keys or the plans. The same steps as the Mac app.
    /// </summary>
    internal sealed class OnboardingView : Grid
    {
        internal enum Step { Welcome, Choose, Keys, Plans }

        private readonly OverlayWindow W;
        private AppSettings S { get { return W.S; } }
        private readonly ContentControl _body = new ContentControl();
        private readonly StackPanel _dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        private Step _step;
        private bool? _ownKeys;   // the choice on the second step

        public OnboardingView(OverlayWindow w)
        {
            W = w;
            RowDefinitions.Add(new RowDefinition());
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
            Children.Add(scroll);

            var footer = new Grid { Background = U.B(0xFF161616) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var inner = new Grid { Margin = new Thickness(26, 16, 20, 16) };
            inner.ColumnDefinitions.Add(new ColumnDefinition());
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.Children.Add(_dots);
            Grid.SetColumn(_buttons, 1);
            inner.Children.Add(_buttons);
            footer.Children.Add(inner);
            Grid.SetColumnSpan(inner, 2);
            SetRow(footer, 1);
            Children.Add(footer);
            Go(Step.Welcome);
        }

        public void Go(Step step)
        {
            _step = step;
            FrameworkElement content;
            switch (step)
            {
                case Step.Choose: content = ChooseStep(); break;
                case Step.Keys: content = KeysStep(); break;
                case Step.Plans: content = PlansStep(); break;
                default: content = WelcomeStep(); break;
            }
            _body.Content = content;
            U.Enter(content, 8, 200);
            RefreshFooter();
        }

        // --- Footer ------------------------------------------------------------------------------

        private int Index { get { return _step == Step.Welcome ? 0 : _step == Step.Choose ? 1 : 2; } }

        private void RefreshFooter()
        {
            _dots.Children.Clear();
            for (int i = 0; i < 3; i++)
            {
                bool on = i == Index;
                _dots.Children.Add(new Border
                {
                    Width = on ? 20 : 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = on ? U.Text : U.B(0xFF3A3A3A),
                    Margin = new Thickness(0, 0, 6, 0)
                });
            }

            _buttons.Children.Clear();
            if (_step != Step.Welcome)
            {
                var back = U.Btn("Btn.Pill", "Back", delegate { Go(_step == Step.Choose ? Step.Welcome : Step.Choose); });
                back.Margin = new Thickness(0, 0, 10, 0);
                _buttons.Children.Add(back);
            }
            string label;
            Action next;
            switch (_step)
            {
                case Step.Choose:
                    label = "Continue";
                    next = delegate { Go(_ownKeys == false ? Step.Plans : Step.Keys); };
                    break;
                case Step.Keys:
                    label = S.MissingKeys.Count == 0 ? "Done" : "Skip for now";
                    next = Finish;
                    break;
                case Step.Plans:
                    if (W.Billing.IsActive) { label = "Done"; next = Finish; }
                    else { label = "Use my own keys"; next = delegate { _ownKeys = true; Go(Step.Keys); }; }
                    break;
                default:
                    label = "Get Started";
                    next = delegate { Go(Step.Choose); };
                    break;
            }
            var primary = U.Btn("Btn.White", label, next);
            primary.IsEnabled = _step != Step.Choose || _ownKeys.HasValue;
            _buttons.Children.Add(primary);
        }

        private void Finish()
        {
            S.OnboardingDone = true;
            W.SaveSettingsSoon();
            W.FinishWelcomeTour();
        }

        // --- Steps -------------------------------------------------------------------------------

        private static StackPanel StepPage()
        {
            return new StackPanel { Margin = new Thickness(34, 30, 34, 24) };
        }

        private static void Header(StackPanel p, string title, string subtitle)
        {
            p.Children.Add(U.T(title, 25, U.Text, FontWeights.Bold));
            var sub = U.T(subtitle, 15, U.Text2);
            sub.Margin = new Thickness(0, 6, 0, 20);
            p.Children.Add(sub);
        }

        private FrameworkElement WelcomeStep()
        {
            var p = new StackPanel { Margin = new Thickness(34, 60, 34, 24), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520 };
            var logo = new Border
            {
                Width = 110,
                Height = 110,
                CornerRadius = new CornerRadius(55),
                Background = U.B(0xFF1A1A1A),
                BorderBrush = U.B(0xFF2A2A2A),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = U.WaveLogo(28, U.Text)
            };
            p.Children.Add(logo);
            var name = U.T("TheCloser", 32, U.Text, FontWeights.Bold);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 26, 0, 6);
            p.Children.Add(name);
            var tag = U.T("An AI that listens, thinks, and hides on screen share.", 15.5, U.Text2);
            tag.HorizontalAlignment = HorizontalAlignment.Center;
            tag.TextAlignment = TextAlignment.Center;
            p.Children.Add(tag);

            var points = new StackPanel();
            points.Children.Add(Point(U.WaveLogo(13, U.Text2), "Hears the interviewer and answers as they ask"));
            points.Children.Add(Point(U.EyeSlash(15, U.Text2), "Hidden from screen shares and recordings"));
            points.Children.Add(Point(U.Icon(U.GVideo, 15, U.Text2), "Works with Zoom, Meet, Teams and any call app"));
            points.Children.Add(Point(U.Icon(U.GLock, 15, U.Text2), "Sessions stay on this PC. We keep no copy of your audio or conversations."));
            var box = U.Box(points, new Thickness(20, 14, 20, 14));
            box.Margin = new Thickness(0, 28, 0, 0);
            p.Children.Add(box);
            return p;
        }

        private static FrameworkElement Point(FrameworkElement icon, string text)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.Margin = new Thickness(0, 2, 0, 0);
            g.Children.Add(icon);
            var t = U.T(text, 15, U.Text);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        private FrameworkElement ChooseStep()
        {
            var p = StepPage();
            Header(p, "How do you want to use it?", "You can switch later in Settings.");
            p.Children.Add(Option(U.GKey, "Bring your own keys", "Free",
                "Use your own OpenRouter and ElevenLabs keys. You pay them directly for what you use.", _ownKeys == true,
                delegate { _ownKeys = true; Go(Step.Choose); }));
            var managed = Option(null, "We handle everything", W.Billing.IsActive ? "Pro" : "Coming soon",
                "No keys, no setup. Top models and transcription, managed for you. From $19/month.", _ownKeys == false,
                delegate { _ownKeys = false; Go(Step.Choose); });
            managed.Margin = new Thickness(0, 12, 0, 0);
            p.Children.Add(managed);
            return p;
        }

        /// <summary>A choice card: icon, title with a badge, description and a radio circle.</summary>
        private static Border Option(string glyph, string title, string badge, string detail, bool selected, Action pick)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var iconBg = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                Background = U.B(0xFF222222),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 16, 0),
                Child = glyph != null ? (UIElement)U.Icon(glyph, 16, U.Text) : U.Sparkle(17, U.Text)
            };
            g.Children.Add(iconBg);
            var text = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(U.T(title, 17, U.Text, FontWeights.SemiBold));
            head.Children.Add(U.Badge(badge));
            text.Children.Add(head);
            var d = U.T(detail, 14, U.Text2);
            d.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(d);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var radio = new Grid { Width = 22, Height = 22, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            radio.Children.Add(new Ellipse { Stroke = selected ? U.Blue : U.B(0xFF555555), StrokeThickness = 1.5, Fill = selected ? U.Blue : Brushes.Transparent });
            if (selected) radio.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brushes.White });
            Grid.SetColumn(radio, 2);
            g.Children.Add(radio);
            var card = new Border
            {
                Background = selected ? U.B(0xFF1C2230) : U.Inner,
                BorderBrush = selected ? U.B(0xFF3B8EFF) : U.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 16, 18, 16),
                Cursor = Cursors.Hand,
                Child = g
            };
            card.MouseLeftButtonUp += delegate { pick(); };
            return card;
        }

        private FrameworkElement KeysStep()
        {
            var p = StepPage();
            bool grok = S.Transcription == "Grok";
            Header(p, "Add your keys", "Both are needed to start an interview.");
            p.Children.Add(KeyInput("OpenRouter", "runs every AI model", "https://openrouter.ai/keys", W.KeyField("sk-or-…", S.OpenRouterKey, v => { S.OpenRouterKey = v; RefreshFooter(); })));
            if (grok)
                p.Children.Add(KeyInput("xAI", "transcribes the interview", "https://console.x.ai", W.KeyField("xai-…", S.XaiKey, v => { S.XaiKey = v; RefreshFooter(); })));
            else
                p.Children.Add(KeyInput("ElevenLabs", "transcribes the interview", "https://elevenlabs.io/app/settings/api-keys", W.KeyField("sk_…", S.ElevenLabsKey, v => { S.ElevenLabsKey = v; RefreshFooter(); })));
            var note = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            var shield = U.Icon(U.GLock, 12, U.Text2);
            shield.Margin = new Thickness(0, 1, 8, 0);
            note.Children.Add(shield);
            note.Children.Add(U.T("Stored only on this PC, and sent only to OpenRouter and " + (grok ? "xAI" : "ElevenLabs") + ".", 13.5, U.Text2));
            p.Children.Add(note);
            return p;
        }

        private static FrameworkElement KeyInput(string label, string hint, string url, FrameworkElement field)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center };
            title.Inlines.Add(new System.Windows.Documents.Run(label) { Foreground = U.Text2, FontWeight = FontWeights.SemiBold });
            title.Inlines.Add(new System.Windows.Documents.Run(" · " + hint) { Foreground = U.Text3 });
            head.Children.Add(title);
            var get = U.Btn("Btn.Link", "Get a key →", () => OverlayWindow.OpenUrl(url));
            get.FontSize = 14;
            get.Padding = new Thickness(0);
            Grid.SetColumn(get, 1);
            head.Children.Add(get);
            sp.Children.Add(head);
            sp.Children.Add(field);
            return sp;
        }

        private FrameworkElement PlansStep()
        {
            var p = StepPage();
            var b = W.Billing;
            if (b.IsActive)
                Header(p, b.IsTester ? "You have tester access" : "You're on " + (b.Plan == "pro_max" ? "Pro Max" : "Pro"),
                    b.IsTester ? "No keys needed. The test budget is shared by all testers." : "No keys needed. You're ready for your next interview.");
            else
                Header(p, "We handle everything", "Coming soon. Until then, it's free with your own keys.");
            p.Children.Add(ProViews.PlanCards(W));
            if (!b.IsActive)
            {
                var interest = U.Btn("Btn.Link", "I'm interested. Tell me when it launches →", () => OverlayWindow.OpenUrl(ProViews.InterestUrl));
                interest.HorizontalAlignment = HorizontalAlignment.Left;
                interest.Padding = new Thickness(0, 6, 0, 6);
                interest.Margin = new Thickness(0, 16, 0, 4);
                p.Children.Add(interest);
                p.Children.Add(ProViews.TesterCodeEntry(W, delegate { Go(Step.Plans); }));
            }
            return p;
        }
    }
}
