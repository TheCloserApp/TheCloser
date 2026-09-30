using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TheCloser.Ui
{
    /// <summary>
    /// "On a call in Zoom? [Start interview] ✕": a slim card in the top-right corner when a call app starts using the
    /// microphone. Hidden from screen sharing like the overlay, and it never takes focus from the call.
    /// </summary>
    internal sealed class CallPromptWindow : Window
    {
        private readonly TextBlock _text;
        private Action _onStart;

        public CallPromptWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = U.Font;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var logo = U.WaveLogo(15, U.Text);
            logo.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(logo);
            _text = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = U.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 1) };
            row.Children.Add(_text);
            var start = U.Btn("Btn.White", "Start interview", delegate { var a = _onStart; Hide(); if (a != null) a(); });
            start.FontSize = 13;
            start.Padding = new Thickness(13, 5, 13, 6);
            row.Children.Add(start);
            var close = U.Btn("Btn.Ghost", U.Icon(U.GClose, 10, null), Hide, "Not now");
            close.Padding = new Thickness(7);
            close.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(close);

            Content = new Border
            {
                Background = U.B(0xFF111111),
                BorderBrush = U.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 8, 8, 8),
                Margin = new Thickness(14),
                MinWidth = 320,
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black },
                Child = row
            };
            SourceInitialized += delegate { Native.SetCaptureExcluded(new WindowInteropHelper(this).Handle, OverlayWindow.StealthOn); };
            SizeChanged += delegate { PlaceTopRight(); };
        }

        public void ShowFor(string app, Action onStart)
        {
            _onStart = onStart;
            _text.Text = "On a call in " + app + "?";
            Show();
            PlaceTopRight();
        }

        private void PlaceTopRight()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 4;
            Top = wa.Top + 4;
        }
    }
}
