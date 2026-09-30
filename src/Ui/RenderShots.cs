using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TheCloser.Ui
{
    /// <summary>
    /// TheCloser.exe --render &lt;folder&gt;: draws each screen to a PNG, so the UI can be reviewed without a Windows PC
    /// (CI uploads them). Every scene starts from default settings; nothing is read from or saved to this PC's settings.
    /// </summary>
    internal static class RenderShots
    {
        private const double Scale = 2;
        private static readonly Color Backdrop = Color.FromRgb(0x5A, 0x5A, 0x5A);
        private static string _dir;
        private static StringBuilder _log;
        private static int _failures;

        public static int Run(string dir)
        {
            _dir = dir;
            _log = new StringBuilder();
            _failures = 0;
            Directory.CreateDirectory(dir);
            OverlayWindow.Offscreen = true;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Init(app);

            // A crash in one screen is logged, not fatal, so the others still render.
            app.DispatcherUnhandledException += delegate(object o, DispatcherUnhandledExceptionEventArgs e)
            {
                _failures++;
                _log.AppendLine("ERROR " + e.Exception);
                e.Handled = true;
            };

            foreach (var step in new[] { OnboardingView.Step.Welcome, OnboardingView.Step.Choose, OnboardingView.Step.Keys, OnboardingView.Step.Plans })
            {
                var st = step;
                Scene("tour-" + st.ToString().ToLowerInvariant(), 520, 820, delegate(OverlayWindow w) { w.ShowTourStep(st); });
            }
            Scene("pill", 470, 360, delegate(OverlayWindow w) { w.SetPanel(false); });
            Scene("bar", 470, 360, delegate(OverlayWindow w) { w.SetPanel(false); w.PreviewDockHover(); });
            Scene("setup", 520, 1000, delegate(OverlayWindow w) { w.ShowView("setup"); });
            Scene("live", 520, 760, delegate(OverlayWindow w) { w.LoadDemo(); });
            Scene("history", 520, 760, delegate(OverlayWindow w) { w.ShowView("history"); });
            foreach (var page in new[] { "general", "ai", "prompts", "memory", "shortcuts" })
            {
                var p = page;
                Scene("settings-" + p, 560, 1200, delegate(OverlayWindow w) { w.ShowSettings(p); });
            }
            // Full length, below the fold too (a window can't be taller than the screen).
            PageScene("setup-full", 560, delegate(OverlayWindow w) { w.ShowView("setup"); });
            PageScene("settings-general-full", 600, delegate(OverlayWindow w) { w.ShowSettings("general"); });
            PageScene("settings-ai-full", 600, delegate(OverlayWindow w) { w.ShowSettings("ai"); });
            MenuScene("menu", delegate(OverlayWindow w) { w.LoadDemo(); });
            CallPromptScene();

            _log.AppendLine(_failures == 0 ? "ALL SCREENS RENDERED" : _failures + " SCREEN(S) FAILED");
            WriteLog();
            return _failures;
        }

        private static OverlayWindow Open(double width, double height)
        {
            var w = new OverlayWindow(AppSettings.Defaults(), true);
            w.Left = -30000;
            w.Top = 0;
            w.Width = width;
            w.Height = height;
            w.Show();
            return w;
        }

        private static void Scene(string name, double width, double height, Action<OverlayWindow> setup)
        {
            OverlayWindow w = null;
            try
            {
                w = Open(width, height);
                setup(w);
                Pump(900); // entrance animations run ~220 ms
                Save(w, width, height, name);
                _log.AppendLine("OK    " + name);
                WriteLog();
            }
            catch (Exception ex)
            {
                _failures++;
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
            finally
            {
                if (w != null) try { w.Close(); } catch { }
            }
        }

        /// <summary>A screen at its full length, on the card's background, at the panel's scale.</summary>
        private static void PageScene(string name, double width, Action<OverlayWindow> setup)
        {
            OverlayWindow w = null;
            try
            {
                w = Open(520, 700);
                setup(w);
                Pump(300);
                var view = w.DetachView();
                var host = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
                    Width = width,
                    Child = view,
                    LayoutTransform = new ScaleTransform(0.86, 0.86)
                };
                host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();
                Pump(600);
                host.UpdateLayout();
                Save(host, host.DesiredSize.Width, host.DesiredSize.Height, name);
                _log.AppendLine("OK    " + name);
                WriteLog();
            }
            catch (Exception ex)
            {
                _failures++;
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
            finally
            {
                if (w != null) try { w.Close(); } catch { }
            }
        }

        /// <summary>The ... menu, opened from its button and drawn on its own.</summary>
        private static void MenuScene(string name, Action<OverlayWindow> setup)
        {
            OverlayWindow w = null;
            ContextMenu menu = null;
            try
            {
                w = Open(520, 760);
                setup(w);
                Pump(300);
                menu = w.BuildMoreMenu();
                menu.PlacementTarget = w.MoreButton;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
                Pump(600);
                Save(menu, menu.ActualWidth, menu.ActualHeight, name);
                _log.AppendLine("OK    " + name);
            }
            catch (Exception ex)
            {
                _failures++;
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
            finally
            {
                if (menu != null) menu.IsOpen = false;
                if (w != null) try { w.Close(); } catch { }
            }
        }

        /// <summary>"On a call in Zoom?", drawn on its own.</summary>
        private static void CallPromptScene()
        {
            CallPromptWindow prompt = null;
            try
            {
                prompt = new CallPromptWindow();
                prompt.ShowFor("Zoom", null);
                prompt.Left = -30000;
                Pump(500);
                Save(prompt, prompt.ActualWidth, prompt.ActualHeight, "call-prompt");
                _log.AppendLine("OK    call-prompt");
            }
            catch (Exception ex)
            {
                _failures++;
                _log.AppendLine("FAIL  call-prompt: " + ex);
            }
            finally
            {
                if (prompt != null) try { prompt.Close(); } catch { }
            }
        }

        /// <summary>Written after every screen, so a crash still leaves a log.</summary>
        private static void WriteLog()
        {
            File.WriteAllText(Path.Combine(_dir, "render-log.txt"), _log.ToString(), Encoding.UTF8);
        }

        /// <summary>Runs the dispatcher (layout, bindings, animations) for a while.</summary>
        private static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        /// <summary>Draws the visual at 2x over a grey backdrop (the window itself is transparent) and saves a PNG.</summary>
        private static void Save(Visual visual, double width, double height, string name)
        {
            int pw = (int)Math.Ceiling(width * Scale), ph = (int)Math.Ceiling(height * Scale);
            var content = new RenderTargetBitmap(pw, ph, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            content.Render(visual);
            var composed = new DrawingVisual();
            using (var dc = composed.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Backdrop), null, new Rect(0, 0, width, height));
                dc.DrawImage(content, new Rect(0, 0, width, height));
            }
            var output = new RenderTargetBitmap(pw, ph, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            output.Render(composed);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(output));
            using (var f = File.Create(Path.Combine(_dir, name + ".png"))) png.Save(f);
        }
    }
}
