using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TheCloser.Ui
{
    /// <summary>"Set up your call": session, reference file, context, files, system prompt, auto-generate, Start.</summary>
    internal sealed class SetupView : Grid
    {
        private readonly OverlayWindow W;
        private AppSettings S { get { return W.S; } }

        private readonly WrapPanel _sessionRow = new WrapPanel();
        private readonly WrapPanel _resumeRow = new WrapPanel();
        private readonly WrapPanel _filesRow = new WrapPanel();
        private readonly WrapPanel _promptRow = new WrapPanel();
        private readonly WrapPanel _languageRow = new WrapPanel();
        private readonly TextBlock _languageHint;
        private readonly ContentControl _audioRow = new ContentControl();
        private readonly TextBlock _audioHint;
        private readonly TextBox _context;
        private readonly CheckBox _auto;
        private readonly Border _banner;
        private readonly TextBlock _bannerText;
        private readonly Button _start;
        private Session _previous;

        public SetupView(OverlayWindow w)
        {
            W = w;
            var stack = new StackPanel { Margin = new Thickness(34, 30, 34, 24) };

            stack.Children.Add(U.T("Set up your call", 27, U.Text, FontWeights.Bold));
            var sub = U.T("Pick a session, attach an optional reference file + context, choose a system prompt, then hit Start.", 15.5, U.Text2);
            sub.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(sub);

            stack.Children.Add(Section(U.Icon("", 17, U.Text), "Session", false));
            stack.Children.Add(_sessionRow);

            stack.Children.Add(Section(U.Icon("", 17, U.Text), "Reference file", true));
            stack.Children.Add(_resumeRow);

            stack.Children.Add(Section(U.Icon("", 17, U.Text), "Context", true));
            _context = U.Input("Who you're calling, product info, account details, talking points...", S.Context, true);
            _context.Height = 132;
            _context.TextChanged += delegate { S.Context = _context.Text; W.SaveSettingsSoon(); };
            stack.Children.Add(_context);
            _filesRow.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(_filesRow);

            stack.Children.Add(Section(U.Icon(U.GChat, 17, U.Text), "System prompt", false));
            stack.Children.Add(_promptRow);

            stack.Children.Add(Section(U.Icon(U.GMic, 17, U.Text), "Audio input", false));
            stack.Children.Add(_audioRow);
            _audioHint = U.T("", 13.5, U.Text3);
            _audioHint.Margin = new Thickness(2, 0, 0, 0);
            stack.Children.Add(_audioHint);

            stack.Children.Add(Section(U.Icon(U.GGlobe, 17, U.Text), "Language", false));
            stack.Children.Add(_languageRow);
            _languageHint = U.T("", 13.5, U.Text3);
            _languageHint.Margin = new Thickness(2, 8, 0, 0);
            stack.Children.Add(_languageHint);

            // Auto-generate
            var autoRow = new Grid { Margin = new Thickness(0, 26, 0, 0) };
            autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            autoRow.ColumnDefinitions.Add(new ColumnDefinition());
            var wand = U.Sparkle(17, U.Text);
            wand.Margin = new Thickness(0, 4, 14, 0);
            wand.VerticalAlignment = VerticalAlignment.Top;
            autoRow.Children.Add(wand);
            var autoText = new StackPanel();
            autoText.Children.Add(U.T("Auto-generate responses", 17, U.Text, FontWeights.SemiBold));
            var autoSub = U.T("Off → hit the Send button to ask. On → AI streams as you go.", 14, U.Text2);
            autoSub.Margin = new Thickness(0, 3, 0, 0);
            autoText.Children.Add(autoSub);
            Grid.SetColumn(autoText, 1);
            autoRow.Children.Add(autoText);
            _auto = new CheckBox { Style = U.Style("Toggle.Switch"), IsChecked = S.AutoGenerate, Margin = new Thickness(18, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            _auto.Checked += delegate { S.AutoGenerate = true; W.SaveSettingsSoon(); };
            _auto.Unchecked += delegate { S.AutoGenerate = false; W.SaveSettingsSoon(); };
            Grid.SetColumn(_auto, 2);
            autoRow.Children.Add(_auto);
            stack.Children.Add(autoRow);

            // Missing-key banner
            _bannerText = U.T("", 15, U.B(0xFFF3E7D2), FontWeights.SemiBold);
            _bannerText.VerticalAlignment = VerticalAlignment.Center;
            var bannerRow = new StackPanel { Orientation = Orientation.Horizontal };
            var warn = U.Icon(U.GWarn, 17, U.Amber);
            warn.Margin = new Thickness(0, 0, 12, 0);
            bannerRow.Children.Add(warn);
            bannerRow.Children.Add(_bannerText);
            _banner = new Border
            {
                Background = U.WarnBg,
                BorderBrush = U.WarnBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 26, 0, 0),
                Cursor = Cursors.Hand,
                Child = bannerRow
            };
            _banner.MouseLeftButtonUp += delegate { W.ShowSettings(S.UseSubscription ? "subscription" : "models"); };
            stack.Children.Add(_banner);

            _start = U.Btn("Btn.White", U.IconText(U.GPlay, "Start call", 10), () => W.StartInterview(_previous));
            _start.FontSize = 16.5;
            _start.Padding = new Thickness(30, 14, 34, 14);
            _start.HorizontalAlignment = HorizontalAlignment.Right;
            _start.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(_start);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
            Children.Add(scroll);
            Refresh();
        }

        private static FrameworkElement Section(UIElement icon, string title, bool optional)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 28, 0, 12) };
            ((FrameworkElement)icon).Margin = new Thickness(0, 0, 12, 0);
            sp.Children.Add(icon);
            var t = U.T(title, 17.5, U.Text, FontWeights.SemiBold);
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            if (optional) sp.Children.Add(U.Badge("Optional"));
            return sp;
        }

        public void SetPrevious(Session s)
        {
            _previous = s;
            Refresh();
        }

        /// <summary>Rebuilds the dynamic rows (after settings change or a file is added).</summary>
        public void Refresh()
        {
            // Session
            _sessionRow.Children.Clear();
            if (_previous == null)
            {
                _sessionRow.Children.Add(Spaced(U.Btn("Btn.White", U.IconText(U.GAddCircle, "New call"), null)));
                _sessionRow.Children.Add(U.Btn("Btn.Link", U.IconText(U.GHistory, "Previous session..."), () => W.PickPreviousSession(this)));
            }
            else
            {
                _sessionRow.Children.Add(Spaced(U.Btn("Btn.Pill", U.IconText(U.GAddCircle, "New call"), () => SetPrevious(null))));
                var title = _previous.Title.Length > 34 ? _previous.Title.Substring(0, 33) + "…" : _previous.Title;
                _sessionRow.Children.Add(Spaced(U.Btn("Btn.White", U.IconText(U.GHistory, title), () => W.PickPreviousSession(this))));
            }

            // Resume
            _resumeRow.Children.Clear();
            if (string.IsNullOrEmpty(S.ResumeFile))
                _resumeRow.Children.Add(U.Btn("Btn.Pill", U.IconText(U.GUpload, "Upload file"), UploadResume));
            else
                _resumeRow.Children.Add(FileChip(S.ResumeFile, () => { S.ResumeFile = ""; W.SaveSettingsSoon(); Refresh(); }));

            // Attached files
            _filesRow.Children.Clear();
            foreach (var f in S.Files.ToList())
            {
                var path = f;
                _filesRow.Children.Add(Spaced(FileChip(path, () => { S.Files.Remove(path); W.SaveSettingsSoon(); Refresh(); })));
            }
            _filesRow.Children.Add(U.Btn("Btn.Pill", U.IconText(U.GAttach, "Attach files"), AttachFiles));

            // Prompts
            _promptRow.Children.Clear();
            foreach (var p in Prompts.All(S))
            {
                var prompt = p;
                var rb = new RadioButton
                {
                    Style = U.Style("Radio.Chip"),
                    GroupName = "prompts",
                    IsChecked = p.Id == S.PromptId,
                    Content = U.IconText(U.Icon(U.GChat, 17, null), p.Name, 8),
                    Margin = new Thickness(0, 0, 8, 8)
                };
                rb.Checked += delegate { S.PromptId = prompt.Id; W.SaveSettingsSoon(); };
                var menu = new ContextMenu();
                var edit = new MenuItem { Header = "Edit..." };
                edit.Click += delegate { W.EditPrompt(prompt, Refresh); };
                menu.Items.Add(edit);
                if (Prompts.IsBuiltIn(p.Id))
                {
                    var copy = new MenuItem { Header = "Duplicate..." };
                    copy.Click += delegate { W.EditPrompt(new PromptDef { Name = prompt.Name + " (copy)", Text = prompt.Text }, Refresh, true); };
                    menu.Items.Add(copy);
                    if (Prompts.IsEdited(S, p.Id))
                    {
                        var reset = new MenuItem { Header = "Reset to original" };
                        reset.Click += delegate { W.ResetPrompt(prompt.Id); };
                        menu.Items.Add(reset);
                    }
                }
                var del = new MenuItem { Header = "Delete" };
                del.Click += delegate { W.DeletePrompt(prompt); };
                menu.Items.Add(del);
                rb.ContextMenu = menu;
                rb.ToolTip = "Right-click to edit or delete";
                _promptRow.Children.Add(rb);
            }
            var create = U.Btn("Btn.Pill", U.IconText(U.GAdd, "Create new", 8), () => W.EditPrompt(null, Refresh));
            create.Margin = new Thickness(0, 0, 8, 8);
            _promptRow.Children.Add(create);

            // Audio input
            _audioRow.Content = W.AudioSourcePicker();
            _audioHint.Text = AudioHint(S);

            // Language
            _languageRow.Children.Clear();
            _languageRow.Children.Add(W.LanguagePicker());
            _languageHint.Text = LanguageHint(S);

            // Key check
            var route = ModelCatalog.Resolve(S, S.Model);
            _banner.Visibility = route.Provider == null ? Visibility.Visible : Visibility.Collapsed;
            _bannerText.Text = route.Missing ?? "";
            _start.IsEnabled = route.Provider != null;
            _auto.IsChecked = S.AutoGenerate;
        }

        /// <summary>What the audio input choice hears, under the chips (also used in Settings).</summary>
        internal static string AudioHint(AppSettings s)
        {
            if (s.EffectiveTranscription == "LiveCaptions")
                return "Live Captions hears what your PC plays. To add your mic, turn on \"Include microphone audio\" in Live Captions' settings.";
            switch (s.AudioSource)
            {
                case "System": return "Only what your PC plays: the other side of the call, a video. Your own voice isn't transcribed.";
                case "Mic": return "Only your microphone - for in-person conversations, or to practise by asking questions out loud.";
                default: return "What your PC plays (the other side of the call, as \"Them\") and your microphone (you, as \"Me\").";
            }
        }

        /// <summary>What the language choice does, under the picker (also used in Settings).</summary>
        internal static string LanguageHint(AppSettings s)
        {
            var lang = SpeechLanguages.Find(s.SpeechLanguage);
            if (lang == null || lang.Code.Length == 0) return "Transcribes whatever language is spoken.";
            bool mixesEnglish = lang.Scripts[0] != "Latin" && lang.Scripts.Contains("Latin");
            var hint = "Only " + lang.Name + (mixesEnglish ? " (and English mixed into it)" : "") + " is transcribed; speech in other languages is ignored.";
            if (s.EffectiveTranscription == "LiveCaptions") hint += " Live Captions itself listens in the language chosen in Windows.";
            return hint;
        }

        private static FrameworkElement Spaced(FrameworkElement e)
        {
            e.Margin = new Thickness(0, 0, 10, 8);
            return e;
        }

        private FrameworkElement FileChip(string path, Action remove)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(U.Icon(U.GDoc, 15, U.Text2));
            var name = Path.GetFileName(path);
            if (name.Length > 36) name = name.Substring(0, 35) + "…";
            sp.Children.Add(new TextBlock { Text = name, Margin = new Thickness(9, 0, 10, 1), VerticalAlignment = VerticalAlignment.Center });
            var x = U.Btn("Btn.Ghost", U.Icon(U.GClose, 11, null), remove, "Remove");
            x.Padding = new Thickness(5);
            sp.Children.Add(x);
            var problem = Attachments.Problem(path);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(21),
                BorderThickness = new Thickness(1),
                BorderBrush = problem == null ? U.B(0xFF3A3A3E) : U.WarnBorder,
                Background = U.B(0xFF202023),
                Padding = new Thickness(14, 5, 6, 5),
                Child = sp,
                ToolTip = problem ?? path
            };
            TextElement.SetForeground(chip, U.Text);
            TextElement.SetFontSize(chip, 14.5);
            return chip;
        }

        private void UploadResume()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Attachments.DialogFilter, Title = "Choose a reference file" };
            if (dlg.ShowDialog(W) != true) return;
            var problem = Attachments.Problem(dlg.FileName);
            if (problem != null) { W.Toast(problem, true); return; }
            S.ResumeFile = dlg.FileName;
            W.SaveSettingsSoon();
            Refresh();
        }

        private void AttachFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Attachments.DialogFilter, Multiselect = true, Title = "Attach files" };
            if (dlg.ShowDialog(W) != true) return;
            foreach (var f in dlg.FileNames)
            {
                var problem = Attachments.Problem(f);
                if (problem != null) { W.Toast(Path.GetFileName(f) + ": " + problem, true); continue; }
                if (!S.Files.Contains(f)) S.Files.Add(f);
            }
            W.SaveSettingsSoon();
            Refresh();
        }
    }
}
