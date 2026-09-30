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
        public string AnthropicKeyEnc { get; set; }
        public string OpenRouterKeyEnc { get; set; }
        public string XaiKeyEnc { get; set; }
        public string WhisperKeyEnc { get; set; }

        // --- Subscription (no Stripe or managed-provider secrets are stored here) ---
        public bool UseSubscription { get; set; }
        public string SubscriptionDeviceEnc { get; set; }
        public string SubscriptionPassEnc { get; set; }
        public long SubscriptionExpiresAt { get; set; }
        public string SubscriptionPlan { get; set; }
        public List<string> SubscriptionModels { get; set; }
        public double SubscriptionAllowanceUSD { get; set; }
        public string CheckoutRequestId { get; set; }
        public string CheckoutPlan { get; set; }
        public string CheckoutSessionId { get; set; }

        [ScriptIgnore] public string SubscriptionPass { get { return Secret.Unprotect(SubscriptionPassEnc); } set { SubscriptionPassEnc = Secret.Protect(value); } }

        // --- Models ---
        public string Model { get; set; }                 // OpenRouter-style slug, e.g. "anthropic/claude-opus-5"
        public List<string> EnabledModels { get; set; }   // shown in the Model menu
        public string Effort { get; set; }
        public string Length { get; set; }

        // --- Call setup ---
        public string PromptId { get; set; }
        public List<PromptDef> CustomPrompts { get; set; }
        public List<string> HiddenPrompts { get; set; }   // built-in prompts you've deleted
        public List<PromptDef> EditedPrompts { get; set; } // your versions of built-in prompts (same ids)
        public string ResumeFile { get; set; }            // the "Reference file" on the setup screen (name kept for saved settings)
        public string Context { get; set; }
        public List<string> Files { get; set; }
        public bool AutoGenerate { get; set; }

        // --- Listening ---
        public string Transcription { get; set; }         // Automatic | LiveCaptions | Grok | Whisper
        public string AudioSource { get; set; }           // Both | System | Mic
        public string GrokModel { get; set; }
        public string WhisperPreset { get; set; }         // OpenAI | Groq | Custom
        public string WhisperBaseUrl { get; set; }
        public string WhisperModel { get; set; }
        public string KeyTerms { get; set; }
        public string SpeechLanguage { get; set; }
        public int SilenceMs { get; set; }
        public int AutoAnswerDelayMs { get; set; }

        // --- Appearance & behaviour ---
        public int OpacityPct { get; set; }
        public int BackgroundPct { get; set; }
        public int TextSizePct { get; set; }
        public bool HideFromCapture { get; set; }
        public bool OfferOnCall { get; set; }
        public bool ShowTranscript { get; set; }
        public bool FocusMode { get; set; }
        public bool OnboardingDone { get; set; }
        public double WinLeft { get; set; }
        public double WinTop { get; set; }
        public double WinWidth { get; set; }
        public double WinHeight { get; set; }

        public AppSettings()
        {
            SubscriptionModels = new List<string>();
            Model = "anthropic/claude-opus-5";
            EnabledModels = new List<string>(ModelCatalog.DefaultEnabled);
            Effort = "low";
            Length = "Short";
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
            WhisperPreset = "OpenAI";
            WhisperBaseUrl = "https://api.openai.com/v1";
            WhisperModel = "gpt-4o-mini-transcribe";
            KeyTerms = "";
            SpeechLanguage = "";
            SilenceMs = 700;
            AutoAnswerDelayMs = 900;
            OpacityPct = 100;
            BackgroundPct = 100;
            TextSizePct = 125;
            HideFromCapture = true;
            OfferOnCall = true;
            ShowTranscript = false;
            FocusMode = false;
            OnboardingDone = false;
            WinLeft = -1;
            WinTop = -1;
            WinWidth = 0;
            WinHeight = 0;
        }

        // --- Keys ------------------------------------------------------------------------------

        [ScriptIgnore] public string AnthropicKey { get { return Secret.Unprotect(AnthropicKeyEnc); } set { AnthropicKeyEnc = Secret.Protect(value); } }
        [ScriptIgnore] public string OpenRouterKey { get { return Secret.Unprotect(OpenRouterKeyEnc); } set { OpenRouterKeyEnc = Secret.Protect(value); } }
        [ScriptIgnore] public string XaiKey { get { return Secret.Unprotect(XaiKeyEnc); } set { XaiKeyEnc = Secret.Protect(value); } }
        [ScriptIgnore] public string WhisperKey { get { return Secret.Unprotect(WhisperKeyEnc); } set { WhisperKeyEnc = Secret.Protect(value); } }

        private static string KeyOrEnv(string key, string env)
        {
            if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
            var v = Environment.GetEnvironmentVariable(env);
            return string.IsNullOrWhiteSpace(v) ? "" : v.Trim();
        }

        [ScriptIgnore] public string EffectiveAnthropicKey { get { return KeyOrEnv(AnthropicKey, "ANTHROPIC_API_KEY"); } }
        [ScriptIgnore] public string EffectiveOpenRouterKey { get { return KeyOrEnv(OpenRouterKey, "OPENROUTER_API_KEY"); } }
        [ScriptIgnore] public string EffectiveXaiKey { get { return KeyOrEnv(XaiKey, "XAI_API_KEY"); } }

        [ScriptIgnore]
        public string EffectiveWhisperKey
        {
            get { return KeyOrEnv(WhisperKey, WhisperPreset == "Groq" ? "GROQ_API_KEY" : "OPENAI_API_KEY"); }
        }

        // --- Derived ---------------------------------------------------------------------------

        [ScriptIgnore] public bool CaptureMic { get { return AudioSource != "System"; } }
        [ScriptIgnore] public bool CaptureSystem { get { return AudioSource != "Mic"; } }

        /// <summary>The transcription engine actually used ("Automatic" picks the best one you have a key for).</summary>
        [ScriptIgnore]
        public string EffectiveTranscription
        {
            get
            {
                if (Transcription != "Automatic") return Transcription;
                if (EffectiveXaiKey.Length > 0) return "Grok";
                if (EffectiveWhisperKey.Length > 0) return "Whisper";
                return "LiveCaptions";
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
            if (string.IsNullOrEmpty(Model)) Model = "anthropic/claude-opus-5";
            if (!EnabledModels.Contains(Model)) EnabledModels.Insert(0, Model);
            if (string.IsNullOrEmpty(PromptId) || PromptId == "interview") PromptId = Prompts.DefaultId; // "interview" was replaced by "call"
            if (HiddenPrompts == null) HiddenPrompts = new List<string>();
            if (EditedPrompts == null) EditedPrompts = new List<PromptDef>();
            if (Prompts.All(this).Count == 0) HiddenPrompts.Clear();
            if (!Prompts.All(this).Any(p => p.Id == PromptId)) PromptId = Prompts.All(this)[0].Id; // its prompt was deleted
            if (ResumeFile == null) ResumeFile = "";
            if (Context == null) Context = "";
            if (KeyTerms == null) KeyTerms = "";
            if (SpeechLanguage == null) SpeechLanguage = "";
            OpacityPct = Math.Max(30, Math.Min(100, OpacityPct));
            BackgroundPct = Math.Max(20, Math.Min(100, BackgroundPct));
            TextSizePct = Math.Max(75, Math.Min(200, TextSizePct));
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
