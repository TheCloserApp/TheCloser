using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TheCloser.Ui
{
    /// <summary>Palette, text/icon factories and small animation helpers shared by every view.</summary>
    internal static class U
    {
        public static readonly FontFamily Font = new FontFamily("Segoe UI");
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");

        public static readonly SolidColorBrush Text = B(0xFFEDEDED);
        public static readonly SolidColorBrush Text2 = B(0xFFA1A1A1);
        public static readonly SolidColorBrush Text3 = B(0xFF808080);
        public static readonly SolidColorBrush Line = B(0xFF222222);
        public static readonly SolidColorBrush Border = B(0xFF262626);
        public static readonly SolidColorBrush Inner = B(0xFF1A1A1A);
        public static readonly SolidColorBrush Green = B(0xFF34C759);
        public static readonly SolidColorBrush Blue = B(0xFF3B8EFF);
        public static readonly SolidColorBrush Red = B(0xFFFF453A);
        public static readonly SolidColorBrush RecText = B(0xFFFF6A61);
        public static readonly SolidColorBrush RecBg = B(0xFF3B1718);
        public static readonly SolidColorBrush Amber = B(0xFFFF9F0A);
        public static readonly SolidColorBrush WarnBg = B(0xFF2D2313);
        public static readonly SolidColorBrush WarnBorder = B(0xFF5C4617);
        public static readonly SolidColorBrush CodeBg = B(0xFF0F0F11);
        public static readonly SolidColorBrush Chip = B(0xFF292929);

        // Glyphs (Segoe Fluent Icons / MDL2)
        public const string GClose = "", GCompose = "", GMore = "", GList = "", GHide = "",
            GView = "", GDown = "", GUp = "", GRight = "", GSend = "", GAddCircle = "",
            GHistory = "", GUpload = "", GAttach = "", GChat = "", GGear = "", GKeyboard = "",
            GPane = "", GMonitor = "", GGlobe = "", GPerson = "", GSearch = "", GLink = "",
            GNewWindow = "", GWarn = "", GPlay = "", GCheck = "", GTrash = "", GAdd = "",
            GMic = "", GDoc = "", GArrowRight = "", GQuestion = "", GEdit = "", GPause = "",
            GCamera = "", GCopy = "";

        public static Color C(uint argb)
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        public static SolidColorBrush B(uint argb)
        {
            var b = new SolidColorBrush(C(argb));
            b.Freeze();
            return b;
        }

        public static Style Style(string key)
        {
            return (Style)Application.Current.FindResource(key);
        }

        public static TextBlock T(string text, double size, Brush fg, FontWeight weight)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = fg, FontWeight = weight, FontFamily = Font, TextWrapping = TextWrapping.Wrap };
        }

        public static TextBlock T(string text, double size, Brush fg)
        {
            return T(text, size, fg, FontWeights.Normal);
        }

        /// <summary>Icon glyph; with fg == null it inherits the parent's foreground (e.g. a button's).</summary>
        public static TextBlock Icon(string glyph, double size, Brush fg)
        {
            var t = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            if (fg != null) t.Foreground = fg;
            return t;
        }

        /// <summary>Icon + label, used as button content.</summary>
        public static StackPanel IconText(object icon, string text, double gap = 9)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var el = icon as UIElement ?? Icon((string)icon, 15, null);
            sp.Children.Add(el);
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(gap, 0, 0, 1) };
            sp.Children.Add(label);
            return sp;
        }

        public static Button Btn(string style, object content, Action click, string tip = null)
        {
            var b = new Button { Style = Style(style), Content = content };
            if (click != null) b.Click += delegate { click(); };
            if (tip != null) b.ToolTip = tip;
            return b;
        }

        public static Button Circle(string glyph, Action click, string tip, double iconSize = 15, Brush fg = null)
        {
            var b = Btn("Btn.Circle", Icon(glyph, iconSize, fg), click, tip);
            return b;
        }

        /// <summary>Rounded inner card used on settings pages.</summary>
        public static Border Box(UIElement child, Thickness padding)
        {
            return new Border
            {
                Background = Inner,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = padding,
                Child = child
            };
        }

        public static Border Divider(Thickness margin)
        {
            return new Border { Height = 1, Background = Line, Margin = margin };
        }

        /// <summary>Small grey "Optional" badge.</summary>
        public static Border Badge(string text)
        {
            return new Border
            {
                Background = Chip,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 3),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = T(text, 12, Text2, FontWeights.SemiBold)
            };
        }

        /// <summary>Keyboard shortcut chip.</summary>
        public static Border Key(string text)
        {
            return new Border
            {
                Background = Chip,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 4, 9, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock { Text = text, FontFamily = Font, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Text }
            };
        }

        // --- Animation ---------------------------------------------------------------------------

        private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        public static void Animate(UIElement el, DependencyProperty prop, double to, int ms, Action done = null)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
            if (done != null) a.Completed += delegate { done(); };
            el.BeginAnimation(prop, a);
        }

        /// <summary>Fades and slides an element in from slightly below.</summary>
        public static void Enter(FrameworkElement el, double dy = 10, int ms = 220)
        {
            var tt = el.RenderTransform as TranslateTransform;
            if (tt == null) { tt = new TranslateTransform(); el.RenderTransform = tt; }
            el.Opacity = 0;
            tt.Y = dy;
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
        }

        public static void Pulse(UIElement el)
        {
            var a = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            el.BeginAnimation(UIElement.OpacityProperty, a);
        }

        // --- Drawn icons -------------------------------------------------------------------------

        /// <summary>Four-point "AI" sparkle.</summary>
        public static Path Sparkle(double size, Brush fg)
        {
            var g = Geometry.Parse("M12,1 C12.8,7.2 16.8,11.2 23,12 C16.8,12.8 12.8,16.8 12,23 C11.2,16.8 7.2,12.8 1,12 C7.2,11.2 11.2,7.2 12,1 Z " +
                                   "M20,1.5 C20.3,3.3 20.9,3.9 22.5,4.2 C20.9,4.5 20.3,5.1 20,6.9 C19.7,5.1 19.1,4.5 17.5,4.2 C19.1,3.9 19.7,3.3 20,1.5 Z");
            var p = new Path { Data = g, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            if (fg != null) p.Fill = fg;
            else p.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
            return p;
        }

        /// <summary>Audio waveform bars (optionally with a mic, as in the empty state).</summary>
        public static FrameworkElement Waveform(double height, Brush fg, bool withMic)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            double[] bars = { 0.35, 0.7, 1.0, 0.6, 0.85, 0.45 };
            double w = Math.Max(2, height / 11);
            foreach (var f in bars)
            {
                sp.Children.Add(new Rectangle
                {
                    Width = w,
                    Height = Math.Max(w, height * f),
                    RadiusX = w / 2,
                    RadiusY = w / 2,
                    Fill = fg,
                    Margin = new Thickness(w * 0.55, 0, w * 0.55, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            if (withMic)
            {
                var mic = Icon(GMic, height * 0.62, fg);
                mic.Margin = new Thickness(w, height * 0.28, 0, 0);
                sp.Children.Add(mic);
            }
            return sp;
        }

        /// <summary>The dock mark: five rounded waveform bars, tallest in the middle.</summary>
        public static FrameworkElement WaveLogo(double height, Brush fg)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            double[] bars = { 0.42, 0.74, 1.0, 0.74, 0.42 };
            double w = Math.Max(2, height * 0.15);
            foreach (var f in bars)
            {
                sp.Children.Add(new Rectangle
                {
                    Width = w,
                    Height = height * f,
                    RadiusX = w / 2,
                    RadiusY = w / 2,
                    Fill = fg,
                    Margin = new Thickness(w * 0.5, 0, w * 0.5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            return sp;
        }

        /// <summary>Password box with a placeholder.</summary>
        public static Grid SecretField(string placeholder, string value, out PasswordBox box)
        {
            var g = new Grid();
            var pb = new PasswordBox { Style = Style("Pwd.Input"), Password = value ?? "" };
            var ph = new TextBlock
            {
                Text = placeholder,
                Foreground = Text3,
                FontSize = 14,
                Margin = new Thickness(15, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = string.IsNullOrEmpty(value) ? Visibility.Visible : Visibility.Collapsed
            };
            pb.PasswordChanged += delegate { ph.Visibility = pb.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            g.Children.Add(pb);
            g.Children.Add(ph);
            box = pb;
            return g;
        }

        public static TextBox Input(string placeholder, string value, bool multiline)
        {
            var t = new TextBox { Style = Style("Text.Input"), Tag = placeholder, Text = value ?? "" };
            if (multiline)
            {
                t.AcceptsReturn = true;
                t.TextWrapping = TextWrapping.Wrap;
                t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                t.VerticalContentAlignment = VerticalAlignment.Top;
            }
            return t;
        }

        /// <summary>Row of chip-style radio buttons acting as a segmented control.</summary>
        public static WrapPanel Segmented(string[] labels, string current, Action<string> onPick)
        {
            var wp = new WrapPanel();
            var group = "seg" + Guid.NewGuid().ToString("N");
            foreach (var label in labels)
            {
                var l = label;
                var rb = new RadioButton { Style = Style("Radio.Chip"), Content = label, GroupName = group, IsChecked = label == current, Margin = new Thickness(0, 0, 8, 8) };
                rb.Checked += delegate { onPick(l); };
                wp.Children.Add(rb);
            }
            return wp;
        }
    }
}
