using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TheCloser.Ui
{
    /// <summary>Settings: icon rail (General, Models, Prompts, Shortcuts) and the page content.</summary>
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
            AddRail("models", U.Sparkle(17, null), "Models");
            AddRail("prompts", U.Icon(U.GChat, 17, null), "Prompts");
            AddRail("shortcuts", U.Icon(U.GKeyboard, 17, null), "Shortcuts");
            Children.Add(new Border { Background = U.B(0xFF121214), CornerRadius = new CornerRadius(18, 0, 0, 18), Child = _railPanel });

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

        public void Show(string key)
        {
            _current = key;
            if (_rail.ContainsKey(key) && _rail[key].IsChecked != true) _rail[key].IsChecked = true;
            FrameworkElement content;
            switch (key)
            {
                case "models": content = ModelsPage(); break;
                case "prompts": content = PromptsPage(); break;
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
            return new StackPanel { Margin = new Thickness(26, 20, 26, 26), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        }

        private static TextBlock H(string text, bool first = false)
        {
            var t = U.T(text, 18, U.Text, FontWeights.SemiBold);
            t.Margin = new Thickness(0, first ? 0 : 26, 0, 10);
            return t;
        }

        private static TextBlock Sub(string text)
        {
            var t = U.T(text, 14, U.Text2);
            t.Margin = new Thickness(0, -4, 0, 12);
            return t;
        }

        private static Grid SwitchRow(string title, string subtitle, bool value, Action<bool> changed)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(U.T(title, 16, U.Text, FontWeights.SemiBold));
            if (subtitle != null)
            {
                var s = U.T(subtitle, 13.5, U.Text2);
                s.Margin = new Thickness(0, 3, 0, 0);
                text.Children.Add(s);
            }
            g.Children.Add(text);
            var sw = new CheckBox { Style = U.Style("Toggle.Switch"), IsChecked = value, Margin = new Thickness(20, 0, 0, 0) };
            sw.Checked += delegate { changed(true); };
            sw.Unchecked += delegate { changed(false); };
            Grid.SetColumn(sw, 1);
            g.Children.Add(sw);
            return g;
        }

        private Grid SliderRow(string label, int min, int max, int value, Action<int> changed)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            var l = U.T(label, 15.5, U.Text2);
            l.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(l);
            var sl = new Slider { Style = U.Style("Slider.Dots"), Minimum = min, Maximum = max, Value = value, SmallChange = 5, LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sl, 1);
            g.Children.Add(sl);
            var v = new TextBlock { Text = value + "%", FontFamily = U.Mono, FontSize = 14.5, Foreground = U.Text, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(v, 2);
            g.Children.Add(v);
            sl.ValueChanged += delegate
            {
                int n = (int)Math.Round(sl.Value);
                v.Text = n + "%";
                changed(n);
            };
            return g;
        }

        // --- General -----------------------------------------------------------------------------

        private FrameworkElement GeneralPage()
        {
            var p = Page();
            p.Children.Add(H("Appearance", true));
            p.Children.Add(Sub("Also in the … menu at the top. Text size sets the size of the answers."));
            var ap = new StackPanel();
            ap.Children.Add(SliderRow("Opacity", 30, 100, S.OpacityPct, n => { S.OpacityPct = n; W.ApplyAppearance(); W.SaveSettingsSoon(); }));
            ap.Children.Add(SliderRow("Background", 20, 100, S.BackgroundPct, n => { S.BackgroundPct = n; W.ApplyAppearance(); W.SaveSettingsSoon(); }));
            ap.Children.Add(SliderRow("Text size", 75, 200, S.TextSizePct, n => { S.TextSizePct = n; W.ApplyAppearance(); W.SaveSettingsSoon(); }));
            p.Children.Add(U.Box(ap, new Thickness(22, 14, 22, 14)));

            p.Children.Add(H("Recording"));
            p.Children.Add(U.Box(SwitchRow("Offer to start when a call begins", "When Zoom, Meet or Teams starts using your mic.", S.OfferOnCall,
                on => { S.OfferOnCall = on; W.SaveSettingsSoon(); }), new Thickness(22, 18, 22, 18)));

            p.Children.Add(H("Privacy"));
            p.Children.Add(U.Box(SwitchRow("Hide from screen sharing", "Keeps the overlay out of screen shares and recordings.", S.HideFromCapture,
                on => { if (S.HideFromCapture != on) W.ToggleStealth(); }), new Thickness(22, 18, 22, 18)));

            p.Children.Add(H("Onboarding"));
            var replay = U.Btn("Btn.Pill", "Replay welcome tour", () => W.ShowWelcomeTour());
            replay.HorizontalAlignment = HorizontalAlignment.Left;
            p.Children.Add(U.Box(replay, new Thickness(22, 18, 22, 18)));
            return p;
        }

        // --- Models & keys -----------------------------------------------------------------------

        private FrameworkElement ModelsPage()
        {
            var p = Page();
            p.Children.Add(H("Model", true));
            p.Children.Add(Sub("Writes every answer. Also in the … menu at the top."));
            var pick = W.ModelPicker();
            pick.HorizontalAlignment = HorizontalAlignment.Left;
            p.Children.Add(pick);

            p.Children.Add(H("API keys"));
            p.Children.Add(Sub("Use your own keys. They're encrypted for your Windows account and only sent to each provider."));
            var keys = new StackPanel();
            keys.Children.Add(KeyRow("Anthropic", "Claude models, direct (fastest, prompt caching)", "sk-ant-...", S.AnthropicKey, "https://console.anthropic.com/settings/keys",
                v => S.AnthropicKey = v, TestAnthropic));
            keys.Children.Add(U.Divider(new Thickness(0, 14, 0, 14)));
            keys.Children.Add(KeyRow("OpenRouter", "GPT, Gemini, Grok, Kimi and hundreds more", "sk-or-...", S.OpenRouterKey, "https://openrouter.ai/keys",
                v => S.OpenRouterKey = v, k => OpenRouterClient.TestKeyAsync(k)));
            keys.Children.Add(U.Divider(new Thickness(0, 14, 0, 14)));
            keys.Children.Add(KeyRow("xAI", "Grok Voice Transcribe - live transcription", "xai-...", S.XaiKey, "https://console.x.ai",
                v => S.XaiKey = v, TestXai));
            keys.Children.Add(U.Divider(new Thickness(0, 14, 0, 14)));
            keys.Children.Add(KeyRow("OpenAI", "Whisper transcription (optional alternative)", "sk-...", S.WhisperKey, "https://platform.openai.com/api-keys",
                v => S.WhisperKey = v, TestWhisper));
            p.Children.Add(U.Box(keys, new Thickness(22, 18, 22, 18)));

            p.Children.Add(H("Model picker"));
            p.Children.Add(Sub("Choose which models show in the model menu."));
            p.Children.Add(U.Box(ModelList(), new Thickness(22, 16, 22, 16)));

            p.Children.Add(H("Answers"));
            var ans = new StackPanel();
            ans.Children.Add(Label("Answer length"));
            ans.Children.Add(U.Segmented(new[] { "Short", "Medium", "Detailed" }, S.Length, v => { S.Length = v; W.SaveSettingsSoon(); }));
            ans.Children.Add(Label("Thinking effort (Claude, GPT and Gemini)"));
            ans.Children.Add(U.Segmented(new[] { "low", "medium", "high" }, S.Effort, v => { S.Effort = v; W.SaveSettingsSoon(); }));
            var effortHint = U.T("Low starts answering fastest - best for live calls.", 13, U.Text3);
            effortHint.Margin = new Thickness(0, -2, 0, 0);
            ans.Children.Add(effortHint);
            p.Children.Add(U.Box(ans, new Thickness(22, 8, 22, 18)));

            p.Children.Add(H("Transcription"));
            var tr = new StackPanel();
            var audioHint = U.T("", 13, U.Text3);
            Action showAudioHint = delegate { audioHint.Text = SetupView.AudioHint(S) + " Also on the Start call screen."; };
            tr.Children.Add(Label("Engine"));
            tr.Children.Add(U.Segmented(new[] { "Automatic", "Live Captions", "xAI Grok", "Whisper" }, EngineLabel(S.Transcription), v =>
            {
                S.Transcription = v == "Live Captions" ? "LiveCaptions" : v == "xAI Grok" ? "Grok" : v;
                W.SaveSettingsSoon();
                W.Ctl.RestartSource();
                showAudioHint();
                W.RefreshSetup();
            }));
            var auto = U.T("Automatic uses xAI Grok when you've added an xAI key, then Whisper, then the free Windows Live Captions.", 13, U.Text3);
            auto.Margin = new Thickness(0, -2, 0, 0);
            tr.Children.Add(auto);
            tr.Children.Add(Label("Audio input"));
            tr.Children.Add(W.AudioSourcePicker(showAudioHint));
            showAudioHint();
            audioHint.Margin = new Thickness(0, -2, 0, 0);
            tr.Children.Add(audioHint);
            tr.Children.Add(Label("Key terms (names, companies, jargon - comma-separated)"));
            var terms = U.Input("e.g. Kubernetes, Priya, Acme Pay", S.KeyTerms, false);
            terms.LostFocus += delegate { if (S.KeyTerms != terms.Text) { S.KeyTerms = terms.Text; W.SaveSettingsSoon(); W.Ctl.RestartSource(); } };
            tr.Children.Add(terms);
            tr.Children.Add(Label("Language"));
            var langHint = U.T("", 13, U.Text3);
            Action showHint = delegate { langHint.Text = SetupView.LanguageHint(S) + " Also on the Start call screen."; };
            var lang = W.LanguagePicker(showHint);
            lang.HorizontalAlignment = HorizontalAlignment.Left;
            tr.Children.Add(lang);
            showHint();
            langHint.Margin = new Thickness(0, 8, 0, 0);
            tr.Children.Add(langHint);
            tr.Children.Add(Label("Whisper provider"));
            tr.Children.Add(U.Segmented(new[] { "OpenAI", "Groq" }, S.WhisperPreset, v =>
            {
                S.WhisperPreset = v;
                S.WhisperBaseUrl = v == "Groq" ? "https://api.groq.com/openai/v1" : "https://api.openai.com/v1";
                S.WhisperModel = v == "Groq" ? "whisper-large-v3-turbo" : "gpt-4o-mini-transcribe";
                W.SaveSettingsSoon();
            }));
            p.Children.Add(U.Box(tr, new Thickness(22, 8, 22, 18)));
            return p;
        }

        private static string EngineLabel(string engine)
        {
            return engine == "LiveCaptions" ? "Live Captions" : engine == "Grok" ? "xAI Grok" : engine;
        }

        private static TextBlock Label(string text)
        {
            var t = U.T(text, 14, U.Text2, FontWeights.SemiBold);
            t.Margin = new Thickness(0, 12, 0, 8);
            return t;
        }

        private FrameworkElement KeyRow(string name, string desc, string placeholder, string value, string url, Action<string> save, Func<string, Task<string>> test)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(U.T(name, 15.5, U.Text, FontWeights.SemiBold));
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center, Fill = string.IsNullOrEmpty(value) ? U.Text3 : U.Green };
            title.Children.Add(dot);
            info.Children.Add(title);
            var d = U.T(desc, 13, U.Text3);
            d.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(d);
            g.Children.Add(info);

            PasswordBox box;
            var field = U.SecretField(placeholder, value, out box);
            field.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(field, 1);
            g.Children.Add(field);
            var status = U.T("", 12.5, U.Text3);
            status.Margin = new Thickness(0, 6, 0, 0);
            box.LostFocus += delegate
            {
                save(box.Password.Trim());
                dot.Fill = box.Password.Trim().Length > 0 ? U.Green : U.Text3;
                W.SaveSettingsSoon();
                W.OnKeysChanged();
            };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Button testBtn = null;
            testBtn = U.Btn("Btn.Ghost", "Test", async () =>
            {
                save(box.Password.Trim());
                W.SaveSettingsSoon();
                W.OnKeysChanged();
                testBtn.IsEnabled = false;
                status.Foreground = U.Text3;
                status.Text = "Testing...";
                string error;
                try { error = await test(box.Password.Trim()); }
                catch (Exception ex) { error = ex.Message; }
                testBtn.IsEnabled = true;
                status.Foreground = error == null ? U.Green : U.Amber;
                status.Text = error == null ? "Connected ✓" : error;
            });
            testBtn.Foreground = U.Blue;
            actions.Children.Add(testBtn);
            var get = U.Btn("Btn.Ghost", U.Icon(U.GNewWindow, 13, null), () => OverlayWindow.OpenUrl(url), "Get a key");
            actions.Children.Add(get);
            Grid.SetColumn(actions, 2);
            g.Children.Add(actions);

            var wrap = new StackPanel();
            wrap.Children.Add(g);
            var statusRow = new Grid();
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(status, 1);
            statusRow.Children.Add(status);
            wrap.Children.Add(statusRow);
            return wrap;
        }

        private async Task<string> TestAnthropic(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "Enter a key first.";
            var model = ModelCatalog.IsAnthropic(S.Model) ? ModelCatalog.AnthropicApiId(S.Model) : "claude-haiku-4-5";
            using (var cts = new System.Threading.CancellationTokenSource(60000))
            {
                try
                {
                    await new ClaudeClient().StreamAsync(key, PromptBuilder.BuildPing(model), delegate { }, cts.Token);
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            }
        }

        private Task<string> TestXai(string key)
        {
            var probe = S.Clone();
            probe.XaiKey = key;
            return GrokStreamingSource.TestConnectionAsync(probe);
        }

        private async Task<string> TestWhisper(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "Enter a key first.";
            try
            {
                var stt = new SpeechToTextClient(S.WhisperBaseUrl, S.WhisperModel, key, "");
                using (var cts = new System.Threading.CancellationTokenSource(30000))
                    await stt.TranscribeAsync(Wav.Encode(new short[16000], 16000), null, cts.Token);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private FrameworkElement ModelList()
        {
            var sp = new StackPanel();
            IEnumerable<ModelInfo> models = ModelCatalog.Curated;
            if (_showAllModels && ModelCatalog.Remote != null) models = ModelCatalog.Remote;
            var extra = S.EnabledModels.Where(slug => !models.Any(m => m.Slug == slug)).Select(slug => new ModelInfo(slug, ModelCatalog.Name(slug))).ToList();
            var all = models.Concat(extra).ToList();

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

            var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var add = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            add.Children.Add(new TextBlock { Text = "Missing a model?", Foreground = U.Text2, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var addBtn = U.Btn("Btn.Link", "Add by OpenRouter ID", () => W.AddModelById(Refresh));
            addBtn.Foreground = U.Text;
            addBtn.FontSize = 14;
            add.Children.Add(addBtn);
            footer.Children.Add(add);
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
            Grid.SetColumn(showAll, 1);
            footer.Children.Add(showAll);
            sp.Children.Add(footer);
            return sp;
        }

        private void FillGroups(Panel host, IEnumerable<ModelInfo> models)
        {
            foreach (var group in models.GroupBy(m => ModelCatalog.Group(m.Slug)))
            {
                var gh = U.T(group.Key.ToUpperInvariant(), 12.5, U.Text3, FontWeights.SemiBold);
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
            p.Children.Add(H("System prompts", true));
            p.Children.Add(Sub("The instructions TheCloser follows. Pick one on the setup screen. Every prompt can be edited, duplicated or deleted; an edited built-in can be reset from its Edit window."));
            var list = new StackPanel();
            var all = Prompts.All(S);
            for (int i = 0; i < all.Count; i++)
            {
                var prompt = all[i];
                if (i > 0) list.Children.Add(U.Divider(new Thickness(0, 12, 0, 12)));
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel();
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(U.T(prompt.Name, 15.5, prompt.Id == S.PromptId ? U.Green : U.Text, FontWeights.SemiBold));
                if (Prompts.IsBuiltIn(prompt.Id)) title.Children.Add(U.Badge(Prompts.IsEdited(S, prompt.Id) ? "Built-in, edited" : "Built-in"));
                info.Children.Add(title);
                var preview = new TextBlock { Text = prompt.Text.Replace("\n", " "), Foreground = U.Text3, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
                info.Children.Add(preview);
                row.Children.Add(info);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                actions.Children.Add(U.Btn("Btn.Ghost", "Edit", () => W.EditPrompt(prompt, Refresh)));
                if (Prompts.IsBuiltIn(prompt.Id))
                    actions.Children.Add(U.Btn("Btn.Ghost", U.Icon(U.GCopy, 13, null), () => W.EditPrompt(new PromptDef { Name = prompt.Name + " (copy)", Text = prompt.Text }, Refresh, true), "Duplicate"));
                actions.Children.Add(U.Btn("Btn.Ghost", U.Icon(U.GTrash, 13, null), () => W.DeletePrompt(prompt), "Delete"));
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);
                list.Children.Add(row);
            }
            p.Children.Add(U.Box(list, new Thickness(22, 18, 22, 18)));
            var create = U.Btn("Btn.Pill", U.IconText(U.GAdd, "New prompt", 8), () => W.EditPrompt(null, Refresh));
            create.HorizontalAlignment = HorizontalAlignment.Left;
            create.Margin = new Thickness(0, 14, 0, 0);
            p.Children.Add(create);
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
                restore.Margin = new Thickness(0, 10, 0, 0);
                p.Children.Add(restore);
            }
            return p;
        }

        // --- Shortcuts ---------------------------------------------------------------------------

        private FrameworkElement ShortcutsPage()
        {
            var p = Page();
            p.Children.Add(H("Global shortcuts", true));
            var list = new StackPanel();
            var rows = new[]
            {
                new object[] { new[] { "Ctrl", "Alt", "Space" }, "Show / hide overlay" },
                new object[] { new[] { "Ctrl", "Enter" }, "Get the answer now (during a call)" },
                new object[] { new[] { "Ctrl", "Shift", "Enter" }, "Screenshot → send to AI (during a call)" },
                new object[] { new[] { "Ctrl", "Alt", "↑↓←→" }, "Move overlay (while it's showing)" },
                new object[] { new[] { "Ctrl", "Alt", "Shift", "↑↓←→" }, "Resize overlay" },
                new object[] { new[] { "Ctrl", "N" }, "New session (when TheCloser is focused)" },
                new object[] { new[] { "Esc" }, "Stop the answer being written" }
            };
            foreach (var r in rows)
            {
                var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var keys = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var k in (string[])r[0]) keys.Children.Add(U.Key(k));
                g.Children.Add(keys);
                var d = U.T((string)r[1], 15, U.Text2);
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
