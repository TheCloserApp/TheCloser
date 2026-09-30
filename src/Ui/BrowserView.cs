using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace TheCloser.Ui
{
    /// <summary>Quick browser launcher (Google / ChatGPT / Claude / any URL). Pages open in a chromeless Edge app window.</summary>
    internal sealed class BrowserView : Grid
    {
        private readonly OverlayWindow W;
        private readonly TextBox _url;
        private readonly StackPanel _recent = new StackPanel { Margin = new Thickness(0, 24, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        private readonly List<string> _opened = new List<string>();

        public BrowserView(OverlayWindow w)
        {
            W = w;
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };

            var globe = U.Icon(U.GGlobe, 46, U.Text2);
            globe.HorizontalAlignment = HorizontalAlignment.Center;
            center.Children.Add(globe);
            var title = U.T("No tabs open", 21, U.Text, FontWeights.SemiBold);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.Margin = new Thickness(0, 16, 0, 0);
            center.Children.Add(title);
            var sub = U.T("Pick a quick start, or type a custom URL to open it in a new tab.", 15, U.Text2);
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.TextAlignment = TextAlignment.Center;
            sub.Margin = new Thickness(0, 8, 0, 22);
            center.Children.Add(sub);

            var quick = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            quick.Children.Add(Quick(U.Icon(U.GSearch, 15, null), "Google", "https://www.google.com"));
            quick.Children.Add(Quick(U.Icon(U.GChat, 15, null), "ChatGPT", "https://chatgpt.com"));
            quick.Children.Add(Quick(U.Sparkle(15, null), "Claude", "https://claude.ai"));
            center.Children.Add(quick);

            // URL field: [link icon | url ................ | ->]
            var urlGrid = new Grid { Width = 470, Margin = new Thickness(0, 14, 0, 0) };
            _url = new TextBox { Style = U.Style("Text.Input"), Tag = "Enter a URL (e.g. example.com)", Padding = new Thickness(42, 11, 46, 11), FontSize = 15.5 };
            _url.KeyDown += delegate(object o, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; OpenTyped(false); } };
            urlGrid.Children.Add(_url);
            var link = U.Icon(U.GLink, 15, U.Text3);
            link.HorizontalAlignment = HorizontalAlignment.Left;
            link.Margin = new Thickness(16, 0, 0, 0);
            link.IsHitTestVisible = false;
            urlGrid.Children.Add(link);
            var go = U.Btn("Btn.Ghost", U.Icon(U.GArrowRight, 13, null), () => OpenTyped(false), "Open");
            go.HorizontalAlignment = HorizontalAlignment.Right;
            go.Margin = new Thickness(0, 0, 6, 0);
            urlGrid.Children.Add(go);
            // The template renders its own rounded border; round the URL box more like a pill.
            urlGrid.Loaded += delegate
            {
                var bd = _url.Template.FindName("bd", _url) as Border;
                if (bd != null) bd.CornerRadius = new CornerRadius(24);
            };
            center.Children.Add(urlGrid);

            var own = U.Btn("Btn.Pill", U.IconText(U.GNewWindow, "Open in its own window"), () => OpenTyped(true));
            own.HorizontalAlignment = HorizontalAlignment.Center;
            own.Margin = new Thickness(0, 22, 0, 0);
            center.Children.Add(own);
            center.Children.Add(_recent);

            Children.Add(center);
        }

        private Button Quick(FrameworkElement icon, string name, string url)
        {
            var b = U.Btn("Btn.Pill", U.IconText(icon, name), () => Open(url, false));
            b.Margin = new Thickness(5, 0, 5, 0);
            return b;
        }

        private void OpenTyped(bool ownWindow)
        {
            var text = _url.Text.Trim();
            if (text.Length == 0) text = "https://www.google.com";
            if (!text.Contains("://"))
                text = text.Contains(".") && !text.Contains(" ") ? "https://" + text : "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
            Open(text, ownWindow);
        }

        private void Open(string url, bool ownWindow)
        {
            if (!TryEdgeApp(url)) OverlayWindow.OpenUrl(url);
            if (!_opened.Contains(url)) _opened.Insert(0, url);
            RenderRecent();
            W.Toast("Opened " + new Uri(url).Host + " in its own window. Note: that window shows up in screen shares.", false);
        }

        /// <summary>Microsoft Edge "app mode": a clean window without tabs or address bar.</summary>
        private static bool TryEdgeApp(string url)
        {
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                var edge = Path.Combine(dir, @"Microsoft\Edge\Application\msedge.exe");
                if (!File.Exists(edge)) continue;
                try
                {
                    Process.Start(new ProcessStartInfo(edge, "--app=\"" + url + "\" --window-size=900,760") { UseShellExecute = false });
                    return true;
                }
                catch { }
            }
            return false;
        }

        private void RenderRecent()
        {
            _recent.Children.Clear();
            if (_opened.Count == 0) return;
            _recent.Children.Add(new TextBlock { Text = "OPENED THIS SESSION", Foreground = U.Text3, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var u in _opened)
            {
                var url = u;
                var b = U.Btn("Btn.Ghost", new TextBlock { Text = url, FontSize = 13.5, Foreground = U.Text2 }, () => Open(url, false), "Open again");
                _recent.Children.Add(b);
            }
        }
    }

    /// <summary>Saved sessions: open to review or continue, delete.</summary>
    /// <summary>History: "Interview" with a count, a search box and your saved interviews - like the Mac's.</summary>
    internal sealed class HistoryView : Grid
    {
        private readonly OverlayWindow W;
        private readonly StackPanel _header = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
        private readonly StackPanel _list = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
        private readonly TextBox _search;
        private string _query = "";

        public HistoryView(OverlayWindow w)
        {
            W = w;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            _search = U.Input("Search sessions…", "", false);
            _search.Padding = new Thickness(40, 10, 14, 10);
            _search.TextChanged += delegate { _query = _search.Text.Trim(); FillList(); };
            Children.Add(_header);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
            SetRow(scroll, 1);
            Children.Add(scroll);
        }

        public void Refresh()
        {
            _header.Children.Clear();
            var sessions = SessionStore.All();
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            title.Children.Add(U.T("Interview", 19, U.Text, FontWeights.SemiBold));
            title.Children.Add(U.Badge(sessions.Count.ToString()));
            _header.Children.Add(title);
            var chip = new Border
            {
                Background = U.B(0xFF1F1F1F),
                BorderBrush = U.B(0xFF3A3A3A),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(12, 5, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = U.IconText(U.Icon(U.GPerson, 13, U.Text), "Interview", 7)
            };
            TextElement.SetForeground(chip, U.Text);
            TextElement.SetFontSize(chip, 14);
            TextElement.SetFontWeight(chip, FontWeights.SemiBold);
            _header.Children.Add(chip);
            var searchHost = new Grid { Margin = new Thickness(0, 12, 0, 6) };
            searchHost.Children.Add(_search);
            var glass = U.Icon(U.GSearch, 14, U.Text2);
            glass.HorizontalAlignment = HorizontalAlignment.Left;
            glass.Margin = new Thickness(15, 0, 0, 0);
            glass.IsHitTestVisible = false;
            searchHost.Children.Add(glass);
            _header.Children.Add(searchHost);
            FillList();
        }

        private void FillList()
        {
            _list.Children.Clear();
            var sessions = SessionStore.All()
                .Where(s => _query.Length == 0 || s.Title.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            s.Qas.Any(q => (q.Question ?? "").IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            if (sessions.Count == 0)
            {
                var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 90, 0, 0) };
                var icon = U.Icon(U.GPerson, 30, U.Text3);
                empty.Children.Add(icon);
                var t = U.T(_query.Length > 0 ? "No matches" : "No interviews yet", 16.5, U.Text2, FontWeights.SemiBold);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.Margin = new Thickness(0, 12, 0, 6);
                empty.Children.Add(t);
                if (_query.Length == 0)
                {
                    var sub = U.T("Start one from the Interview tab — it'll land here when you're done.", 14, U.Text3);
                    sub.TextAlignment = TextAlignment.Center;
                    sub.MaxWidth = 340;
                    empty.Children.Add(sub);
                }
                _list.Children.Add(empty);
                return;
            }
            foreach (var session in sessions)
            {
                var s = session;
                var row = new Grid { Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
                info.Children.Add(new TextBlock { Text = s.Title, FontSize = 15.5, FontWeight = FontWeights.SemiBold, Foreground = U.Text, TextTrimming = TextTrimming.CharacterEllipsis });
                var meta = s.Updated.ToString("ddd, MMM d · h:mm tt") + "  ·  " + s.Qas.Count + (s.Qas.Count == 1 ? " answer" : " answers") +
                           "  ·  " + TimeSpan.FromSeconds(s.ElapsedSeconds).ToString(@"h\:mm\:ss");
                info.Children.Add(new TextBlock { Text = meta, FontSize = 13, Foreground = U.Text3, Margin = new Thickness(0, 3, 0, 0) });
                row.Children.Add(info);
                row.MouseLeftButtonUp += delegate { W.OpenSession(s); };
                var del = U.Btn("Btn.Ghost", U.Icon(U.GTrash, 14, null), () => W.ConfirmDeleteSession(s, Refresh), "Delete");
                del.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
                _list.Children.Add(row);
                _list.Children.Add(U.Divider(new Thickness(0, 2, 0, 2)));
            }
        }
    }
}
