using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace TheCloser
{
    /// <summary>
    /// Notices when a call app (Zoom, Teams, Meet in a browser, ...) starts using the microphone, using the per-app
    /// "last used" timestamps Windows keeps for the microphone privacy indicator. Nothing is recorded.
    /// </summary>
    internal sealed class CallDetector : IDisposable
    {
        private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        public event Action<string> CallStarted;

        private readonly System.Threading.Timer _timer;
        private HashSet<string> _active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _first = true;

        public CallDetector()
        {
            _timer = new System.Threading.Timer(delegate { Poll(); }, null, 1500, 3000);
        }

        private void Poll()
        {
            try
            {
                var now = ActiveApps();
                if (!_first)
                {
                    foreach (var key in now)
                    {
                        if (_active.Contains(key)) continue;
                        var name = CallAppName(key);
                        var h = CallStarted;
                        if (name != null && h != null) h(name);
                    }
                }
                _first = false;
                _active = now;
            }
            catch { }
        }

        /// <summary>Consent-store entries whose microphone use has started but not stopped.</summary>
        internal static HashSet<string> ActiveApps()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var root = Registry.CurrentUser.OpenSubKey(Root))
            {
                if (root == null) return set;
                Collect(root, set);
                using (var np = root.OpenSubKey("NonPackaged")) if (np != null) Collect(np, set);
            }
            return set;
        }

        private static void Collect(RegistryKey parent, HashSet<string> set)
        {
            foreach (var name in parent.GetSubKeyNames())
            {
                if (name == "NonPackaged") continue;
                using (var k = parent.OpenSubKey(name))
                {
                    if (k == null) continue;
                    var start = k.GetValue("LastUsedTimeStart");
                    var stop = k.GetValue("LastUsedTimeStop");
                    if (start is long && (long)start != 0 && stop is long && (long)stop == 0) set.Add(name);
                }
            }
        }

        internal static string CallAppName(string key)
        {
            var k = key.ToLowerInvariant();
            if (k.Contains("thecloser")) return null;
            if (k.Contains("zoom")) return "Zoom";
            if (k.Contains("teams")) return "Microsoft Teams";
            if (k.Contains("webex") || k.Contains("ciscocollab")) return "Webex";
            if (k.Contains("slack")) return "Slack";
            if (k.Contains("discord")) return "Discord";
            if (k.Contains("skype")) return "Skype";
            if (k.EndsWith("chrome.exe")) return "Chrome";
            if (k.EndsWith("msedge.exe")) return "Edge";
            if (k.EndsWith("firefox.exe")) return "Firefox";
            if (k.EndsWith("brave.exe")) return "Brave";
            return null;
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
