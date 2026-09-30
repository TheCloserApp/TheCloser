using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TheCloser.Ui
{
    /// <summary>Settings: icon rail (General, AI, Prompts, Memory, Shortcuts) and the page content - the same pages as the Mac app.</summary>
    internal sealed class SettingsView : Grid
    {
        private readonly OverlayWindow W;
        private AppSettings S { get { return W.S; } }
        private readonly ScrollViewer _page = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly Dictionary<string, RadioButton> _rail = new Dictionary<string, RadioButton>();
        private readonly StackPanel _railPanel;
        private readonly List<TextBlock> _railLabels = new List<TextBlock>();
        private bool _railExpanded;
        private string _current = "general";
        private bool _showAllModels;
        private string _modelFilter = "";

        public SettingsView(OverlayWindow w)
        {
            W = w;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition());

            _railPanel = new StackPanel { Margin = new Thickness(12, 16, 12, 12) };
            var paneBtn = U.Btn("Btn.Ghost", U.Icon(U.GPane, 16, null), ToggleRail, "Show labels");
            paneBtn.Width = 44;
            paneBtn.Height = 40;
            paneBtn.HorizontalAlignment = HorizontalAlignment.Left;
            paneBtn.Margin = new Thickness(0, 0, 0, 14);
            _railPanel.Children.Add(paneBtn);
            AddRail("general", U.Icon(U.GGear, 17, null), "General");
            AddRail("ai", U.Sparkle(17, null), "AI");
            AddRail("prompts", U.Icon(U.GChat, 17, null), "Prompts");
            AddRail("memory", U.Icon(U.GMemory, 17, null), "Memory");
            AddRail("shortcuts", U.Icon(U.GKeyboard, 17, null), "Shortcuts");
            Children.Add(new Border { Background = U.B(0xFF181818), CornerRadius = new CornerRadius(18, 0, 0, 18), Child = _railPanel });

            var divider = new Border { Width = 1, Background = U.Line };
            SetColumn(divider, 1);
            Children.Add(divider);

            SetColumn(_page, 2);
            Children.Add(_page);
            Show("general");
        }

        private void AddRail(string key, FrameworkElement icon, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(icon);
            var t = new TextBlock { Text = label, FontSize = 14.5, Margin = new Thickness(12, 0, 4, 1), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            _railLabels.Add(t);
            sp.Children.Add(t);
            var rb = new RadioButton { Style = U.Style("Radio.Rail"), GroupName = "rail", Content = sp, Margin = new Thickness(0, 0, 0, 6), ToolTip = label, HorizontalContentAlignment = HorizontalAlignment.Left };
            rb.Checked += delegate { Show(key); };
            _rail[key] = rb;
            _railPanel.Children.Add(rb);
        }

        private void ToggleRail()
        {
            _railExpanded = !_railExpanded;
            foreach (var t in _railLabels) t.Visibility = _railExpanded ? Visibility.Visible : Visibility.Collapsed;
            foreach (var rb in _rail.Values)
            {
                rb.Width = _railExpanded ? 150 : 44;
                rb.Padding = new Thickness(_railExpanded ? 12 : 0, 0, 0, 0);
            }
        }

        /// <summary>Opens a page. Older page names still work: "models" and "subscription" are now "ai".</summary>
        public void Show(string key)
        {
            if (key == "models" || key == "subscription") key = "ai";
            // Pro manages memory itself.
            if (key == "memory" && W.Billing.IsActive) key = "general";
            _current = key;
            _rail["memory"].Visibility = W.Billing.IsActive ? Visibility.Collapsed : Visibility.Visible;
            if (_rail.ContainsKey(key) && _rail[key].IsChecked != true) _rail[key].IsChecked = true;
            FrameworkElement content;
            switch (key)
            {
                case "ai": content = AiPage(); break;
                case "prompts": content = PromptsPage(); break;
                case "memory": content = MemoryPage(); break;
                case "shortcuts": content = ShortcutsPage(); break;
                default: content = GeneralPage(); break;
            }
            _page.Content = content;
            _page.ScrollToTop();
            U.Enter(content, 8, 200);
        }

        public void Refresh()
        {
            Show(_current);
        }

        // --- Building blocks ---------------------------------------------------------------------

        private static StackPanel Page()
        {
            return new StackPanel { Margin = new Thickness(26, 20, 26, 26), MaxWidth = 860 };
        }

        private static TextBlock H(string text, bool first = false)
        {
            var t = U.T(text, 17, U.Text, FontWeights.SemiBold);
            t.Margin = new Thickness(0, first ? 0 : 28, 0, 10);
            return t;
        }

        private static TextBlock Sub(string text)
        {
            var t = U.T(text, 14, U.Text2);
            t.Margin = new Thickness(0, -4, 0, 12);
            return t;
        }

        private static StackPanel TwoLine(string title, string subtitle)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(U.T(title, 15.5, U.Text));
            if (subtitle != null)
            {
                var s = U.T(subtitle, 13, U.Text2);
                s.Margin = new Thickness(0, 2, 0, 0);
                text.Children.Add(s);
            }
            return text;
        }

        private static Grid SwitchRow(string title, string subtitle, bool value, Action<bool> changed)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(TwoLine(title, subtitle));
            var sw = new CheckBox { Style = U.Style("Toggle.Switch"), IsChecked = value, Margin = new Thickness(20, 0, 0, 0) };
            sw.Checked += delegate { changed(true); };
            sw.Unchecked += delegate { changed(false); };
            Grid.SetColumn(sw, 1);
            g.Children.Add(sw);
            return g;
        }

        private static Grid SliderRow(string label, int min, int max, int value, Func<int, string> display, Action<int> changed)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            var l = U.T(label, 15, U.Text2);
            l.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(l);
            var sl = new Slider { Style = U.Style("Slider.Dots"), Minimum = min, Maximum = max, Value = value, SmallChange = 5, LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sl, 1);
            g.Children.Add(sl);
            var v = new TextBlock { Text = display(value), FontFamily = U.Mono, FontSize = 14, Foreground = U.Text2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(v, 2);
            g.Children.Add(v);
            sl.ValueChanged += delegate
            {
                int n = (int)Math.Round(sl.Value);
                v.Text = display(n);
                changed(n);
            };
            return g;
        }

        private static string Pct(int n) { return n + "%"; }

        private static Border Box(UIElement child)
        {
            return U.Box(child, new Thickness(20, 16, 20, 16));
        }

        // --- General -----------------------------------------------------------------------------

        private FrameworkElement GeneralPage()
        {
            var p = Page();
            p.Children.Add(H("Appearance", true));
            var ap = new StackPanel();
            ap.Children.Add(SliderRow("Opacity", 20, 100, S.OpacityPct, Pct, n => { S.OpacityPct = n; W.ApplyAppearance(); W.SaveSettingsSoon(); }));
            ap.Children.Add(SliderRow("Background", 0, 100, S.BackgroundPct, n => n == 0 ? "Off" : n + "%", n => { S.BackgroundPct = n; W.ApplyAppearance(); W.SaveSettingsSoon(); }));
            ap.Children.Add(SliderRow("Text size", 80, 160, S.TextSizePct, Pct, n => { S.TextSizePct = n; W.ApplyAppearance(); W.SaveSettingsSoon(); }));
            ap.Children.Add(KeywordRow());
            p.Children.Add(Box(ap));

            p.Children.Add(H("Recording"));
            var rec = new StackPanel();
            rec.Children.Add(SwitchRow("Offer to start when a call begins", "When Zoom, Meet or Teams starts using your mic.", S.OfferOnCall,
                on => { S.OfferOnCall = on; W.SaveSettingsSoon(); W.OnOfferOnCallChanged(); }));
            // Pro always transcribes on this PC, so there's nothing to pick.
            if (!W.Billing.IsActive)
            {
                var engine = new Grid { Margin = new Thickness(0, 16, 0, 0) };
                engine.ColumnDefinitions.Add(new ColumnDefinition());
                engine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                engine.Children.Add(TwoLine("Transcription engine", "Windows runs on this PC. ElevenLabs and Grok need a key."));
                var pick = W.EnginePicker(Refresh);
                pick.Margin = new Thickness(16, 0, 0, 0);
                pick.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(pick, 1);
                engine.Children.Add(pick);
                rec.Children.Add(engine);
            }
            p.Children.Add(Box(rec));

            p.Children.Add(H("Privacy"));
            p.Children.Add(Box(SwitchRow("Hide from screen sharing", "Keeps the overlay out of screen shares and recordings.", S.HideFromCapture,
                on => { if (S.HideFromCapture != on) W.ToggleStealth(); })));

            p.Children.Add(H("Onboarding"));
            var replay = U.Btn("Btn.Pill", "Replay welcome tour", () => W.ShowWelcomeTour());
            replay.HorizontalAlignment = HorizontalAlignment.Left;
            p.Children.Add(Box(replay));
            return p;
        }

        /// <summary>Keywords: the style picker and a sample answer showing it at the current text size.</summary>
        private FrameworkElement KeywordRow()
        {
            var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = U.T("Keywords", 15, U.Text2);
            l.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(l);
            var preview = new Border { Background = U.B(0xFF0A0A0A), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 12, 0, 4) };
            Action showPreview = delegate { preview.Child = MarkdownView.Render(KeywordStyles.Sample, OverlayWindow.AnswerSize(S), S.KeywordStyle); };
            var pick = W.KeywordPicker(showPreview);
            pick.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(pick, 1);
            g.Children.Add(pick);
            sp.Children.Add(g);
            showPreview();
            sp.Children.Add(preview);
            return sp;
        }

        // --- AI ----------------------------------------------------------------------------------

        private FrameworkElement AiPage()
        {
            var p = Page();
            AddProSection(p);

            p.Children.Add(H("Model picker"));
            p.Children.Add(Sub("Choose which models show in the model menu."));
            p.Children.Add(Box(ModelList()));

            // Pro includes the keys.
            if (!W.Billing.IsActive)
            {
                bool grok = S.Transcription == "Grok";
                p.Children.Add(H("API keys"));
                p.Children.Add(Sub("OpenRouter runs the models; " + (grok ? "xAI" : "ElevenLabs") + " transcribes. Stored only on this PC."));
                var keys = new StackPanel();
                keys.Children.Add(KeyLabel("OpenRouter", true));
                keys.Children.Add(W.KeyField("sk-or-…", S.OpenRouterKey, v => S.OpenRouterKey = v));
                keys.Children.Add(KeyLabel("ElevenLabs", false));
                keys.Children.Add(W.KeyField("sk_…", S.ElevenLabsKey, v => S.ElevenLabsKey = v));
                if (grok || !string.IsNullOrEmpty(S.XaiKey))
                {
                    keys.Children.Add(KeyLabel("xAI", false));
                    keys.Children.Add(W.KeyField("xai-…", S.XaiKey, v => S.XaiKey = v));
                }
                p.Children.Add(Box(keys));
            }
            return p;
        }

        private static TextBlock KeyLabel(string name, bool first)
        {
            var t = U.T(name, 14, U.Text2, FontWeights.SemiBold);
            t.Margin = new Thickness(0, first ? 0 : 14, 0, 6);
            return t;
        }

        /// <summary>TheCloser Pro: coming soon with the tester code, or your plan (or the test budget) and its usage.</summary>
        private void AddProSection(StackPanel p)
        {
            var b = W.Billing;
            if (b.IsActive)
            {
                var plan = b.Plan == "pro_max" ? "TheCloser Pro Max" : "TheCloser Pro";
                p.Children.Add(H(b.IsTester ? plan + " · Tester" : plan, true));
                p.Children.Add(Sub(b.IsTester
                    ? "Tester access, from a budget shared by all testers. Thanks for helping!"
                    : "Models and transcription included. Tied to this PC."));
                var box = new StackPanel();
                box.Children.Add(ProViews.UsageMeter(b));
                // Testers have no subscription to manage or upgrade.
                if (!b.IsTester)
                {
                    box.Children.Add(ActionRow("Change plan, card, or cancel.", U.Btn("Btn.Pill", "Manage subscription", W.ManageSubscription)));
                    if (b.Plan == "pro")
                    {
                        box.Children.Add(U.Divider(new Thickness(0, 14, 0, 0)));
                        box.Children.Add(W.WaitingForUpgrade
                            ? ActionRow("Confirm in your browser. This updates once it's done.", U.Btn("Btn.Ghost", "Cancel", W.StopWaitingForCheckout))
                            : ActionRow("Pro Max: the most powerful models and 2.5× the usage.", U.Btn("Btn.White", "Upgrade to Pro Max", W.UpgradeToProMax)));
                    }
                }
                p.Children.Add(Box(box));
                return;
            }
            if (SubscriptionClient.PurchaseEnabled)
            {
                p.Children.Add(H("TheCloser Pro", true));
                p.Children.Add(Sub("No keys: we run the models and transcription."));
                p.Children.Add(ProViews.PlanPicker(W, Refresh));
                return;
            }
            p.Children.Add(H("TheCloser Pro", true));
            p.Children.Add(Sub("Coming soon: no keys, with the models and transcription included."));
            p.Children.Add(Box(ProViews.TesterCodeEntry(W, Refresh)));
        }

        /// <summary>A line of explanation with its button on the right ("Change plan, card, or cancel." [Manage subscription]).</summary>
        private static Grid ActionRow(string text, Button action)
        {
            var g = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = U.T(text, 13.5, U.Text2);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(t);
            action.FontSize = 14;
            action.Padding = new Thickness(14, 7, 14, 7);
            Grid.SetColumn(action, 1);
            g.Children.Add(action);
            return g;
        }

        private FrameworkElement ModelList()
        {
            var sp = new StackPanel();
            IEnumerable<ModelInfo> models = ModelCatalog.Curated;
            if (_showAllModels && ModelCatalog.Remote != null) models = ModelCatalog.Remote;
            var extra = S.EnabledModels.Where(slug => !models.Any(m => m.Slug == slug)).Select(slug => new ModelInfo(slug, ModelCatalog.Name(slug))).ToList();
            var all = models.Concat(extra).ToList();
            // Pro: only the plan's models.
            if (W.Billing.IsActive) all = all.Where(m => W.Billing.Models.Contains(m.Slug)).ToList();

            if (_showAllModels)
            {
                var filter = U.Input("Search models", _modelFilter, false);
                filter.Margin = new Thickness(0, 0, 0, 10);
                var listHost = new StackPanel();
                Action rebuild = delegate
                {
                    listHost.Children.Clear();
                    FillGroups(listHost, all.Where(m => _modelFilter.Length == 0 || m.Name.IndexOf(_modelFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                        m.Slug.IndexOf(_modelFilter, StringComparison.OrdinalIgnoreCase) >= 0).Take(200));
                };
                filter.TextChanged += delegate { _modelFilter = filter.Text.Trim(); rebuild(); };
                sp.Children.Add(filter);
                sp.Children.Add(listHost);
                rebuild();
            }
            else FillGroups(sp, all);

            if (!W.Billing.IsActive)
            {
                var showAll = U.Btn("Btn.Pill", _showAllModels ? "Show fewer" : "Show all", async () =>
                {
                    if (!_showAllModels && ModelCatalog.Remote == null)
                    {
                        try { await ModelCatalog.FetchRemoteAsync(); }
                        catch (Exception ex) { W.Toast("Couldn't load the OpenRouter model list: " + ex.Message, true); return; }
                    }
                    _showAllModels = !_showAllModels;
                    Refresh();
                });
                showAll.FontSize = 14;
                showAll.Padding = new Thickness(16, 7, 16, 7);
                showAll.HorizontalAlignment = HorizontalAlignment.Right;
                showAll.Margin = new Thickness(0, 12, 0, 0);
                sp.Children.Add(showAll);
            }

            var request = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            request.Children.Add(new TextBlock { Text = "Missing a model?", Foreground = U.Text2, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var ask = U.Btn("Btn.Link", "Request it", () => OverlayWindow.OpenUrl(RequestModelUrl()));
            ask.Foreground = U.Text;
            ask.FontSize = 14;
            request.Children.Add(ask);
            sp.Children.Add(request);
            return sp;
        }

        /// <summary>The website's model request form. Says which plan is asking, and nothing about who.</summary>
        private string RequestModelUrl()
        {
            var plan = W.Billing.IsActive ? W.Billing.Plan : "own_keys";
            return "https://www.thecloser.tech/request-model?source=app&plan=" + Uri.EscapeDataString(plan);
        }

        private void FillGroups(Panel host, IEnumerable<ModelInfo> models)
        {
            foreach (var group in models.GroupBy(m => ModelCatalog.Group(m.Slug)))
            {
                var gh = U.T(group.Key.ToUpperInvariant(), 12.5, U.Text2, FontWeights.SemiBold);
                gh.Margin = new Thickness(0, host.Children.Count == 0 ? 2 : 14, 0, 6);
                host.Children.Add(gh);
                foreach (var m in group)
                {
                    var model = m;
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var cb = new CheckBox { Style = U.Style("Check.Round"), Content = m.Name, IsChecked = S.EnabledModels.Contains(m.Slug), Margin = new Thickness(14, 6, 0, 6) };
                    cb.Checked += delegate { if (!S.EnabledModels.Contains(model.Slug)) S.EnabledModels.Add(model.Slug); W.SaveSettingsSoon(); };
                    cb.Unchecked += delegate
                    {
                        if (S.EnabledModels.Count <= 1) { cb.IsChecked = true; return; }
                        S.EnabledModels.Remove(model.Slug);
                        if (S.Model == model.Slug) { S.Model = S.EnabledModels[0]; W.OnKeysChanged(); }
                        W.SaveSettingsSoon();
                    };
                    row.Children.Add(cb);
                    var slug = new TextBlock { Text = m.Slug, FontFamily = U.Mono, FontSize = 12.5, Foreground = U.Text3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                    Grid.SetColumn(slug, 1);
                    row.Children.Add(slug);
                    host.Children.Add(row);
                }
            }
        }

        // --- Prompts -----------------------------------------------------------------------------

        private FrameworkElement PromptsPage()
        {
            var p = Page();
            var all = Prompts.All(S);
            var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(U.T("Prompts", 19, U.Text, FontWeights.SemiBold));
            title.Children.Add(U.Badge(all.Count.ToString()));
            header.Children.Add(title);
            var create = U.Btn("Btn.White", U.IconText(U.GAdd, "New prompt", 8), () => W.EditPrompt(null, Refresh));
            create.Padding = new Thickness(16, 8, 18, 8);
            Grid.SetColumn(create, 1);
            header.Children.Add(create);
            p.Children.Add(header);

            var list = new StackPanel();
            foreach (var pr in all)
            {
                var prompt = pr;
                var row = new Grid { Margin = new Thickness(0, 6, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = U.Icon(PromptGlyph(prompt.Id), 17, U.Text2);
                icon.VerticalAlignment = VerticalAlignment.Top;
                icon.Margin = new Thickness(0, 3, 14, 0);
                row.Children.Add(icon);
                var info = new StackPanel();
                info.Children.Add(U.T(prompt.Name, 16, U.Text));
                var preview = new TextBlock { Text = prompt.Text.Replace("\n", " "), Foreground = U.Text2, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40, Margin = new Thickness(0, 3, 0, 0) };
                info.Children.Add(preview);
                Grid.SetColumn(info, 1);
                row.Children.Add(info);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                actions.Children.Add(U.Btn("Btn.Ghost", U.Icon(U.GEdit, 14, null), () => W.EditPrompt(prompt, Refresh), "Edit"));
                actions.Children.Add(U.Btn("Btn.Ghost", U.Icon(U.GTrash, 14, null), () => W.DeletePrompt(prompt), "Delete"));
                Grid.SetColumn(actions, 2);
                row.Children.Add(actions);
                list.Children.Add(row);
            }
            if (all.Count == 0) list.Children.Add(Sub("No saved prompts. Interviews use the default prompt."));
            p.Children.Add(list);
            if (S.HiddenPrompts.Count > 0)
            {
                var restore = U.Btn("Btn.Link", "Restore deleted built-in prompts (" + S.HiddenPrompts.Count + ")", () =>
                {
                    Prompts.RestoreBuiltIns(S);
                    W.SaveSettingsSoon();
                    Refresh();
                    W.RefreshSetup();
                });
                restore.HorizontalAlignment = HorizontalAlignment.Left;
                restore.Margin = new Thickness(0, 6, 0, 0);
                p.Children.Add(restore);
            }
            return p;
        }

        private static string PromptGlyph(string id)
        {
            switch (id)
            {
                case "interview": return U.GPerson;
                case "meeting": return U.GPeople;
                case "call": return U.GPhone;
                default: return U.GChat;
            }
        }

        // --- Memory ------------------------------------------------------------------------------

        private FrameworkElement MemoryPage()
        {
            var p = Page();
            p.Children.Add(H("Within session", true));
            var within = new StackPanel();
            within.Children.Add(SwitchRow("Replay previous turns", "Include prior user/assistant turns as context.", S.ReplayTurns,
                on => { S.ReplayTurns = on; W.SaveSettingsSoon(); Refresh(); }));
            var window = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), IsEnabled = S.ReplayTurns, Opacity = S.ReplayTurns ? 1 : 0.4 };
            var allLabel = U.T("All turns", 15, U.Text);
            allLabel.VerticalAlignment = VerticalAlignment.Center;
            window.Children.Add(allLabel);
            var all = new CheckBox { Style = U.Style("Toggle.Switch"), IsChecked = S.ReplayAllTurns, Margin = new Thickness(12, 0, 22, 0) };
            window.Children.Add(all);
            var count = U.T("", 15, U.Text);
            count.VerticalAlignment = VerticalAlignment.Center;
            count.MinWidth = 70;
            window.Children.Add(count);
            var less = U.Circle(U.GRemove, null, "Fewer turns", 11);
            var more = U.Circle(U.GAdd, null, "More turns", 11);
            less.Margin = new Thickness(10, 0, 0, 0);
            more.Margin = new Thickness(6, 0, 0, 0);
            window.Children.Add(less);
            window.Children.Add(more);
            Action sync = delegate
            {
                count.Text = S.ReplayTurnCount + (S.ReplayTurnCount == 1 ? " turn" : " turns");
                count.Opacity = S.ReplayAllTurns ? 0.4 : 1;
                less.IsEnabled = !S.ReplayAllTurns && S.ReplayTurnCount > 1;
                more.IsEnabled = !S.ReplayAllTurns && S.ReplayTurnCount < 50;
            };
            all.Checked += delegate { S.ReplayAllTurns = true; W.SaveSettingsSoon(); sync(); };
            all.Unchecked += delegate { S.ReplayAllTurns = false; W.SaveSettingsSoon(); sync(); };
            less.Click += delegate { S.ReplayTurnCount = Math.Max(1, S.ReplayTurnCount - 1); W.SaveSettingsSoon(); sync(); };
            more.Click += delegate { S.ReplayTurnCount = Math.Min(50, S.ReplayTurnCount + 1); W.SaveSettingsSoon(); sync(); };
            sync();
            within.Children.Add(window);
            p.Children.Add(Box(within));

            p.Children.Add(H("Across sessions"));
            p.Children.Add(Box(SwitchRow("Pull from past sessions", "Include one recent turn from recent other sessions.", S.PullPastSessions,
                on => { S.PullPastSessions = on; W.SaveSettingsSoon(); })));
            return p;
        }

        // --- Shortcuts ---------------------------------------------------------------------------

        private FrameworkElement ShortcutsPage()
        {
            var p = Page();
            p.Children.Add(H("Global shortcuts", true));
            var list = new StackPanel();
            foreach (var r in OverlayWindow.Shortcuts)
            {
                var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var keys = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var k in r.Keys) keys.Children.Add(U.Key(k));
                g.Children.Add(keys);
                var d = U.T(r.Description, 15, U.Text2);
                d.HorizontalAlignment = HorizontalAlignment.Right;
                d.VerticalAlignment = VerticalAlignment.Center;
                d.Margin = new Thickness(24, 0, 0, 0);
                Grid.SetColumn(d, 1);
                g.Children.Add(d);
                list.Children.Add(g);
            }
            p.Children.Add(U.Box(list, new Thickness(18, 12, 22, 12)));
            return p;
        }
    }
}
