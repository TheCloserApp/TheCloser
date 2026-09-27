using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TheCloser
{
    /// <summary>Offline billing regression checks. No real settings files, network calls, or payments.</summary>
    internal static class SubscriptionSelfTest
    {
        internal static void Run(Action<bool, string> check)
        {
            var handler = new FakeBillingHandler();
            var settings = new AppSettings();
            using (var http = new HttpClient(handler))
            {
                var billing = new SubscriptionClient(settings, http, false);
                var sameUser = new SubscriptionClient(settings.Clone(), http, false);
                check(billing.DeviceId.Length == 64 && billing.DeviceId.All(c => "0123456789abcdef".Contains(c)), "device identity is an opaque SHA-256 digest");
                check(billing.DeviceId == sameUser.DeviceId, "device identity survives a settings round trip");
                check(billing.DeviceId != new SubscriptionClient(new AppSettings(), http, false).DeviceId, "separate settings get separate device identities");

                billing.ApplyPass(Pass());
                check(billing.IsActive && billing.Plan == "pro", "paid response activates subscription");
                check(!Json.Serialize(settings).Contains("test-signed-pass"), "subscription pass is encrypted in persisted JSON");
                check(settings.Clone().SubscriptionPass == "test-signed-pass", "subscription pass decrypts after a round trip");
                check(Throws(delegate { billing.ApplyPass(Json.Obj("pass", "bad", "plan", "pro", "expiresAt", 0, "models", new object[] { "x" })); }), "expired pass response is rejected");
                check(settings.SubscriptionPass == "test-signed-pass", "incomplete response cannot replace a valid pass");

                settings.UseSubscription = true;
                settings.AnthropicKey = "test-anthropic-key";
                settings.OpenRouterKey = "test-openrouter-key";
                check(ModelCatalog.Resolve(settings, "anthropic/claude-haiku-4.5").Provider == "subscription", "subscription answers ignore stored provider keys");
                check(ModelCatalog.Resolve(settings, "anthropic/not-in-plan").Provider == null, "plan model allowlist blocks unavailable model");
                check(QuestionGate.Route(settings).Provider == "subscription", "subscription question detection ignores stored provider keys");
                check(Throws(delegate { AnswerEngine.StreamAsync(settings, new AnswerRequest(), delegate { }, CancellationToken.None); }), "missing subscription service never falls back to own keys");

                handler.Responses.Enqueue(Reply(200, Pass()));
                billing.RefreshAsync().GetAwaiter().GetResult();
                var before = handler.Requests.Count;
                check(Throws(delegate { billing.StreamAsync(Json.Obj("model", "not-in-plan"), delegate { }, CancellationToken.None).GetAwaiter().GetResult(); }), "unavailable model is blocked before streaming");
                check(handler.Requests.Count == before, "unavailable model spends no requests or own-key credits");
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    check(Throws(delegate { billing.StreamAsync(Json.Obj("model", "anthropic/claude-haiku-4.5"), delegate { }, cancel.Token).GetAwaiter().GetResult(); }), "cancelled answer stops before network access");
                }
                check(handler.Requests.Count == before, "cancelled answer does not send a request");
                handler.Responses.Enqueue(StreamReply("Included answer"));
                var streamed = "";
                var answer = billing.StreamAsync(Json.Obj("model", "anthropic/claude-haiku-4.5"), t => streamed += t, CancellationToken.None).GetAwaiter().GetResult();
                check(answer.Text == "Included answer" && streamed == answer.Text, "managed answer stream reaches the app");
                check(handler.Paths.Last() == "/api/chat" && handler.Auth.Last() == "Bearer test-signed-pass", "managed answers send only the signed pass to the managed endpoint");
                check(handler.Devices.Last() == billing.DeviceId, "managed answers include device binding");
                check(object.Equals(Json.Get(handler.Requests.Last(), "stream"), true), "managed answers request streaming");
                handler.Responses.Enqueue(Reply(401, Json.Obj("error", "pass_expired")));
                handler.Responses.Enqueue(Reply(200, Pass()));
                handler.Responses.Enqueue(StreamReply("Renewed answer"));
                answer = billing.StreamAsync(Json.Obj("model", "anthropic/claude-haiku-4.5"), delegate { }, CancellationToken.None).GetAwaiter().GetResult();
                check(answer.Text == "Renewed answer", "expired server pass refreshes and retries through the managed endpoint");
                check(handler.Paths.Skip(handler.Paths.Count - 3).SequenceEqual(new[] { "/api/chat", "/api/pass", "/api/chat" }), "expired pass retry uses one renewal before retrying AI");


                handler.Responses.Enqueue(Reply(402, Json.Obj("error", "no_subscription")));
                billing.RefreshAsync().GetAwaiter().GetResult();
                check(!billing.IsActive && billing.Status == "no_subscription", "ended subscription invalidates cached access");
                check(ModelCatalog.Resolve(settings, "anthropic/claude-haiku-4.5").Provider == null, "ended subscription never falls back to own keys");
                check(billing.CanManageBilling, "inactive subscription keeps signed pass for billing recovery");
                handler.Responses.Enqueue(Reply(200, Json.Obj("url", "https://billing.stripe.com/p/session/recover")));
                billing.PortalAsync().GetAwaiter().GetResult();
                check(Json.Str(handler.Requests.Last(), "device") == billing.DeviceId, "billing recovery sends device binding with expired signed pass");

                handler.Responses.Enqueue(Reply(200, Json.Obj("url", "https://checkout.stripe.com/c/pay/test", "sessionId", "cs_test_checkout")));
                billing.CheckoutAsync("pro").GetAwaiter().GetResult();
                var firstRequestId = settings.CheckoutRequestId;
                check(billing.PendingCheckout && settings.CheckoutSessionId == "cs_test_checkout", "checkout correlation survives browser handoff");
                handler.Responses.Enqueue(Reply(200, Json.Obj("url", "https://checkout.stripe.com/c/pay/test", "sessionId", "cs_test_checkout")));
                billing.CheckoutAsync("pro").GetAwaiter().GetResult();
                check(settings.CheckoutRequestId == firstRequestId, "checkout retry reuses its idempotency key");
                handler.Responses.Enqueue(Reply(409, Json.Obj("error", "payment_pending")));
                billing.RefreshAsync().GetAwaiter().GetResult();
                check(billing.Status == "payment_pending" && billing.PendingCheckout, "pending payment remains pending for safe polling");
                check(Json.Str(handler.Requests.Last(), "checkoutSessionId") == "cs_test_checkout", "activation checks exact Stripe checkout session");
                handler.Responses.Enqueue(Reply(200, Pass()));
                billing.RefreshAsync().GetAwaiter().GetResult();
                check(billing.IsActive && !billing.PendingCheckout, "confirmed activation clears pending checkout");
                handler.Responses.Enqueue(Reply(200, Json.Obj("url", "https://checkout.stripe.com/c/pay/expired", "sessionId", "cs_test_expired")));
                billing.CheckoutAsync("pro").GetAwaiter().GetResult();
                handler.Responses.Enqueue(Reply(409, Json.Obj("error", "checkout_expired")));
                billing.RefreshAsync().GetAwaiter().GetResult();
                check(!billing.PendingCheckout && settings.CheckoutRequestId == null, "expired checkout clears attempt for the next explicit purchase");

                settings.UseSubscription = false;
                check(ModelCatalog.Resolve(settings, "anthropic/claude-haiku-4.5").Provider == "anthropic", "own-key mode keeps existing direct routing");
                check(handler.Responses.Count == 0, "all fake server responses were consumed");
            }
            check(SubscriptionClient.SafeStripeUrl("https://billing.stripe.com/p/session/test").StartsWith("https://billing.stripe.com/"), "Stripe portal URL accepted");
            foreach (var url in new[] { "http://checkout.stripe.com/pay", "https://checkout.stripe.com.evil.test/pay", "https://checkout.stripe.com@evil.test/pay", "file:///tmp/test", "https://billing.stripe.com:8443/pay", "javascript:alert(1)" })
                check(Throws(delegate { SubscriptionClient.SafeStripeUrl(url); }), "unsafe payment URL rejected: " + url);
            check(SubscriptionClient.PriceLabel(Json.Obj("unitAmount", 1299, "currency", "usd", "interval", "month", "intervalCount", 1)) == "USD 12.99 / month", "monthly price uses exact Stripe minor units");
            check(SubscriptionClient.PriceLabel(Json.Obj("unitAmount", 1200, "currency", "jpy", "interval", "month", "intervalCount", 1)) == "JPY 1200 / month", "zero-decimal currency is displayed correctly");
            check(SubscriptionClient.PriceLabel(Json.Obj("unitAmount", 1200, "currency", "ugx", "interval", "month", "intervalCount", 1)) == "UGX 12.00 / month", "Stripe special charge-unit currency is displayed correctly");
            check(SubscriptionClient.PriceLabel(null) == null, "missing live price disables purchase");
        }

        private static object Pass()
        {
            var expires = (long)(DateTime.UtcNow.AddHours(2) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return Json.Obj("pass", "test-signed-pass", "plan", "pro", "expiresAt", expires, "allowanceUSD", 8,
                "models", new object[] { "anthropic/claude-haiku-4.5", "openai/gpt-5.4-mini" });
        }

        private static HttpResponseMessage Reply(int status, object body)
        {
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Json.Serialize(body)) };
        }

        private static HttpResponseMessage StreamReply(string text)
        {
            var data = Json.Serialize(Json.Obj("choices", new object[] { Json.Obj("delta", Json.Obj("content", text), "finish_reason", "stop") }));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + data + "\n\ndata: [DONE]\n\n") };
        }

        private static bool Throws(Action action)
        {
            try { action(); return false; }
            catch { return true; }
        }

        private sealed class FakeBillingHandler : HttpMessageHandler
        {
            internal readonly Queue<HttpResponseMessage> Responses = new Queue<HttpResponseMessage>();
            internal readonly List<object> Requests = new List<object>();
            internal readonly List<string> Paths = new List<string>();
            internal readonly List<string> Auth = new List<string>();
            internal readonly List<string> Devices = new List<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri.Host != "www.thecloser.tech") throw new InvalidOperationException("Unexpected API host in test.");
                Requests.Add(request.Content == null ? null : Json.Parse(await request.Content.ReadAsStringAsync().ConfigureAwait(false)));
                Paths.Add(request.RequestUri.AbsolutePath);
                Auth.Add(request.Headers.Authorization == null ? null : request.Headers.Authorization.ToString());
                Devices.Add(request.Headers.GetValues("X-Device").First());
                if (Responses.Count == 0) throw new InvalidOperationException("Unexpected request in test.");
                return Responses.Dequeue();
            }
        }
    }
}
