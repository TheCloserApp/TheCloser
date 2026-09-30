using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TheCloser
{
    internal sealed class SubscriptionPlan
    {
        public string Id, Name, PriceText;
        public double AllowanceUSD;
        public List<string> Models;
        public bool CanPurchase;
    }

    /// <summary>The desktop stores an encrypted, short-lived subscription pass, never Stripe or provider keys.</summary>
    internal sealed class SubscriptionClient
    {
        internal const string ApiBase = "https://www.thecloser.tech/api/";
        private static readonly HttpClient DefaultHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        private readonly AppSettings S;
        private readonly HttpClient Http;
        private readonly bool Persist;
        private readonly SemaphoreSlim RefreshLock = new SemaphoreSlim(1, 1);
        private DateTime _lastVerified;
        public event Action StateChanged;

        public string DeviceId { get; private set; }
        public string Status { get; private set; }
        public string Error { get; private set; }
        public string Plan { get { return S.SubscriptionPlan; } }
        public DateTime ExpiresAt { get { return FromUnix(S.SubscriptionExpiresAt); } }
        public double AllowanceUSD { get { return S.SubscriptionAllowanceUSD; } }
        public double UsedUSD { get; private set; }
        public double RemainingUSD { get; private set; }
        public double UsedFraction { get; private set; }
        public DateTime PeriodEnd { get; private set; }
        public bool Renews { get; private set; }
        public bool HasUsage { get; private set; }
        public bool PendingCheckout { get { return !string.IsNullOrEmpty(S.CheckoutRequestId) || !string.IsNullOrEmpty(S.CheckoutSessionId); } }
        public List<string> Models { get { return new List<string>(S.SubscriptionModels ?? new List<string>()); } }
        public bool IsActive { get { return HasActivePass(S); } }
        /// <summary>Pro through a tester code rather than a subscription: billing doesn't apply.</summary>
        public bool IsTester { get { return IsActive && S.SubscriptionTester; } }
        public bool CanManageBilling { get { return !string.IsNullOrEmpty(S.SubscriptionPass); } }

        public SubscriptionClient(AppSettings settings) : this(settings, DefaultHttp, true) { }

        internal SubscriptionClient(AppSettings settings, HttpClient http, bool persist)
        {
            S = settings;
            Http = http;
            Persist = persist;
            var token = Secret.Unprotect(S.SubscriptionDeviceEnc);
            if (string.IsNullOrEmpty(token))
            {
                var bytes = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                token = Convert.ToBase64String(bytes);
                S.SubscriptionDeviceEnc = Secret.Protect(token);
                Save();
            }
            using (var sha = SHA256.Create())
                DeviceId = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("TheCloser.Windows.v1:" + token))).Replace("-", "").ToLowerInvariant();
            Status = IsActive ? "active" : "unknown";
        }

        internal static bool HasActivePass(AppSettings s)
        {
            return !string.IsNullOrEmpty(s.SubscriptionPass) && FromUnix(s.SubscriptionExpiresAt) > DateTime.UtcNow &&
                   (s.SubscriptionPlan == "pro" || s.SubscriptionPlan == "pro_max") && s.SubscriptionModels != null && s.SubscriptionModels.Count > 0;
        }

        internal static DateTime FromUnix(long seconds)
        {
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds); }
            catch { return DateTime.MinValue; }
        }

        private void Save() { if (Persist) S.Save(); }
        private void SaveCheckout()
        {
            if (Persist && !S.TrySave()) throw new SubscriptionException("settings_unwritable", "Your subscription identity couldn't be saved on this PC. Make your app settings folder writable before starting checkout.");
        }
        private void Changed() { var h = StateChanged; if (h != null) h(); }

        public Task RefreshAsync() { return RefreshCoreAsync(true, CancellationToken.None); }

        private async Task RefreshCoreAsync(bool force, CancellationToken ct)
        {
            await RefreshLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!force && IsActive && ExpiresAt >= DateTime.UtcNow.AddMinutes(5) && _lastVerified >= DateTime.UtcNow.AddMinutes(-5)) return;
                var request = Json.Obj("device", DeviceId);
                if (!string.IsNullOrEmpty(S.SubscriptionPass)) request["pass"] = S.SubscriptionPass;
                if (!string.IsNullOrEmpty(S.CheckoutSessionId)) request["checkoutSessionId"] = S.CheckoutSessionId;
                var response = await SendAsync("pass", request, null, ct).ConfigureAwait(false);
                ApplyPass(response);
                _lastVerified = DateTime.UtcNow;
                Save();
            }
            catch (SubscriptionException ex)
            {
                Status = ex.Code;
                Error = ex.Message;
                if (ex.Code == "checkout_expired") ClearCheckout();
                if (ex.Code == "no_subscription")
                {
                    // Keep the previous pass for server-side recovery and portal access, but never use it for AI.
                    S.SubscriptionExpiresAt = 0;
                    S.SubscriptionModels = new List<string>();
                    S.SubscriptionTester = false;
                    HasUsage = false;
                    Save();
                }
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                Status = "error";
                Error = ex is OperationCanceledException ? "Subscription check timed out. Try again." : "Couldn't check your subscription. Check your connection and try again.";
            }
            finally
            {
                RefreshLock.Release();
                Changed();
            }
        }

        internal void ApplyPass(object response)
        {
            var pass = Json.Str(response, "pass");
            var plan = Json.Str(response, "plan");
            long expires;
            var rawModels = Json.Get(response, "models") as object[];
            var models = rawModels == null ? new List<string>() : rawModels.OfType<string>().Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
            if (string.IsNullOrEmpty(pass) || (plan != "pro" && plan != "pro_max") ||
                !long.TryParse(Json.Str(response, "expiresAt"), out expires) || FromUnix(expires) <= DateTime.UtcNow || models.Count == 0)
                throw new SubscriptionException("invalid_response", "The subscription service returned an incomplete response. Try again.");
            S.SubscriptionPass = pass;
            S.SubscriptionPlan = plan;
            S.SubscriptionExpiresAt = expires;
            S.SubscriptionModels = models;
            S.SubscriptionAllowanceUSD = Number(response, "allowanceUSD");
            S.SubscriptionTester = object.Equals(Json.Get(response, "tester"), true);
            S.CheckoutSessionId = null;
            S.CheckoutRequestId = null;
            S.CheckoutPlan = null;
            Status = "active";
            Error = null;
        }

        /// <summary>
        /// "Have a tester code?": Pro paid from the test budget all testers share, no payment. The server checks the
        /// code (case, spaces and dashes don't matter) and returns a pass like a subscription's.
        /// </summary>
        public async Task RedeemAsync(string code)
        {
            code = (code ?? "").Trim();
            if (code.Length == 0) return;
            var response = await SendAsync("pass", Json.Obj("device", DeviceId, "code", code), null, CancellationToken.None).ConfigureAwait(false);
            ApplyPass(response);
            S.UseSubscription = true;
            _lastVerified = DateTime.UtcNow;
            Save();
            Changed();
        }

        public async Task<string> CheckoutAsync(string plan)
        {
            if (plan != "pro" && plan != "pro_max") throw new ArgumentException("Choose Pro or Pro Max.", "plan");
            // Reuse one idempotency key for retries and double-clicks. Only a different plan starts another attempt.
            if (string.IsNullOrEmpty(S.CheckoutRequestId) || S.CheckoutPlan != plan)
            {
                S.CheckoutRequestId = Guid.NewGuid().ToString("D");
                S.CheckoutPlan = plan;
                S.CheckoutSessionId = null;
                Save();
            }
            SaveCheckout();
            object response;
            try { response = await SendAsync("checkout", Json.Obj("device", DeviceId, "plan", plan, "requestId", S.CheckoutRequestId), null, CancellationToken.None).ConfigureAwait(false); }
            catch (SubscriptionException ex)
            {
                if (ex.Code == "checkout_expired") { ClearCheckout(); Changed(); }
                throw;
            }
            var url = SafeStripeUrl(Json.Str(response, "url"));
            S.CheckoutSessionId = Json.Str(response, "sessionId");
            SaveCheckout();
            Changed();
            return url;
        }

        private void ClearCheckout()
        {
            S.CheckoutRequestId = null;
            S.CheckoutSessionId = null;
            S.CheckoutPlan = null;
            Save();
        }

        public async Task<string> PortalAsync()
        {
            if (!CanManageBilling) await RefreshAsync().ConfigureAwait(false);
            if (!CanManageBilling) throw new SubscriptionException("no_subscription", "Refresh your subscription first to open Manage billing.");
            var response = await SendAsync("portal", Json.Obj("device", DeviceId), S.SubscriptionPass, CancellationToken.None).ConfigureAwait(false);
            return SafeStripeUrl(Json.Str(response, "url"));
        }
        public Task<string> UpgradeAsync() { return BillingUrlAsync("upgrade"); }
        public Task<string> UpgradeAsync(string plan)
        {
            if (plan != "pro_max") throw new ArgumentException("Use Manage billing to change this plan.", "plan");
            return UpgradeAsync();
        }

        private async Task<string> BillingUrlAsync(string endpoint)
        {
            await EnsureActiveAsync(CancellationToken.None).ConfigureAwait(false);
            return SafeStripeUrl(Json.Str(await SendAsync(endpoint, Json.Obj(), S.SubscriptionPass, CancellationToken.None).ConfigureAwait(false), "url"));
        }

        public async Task RefreshUsageAsync()
        {
            await EnsureActiveAsync(CancellationToken.None).ConfigureAwait(false);
            var response = await SendAsync("usage", null, S.SubscriptionPass, CancellationToken.None).ConfigureAwait(false);
            UsedUSD = Number(response, "usedUSD");
            RemainingUSD = Number(response, "remainingUSD");
            UsedFraction = Math.Max(0, Math.Min(1, Number(response, "usedFraction")));
            S.SubscriptionAllowanceUSD = Number(response, "allowanceUSD");
            long end;
            PeriodEnd = long.TryParse(Json.Str(response, "periodEnd"), out end) ? FromUnix(end) : DateTime.MinValue;
            Renews = object.Equals(Json.Get(response, "renews"), true);
            HasUsage = true;
            Changed();
        }

        public async Task<List<SubscriptionPlan>> LoadPlansAsync()
        {
            var response = await SendAsync("plans", null, null, CancellationToken.None).ConfigureAwait(false);
            var result = new List<SubscriptionPlan>();
            foreach (var id in new[] { "pro", "pro_max" })
            {
                var source = Json.Get(response, "plans", id);
                if (source == null) continue;
                var rawModels = Json.Get(source, "models") as object[];
                var price = Json.Get(source, "price");
                var label = PriceLabel(price);
                result.Add(new SubscriptionPlan {
                    Id = id, Name = Json.Str(source, "name") ?? id, AllowanceUSD = Number(source, "allowanceUSD"),
                    Models = rawModels == null ? new List<string>() : rawModels.OfType<string>().ToList(),
                    PriceText = label ?? "Price unavailable", CanPurchase = label != null
                });
            }
            return result;
        }

        internal static string PriceLabel(object price)
        {
            if (price == null || Json.Str(price, "interval") != "month") return null;
            long minor, count;
            if (!long.TryParse(Json.Str(price, "unitAmount"), out minor) || minor < 0 ||
                !long.TryParse(Json.Str(price, "intervalCount"), out count) || count < 1) return null;
            var currency = (Json.Str(price, "currency") ?? "").ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsLetter)) return null;
            // Stripe charge amounts use two decimals for ISK and UGX despite their zero-decimal currency units.
            var zeroDecimal = new[] { "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "VND", "VUV", "XAF", "XOF", "XPF" };
            var amount = zeroDecimal.Contains(currency) ? minor.ToString(CultureInfo.InvariantCulture) : (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
            return currency + " " + amount + (count == 1 ? " / month" : " / " + count + " months");
        }

        private async Task EnsureActiveAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsActive || ExpiresAt < DateTime.UtcNow.AddMinutes(5) || _lastVerified < DateTime.UtcNow.AddMinutes(-5))
                await RefreshCoreAsync(false, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!IsActive) throw new SubscriptionException(Status, Error ?? "Open Settings > AI to activate your plan.");
        }

        public async Task<StreamResult> StreamAsync(Dictionary<string, object> body, Action<string> onText, CancellationToken ct)
        {
            await EnsureActiveAsync(ct).ConfigureAwait(false);
            var model = Json.Str(body, "model");
            if (!Models.Contains(model)) throw new SubscriptionException("model_not_in_plan", "This model isn't included in your subscription. Pick an included model in the Model menu.");
            try
            {
                return await OpenRouterClient.StreamManagedAsync(S.SubscriptionPass, DeviceId, body, onText, ct, Http).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                if (ex.Status != 401) throw;
            }
            // The Windows .NET Framework compiler uses C# 5, which cannot await inside catch.
            await RefreshCoreAsync(true, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!IsActive) throw new SubscriptionException(Status, Error ?? "Refresh your subscription in Settings.");
            if (!Models.Contains(model)) throw new SubscriptionException("model_not_in_plan", "This model isn't included in your current plan.");
            return await OpenRouterClient.StreamManagedAsync(S.SubscriptionPass, DeviceId, body, onText, ct, Http).ConfigureAwait(false);
        }

        internal static string SafeStripeUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo) || (uri.Host != "checkout.stripe.com" && uri.Host != "billing.stripe.com"))
                throw new SubscriptionException("invalid_url", "The billing service returned an invalid Stripe link. Try again.");
            return uri.AbsoluteUri;
        }

        private async Task<object> SendAsync(string endpoint, Dictionary<string, object> body, string pass, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, ApiBase + endpoint))
            {
                if (pass != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pass);
                request.Headers.TryAddWithoutValidation("X-Device", DeviceId);
                if (body != null) request.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    object parsed;
                    try { parsed = Json.Parse(text); }
                    catch { throw new SubscriptionException("invalid_response", "The subscription service is unavailable. Try again shortly."); }
                    if (!response.IsSuccessStatusCode)
                    {
                        var code = Json.Str(parsed, "error") ?? "service_error";
                        throw new SubscriptionException(code, MessageFor(code));
                    }
                    return parsed;
                }
            }
        }

        internal static string MessageFor(string code)
        {
            switch (code)
            {
                case "no_subscription": return "No active subscription found for this Windows account. Choose a plan or finish your Stripe checkout.";
                case "setting_up": return "Payment received. Your subscription is being activated; check again shortly.";
                case "payment_pending": return "Waiting for Stripe payment confirmation. Finish checkout, then refresh.";
                case "already_subscribed": return "This Windows account already has a subscription. Refresh, then choose Manage billing.";
                case "already_pro_max": return "You're already on Pro Max. Refresh to update your plan.";
                case "pass_expired": return "Your subscription needs to be refreshed. Try again.";
                case "price_missing": return "This plan isn't available for purchase yet. Try again later.";
                case "checkout_expired": return "This Stripe checkout has expired. Choose a plan to start a new checkout.";
                case "bad_checkout_session": return "The checkout session couldn't be verified for this Windows account.";
                case "invalid_code": return "That code isn't valid.";
                case "tester_budget_not_set": return "The code couldn't be checked right now. Try again in a moment.";
                case "tester_access": return "Tester access has no subscription to manage.";
                default: return "The subscription service couldn't complete this request. Try again shortly.";
            }
        }

        private static double Number(object node, string key)
        {
            double result;
            return double.TryParse(Json.Str(node, key), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && !double.IsNaN(result) && !double.IsInfinity(result) ? result : 0;
        }
    }

    internal sealed class SubscriptionException : Exception
    {
        public readonly string Code;
        public SubscriptionException(string code, string message) : base(message) { Code = code; }
    }
}
