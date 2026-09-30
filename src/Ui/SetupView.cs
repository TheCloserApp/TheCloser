using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TheCloser.Ui
{
    /// <summary>"Set up your interview": session, language, resume, context and files, system prompt, auto-generate, Start.</summary>
    internal sealed class SetupView : Grid
    {
        private readonly OverlayWindow W;
        private AppSettings S { get { return W.S; } }

        private readonly WrapPanel _sessionRow = new WrapPanel();
        private readonly WrapPanel _languageRow = new WrapPanel();
        private readonly WrapPanel _resumeRow = new WrapPanel();
        private readonly WrapPanel _filesRow = new WrapPanel();
        private readonly WrapPanel _promptRow = new WrapPanel();
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

            stack.Children.Add(U.T("Set up your interview", 27, U.Text, FontWeights.Bold));

            stack.Children.Add(Section(U.GLayers, "Session", false));
            stack.Children.Add(_sessionRow);

            stack.Children.Add(Section(U.GFont, "Language", false));
            stack.Children.Add(_languageRow);

            stack.Children.Add(Section(U.GDoc, "Resume", true));
            stack.Children.Add(_resumeRow);

            stack.Children.Add(Section(U.GList, "Context", true));
            _context = U.Input("Role, company, JD, talking points…", S.Context, true);
            _context.Height = 118;
            _context.TextChanged += delegate { S.Context = _context.Text; W.SaveSettingsSoon(); };
            stack.Children.Add(_context);
            _filesRow.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(_filesRow);

            stack.Children.Add(Section(U.GChat, "System prompt", false));
            stack.Children.Add(_promptRow);

            // Auto-generate
            var autoRow = new Grid { Margin = new Thickness(0, 24, 0, 0) };
            autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            autoRow.ColumnDefinitions.Add(new ColumnDefinition());
            var wand = U.Sparkle(16, U.Text2);
            wand.Margin = new Thickness(0, 4, 14, 0);
            wand.VerticalAlignment = VerticalAlignment.Top;
            autoRow.Children.Add(wand);
            var autoText = new StackPanel();
            autoText.Children.Add(U.T("Auto-generate responses", 17, U.Text, FontWeights.SemiBold));
            var autoSub = U.T("Answers each real question as it's asked.", 14, U.Text2);
            autoSub.Margin = new Thickness(0, 3, 0, 0);
            autoText.Children.Add(autoSub);
            Grid.SetColumn(autoText, 1);
            autoRow.Children.Add(autoText);
            _auto = new CheckBox { Style = U.Style("Toggle.Switch"), IsChecked = S.AutoGenerate, Margin = new Thickness(18, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            _auto.Checked += delegate { W.SetAutoGenerate(true); };
            _auto.Unchecked += delegate { W.SetAutoGenerate(false); };
            Grid.SetColumn(_auto, 2);
            autoRow.Children.Add(_auto);
            stack.Children.Add(autoRow);

            // Missing-key banner
            _bannerText = U.T("", 15, U.B(0xFFF3E7D2), FontWeights.SemiBold);
            _bannerText.VerticalAlignment = VerticalAlignment.Center;
            var bannerRow = new Grid();
            bannerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bannerRow.ColumnDefinitions.Add(new ColumnDefinition());
            var warn = U.Icon(U.GWarn, 17, U.Amber);
            warn.Margin = new Thickness(0, 0, 12, 0);
            bannerRow.Children.Add(warn);
            Grid.SetColumn(_bannerText, 1);
            bannerRow.Children.Add(_bannerText);
            _banner = new Border
            {
                Background = U.WarnBg,
                BorderBrush = U.WarnBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 24, 0, 0),
                Cursor = Cursors.Hand,
                Child = bannerRow
            };
            _banner.MouseLeftButtonUp += delegate { W.ShowSettings("ai"); };
            stack.Children.Add(_banner);

            _start = U.Btn("Btn.White", U.IconText(U.GPlay, "Start interview", 10), () => W.StartInterview(_previous));
            _start.FontSize = 16.5;
            _start.Padding = new Thickness(28, 13, 32, 13);
            _start.HorizontalAlignment = HorizontalAlignment.Right;
            _start.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(_start);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
            Children.Add(scroll);
            Refresh();
        }

        private static FrameworkElement Section(string glyph, string title, bool optional)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 26, 0, 12) };
            var icon = U.Icon(glyph, 16, U.Text2);
            icon.Margin = new Thickness(0, 0, 12, 0);
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
            // Session: "New interview", or continue one from the list.
            _sessionRow.Children.Clear();
            var fresh = U.Btn(_previous == null ? "Btn.White" : "Btn.Pill", U.IconText(U.GAddCircle, "New interview"), () => SetPrevious(null));
            _sessionRow.Children.Add(Spaced(fresh));
            Button pick = null;
            var label = _previous == null ? "Previous session…" : (_previous.Title.Length > 30 ? _previous.Title.Substring(0, 29) + "…" : _previous.Title);
            pick = U.Btn("Btn.Pill", OverlayWindow.PickerLabel(U.Icon(U.GHistory, 15, null), label), () => OpenSessionMenu(pick));
            _sessionRow.Children.Add(Spaced(pick));

            _languageRow.Children.Clear();
            _languageRow.Children.Add(W.LanguagePicker());

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

            // System prompt: the picked prompt (a menu) and "Create new".
            _promptRow.Children.Clear();
            Button prompt = null;
            prompt = U.Btn("Btn.Pill", OverlayWindow.PickerLabel(U.Icon(U.GChat, 15, null), Prompts.DisplayName(S, S.PromptId)), () => OpenPromptMenu(prompt));
            _promptRow.Children.Add(Spaced(prompt));
            _promptRow.Children.Add(Spaced(U.Btn("Btn.Pill", U.IconText(U.GAdd, "Create new", 8), () => W.EditPrompt(null, Refresh))));

            // Key check
            var missing = S.MissingKeysText;
            _banner.Visibility = missing != null ? Visibility.Visible : Visibility.Collapsed;
            _bannerText.Text = missing ?? "";
            _start.IsEnabled = missing == null;
            _auto.IsChecked = S.AutoGenerate;
        }

        private void OpenSessionMenu(Button target)
        {
            var menu = new ContextMenu { MaxHeight = 440 };
            menu.Items.Add(OverlayWindow.Item("New interview", () => SetPrevious(null)));
            menu.Items.Add(new Separator());
            var recent = SessionStore.All().Take(20).ToList();
            if (recent.Count == 0) menu.Items.Add(new MenuItem { Header = "No past sessions yet", IsEnabled = false });
            foreach (var s in recent)
            {
                var sess = s;
                var mi = new MenuItem { Header = sess.Title, IsCheckable = true, IsChecked = _previous != null && _previous.Id == sess.Id, InputGestureText = sess.Updated.ToString("MMM d") };
                mi.Click += delegate { SetPrevious(sess); };
                menu.Items.Add(mi);
            }
            OverlayWindow.OpenBelow(menu, target);
        }

        private void OpenPromptMenu(Button target)
        {
            var menu = new ContextMenu { MaxHeight = 440 };
            var def = new MenuItem { Header = Prompts.DefaultName, IsCheckable = true, IsChecked = string.IsNullOrEmpty(S.PromptId) };
            def.Click += delegate { PickPrompt(Prompts.DefaultId); };
            menu.Items.Add(def);
            var all = Prompts.All(S);
            if (all.Count > 0) menu.Items.Add(new Separator());
            foreach (var p in all)
            {
                var prompt = p;
                var mi = new MenuItem { Header = prompt.Name, IsCheckable = true, IsChecked = prompt.Id == S.PromptId };
                mi.Click += delegate { PickPrompt(prompt.Id); };
                menu.Items.Add(mi);
            }
            OverlayWindow.OpenBelow(menu, target);
        }

        private void PickPrompt(string id)
        {
            S.PromptId = id;
            W.SaveSettingsSoon();
            Refresh();
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
                BorderBrush = problem == null ? U.B(0xFF3A3A3A) : U.WarnBorder,
                Background = U.B(0xFF1F1F1F),
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
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Attachments.DialogFilter, Title = "Choose your resume" };
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
