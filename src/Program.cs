using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;

// csc doesn't stamp a target framework by itself; without this the runtime applies legacy
// compatibility defaults (e.g. TLS 1.0 only for HTTPS and WebSockets).
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace TheCloser
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Native.AllowDarkApp();
            EnableModernTls();

            if (args.Length > 0 && args[0] == "--selftest")
                return SelfTest.Run(args);
            if (args.Length > 1 && args[0] == "--write-icon")
            {
                Brand.WriteIcon(args[1]);
                return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, "TheCloser.SingleInstance.v1", out created))
            {
                if (!created)
                {
                    MessageBox.Show("TheCloser is already running. Press Ctrl+Alt+Space to show it, or use the tray icon.",
                        "TheCloser", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }

                AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);

                var settings = AppSettings.Load();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { LogError(e.Exception); e.Handled = true; };
                Theme.Init(app);

                var window = new Ui.OverlayWindow(settings, args.Contains("--capturable"));
                window.Show();
                if (args.Contains("--demo")) window.LoadDemo();
                int pageArg = Array.IndexOf(args, "--open-settings");
                if (pageArg >= 0) window.OpenSettingsPage(pageArg + 1 < args.Length ? args[pageArg + 1] : null);

                app.Run();
            }
            return 0;
        }

        /// <summary>Makes sure HTTPS and WebSocket connections can use TLS 1.2 / 1.3 (api.anthropic.com, api.x.ai).</summary>
        private static void EnableModernTls()
        {
            if (ServicePointManager.SecurityProtocol == SecurityProtocolType.SystemDefault) return; // OS decides: already modern
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            }
            catch (NotSupportedException)
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
        }

        internal static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"),
                    DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
