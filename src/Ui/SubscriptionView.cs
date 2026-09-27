using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace TheCloser.Ui
{
    internal sealed partial class SettingsView
    {
        private List<SubscriptionPlan> _plans;
        private bool _plansLoading, _billingBusy;
        private string _plansError, _billingNotice;

        public void RefreshSubscription()
        {
            if (_current == "subscription") _page.Content = SubscriptionPage();
        }

        private FrameworkElement SubscriptionPage()
        {
            var p = Page();
            p.Children.Add(H("Subscription", true));
            p.Children.Add(Sub("AI answers without managing provider keys. Pay securely with Stripe. Your subscription is linked to this Windows account on this PC."));
            var mode = SwitchRow("Use subscription for AI answers", "Switch off to use your own AI keys. This does not cancel your subscription.", S.UseSubscription, async on =>
            {
                S.UseSubscription = on;
                S.Save();
                W.OnKeysChanged();
                RefreshSubscription();
                if (on) await W.RefreshBillingAsync();
            });
            p.Children.Add(U.Box(mode, new Thickness(22, 18, 22, 18)));

            p.Children.Add(H(W.Billing.IsActive ? (W.Billing.Plan == "pro_max" ? "Pro Max is active" : "Pro is active") : "Your plan"));
            string status;
            if (W.Billing.IsActive) status = "AI answers are ready. Choose any included model in the model menu.";
            else if (W.Billing.PendingCheckout) status = "Finish payment in your browser, then return here. We'll check for activation automatically; you can also choose Refresh.";
            else if (W.Billing.Status == "no_subscription") status = "No active subscription was found. Choose a plan below, or manage billing to update a failed payment.";
            else status = "Choose a plan to pay securely on Stripe, or refresh to restore this PC's subscription.";
            p.Children.Add(Sub(status));
            if (!string.IsNullOrEmpty(W.Billing.Error)) p.Children.Add(Sub(W.Billing.Error));
            if (!string.IsNullOrEmpty(_billingNotice)) p.Children.Add(Sub(_billingNotice));

            if (W.Billing.HasUsage && W.Billing.IsActive)
            {
                var usage = new StackPanel();
                usage.Children.Add(U.T("USD " + W.Billing.RemainingUSD.ToString("0.00", CultureInfo.InvariantCulture) + " of " + W.Billing.AllowanceUSD.ToString("0.00", CultureInfo.InvariantCulture) + " AI allowance remaining", 15, U.Text));
                if (W.Billing.PeriodEnd != DateTime.MinValue)
                    usage.Children.Add(Sub((W.Billing.Renews ? "Renews " : "Access ends ") + W.Billing.PeriodEnd.ToLocalTime().ToString("d")));
                p.Children.Add(U.Box(usage, new Thickness(22, 18, 22, 10)));
            }

            var controls = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            controls.Children.Add(BillingButton("Refresh", async delegate
            {
                await W.RefreshBillingAsync();
                _plansError = null;
                _plans = await W.Billing.LoadPlansAsync();
                _billingNotice = W.Billing.IsActive ? "Your subscription is up to date." : null;
            }));
            if (!string.IsNullOrEmpty(S.SubscriptionPass))
                controls.Children.Add(BillingButton("Manage billing", async delegate
                {
                    OpenBillingBrowser(await W.Billing.PortalAsync());
                    _billingNotice = "Manage your payment method, invoices, plan or cancellation in Stripe. Refresh here after making changes.";
                }));
            p.Children.Add(controls);

            p.Children.Add(H("Plans"));
            if (_plans == null)
            {
                p.Children.Add(Sub(_plansError ?? "Loading current Stripe prices…"));
                if (!_plansLoading && _plansError == null) LoadSubscriptionPlans();
            }
            else foreach (var plan in _plans)
            {
                var choice = plan;
                var card = new StackPanel();
                card.Children.Add(U.T(plan.Name, 17, U.Text, FontWeights.SemiBold));
                card.Children.Add(U.T(plan.PriceText, 16, U.Text));
                card.Children.Add(Sub("USD " + plan.AllowanceUSD.ToString("0.00", CultureInfo.InvariantCulture) + " of AI usage per billing period · " + plan.Models.Count + " included models"));
                card.Children.Add(Sub(string.Join(" · ", plan.Models.ConvertAll(ModelCatalog.Name))));
                var isCurrent = W.Billing.IsActive && W.Billing.Plan == plan.Id;
                var button = BillingButton(isCurrent ? "Current plan" : W.Billing.IsActive ? (plan.Id == "pro_max" ? "Upgrade on Stripe" : "Change plan on Stripe") : "Choose " + plan.Name, async delegate
                {
                    string url;
                    if (W.Billing.IsActive)
                        url = choice.Id == "pro_max" ? await W.Billing.UpgradeAsync() : await W.Billing.PortalAsync();
                    else
                    {
                        S.UseSubscription = true;
                        S.Save();
                        url = await W.Billing.CheckoutAsync(choice.Id);
                    }
                    OpenBillingBrowser(url);
                    W.WatchCheckout();
                    _billingNotice = "Stripe shows the final total, tax and recurring terms before you confirm. Return here after payment.";
                });
                button.IsEnabled = !_billingBusy && plan.CanPurchase && !isCurrent;
                card.Children.Add(button);
                var box = U.Box(card, new Thickness(22, 18, 22, 18));
                box.Margin = new Thickness(0, 0, 0, 12);
                p.Children.Add(box);
            }
            p.Children.Add(Sub("Subscriptions include AI answers. Use free Windows Live Captions for transcription, or add a separate xAI/OpenAI speech key in Models. Subscription prices and AI usage allowances are different amounts."));
            p.Children.Add(Sub("A subscription is linked to one device. A Mac subscription does not transfer to Windows. Keep your Windows app settings to retain access."));
            return p;
        }

        private async void LoadSubscriptionPlans()
        {
            _plansLoading = true;
            try { _plans = await W.Billing.LoadPlansAsync(); }
            catch (Exception ex) { _plansError = "Couldn't load prices: " + ex.Message + " Use Refresh to try again."; }
            finally { _plansLoading = false; RefreshSubscription(); }
        }

        private Button BillingButton(string label, Func<Task> action)
        {
            var button = U.Btn("Btn.Pill", label, async delegate
            {
                if (_billingBusy) return;
                _billingBusy = true;
                _billingNotice = null;
                RefreshSubscription();
                try { await action(); }
                catch (Exception ex) { _billingNotice = ex.Message; }
                finally { _billingBusy = false; RefreshSubscription(); W.OnKeysChanged(); }
            });
            button.IsEnabled = !_billingBusy;
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 6, 10, 6);
            return button;
        }

        private static void OpenBillingBrowser(string url)
        {
            // Fail visibly if Windows cannot open the browser; the saved Checkout attempt is reusable.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SubscriptionClient.SafeStripeUrl(url)) { UseShellExecute = true });
        }
    }
}
