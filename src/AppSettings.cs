using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TheCloser
{
    /// <summary>A user-written system prompt ("Create new" on the setup screen).</summary>
    public sealed class PromptDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Text { get; set; }
    }

    /// <summary>User settings, persisted as JSON in %APPDATA%\TheCloser. API keys are DPAPI-encrypted.</summary>
    public sealed class AppSettings
    {
        // --- API keys (encrypted) ---
        public string OpenRouterKeyEnc { get; set; }
        public string ElevenLabsKeyEnc { get; set; }
        public string XaiKeyEnc { get; set; }

        // --- Subscription (no Stripe or managed-provider secrets are stored here) ---
        public bool UseSubscription { get; set; }
        public string SubscriptionDeviceEnc { get; set; }
        public string SubscriptionPassEnc { get; set; }
        public long SubscriptionExpiresAt { get; set; }
        public string SubscriptionPlan { get; set; }
        public List<string> SubscriptionModels { get; set; }
        public double SubscriptionAllowanceUSD { get; set; }
        public bool SubscriptionTester { get; set; }       // Pro through a tester code
        public string CheckoutRequestId { get; set; }
        public string CheckoutPlan { get; set; }
        public string CheckoutSessionId { get; set; }

        [ScriptIgnore] public string SubscriptionPass { get { return Secret.Unprotect(SubscriptionPassEnc); } set { SubscriptionPassEnc = Secret.Protect(value); } }

        // --- Models ---
        public string Model { get; set; }                 // OpenRouter id, e.g. "anthropic/claude-sonnet-5"
        public List<string> EnabledModels { get; set; }   // shown in the Model menu
        public string Effort { get; set; }

        // --- Interview setup ---
        public string PromptId { get; set; }              // "" = Default (Interview)
        public List<PromptDef> CustomPrompts { get; set; }
        public List<string> HiddenPrompts { get; set; }   // built-in prompts you've deleted
        public List<PromptDef> EditedPrompts { get; set; } // your versions of built-in prompts (same ids)
        public string ResumeFile { get; set; }            // the "Resume" on the setup screen
        public string Context { get; set; }
        public List<string> Files { get; set; }
        public bool AutoGenerate { get; set; }

        // --- Listening ---
        public string Transcription { get; set; }         // Automatic | LiveCaptions (Windows, on-device) | ElevenLabs | Grok
        public string AudioSource { get; set; }           // Both | System | Mic
        public string GrokModel { get; set; }
        public string KeyTerms { get; set; }
        public string SpeechLanguage { get; set; }
        public int SilenceMs { get; set; }
        public int AutoAnswerDelayMs { get; set; }

        // --- Appearance & behaviour ---
        public int OpacityPct { get; set; }
        public int BackgroundPct { get; set; }
        public int TextSizePct { get; set; }
        public string KeywordStyle { get; set; }          // see Ui.KeywordStyles
        public bool HideFromCapture { get; set; }
        public bool OfferOnCall { get; set; }
        public bool ShowTranscript { get; set; }
        public bool FocusMode { get; set; }
        public bool OnboardingDone { get; set; }

        // --- Memory: what earlier answers each request includes ---
        public bool ReplayTurns { get; set; }             // earlier answers from this session
        public bool ReplayAllTurns { get; set; }
        public int ReplayTurnCount { get; set; }
        public bool PullPastSessions { get; set; }        // one recent answer from each recent other session
        public double WinLeft { get; set; }
        public double WinTop { get; set; }
        public double WinWidth { get; set; }
        public double WinHeight { get; set; }

        public AppSettings()
        {
            SubscriptionModels = new List<string>();
            Model = ModelCatalog.DefaultModel;
            EnabledModels = new List<string>(ModelCatalog.DefaultEnabled);
            Effort = "low";
            PromptId = Prompts.DefaultId;
            CustomPrompts = new List<PromptDef>();
            HiddenPrompts = new List<string>();
            EditedPrompts = new List<PromptDef>();
            ResumeFile = "";
            Context = "";
            Files = new List<string>();
            AutoGenerate = true;
            Transcription = "Automatic";
            AudioSource = "Both";
            GrokModel = "grok-voice-transcribe-2.0";
            KeyTerms = "";
            SpeechLanguage = "en";
            SilenceMs = 700;
            AutoAnswerDelayMs = 900;
            OpacityPct = 100;
            BackgroundPct = 60;
            TextSizePct = 100;
            KeywordStyle = "lightBlue";
            HideFromCapture = true;
            OfferOnCall = true;
            ShowTranscript = false;
            FocusMode = false;
            OnboardingDone = false;
            ReplayTurns = true;
            ReplayAllTurns = false;
            ReplayTurnCount = 6;
            PullPastSessions = false;
            WinLeft = -1;
            WinTop = -1;
            WinWidth = 0;
            WinHeight = 0;
        }

        // --- Keys ------------------------------------------------------------------------------

        [ScriptIgnore] public string OpenRouterKey { get { return Secret.Unprotect(OpenRouterKeyEnc); } set { OpenRouterKeyEnc = Secret.Protect(value); } }
        [ScriptIgnore] public string ElevenLabsKey { get { return Secret.Unprotect(ElevenLabsKeyEnc); } set { ElevenLabsKeyEnc = Secret.Protect(value); } }
        [ScriptIgnore] public string XaiKey { get { return Secret.Unprotect(XaiKeyEnc); } set { XaiKeyEnc = Secret.Protect(value); } }

        private static string KeyOrEnv(string key, string env)
        {
            if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
            var v = Environment.GetEnvironmentVariable(env);
            return string.IsNullOrWhiteSpace(v) ? "" : v.Trim();
        }

        [ScriptIgnore] public string EffectiveOpenRouterKey { get { return KeyOrEnv(OpenRouterKey, "OPENROUTER_API_KEY"); } }
        [ScriptIgnore] public string EffectiveElevenLabsKey { get { return KeyOrEnv(ElevenLabsKey, "ELEVENLABS_API_KEY"); } }
        [ScriptIgnore] public string EffectiveXaiKey { get { return KeyOrEnv(XaiKey, "XAI_API_KEY"); } }

        // --- Derived ---------------------------------------------------------------------------

        [ScriptIgnore] public bool CaptureMic { get { return AudioSource != "System"; } }
        [ScriptIgnore] public bool CaptureSystem { get { return AudioSource != "Mic"; } }

        /// <summary>
        /// The transcription engine actually used. Pro transcribes the interviewer with Grok on TheCloser's xAI account
        /// ("ProGrok"); a cloud engine without its key falls back to Windows Live Captions; "Automatic" prefers
        /// ElevenLabs, then Grok. Same rules as the Mac app.
        /// </summary>
        [ScriptIgnore]
        public string EffectiveTranscription
        {
            get
            {
                if (UseSubscription && SubscriptionClient.HasActivePass(this)) return "ProGrok";
                switch (Transcription)
                {
                    case "LiveCaptions": return "LiveCaptions";
                    case "ElevenLabs": return EffectiveElevenLabsKey.Length > 0 ? "ElevenLabs" : "LiveCaptions";
                    case "Grok": return EffectiveXaiKey.Length > 0 ? "Grok" : "LiveCaptions";
                    default:
                        if (EffectiveElevenLabsKey.Length > 0) return "ElevenLabs";
                        return EffectiveXaiKey.Length > 0 ? "Grok" : "LiveCaptions";
                }
            }
        }

        /// <summary>Keys an interview needs before it can start: OpenRouter, plus ElevenLabs (or xAI when Grok is picked). None on Pro.</summary>
        [ScriptIgnore]
        public List<string> MissingKeys
        {
            get
            {
                var missing = new List<string>();
                if (UseSubscription && SubscriptionClient.HasActivePass(this)) return missing;
                if (EffectiveOpenRouterKey.Length == 0) missing.Add("OpenRouter");
                if (Transcription == "Grok") { if (EffectiveXaiKey.Length == 0) missing.Add("xAI"); }
                else if (EffectiveElevenLabsKey.Length == 0) missing.Add("ElevenLabs");
                return missing;
            }
        }

        /// <summary>"Add your OpenRouter and ElevenLabs keys to start. Click to open API keys." (null when nothing is missing)</summary>
        [ScriptIgnore]
        public string MissingKeysText
        {
            get
            {
                var m = MissingKeys;
                if (m.Count == 0) return null;
                return "Add your " + string.Join(" and ", m) + " key" + (m.Count > 1 ? "s" : "") + " to start. Click to open API keys.";
            }
        }

        // --- Persistence -----------------------------------------------------------------------

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TheCloser"); }
        }

        private static string FilePath
        {
            get { return Path.Combine(Folder, "settings.json"); }
        }

        /// <summary>The app used to be called CueCard: bring its settings and saved sessions over on first run.</summary>
        private static void MoveFromOldName()
        {
            try
            {
                var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CueCard");
                if (File.Exists(FilePath) || !Directory.Exists(old)) return;
                foreach (var src in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(Folder, src.Substring(old.Length).TrimStart('\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (!File.Exists(dest)) File.Copy(src, dest);
                }
            }
            catch { }
        }

        private void Normalize()
        {
            if (SubscriptionModels == null) SubscriptionModels = new List<string>();
            if (EnabledModels == null || EnabledModels.Count == 0) EnabledModels = new List<string>(ModelCatalog.DefaultEnabled);
            EnabledModels = EnabledModels.Select(ModelCatalog.NormalizeSlug).Where(m => m.Length > 0).Distinct().ToList();
            if (CustomPrompts == null) CustomPrompts = new List<PromptDef>();
            if (Files == null) Files = new List<string>();
            Model = ModelCatalog.NormalizeSlug(Model);
            if (string.IsNullOrEmpty(Model) || Model == "anthropic/claude-opus-5") Model = ModelCatalog.DefaultModel; // the old default
            EnabledModels.Remove("anthropic/claude-opus-5");
            if (!EnabledModels.Contains(Model)) EnabledModels.Insert(0, Model);
            if (HiddenPrompts == null) HiddenPrompts = new List<string>();
            if (EditedPrompts == null) EditedPrompts = new List<PromptDef>();
            if (PromptId == null || !Prompts.All(this).Any(p => p.Id == PromptId)) PromptId = Prompts.DefaultId; // deleted, or the old defaults
            if (Transcription != "LiveCaptions" && Transcription != "ElevenLabs" && Transcription != "Grok") Transcription = "Automatic";
            if (ResumeFile == null) ResumeFile = "";
            if (Context == null) Context = "";
            if (KeyTerms == null) KeyTerms = "";
            if (SpeechLanguage == null) SpeechLanguage = "en";
            if (!Ui.KeywordStyles.IsKnown(KeywordStyle)) KeywordStyle = Ui.KeywordStyles.Standard;
            OpacityPct = Math.Max(20, Math.Min(100, OpacityPct));
            BackgroundPct = Math.Max(0, Math.Min(100, BackgroundPct));
            TextSizePct = Math.Max(80, Math.Min(160, TextSizePct));
            ReplayTurnCount = Math.Max(1, Math.Min(50, ReplayTurnCount));
        }

        /// <summary>Default settings, never read from or written to disk (`--render`).</summary>
        internal static AppSettings Defaults()
        {
            var d = new AppSettings();
            d.Normalize();
            return d;
        }

        public static AppSettings Load()
        {
            MoveFromOldName();
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = Json.Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null)
                    {
                        s.Normalize();
                        return s;
                    }
                }
            }
            catch { }
            var d = new AppSettings();
            d.Normalize();
            return d;
        }

        private static readonly object SaveLock = new object();

        public void Save() { TrySave(); }

        internal bool TrySave()
        {
            lock (SaveLock)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, Json.Serialize(this), Encoding.UTF8);
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                    else File.Move(tmp, FilePath);
                    return true;
                }
                catch { return false; }
            }
        }

        public AppSettings Clone()
        {
            var c = Json.Deserialize<AppSettings>(Json.Serialize(this));
            c.Normalize();
            return c;
        }
    }

    /// <summary>Encrypts secrets for the current Windows user (DPAPI).</summary>
    internal static class Secret
    {
        // Keeps the app's old name: changing it would make every saved key undecryptable.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CueCard.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        public static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }
    }
}
