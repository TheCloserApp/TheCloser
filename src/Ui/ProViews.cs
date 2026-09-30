using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TheCloser.Ui
{
    /// <summary>TheCloser Pro pieces shared by the welcome tour and Settings > AI: plans, the tester code and the usage bar.</summary>
    internal static class ProViews
    {
        public const string InterestUrl = "https://www.thecloser.tech/?utm_source=app&utm_medium=onboarding&utm_campaign=pro_interest";

        private static readonly string[] ProHighlights = { "No API keys needed", "Top models included", "Transcription built in", "Request new models" };
        private static readonly string[] ProMaxHighlights = { "Everything in Pro", "The most powerful models", "2.5× the monthly usage" };

        /// <summary>Pro and Pro Max side by side, marked "Soon" while buying is off; just your plan while it's active.</summary>
        public static Grid PlanCards(OverlayWindow w)
        {
            var g = new Grid();
            if (w.Billing.IsActive)
            {
                g.Children.Add(w.Billing.Plan == "pro_max"
                    ? PlanCard("Pro Max", "$39", ProMaxHighlights, "Current plan")
                    : PlanCard("Pro", "$19", ProHighlights, "Current plan"));
                return g;
            }
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(PlanCard("Pro", "$19", ProHighlights, "Soon"));
            var max = PlanCard("Pro Max", "$39", ProMaxHighlights, "Soon");
            Grid.SetColumn(max, 2);
            g.Children.Add(max);
            return g;
        }

        private static Border PlanCard(string name, string price, string[] highlights, string badge)
        {
            var sp = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(U.T(name, 17, U.Text, FontWeights.SemiBold));
            title.Children.Add(U.Badge(badge));
            sp.Children.Add(title);
            var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
            priceRow.Children.Add(U.T(price, 27, U.Text, FontWeights.Bold));
            var per = U.T(" /month", 14.5, U.Text2);
            per.VerticalAlignment = VerticalAlignment.Bottom;
            per.Margin = new Thickness(4, 0, 0, 5);
            priceRow.Children.Add(per);
            sp.Children.Add(priceRow);
            foreach (var h in highlights)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                var tick = U.Icon(U.GCheck, 11, U.Text2);
                tick.Margin = new Thickness(0, 1, 9, 0);
                row.Children.Add(tick);
                row.Children.Add(U.T(h, 14, U.Text2));
                sp.Children.Add(row);
            }
            return U.Box(sp, new Thickness(18, 16, 18, 16));
        }

        /// <summary>"Have a tester code?": a link that opens a code field and Apply. `done` runs once the code works.</summary>
        public static FrameworkElement TesterCodeEntry(OverlayWindow w, Action done)
        {
            var host = new StackPanel();
            var link = U.Btn("Btn.Link", "Have a tester code?", null);
            link.Padding = new Thickness(0, 4, 0, 4);
            link.HorizontalAlignment = HorizontalAlignment.Left;
            var row = new Grid { Visibility = Visibility.Collapsed };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var input = U.Input("Tester code", "", false);
            input.Padding = new Thickness(12, 8, 12, 8);
            row.Children.Add(input);
            var apply = U.Btn("Btn.Pill", "Apply", null);
            apply.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(apply, 1);
            row.Children.Add(apply);
            var problem = U.T("", 13.5, U.Amber);
            problem.Margin = new Thickness(0, 8, 0, 0);
            problem.Visibility = Visibility.Collapsed;

            link.Click += delegate
            {
                link.Visibility = Visibility.Collapsed;
                row.Visibility = Visibility.Visible;
                input.Focus();
            };
            Action redeem = async delegate
            {
                var code = input.Text.Trim();
                if (code.Length == 0) return;
                apply.IsEnabled = false;
                apply.Content = "Checking…";
                problem.Visibility = Visibility.Collapsed;
                var error = await w.RedeemTesterCodeAsync(code);
                apply.IsEnabled = true;
                apply.Content = "Apply";
                if (error == null) { if (done != null) done(); return; }
                problem.Text = error;
                problem.Visibility = Visibility.Visible;
            };
            apply.Click += delegate { redeem(); };
            input.KeyDown += delegate(object o, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; redeem(); } };

            host.Children.Add(link);
            host.Children.Add(row);
            host.Children.Add(problem);
            return host;
        }

        /// <summary>The allowance as a thin bar: "42% of this month's allowance used" (a percentage, not dollars).</summary>
        public static FrameworkElement UsageMeter(SubscriptionClient b)
        {
            var sp = new StackPanel();
            double used = b.HasUsage ? b.UsedFraction : 0;
            var track = new Grid { Height = 5 };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = U.B(0x1AFFFFFF) });
            var fill = new Border
            {
                CornerRadius = new CornerRadius(2.5),
                Background = used >= 1 ? U.Red : used >= 0.8 ? U.Amber : U.Text,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            track.SizeChanged += delegate { fill.Width = track.ActualWidth * used; };
            track.Children.Add(fill);
            sp.Children.Add(track);
            int pct = (int)Math.Round(used * 100);
            string text = !b.HasUsage ? "Checking usage…"
                : b.IsTester ? pct + "% of the test budget used"
                : pct + "% of this month's allowance used";
            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(U.T(text, 13.5, U.Text2));
            if (b.HasUsage && !b.IsTester && b.PeriodEnd != DateTime.MinValue)
            {
                var resets = U.T((b.Renews ? "Resets " : "Ends ") + b.PeriodEnd.ToLocalTime().ToString("MMMM d", CultureInfo.CurrentCulture), 13.5, U.Text3);
                Grid.SetColumn(resets, 1);
                row.Children.Add(resets);
            }
            sp.Children.Add(row);
            return sp;
        }
    }
}
