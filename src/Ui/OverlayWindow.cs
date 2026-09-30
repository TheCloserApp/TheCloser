using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace TheCloser.Ui
{
    /// <summary>
    /// The overlay: three floating panels (title bar, content card, dock) on a transparent, always-on-top window
    /// that is excluded from screen capture. Hosts the welcome tour and the interview setup, live interview,
    /// settings, browser and history views - the same screens as the Mac app.
    /// </summary>
    internal sealed class OverlayWindow : Window
    {
        private const int HkToggle = 1, HkAnswer = 2, HkShot = 3, HkMove = 10, HkSize = 20;

        public readonly AppSettings S;
        public readonly SessionController Ctl;
        public readonly SubscriptionClient Billing;
        private readonly bool _forceCapturable;
        internal static bool StealthOn;
        /// <summary>Drawn to images by `--render`: no tray icon, hotkeys, call detector or billing checks, and closing doesn't quit.</summary>
        internal static bool Offscreen;

        private readonly Grid _root = new Grid();
        private Border _top, _card, _dock;
        private SolidColorBrush _topBrush, _cardBrush, _dockBrush;
        private TextBlock _title;
        private Button _xBtn, _composeBtn, _moreBtn;
        private readonly Grid _cardGrid = new Grid();
        private readonly ContentControl _viewHost = new ContentControl();
        private readonly Grid _modalLayer = new Grid { Visibility = Visibility.Collapsed };
        private readonly StackPanel _toasts = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(18, 0, 18, 16) };

        private SetupView _setup;
        private SessionView _session;
        private SettingsView _settings;
        private BrowserView _browser;
        private HistoryView _history;
        private OnboardingView _tour;
        private CallPromptWindow _callPrompt;
        private string _view = "setup";

        /// <summary>Everything is drawn at this scale, so text and controls are a notch smaller than their nominal sizes.</summary>
        private const double UiScale = 0.86;

        private Grid _dockAsk;
        private StackPanel _dockNav;
        private Grid _dockMore;              // separator + nav buttons (or the Ask box during a call)
        private Border _dockMoreHost;        // clips _dockMore while it slides open
        private ColumnDefinition _dockMoreCol;
        private TextBox _ask;
        private Button _send;
        private RadioButton _navHome, _navWeb, _navSettings;
        private FrameworkElement _grip;

        // The panel (title bar + card) opens from the dock and closes with the X, leaving just the capsule.
        private bool _panelOpen;
        private bool _dockHover, _dockExpanded, _dockPinned, _moreMenuOpen;
        private DateTime _dockHoverUntil;
        private bool _topShown = true, _dockShown = true, _gripShown = true;

        private WinForms.NotifyIcon _tray;
        private CallDetector _calls;
        private readonly Dictionary<string, DateTime> _callOffered = new Dictionary<string, DateTime>();
        private IntPtr _hwnd;
        private readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly DispatcherTimer _billingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private DateTime _billingChecked;
        private DateTime _checkoutWatchUntil = DateTime.UtcNow.AddMinutes(10);
        /// <summary>Waiting for "Upgrade to Pro Max" to be confirmed in the browser.</summary>
        internal bool WaitingForUpgrade { get; private set; }
        private bool _billingRefreshing, _closed;
        private bool _modalOpen;
        private readonly HashSet<int> _registered = new HashSet<int>();

        public OverlayWindow(AppSettings settings, bool forceCapturable)
        {
            S = settings;
            _forceCapturable = forceCapturable;
            StealthOn = S.HideFromCapture && !forceCapturable;
            Billing = Offscreen ? new SubscriptionClient(S, new System.Net.Http.HttpClient(), false) : new SubscriptionClient(S);
            Ctl = new SessionController(S, Dispatcher, Billing);

            Title = "TheCloser";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            FontFamily = U.Font;
            Foreground = U.Text;
            UseLayoutRounding = true;
            MinWidth = 440;
            MinHeight = 330;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            BuildChrome();
            _setup = new SetupView(this);
            _session = new SessionView(this);
            _settings = new SettingsView(this);
            _browser = new BrowserView(this);
            _history = new HistoryView(this);
            _tour = new OnboardingView(this);
            Content = _root;
            ApplyBounds();
            ApplyAppearance();

            Ctl.StateChanged += OnSessionState;
            Ctl.QasChanged += OnQasChanged;
            Ctl.TranscriptChanged += () => _session.RefreshTranscript();
            Ctl.Status += OnSessionStatus;
            Ctl.NeedsKey += () => ShowSettings("ai");
            Billing.StateChanged += delegate
            {
                if (_closed || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (_closed) return;
                    if (S.UseSubscription && Billing.IsActive && !Billing.Models.Contains(S.Model))
                    {
                        S.Model = Billing.Models[0];
                        SaveSettingsSoon();
                    }
                    if (WaitingForUpgrade && Billing.Plan == "pro_max") WaitingForUpgrade = false;
                    if (_view == "settings") _settings.Refresh();
                    if (_view == "tour") _tour.Refresh();
                    OnKeysChanged();
                });
            };
            _billingTimer.Tick += async delegate
            {
                if (!S.UseSubscription && !Billing.PendingCheckout) return;
                bool waiting = (Billing.PendingCheckout || WaitingForUpgrade) && DateTime.UtcNow < _checkoutWatchUntil;
                var interval = waiting ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(45);
                if (DateTime.UtcNow - _billingChecked >= interval) await RefreshBillingAsync();
            };
            if (!Offscreen)
            {
                _billingTimer.Start();
                Loaded += async delegate { if (S.UseSubscription || Billing.PendingCheckout) await RefreshBillingAsync(); };
            }

            _tick.Tick += delegate { if (_view == "session") _session.Tick(); if (!Offscreen) UpdateHover(); };
            _tick.Start();
            _saveTimer.Tick += delegate { _saveTimer.Stop(); S.Save(); };

            PreviewKeyDown += OnPreviewKey;
            IsVisibleChanged += delegate { UpdateHotkeys(); };
            Closing += delegate { OnClosing(); };

            if (!Offscreen)
            {
                BuildTray();
                if (S.OfferOnCall) StartCallDetector();
            }
            ShowView("setup");
            SetPanel(false); // start as just the capsule; the dock opens the panel
            if (!S.OnboardingDone && !Offscreen) ShowWelcomeTour();
        }

        // =========================================================================================
        // Chrome
        // =========================================================================================

        private static DropShadowEffect Shadow()
        {
            return new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.5, Color = Colors.Black, RenderingBias = RenderingBias.Performance };
        }

        private void BuildChrome()
        {
            _root.Margin = new Thickness(18, 14, 18, 18);
            _root.LayoutTransform = new ScaleTransform(UiScale, UiScale);
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title bar -------------------------------------------------------------------------
            _topBrush = new SolidColorBrush(U.C(0xFF111111));
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition());
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _xBtn = U.Circle(U.GClose, () => SetPanel(false), "Close panel", 12);
            topGrid.Children.Add(_xBtn);
            _title = new TextBlock { FontSize = 18.5, FontWeight = FontWeights.SemiBold, Foreground = U.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 12, 1), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(_title, 1);
            topGrid.Children.Add(_title);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            _composeBtn = U.Circle(U.GCompose, NewInterviewSetup, "New interview setup  (Ctrl+N)", 14);
            _moreBtn = U.Circle(U.GMore, ShowMoreMenu, "More", 15, U.Text2);
            _moreBtn.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(_composeBtn);
            right.Children.Add(_moreBtn);
            Grid.SetColumn(right, 2);
            topGrid.Children.Add(right);
            _top = new Border
            {
                Background = _topBrush,
                BorderBrush = U.B(0xFF262626),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(24),
                Padding = new Thickness(9, 8, 9, 8),
                Child = topGrid,
                Effect = Shadow()
            };
            _top.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
            Grid.SetRow(_top, 0);
            _root.Children.Add(_top);

            // Content card ------------------------------------------------------------------------
            _cardBrush = new SolidColorBrush(U.C(0xFF111111));
            _cardGrid.Children.Add(_viewHost);
            _cardGrid.Children.Add(_toasts);
            _cardGrid.Children.Add(_modalLayer);
            _cardGrid.SizeChanged += delegate { _cardGrid.Clip = new RectangleGeometry(new Rect(0, 0, _cardGrid.ActualWidth, _cardGrid.ActualHeight), 20, 20); };
            _card = new Border
            {
                Background = _cardBrush,
                BorderBrush = U.B(0xFF262626),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(21),
                Child = _cardGrid,
                Effect = Shadow()
            };
            Grid.SetRow(_card, 2);
            _root.Children.Add(_card);

            // Dock ------------------------------------------------------------------------------
            // A round capsule with the waveform mark. Hovering slides it open to the nav buttons (or the Ask box
            // during a call); the monitor button opens the panel above it.
            _dockBrush = new SolidColorBrush(U.C(0xFF111111));
            var logo = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "TheCloser", Child = U.WaveLogo(22, U.Text) };
            logo.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e)
            {
                // Drag the capsule to move the window; a click without moving opens or closes the panel.
                e.Handled = true;
                double left = Left, top = Top;
                try { DragMove(); } catch { }
                if (Math.Abs(Left - left) < 2 && Math.Abs(Top - top) < 2) OnLogoClick();
                else SaveBounds();
            };
            var dockRow = new Grid();
            dockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _dockMoreCol = new ColumnDefinition { Width = GridLength.Auto };
            dockRow.ColumnDefinitions.Add(_dockMoreCol);
            dockRow.Children.Add(logo);

            _dockMore = new Grid { VerticalAlignment = VerticalAlignment.Center };
            _dockMore.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _dockMore.ColumnDefinitions.Add(new ColumnDefinition());
            var sep = new Border { Width = 1, Height = 28, Background = U.B(0xFF2A2A2A), Margin = new Thickness(8, 0, 12, 0) };
            _dockMore.Children.Add(sep);

            _dockAsk = new Grid();
            _dockAsk.ColumnDefinitions.Add(new ColumnDefinition());
            _dockAsk.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _ask = new TextBox { Style = U.Style("Text.Input"), Tag = "Ask anything", FontSize = 15, Padding = new Thickness(14, 9, 14, 9), Background = U.B(0xFF0A0A0A), BorderBrush = U.B(0xFF292929), VerticalAlignment = VerticalAlignment.Center };
            _ask.KeyDown += delegate(object o, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; SendAsk(); } };
            _ask.TextChanged += delegate { _send.Tag = _ask.Text.Trim().Length > 0 ? "ready" : null; };
            _dockAsk.Children.Add(_ask);
            _send = U.Btn("Btn.Send", U.Icon(U.GSend, 15, null), SendAsk, "Send  (Enter)");
            _send.Margin = new Thickness(10, 0, 4, 0);
            Grid.SetColumn(_send, 1);
            _dockAsk.Children.Add(_send);
            Grid.SetColumn(_dockAsk, 1);
            _dockMore.Children.Add(_dockAsk);

            _dockNav = new StackPanel { Orientation = Orientation.Horizontal };
            _navHome = NavButton(U.GMonitor, "Interview", "Radio.Dock", () => NavTo("main"));
            _navWeb = NavButton(U.GGlobe, "Browser", "Radio.Dock", () => NavTo("browser"));
            _navSettings = NavButton(U.GPerson, "Profile", "Radio.Dock", () => NavTo("settings"));
            _navSettings.Margin = new Thickness(0, 0, 2, 0);
            _dockNav.Children.Add(_navHome);
            _dockNav.Children.Add(_navWeb);
            _dockNav.Children.Add(_navSettings);
            Grid.SetColumn(_dockNav, 1);
            _dockMore.Children.Add(_dockNav);

            _dockMoreHost = new Border { ClipToBounds = true, Width = 0, Child = _dockMore, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_dockMoreHost, 1);
            dockRow.Children.Add(_dockMoreHost);

            _dock = new Border
            {
                Background = _dockBrush,
                BorderBrush = U.B(0xFF292929),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(30),
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = dockRow,
                Effect = Shadow()
            };
            _dock.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
            Grid.SetRow(_dock, 4);
            _root.Children.Add(_dock);

            _grip = BuildGrip();
            Grid.SetRow(_grip, 4);
            _root.Children.Add(_grip);
        }

        private RadioButton NavButton(string glyph, string tip, string style, Action click)
        {
            var rb = new RadioButton { Style = U.Style(style), GroupName = "dock", Content = U.Icon(glyph, 17, null), ToolTip = tip, Margin = new Thickness(0, 0, 8, 0) };
            rb.Click += delegate { click(); };
            return rb;
        }

        /// <summary>Bottom-right resize handle.</summary>
        private FrameworkElement BuildGrip()
        {
            var arrows = new Path
            {
                Data = Geometry.Parse("M1,5 V1 H5 M1,1 L6,6 M13,9 V13 H9 M13,13 L8,8"),
                Stroke = U.Text2,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 14,
                Height = 14,
                IsHitTestVisible = false
            };
            var face = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(10),
                Background = U.B(0xFF1A1A1A),
                BorderBrush = U.B(0xFF292929),
                BorderThickness = new Thickness(1),
                Child = arrows,
                Cursor = Cursors.SizeNWSE,
                ToolTip = "Drag to resize",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -2, -4),
                Effect = Shadow()
            };
            Point? start = null;
            Size startSize = Size.Empty;
            face.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e)
            {
                start = PointToScreen(e.GetPosition(this));
                startSize = new Size(Width, Height);
                face.CaptureMouse();
                e.Handled = true;
            };
            face.MouseMove += delegate(object o, MouseEventArgs e)
            {
                if (start == null) return;
                var now = PointToScreen(e.GetPosition(this));
                var m = PresentationSource.FromVisual(this).CompositionTarget.TransformFromDevice;
                var d = m.Transform(new Point(now.X - start.Value.X, now.Y - start.Value.Y));
                Width = Math.Max(MinWidth, startSize.Width + d.X);
                Height = Math.Max(MinHeight, startSize.Height + d.Y);
            };
            face.MouseLeftButtonUp += delegate
            {
                start = null;
                face.ReleaseMouseCapture();
                SaveBounds();
            };
            return face;
        }

        // =========================================================================================
        // Views
        // =========================================================================================

        private string MainView()
        {
            return Ctl.Current != null && (Ctl.Active || _reviewing) ? "session" : "setup";
        }

        private bool _reviewing;

        public void ShowView(string view)
        {
            if (view == "session" && Ctl.Current == null) view = "setup";
            _view = view;
            FrameworkElement el;
            switch (view)
            {
                case "session": el = _session; break;
                case "settings": el = _settings; break;
                case "browser": el = _browser; break;
                case "history": _history.Refresh(); el = _history; break;
                case "tour": el = _tour; break;
                default: _setup.Refresh(); el = _setup; break;
            }
            if (_viewHost.Content != el)
            {
                _viewHost.Content = el;
                U.Enter(el, 10, 220);
            }
            if (view == "session") { _session.RefreshQas(); _session.RefreshTranscript(); }
            RefreshTopBar();
            if (!_panelOpen) SetPanel(true);
            else RefreshDock();
            ApplyAppearance();
        }

        /// <summary>Opens or closes the panel (title bar + card). Closed, only the dock capsule is left on screen.</summary>
        public void SetPanel(bool open)
        {
            _panelOpen = open;
            if (!open) _dockPinned = false;
            var vis = open ? Visibility.Visible : Visibility.Collapsed;
            _top.Visibility = vis;
            _card.Visibility = vis;
            _grip.Visibility = vis;
            if (open) U.Enter(_card, 12, 200);
            if (!open && _modalOpen) CloseModal();
            RefreshDock();
        }

        /// <summary>Dock buttons: open that view, or close the panel if it's already showing.</summary>
        private void NavTo(string target)
        {
            bool showing = _panelOpen && (target == "main" ? _view == "setup" || _view == "session" || _view == "history" || _view == "tour" : _view == target);
            if (showing) SetPanel(false);
            else if (target == "settings") ShowSettings(null);
            else ShowView(target == "main" ? MainView() : target);
        }

        public void ShowSettings(string page)
        {
            ShowView("settings");
            if (page != null) _settings.Show(page);
        }

        public void RefreshSetup()
        {
            _setup.Refresh();
        }

        public async Task RefreshBillingAsync()
        {
            if (_billingRefreshing || _closed) return;
            _billingRefreshing = true;
            try
            {
                await Billing.RefreshAsync();
                if (Billing.IsActive) await Billing.RefreshUsageAsync();
            }
            catch (Exception ex) { if (!_closed) Toast("Couldn't refresh your subscription: " + ex.Message, true); }
            finally { _billingChecked = DateTime.UtcNow; _billingRefreshing = false; }
        }

        public void WatchCheckout()
        {
            _checkoutWatchUntil = DateTime.UtcNow.AddMinutes(10);
            _billingChecked = DateTime.MinValue;
        }

        /// <summary>A plan's Subscribe button: Stripe Checkout opens in the browser, and the app switches over once it's paid.</summary>
        public async void Subscribe(string plan)
        {
            S.UseSubscription = true;
            SaveSettingsSoon();
            try
            {
                OpenUrl(await Billing.CheckoutAsync(plan));
                WatchCheckout();
            }
            catch (Exception ex) { Toast(ex.Message, true); }
            RefreshBillingViews();
        }

        /// <summary>"Already subscribed on this PC? Restore": looks the subscription up again.</summary>
        public async void RestoreSubscription()
        {
            S.UseSubscription = true;
            SaveSettingsSoon();
            await RefreshBillingAsync();
            if (!Billing.IsActive) Toast("No subscription was found for this PC.", true);
            RefreshBillingViews();
        }

        public void StopWaitingForCheckout()
        {
            Billing.StopWaitingForCheckout();
            WaitingForUpgrade = false;
            RefreshBillingViews();
        }

        /// <summary>"Upgrade to Pro Max": Stripe shows the prorated charge; the app switches once it's confirmed.</summary>
        public async void UpgradeToProMax()
        {
            try
            {
                OpenUrl(await Billing.UpgradeAsync());
                WaitingForUpgrade = true;
                WatchCheckout();
            }
            catch (Exception ex) { Toast(ex.Message, true); }
            RefreshBillingViews();
        }

        /// <summary>"Manage subscription": Stripe's billing page (plan, card, invoices, cancel).</summary>
        public async void ManageSubscription()
        {
            try { OpenUrl(await Billing.PortalAsync()); }
            catch (Exception ex) { Toast(ex.Message, true); }
        }

        private void RefreshBillingViews()
        {
            if (_view == "settings") _settings.Refresh();
            if (_view == "tour") _tour.Refresh();
        }

        private void RefreshTopBar()
        {
            switch (_view)
            {
                case "session": _title.Text = Ctl.Current != null ? Ctl.Current.Title : "Session"; break;
                case "settings": _title.Text = "Settings"; break;
                case "browser": _title.Text = "Browser"; break;
                case "history": _title.Text = "History"; break;
                case "tour": _title.Text = "Welcome"; break;
                default: _title.Text = "Interview"; break;
            }
            // The compose ("new interview") button only makes sense on the interview screens.
            _composeBtn.Visibility = _view == "settings" || _view == "browser" || _view == "tour" ? Visibility.Collapsed : Visibility.Visible;
        }

        private bool AskMode { get { return _panelOpen && _view == "session"; } }

        /// <summary>The dock shows the Ask box while a session is open and the nav buttons everywhere else.</summary>
        private void RefreshDock()
        {
            bool ask = AskMode;
            _dockAsk.Visibility = ask ? Visibility.Visible : Visibility.Collapsed;
            _dockNav.Visibility = ask ? Visibility.Collapsed : Visibility.Visible;
            _navHome.IsChecked = _panelOpen && (_view == "setup" || _view == "session" || _view == "history");
            _navWeb.IsChecked = _panelOpen && _view == "browser";
            _navSettings.IsChecked = _panelOpen && _view == "settings";
            // Profile turns amber while keys are missing, like the Mac.
            _navSettings.Style = U.Style(S.MissingKeys.Count > 0 ? "Radio.DockAccent" : "Radio.Dock");
            _dock.HorizontalAlignment = ask ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            _dockMoreCol.Width = ask ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            UpdateDockExpansion(true);
        }

        /// <summary>The capsule is just the logo until you hover or click it (or the panel is open); then the rest slides out.</summary>
        private void UpdateDockExpansion(bool force)
        {
            bool expand = _panelOpen || _dockHover || _dockPinned;
            if (!force && expand == _dockExpanded) return;
            _dockExpanded = expand;
            if (AskMode)
            {
                _dockMoreHost.BeginAnimation(WidthProperty, null);
                _dockMoreHost.Width = double.NaN;
                return;
            }
            _dockMore.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double to = expand ? _dockMore.DesiredSize.Width : 0;
            double from = double.IsNaN(_dockMoreHost.Width) ? _dockMoreHost.ActualWidth : _dockMoreHost.Width;
            _dockMoreHost.BeginAnimation(WidthProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(expand ? 200 : 170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        /// <summary>
        /// Runs with the 50 ms tick. Hover is read from the cursor position rather than mouse events: a faded-out bar is
        /// fully transparent, and a transparent window lets the mouse fall through to the app underneath.
        /// </summary>
        private void UpdateHover()
        {
            if (!IsVisible) return;
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            var at = new Point(p.X, p.Y);
            bool overDock = Over(_dock, at, 6), overTop = Over(_top, at, 8), overGrip = Over(_grip, at, 6), overCard = Over(_card, at, 4);

            // Short grace period so the capsule doesn't snap shut when the pointer grazes its edge.
            var now = DateTime.UtcNow;
            if (overDock) _dockHoverUntil = now.AddMilliseconds(350);
            bool hover = overDock || now < _dockHoverUntil;
            if (hover != _dockHover)
            {
                _dockHover = hover;
                UpdateDockExpansion(false);
            }

            // With a screen open, the title bar and the dock stay out of sight until you point at the panel - the
            // screen shows where TheCloser is. With just the capsule or the bar, they always show (like the Mac).
            bool typing = _ask.IsKeyboardFocusWithin && _ask.Text.Length > 0;
            bool chrome = !_panelOpen || hover || overTop || overCard || overGrip || typing || _moreMenuOpen || _modalOpen;
            SetShown(_top, ref _topShown, chrome);
            SetShown(_dock, ref _dockShown, chrome);
            SetShown(_grip, ref _gripShown, chrome);
        }

        private static bool Over(FrameworkElement el, Point screen, double pad)
        {
            if (el == null || !el.IsVisible || el.ActualWidth <= 0) return false;
            try
            {
                var p = el.PointFromScreen(screen);
                return p.X >= -pad && p.Y >= -pad && p.X <= el.ActualWidth + pad && p.Y <= el.ActualHeight + pad;
            }
            catch (InvalidOperationException) { return false; }
        }

        private static void SetShown(UIElement el, ref bool state, bool on)
        {
            if (state == on) return;
            state = on;
            U.Animate(el, OpacityProperty, on ? 1 : 0, on ? 140 : 280);
        }

        // =========================================================================================
        // Actions from the chrome
        // =========================================================================================

        /// <summary>Hovering previews the bar; clicking keeps it open until you click again (which also closes the panel).</summary>
        private void OnLogoClick()
        {
            if (_panelOpen || _dockPinned)
            {
                SetPanel(false);
                _dockPinned = false;
            }
            else _dockPinned = true;
            UpdateDockExpansion(false);
        }

        /// <summary>"Hide window" from the menu: the first time, say how to get it back.</summary>
        private void HideWithHint()
        {
            HideWindow();
            if (!_trayHintShown && _tray != null)
            {
                _trayHintShown = true;
                try { _tray.ShowBalloonTip(3000, "TheCloser is still running", "Press Ctrl+Alt+Space to bring it back.", WinForms.ToolTipIcon.Info); }
                catch { }
            }
        }

        private void NewInterviewSetup()
        {
            _reviewing = false;
            _setup.SetPrevious(null);
            ShowView("setup");
        }

        private void SendAsk()
        {
            var text = _ask.Text.Trim();
            if (text.Length == 0) return;
            if (Ctl.Current == null)
            {
                Ctl.Open(new Session());
                _reviewing = true;
            }
            _ask.Clear();
            ShowView("session");
            Ctl.Answer(AnswerKind.Ask, text, null);
        }

        private void ShowMoreMenu()
        {
            var menu = BuildMoreMenu();
            menu.PlacementTarget = _moreBtn;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>The ... menu: the same items, in the same order, as the Mac app's.</summary>
        internal ContextMenu BuildMoreMenu()
        {
            var menu = new ContextMenu();
            bool live = Ctl.Active;
            if (live)
            {
                menu.Items.Add(Ctl.Paused ? Item("Resume interview", delegate { Ctl.Resume(); }) : Item("Pause interview", delegate { Ctl.Pause(); }));
                menu.Items.Add(Item("End session", delegate { Ctl.End(); }));
                menu.Items.Add(new Separator());
            }

            // Same switch as on the setup screen; it applies straight away, mid-interview included.
            menu.Items.Add(Choices("Auto-generate responses: " + (S.AutoGenerate ? "On" : "Off"), new[] { "On", "Off" }, S.AutoGenerate ? "On" : "Off",
                v => SetAutoGenerate(v == "On")));
            var model = new MenuItem { Header = "Model: " + ModelCatalog.Name(S.Model) };
            AddModelItems(model, null);
            menu.Items.Add(model);
            menu.Items.Add(new Separator());

            menu.Items.Add(Item("New interview setup", NewInterviewSetup, "Ctrl+N"));
            menu.Items.Add(Item("History", delegate { ShowView("history"); }));
            if (Ctl.Current != null) menu.Items.Add(Item("Rename…", RenameSessionDialog));
            menu.Items.Add(new Separator());

            if (Ctl.Current != null)
            {
                var export = new MenuItem { Header = "Export" };
                export.Items.Add(Item("Copy as Markdown", CopySessionMarkdown));
                export.Items.Add(Item("Save as Markdown…", ExportSession));
                menu.Items.Add(export);
            }
            int audio = Math.Max(0, Array.IndexOf(AudioValues, S.AudioSource));
            menu.Items.Add(Choices("Audio source: " + AudioLabels[audio], AudioLabels, AudioLabels[audio],
                v => SetAudioSource(AudioValues[Array.IndexOf(AudioLabels, v)])));
            menu.Items.Add(Percents("Transparency", new[] { 100, 85, 70, 55, 40 }, S.OpacityPct, n => { S.OpacityPct = n; ApplyAppearance(); SaveSettingsSoon(); }));
            menu.Items.Add(Percents("Background", new[] { 100, 80, 60, 40, 20 }, S.BackgroundPct, n => { S.BackgroundPct = n; ApplyAppearance(); SaveSettingsSoon(); }));
            menu.Items.Add(Percents("Text size", new[] { 90, 100, 115, 130, 150 }, S.TextSizePct, n => { S.TextSizePct = n; ApplyAppearance(); SaveSettingsSoon(); }));
            var keywords = new MenuItem { Header = "Keywords: " + KeywordStyles.Name(S.KeywordStyle) };
            AddKeywordItems(keywords, null);
            menu.Items.Add(keywords);
            var engine = new MenuItem { Header = "Transcription: " + EngineShortName(S.EffectiveTranscription) };
            AddEngineItems(engine, null);
            menu.Items.Add(engine);

            if (live)
            {
                menu.Items.Add(new Separator());
                var trans = new MenuItem { Header = "Show live transcript", IsCheckable = true, IsChecked = S.ShowTranscript };
                trans.Click += delegate { SetShowTranscript(trans.IsChecked); };
                menu.Items.Add(trans);
                var focus = new MenuItem { Header = "Focus mode (current Q&A only)", IsCheckable = true, IsChecked = S.FocusMode };
                focus.Click += delegate { SetFocusMode(focus.IsChecked); };
                menu.Items.Add(focus);
            }

            if (Ctl.Current != null)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete Session", delegate { ConfirmDeleteSession(Ctl.Current, null); }));
            }

            // App-level controls: the tray icon is out of reach during a full-screen call, so these live here too.
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Reset overlay position", ResetPosition));
            menu.Items.Add(Item("Close to tray", HideWithHint));
            menu.Items.Add(Item("Quit TheCloser", delegate { Close(); }));
            menu.Opened += delegate { _moreMenuOpen = true; };
            menu.Closed += delegate
            {
                _moreMenuOpen = false;
                if (_view == "settings") _settings.Refresh(); // its controls show the old values
            };
            return menu;
        }

        internal Button MoreButton { get { return _moreBtn; } }

        /// <summary>Takes the current screen out of the panel, to draw it at full length (`--render`).</summary>
        internal FrameworkElement DetachView()
        {
            var el = _viewHost.Content as FrameworkElement;
            _viewHost.Content = null;
            return el;
        }

        /// <summary>Slides the capsule open as if hovered (`--render`).</summary>
        internal void PreviewDockHover()
        {
            _dockHover = true;
            UpdateDockExpansion(true);
        }

        internal static MenuItem Item(string header, Action click, string gesture = null)
        {
            var mi = new MenuItem { Header = header };
            if (gesture != null) mi.InputGestureText = gesture;
            mi.Click += delegate { click(); };
            return mi;
        }

        /// <summary>A submenu of choices with the current one ticked, e.g. "Audio source: Both > Mic / System / Both".</summary>
        private static MenuItem Choices(string header, string[] options, string current, Action<string> pick)
        {
            var parent = new MenuItem { Header = header };
            foreach (var o in options)
            {
                var option = o;
                var mi = new MenuItem { Header = option, IsCheckable = true, IsChecked = option == current };
                mi.Click += delegate { pick(option); };
                parent.Items.Add(mi);
            }
            return parent;
        }

        /// <summary>"Transparency: 85% > 100% / 85% / 70% …" (the Mac's presets; Settings has the sliders).</summary>
        private static MenuItem Percents(string label, int[] levels, int current, Action<int> pick)
        {
            var parent = new MenuItem { Header = label + ": " + current + "%" };
            foreach (var l in levels)
            {
                var level = l;
                var mi = new MenuItem { Header = level + "%", IsCheckable = true, IsChecked = level == current };
                mi.Click += delegate { pick(level); };
                parent.Items.Add(mi);
            }
            return parent;
        }

        // =========================================================================================
        // Pickers (the ... menu, the setup screen and Settings share these)
        // =========================================================================================

        /// <summary>Adds the enabled models to a menu under their provider, the current one ticked.</summary>
        private void AddModelItems(ItemsControl parent, Action picked)
        {
            var slugs = S.UseSubscription && Billing.IsActive ? Billing.Models : S.EnabledModels;
            foreach (var group in slugs.GroupBy(m => ModelCatalog.Group(m)))
            {
                if (parent.Items.Count > 0) parent.Items.Add(new Separator());
                parent.Items.Add(new MenuItem { Header = group.Key, IsEnabled = false, FontSize = 12.5 });
                foreach (var slug in group)
                {
                    var m = slug;
                    var mi = new MenuItem { Header = ModelCatalog.Name(m), IsCheckable = true, IsChecked = m == S.Model };
                    mi.Click += delegate { SetModel(m); if (picked != null) picked(); };
                    parent.Items.Add(mi);
                }
            }
        }

        public void SetModel(string slug)
        {
            S.Model = slug;
            SaveSettingsSoon();
            OnKeysChanged();
            Toast("Answers now use " + ModelCatalog.Name(slug) + ".", false);
        }

        public void SetAutoGenerate(bool on)
        {
            if (S.AutoGenerate == on) return;
            S.AutoGenerate = on;
            SaveSettingsSoon();
            _setup.Refresh();
        }

        /// <summary>Locks transcription to one language ("" = detect). Takes effect right away, even mid-interview.</summary>
        public void SetSpeechLanguage(string code)
        {
            code = code ?? "";
            if (S.SpeechLanguage == code) return;
            S.SpeechLanguage = code;
            SaveSettingsSoon();
            Ctl.RestartSource();
            _setup.Refresh();
        }

        // The Mac's labels.
        private static readonly string[] AudioLabels = { "Mic", "System", "Both" };
        private static readonly string[] AudioValues = { "Mic", "System", "Both" };

        /// <summary>What to listen to: your microphone, what the PC plays, or both. Applies right away.</summary>
        public void SetAudioSource(string value)
        {
            if (S.AudioSource == value) return;
            S.AudioSource = value;
            SaveSettingsSoon();
            Ctl.RestartSource();
        }

        private static readonly string[] EngineIds = { "Automatic", "LiveCaptions", "ElevenLabs", "Grok" };
        private static readonly string[] EngineNames = { "Automatic", "Windows (on-device, free)", "ElevenLabs (cloud)", "Grok Transcribe 2 (cloud)" };
        // Pro: both cloud engines are included (TheCloser's keys), or this PC.
        private static readonly string[] ProEngineIds = { "Grok", "ElevenLabs", "LiveCaptions" };
        private static readonly string[] ProEngineNames = { "Grok Transcribe 2 (included)", "ElevenLabs (included)", "Windows (on-device, free)" };

        private static string EngineShortName(string engine)
        {
            if (engine == "ProGrok") return "Grok";
            if (engine == "ProElevenLabs") return "ElevenLabs";
            return engine == "LiveCaptions" ? "Windows" : engine;
        }

        private bool ProEngines { get { return S.UseSubscription && Billing.IsActive; } }

        private void AddEngineItems(ItemsControl parent, Action picked)
        {
            bool pro = ProEngines;
            var ids = pro ? ProEngineIds : EngineIds;
            var names = pro ? ProEngineNames : EngineNames;
            var current = pro ? S.ProTranscription : S.Transcription;
            for (int i = 0; i < ids.Length; i++)
            {
                var id = ids[i];
                var mi = new MenuItem { Header = names[i], IsCheckable = true, IsChecked = current == id };
                mi.Click += delegate { if (pro) SetProEngine(id); else SetEngine(id); if (picked != null) picked(); };
                parent.Items.Add(mi);
            }
        }

        /// <summary>Switches the transcription engine; mid-interview the new one takes over at once.</summary>
        public void SetEngine(string id)
        {
            if (S.Transcription == id) return;
            S.Transcription = id;
            SaveSettingsSoon();
            Ctl.RestartSource();
            OnKeysChanged();
        }

        /// <summary>Pro's pick: Grok or ElevenLabs on TheCloser's account, or Windows on this PC.</summary>
        public void SetProEngine(string id)
        {
            if (S.ProTranscription == id) return;
            S.ProTranscription = id;
            SaveSettingsSoon();
            Ctl.RestartSource();
            OnKeysChanged();
        }

        /// <summary>Pill showing the transcription engine; opens the list (Settings > General).</summary>
        public Button EnginePicker(Action changed)
        {
            bool pro = ProEngines;
            var label = pro
                ? ProEngineNames[Math.Max(0, Array.IndexOf(ProEngineIds, S.ProTranscription))]
                : EngineNames[Math.Max(0, Array.IndexOf(EngineIds, S.Transcription))];
            Button b = null;
            b = U.Btn("Btn.Pill", PickerLabel(null, label), delegate
            {
                var menu = new ContextMenu();
                AddEngineItems(menu, changed);
                OpenBelow(menu, b);
            }, "Transcription engine");
            return b;
        }

        private void AddKeywordItems(ItemsControl parent, Action picked)
        {
            bool highlights = false;
            foreach (var id in KeywordStyles.Ids)
            {
                var style = id;
                if (KeywordStyles.IsHighlight(style) && !highlights) { parent.Items.Add(new Separator()); highlights = true; }
                var mi = new MenuItem { Header = KeywordStyles.Name(style), IsCheckable = true, IsChecked = S.KeywordStyle == style };
                mi.Click += delegate { SetKeywordStyle(style); if (picked != null) picked(); };
                parent.Items.Add(mi);
            }
        }

        public void SetKeywordStyle(string id)
        {
            S.KeywordStyle = id;
            SaveSettingsSoon();
            _session.OnSettingsChanged();
        }

        /// <summary>Pill showing the keyword style; opens the styles (Settings > General > Keywords).</summary>
        public Button KeywordPicker(Action changed)
        {
            Button b = null;
            b = U.Btn("Btn.Pill", PickerLabel(null, KeywordStyles.Name(S.KeywordStyle)), delegate
            {
                var menu = new ContextMenu();
                AddKeywordItems(menu, delegate
                {
                    b.Content = PickerLabel(null, KeywordStyles.Name(S.KeywordStyle));
                    if (changed != null) changed();
                });
                OpenBelow(menu, b);
            }, "How keywords in answers stand out");
            return b;
        }

        /// <summary>Pill showing the transcription language; opens the language list.</summary>
        public Button LanguagePicker(Action changed = null)
        {
            Button b = null;
            b = U.Btn("Btn.Pill", null, delegate
            {
                var menu = new ContextMenu { MaxHeight = 440 };
                var current = SpeechLanguages.Find(S.SpeechLanguage);
                foreach (var l in SpeechLanguages.All)
                {
                    var lang = l;
                    var mi = new MenuItem { Header = lang.Name, IsCheckable = true, IsChecked = lang == current };
                    mi.Click += delegate
                    {
                        SetSpeechLanguage(lang.Code);
                        b.Content = PickerLabel(U.Icon(U.GGlobe, 15, null), SpeechLanguages.Name(S.SpeechLanguage));
                        if (changed != null) changed();
                    };
                    menu.Items.Add(mi);
                }
                OpenBelow(menu, b);
            }, "Language of the interview");
            b.Content = PickerLabel(U.Icon(U.GGlobe, 15, null), SpeechLanguages.Name(S.SpeechLanguage));
            return b;
        }

        /// <summary>A key field with a show/hide eye, saved as you type.</summary>
        public FrameworkElement KeyField(string placeholder, string value, Action<string> save)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            PasswordBox box;
            var hidden = U.SecretField(placeholder, value, out box);
            var shown = U.Input(placeholder, value, false);
            shown.FontFamily = U.Mono;
            shown.FontSize = 14;
            shown.Padding = new Thickness(12, 9, 12, 9);
            shown.Visibility = Visibility.Collapsed;
            g.Children.Add(hidden);
            g.Children.Add(shown);
            bool syncing = false;
            Action<string> changed = delegate(string v)
            {
                if (syncing) return;
                syncing = true;
                if (box.Password != v) box.Password = v;
                if (shown.Text != v) shown.Text = v;
                syncing = false;
                save(v.Trim());
                SaveSettingsSoon();
                OnKeysChanged();
            };
            box.PasswordChanged += delegate { changed(box.Password); };
            shown.TextChanged += delegate { changed(shown.Text); };
            Button eye = null;
            eye = U.Btn("Btn.Ghost", U.Icon(U.GView, 15, null), delegate
            {
                bool reveal = shown.Visibility != Visibility.Visible;
                shown.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
                hidden.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
                eye.Content = reveal ? U.EyeSlash(15, null) : (FrameworkElement)U.Icon(U.GView, 15, null);
            }, "Show or hide the key");
            eye.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(eye, 1);
            g.Children.Add(eye);
            return g;
        }

        internal static StackPanel PickerLabel(FrameworkElement icon, string text)
        {
            var sp = icon != null ? U.IconText(icon, text) : new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (icon == null) sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) });
            var chevron = U.Icon(U.GDown, 10, U.Text2);
            chevron.Margin = new Thickness(10, 1, 0, 0);
            sp.Children.Add(chevron);
            return sp;
        }

        internal static void OpenBelow(ContextMenu menu, UIElement target)
        {
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // =========================================================================================
        // Sessions (called by the views)
        // =========================================================================================

        public async void StartInterview(Session previous)
        {
            if (S.UseSubscription && !Billing.IsActive) await RefreshBillingAsync();
            if (S.MissingKeys.Count > 0 || ModelCatalog.Resolve(S, S.Model).Provider == null) { ShowSettings("ai"); return; }
            var session = previous ?? new Session { PromptId = S.PromptId };
            _reviewing = false;
            // Every interview starts like the Mac's: the transcript strip showing, one Q&A at a time.
            S.ShowTranscript = true;
            S.FocusMode = true;
            SaveSettingsSoon();
            Ctl.Begin(session);
            ShowView("session");
        }

        public void ContinueSession()
        {
            if (Ctl.Current == null) return;
            _reviewing = false;
            Ctl.Begin(Ctl.Current);
            ShowView("session");
        }

        public void OpenSession(Session session)
        {
            Ctl.Open(session);
            _reviewing = true;
            ShowView("session");
        }

        public void ConfirmDeleteSession(Session s, Action onDone)
        {
            var body = U.T("Delete “" + s.Title + "”? This can't be undone.", 15, U.Text2);
            var cancel = U.Btn("Btn.Pill", "Cancel", CloseModal);
            var del = U.Btn("Btn.White", "Delete", delegate
            {
                if (Ctl.Current != null && Ctl.Current.Id == s.Id) Ctl.DeleteCurrent();
                else SessionStore.Delete(s.Id);
                CloseModal();
                if (onDone != null) onDone();
            });
            ShowModal(ModalShell("Delete session", body, cancel, del));
        }

        private void RenameSessionDialog()
        {
            if (Ctl.Current == null) return;
            var input = U.Input("Session name", Ctl.Current.Title, false);
            var cancel = U.Btn("Btn.Pill", "Cancel", CloseModal);
            var save = U.Btn("Btn.White", "Rename", delegate
            {
                var t = input.Text.Trim();
                if (t.Length > 0) Ctl.Rename(t);
                CloseModal();
            });
            ShowModal(ModalShell("Rename session", input, cancel, save));
        }

        private void ExportSession()
        {
            if (Ctl.Current == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
                FileName = SafeFileName(Ctl.Current.Title) + ".md",
                Title = "Export session"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var text = dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? Ctl.Current.ToPlainText() : Ctl.Current.ToMarkdown();
                System.IO.File.WriteAllText(dlg.FileName, text, Encoding.UTF8);
                Toast("Exported to " + System.IO.Path.GetFileName(dlg.FileName), false);
            }
            catch (Exception ex) { Toast("Export failed: " + ex.Message, true); }
        }

        private void CopySessionMarkdown()
        {
            if (Ctl.Current == null) return;
            try
            {
                Clipboard.SetText(Ctl.Current.ToMarkdown());
                Toast("Copied as Markdown.", false);
            }
            catch (Exception ex) { Toast("Couldn't copy: " + ex.Message, true); }
        }

        /// <summary>Opens a URL in the user's default browser.</summary>
        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "session";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
            name = name.Trim();
            return name.Length == 0 ? "session" : name;
        }

        // =========================================================================================
        // Answers, screen and settings hooks (called by the views)
        // =========================================================================================

        private void AnswerNow()
        {
            if (Ctl.Current == null) { ShowWindow(); Toast("Start an interview first, then I can answer.", true); return; }
            ShowView("session");
            Ctl.Answer(AnswerKind.Manual, null, null);
        }

        private void CaptureAndAnswer()
        {
            if (Ctl.Current == null) { Ctl.Open(new Session { Title = "Screen" }); _reviewing = true; }
            string shot;
            try { shot = ScreenGrab.CaptureJpegBase64(_hwnd); }
            catch (Exception ex) { ShowWindow(); Toast("Couldn't capture the screen: " + ex.Message, true); return; }
            ShowView("session");
            Ctl.Answer(AnswerKind.Screen, null, shot);
        }

        /// <summary>One question and answer at a time (with ‹ › to flip back), or the whole conversation.</summary>
        public void SetFocusMode(bool on)
        {
            S.FocusMode = on;
            SaveSettingsSoon();
            _session.RefreshQas();
        }

        public void SetShowTranscript(bool on)
        {
            S.ShowTranscript = on;
            SaveSettingsSoon();
            _session.RefreshTranscript();
            _session.RefreshState();
        }

        public void ToggleStealth()
        {
            S.HideFromCapture = !S.HideFromCapture;
            StealthOn = S.HideFromCapture && !_forceCapturable;
            ApplyCaptureExclusion();
            SaveSettingsSoon();
            _session.RefreshState();
            Toast(S.HideFromCapture ? "Hidden from screen sharing." : "Now visible to screen sharing.", false);
        }

        public void OnKeysChanged()
        {
            _setup.Refresh();
            _session.RefreshState();
            RefreshDock();
        }

        public void SaveSettingsSoon()
        {
            if (Offscreen) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void EditPrompt(PromptDef prompt, Action onSaved)
        {
            EditPrompt(prompt, onSaved, false);
        }

        public void EditPrompt(PromptDef prompt, Action onSaved, bool duplicate)
        {
            bool isNew = duplicate || prompt == null || string.IsNullOrEmpty(prompt.Id);
            bool builtIn = !isNew && Prompts.IsBuiltIn(prompt.Id);
            var name = U.Input("Prompt name", prompt != null ? prompt.Name : "", false);
            var instr = U.Input("Describe how TheCloser should answer during the interview…", prompt != null ? prompt.Text : "", true);
            instr.Height = 200;
            var body = new StackPanel();
            body.Children.Add(Label2("Name"));
            body.Children.Add(name);
            body.Children.Add(Label2("Instructions"));
            body.Children.Add(instr);
            var cancel = U.Btn("Btn.Pill", "Cancel", CloseModal);
            var save = U.Btn("Btn.White", isNew ? "Save and use" : "Save prompt", delegate
            {
                var nm = name.Text.Trim();
                var tx = instr.Text.Trim();
                if (nm.Length == 0 || tx.Length == 0) { Toast("Give the prompt a name and instructions.", true); return; }
                if (isNew)
                {
                    var def = new PromptDef { Id = Prompts.NewId(), Name = nm, Text = tx };
                    S.CustomPrompts.Add(def);
                    S.PromptId = def.Id;
                }
                else if (builtIn) Prompts.EditBuiltIn(S, prompt.Id, nm, tx);
                else
                {
                    prompt.Name = nm;
                    prompt.Text = tx;
                }
                SaveSettingsSoon();
                CloseModal();
                if (onSaved != null) onSaved();
                RefreshSetup();
            });
            if (builtIn && Prompts.IsEdited(S, prompt.Id))
            {
                var reset = U.Btn("Btn.Pill", "Reset to original", delegate
                {
                    ResetPrompt(prompt.Id);
                    CloseModal();
                    if (onSaved != null) onSaved();
                });
                ShowModal(ModalShell("Edit prompt", body, reset, cancel, save));
            }
            else ShowModal(ModalShell(isNew ? "New prompt" : "Edit prompt", body, cancel, save));
        }

        /// <summary>Puts an edited built-in prompt back to how it shipped.</summary>
        public void ResetPrompt(string id)
        {
            Prompts.ResetBuiltIn(S, id);
            SaveSettingsSoon();
            RefreshSetup();
            if (_view == "settings") _settings.Refresh();
            Toast("Reset “" + Prompts.Find(S, id).Name + "” to the original.", false);
        }

        /// <summary>Deletes a prompt (built-ins can be brought back from Settings > Prompts), then refreshes both screens.</summary>
        public void DeletePrompt(PromptDef prompt)
        {
            Prompts.Delete(S, prompt.Id);
            SaveSettingsSoon();
            RefreshSetup();
            if (_view == "settings") _settings.Refresh();
            Toast("Deleted “" + prompt.Name + "”." + (Prompts.IsBuiltIn(prompt.Id) ? " Restore it from Settings > Prompts." : ""), false);
        }

        /// <summary>The welcome tour, in the panel (first run, and Settings > General > Replay welcome tour).</summary>
        public void ShowWelcomeTour()
        {
            _tour.Go(OnboardingView.Step.Welcome);
            ShowView("tour");
        }

        /// <summary>A step of the tour (`--render`).</summary>
        internal void ShowTourStep(OnboardingView.Step step)
        {
            _tour.Go(step);
            ShowView("tour");
        }

        /// <summary>The tour's last button: on to the interview setup.</summary>
        public void FinishWelcomeTour()
        {
            ShowView(MainView());
        }

        /// <summary>"Have a tester code?": null when it worked, otherwise what went wrong.</summary>
        public async Task<string> RedeemTesterCodeAsync(string code)
        {
            try
            {
                await Billing.RedeemAsync(code);
                try { await Billing.RefreshUsageAsync(); } catch { }
            }
            catch (SubscriptionException ex) { return ex.Message; }
            catch (Exception) { return "Couldn't reach TheCloser. Check your connection and try again."; }
            SaveSettingsSoon();
            OnKeysChanged();
            if (_view == "settings") _settings.Refresh();
            Toast("Tester access is on. Thanks for testing!", false);
            return null;
        }

        // =========================================================================================
        // Appearance & bounds
        // =========================================================================================

        /// <summary>
        /// Opacity, and the Background setting - which is for answers shown over a call. Setup, Settings, History and the
        /// browser are read up close, and text from the window behind showing through them looks broken, so they're solid
        /// (like the Mac).
        /// </summary>
        public void ApplyAppearance()
        {
            Opacity = Math.Max(0.2, S.OpacityPct / 100.0);
            byte bar = (byte)Math.Round(255.0 * Math.Max(0, Math.Min(100, S.BackgroundPct)) / 100.0);
            byte surface = _view == "session" ? bar : (byte)255;
            SetBgAlpha(_topBrush, 0x111111, surface);
            SetBgAlpha(_cardBrush, 0x111111, surface);
            SetBgAlpha(_dockBrush, 0x111111, bar);
            if (_session != null) _session.OnSettingsChanged();
        }

        /// <summary>Answer text size at the Text size setting (100% is the default).</summary>
        public static double AnswerSize(AppSettings s)
        {
            return 18.0 * s.TextSizePct / 100.0;
        }

        /// <summary>"Reset overlay position": back to the top right, at the default size.</summary>
        private void ResetPosition()
        {
            S.WinLeft = -1;
            S.WinTop = -1;
            S.WinWidth = 0;
            S.WinHeight = 0;
            ApplyBounds();
            SaveBounds();
        }

        private static void SetBgAlpha(SolidColorBrush brush, int rgb, byte a)
        {
            brush.Color = Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        private void ApplyBounds()
        {
            var wa = SystemParameters.WorkArea;
            double w = S.WinWidth > 0 ? S.WinWidth : 470;
            double h = S.WinHeight > 0 ? S.WinHeight : Math.Min(680, wa.Height - 48);
            w = Math.Max(MinWidth, Math.Min(w, wa.Width));
            h = Math.Max(MinHeight, Math.Min(h, wa.Height));
            double left = S.WinLeft >= 0 ? S.WinLeft : wa.Right - w - 24;
            double top = S.WinTop >= 0 ? S.WinTop : wa.Top + 24;
            Left = Math.Max(wa.Left, Math.Min(left, wa.Right - w));
            Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - h));
            Width = w;
            Height = h;
        }

        private void SaveBounds()
        {
            if (WindowState != WindowState.Normal) return;
            S.WinLeft = Left;
            S.WinTop = Top;
            S.WinWidth = Width;
            S.WinHeight = Height;
            SaveSettingsSoon();
        }

        // =========================================================================================
        // Window show / hide
        // =========================================================================================

        private void ShowWindow()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Topmost = true;
            Activate();
        }

        private void HideWindow()
        {
            Hide();
        }

        private void ToggleWindow()
        {
            if (IsVisible) HideWindow();
            else ShowWindow();
        }

        // =========================================================================================
        // Modal layer
        // =========================================================================================

        private void ShowModal(FrameworkElement card)
        {
            _modalLayer.Children.Clear();
            var scrim = new Border { Background = U.B(0xB0000000) };
            scrim.MouseLeftButtonDown += delegate { CloseModal(); };
            _modalLayer.Children.Add(scrim);
            card.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; };
            _modalLayer.Children.Add(card);
            _modalLayer.Visibility = Visibility.Visible;
            _modalOpen = true;
            U.Enter(card, 12, 170);
        }

        private void CloseModal()
        {
            _modalLayer.Children.Clear();
            _modalLayer.Visibility = Visibility.Collapsed;
            _modalOpen = false;
        }

        private Border ModalShell(string title, UIElement body, params Button[] actions)
        {
            var stack = new StackPanel();
            stack.Children.Add(U.T(title, 19, U.Text, FontWeights.SemiBold));
            var host = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            host.Children.Add(body);
            stack.Children.Add(host);
            if (actions != null && actions.Length > 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
                foreach (var b in actions) { b.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(b); }
                stack.Children.Add(row);
            }
            return new Border
            {
                Background = U.B(0xFF111111),
                BorderBrush = U.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(24),
                MaxWidth = 560,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(28),
                Effect = Shadow(),
                Child = stack
            };
        }

        private static TextBlock Label2(string text)
        {
            var t = U.T(text, 13.5, U.Text2, FontWeights.SemiBold);
            t.Margin = new Thickness(0, 14, 0, 6);
            return t;
        }

        // =========================================================================================
        // Toasts
        // =========================================================================================

        public void Toast(string text, bool problem)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var tb = U.T(text, 14, problem ? U.B(0xFFF3D9A6) : U.Text, FontWeights.SemiBold);
            tb.TextAlignment = TextAlignment.Center;
            var pill = new Border
            {
                Background = problem ? U.WarnBg : U.B(0xF01F1F22),
                BorderBrush = problem ? U.WarnBorder : U.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(16, 9, 16, 10),
                Margin = new Thickness(0, 6, 0, 0),
                MaxWidth = 400,
                Effect = Shadow(),
                Child = tb
            };
            _toasts.Children.Add(pill);
            while (_toasts.Children.Count > 3) _toasts.Children.RemoveAt(0);
            U.Enter(pill, 10, 170);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(problem ? 6 : 3.5) };
            timer.Tick += delegate
            {
                timer.Stop();
                U.Animate(pill, OpacityProperty, 0, 200, delegate { _toasts.Children.Remove(pill); });
            };
            timer.Start();
        }

        // =========================================================================================
        // Session controller events
        // =========================================================================================

        private void OnSessionState()
        {
            if (Ctl.Current != null && !Ctl.Active) _reviewing = true;
            _session.RefreshState();
            RefreshTopBar();
            RefreshDock();
            if (_view == "setup" && Ctl.Current != null && (Ctl.Active || _reviewing)) ShowView("session");
            else if (_view == "session" && Ctl.Current == null) ShowView("setup");
        }

        private void OnQasChanged()
        {
            _session.RefreshQas();
            // A new answer is starting during the call: bring the panel back if you'd closed it.
            var s = Ctl.Current;
            if (!_panelOpen && Ctl.Active && s != null && s.Qas.Count > 0 && s.Qas[s.Qas.Count - 1].Streaming)
                ShowView("session");
        }

        private void OnSessionStatus(string text, bool problem)
        {
            Toast(text, problem);
        }

        // =========================================================================================
        // Call detector
        // =========================================================================================

        private void StartCallDetector()
        {
            if (_calls != null) return;
            _calls = new CallDetector();
            _calls.CallStarted += delegate(string app) { Dispatcher.BeginInvoke((Action)delegate { OfferCall(app); }); };
        }

        /// <summary>Settings > General > "Offer to start when a call begins" changed.</summary>
        public void OnOfferOnCallChanged()
        {
            if (Offscreen) return;
            if (S.OfferOnCall) StartCallDetector();
            else
            {
                if (_calls != null) { _calls.Dispose(); _calls = null; }
                if (_callPrompt != null) _callPrompt.Hide();
            }
        }

        /// <summary>"On a call in Zoom? Start interview": the card in the top-right corner, like the Mac.</summary>
        private void OfferCall(string app)
        {
            if (Ctl.Active || !S.OfferOnCall) return;
            DateTime last;
            if (_callOffered.TryGetValue(app, out last) && (DateTime.UtcNow - last).TotalMinutes < 5) return;
            _callOffered[app] = DateTime.UtcNow;
            if (_callPrompt == null) _callPrompt = new CallPromptWindow();
            _callPrompt.ShowFor(app, StartFromCallPrompt);
        }

        /// <summary>Opens the interview setup and, when the keys are there, starts right away.</summary>
        private void StartFromCallPrompt()
        {
            ShowWindow();
            if (S.MissingKeys.Count > 0) { ShowView("setup"); return; }
            StartInterview(null);
        }

        // =========================================================================================
        // Tray
        // =========================================================================================

        private void BuildTray()
        {
            _tray = new WinForms.NotifyIcon { Icon = Brand.TrayIcon(), Visible = true, Text = "TheCloser" };
            _tray.DoubleClick += delegate { Dispatcher.Invoke((Action)ShowWindow); };
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show / hide  (Ctrl+Alt+Space)", null, delegate { Dispatcher.Invoke((Action)ToggleWindow); });
            menu.Items.Add("New interview", null, delegate { Dispatcher.Invoke((Action)delegate { ShowWindow(); NewInterviewSetup(); }); });
            menu.Items.Add("Settings", null, delegate { Dispatcher.Invoke((Action)delegate { ShowWindow(); ShowSettings(null); }); });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Quit TheCloser", null, delegate { Dispatcher.Invoke((Action)delegate { Close(); }); });
            _tray.ContextMenuStrip = menu;
        }

        // =========================================================================================
        // Global hotkeys + window messages
        // =========================================================================================

        internal sealed class Shortcut
        {
            public readonly string[] Keys;
            public readonly string Description;
            public Shortcut(string description, params string[] keys) { Description = description; Keys = keys; }
        }

        /// <summary>The global shortcuts, as Settings > Shortcuts lists them: the Mac's set, with Ctrl for ⌘.</summary>
        internal static readonly Shortcut[] Shortcuts =
        {
            new Shortcut("Show / hide overlay", "Ctrl", "Alt", "Space"),
            new Shortcut("Get the answer now (during an interview)", "Ctrl", "Enter"),
            new Shortcut("Screenshot → send to AI (during an interview)", "Ctrl", "Shift", "Enter"),
            new Shortcut("Move overlay (while it's showing)", "Ctrl", "Shift", "↑↓←→"),
            new Shortcut("Resize overlay", "Ctrl", "Alt", "Shift", "↑↓←→"),
            new Shortcut("New session", "Ctrl", "N")
        };

        private const uint VK_SPACE = 0x20, VK_RETURN = 0x0D, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
        private static readonly uint[] Arrows = { VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN };

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(_hwnd);
            if (src != null) src.AddHook(WndProc);
            ApplyCaptureExclusion();
            UpdateHotkeys();
        }

        private void ApplyCaptureExclusion()
        {
            if (_hwnd != IntPtr.Zero) Native.SetCaptureExcluded(_hwnd, StealthOn);
        }

        private void UpdateHotkeys()
        {
            if (_hwnd == IntPtr.Zero || Offscreen) return;
            Register(HkToggle, Native.MOD_CONTROL | Native.MOD_ALT, VK_SPACE, true);
            if (IsVisible)
            {
                Register(HkAnswer, Native.MOD_CONTROL, VK_RETURN, true);
                Register(HkShot, Native.MOD_CONTROL | Native.MOD_SHIFT, VK_RETURN, true);
                for (int i = 0; i < 4; i++)
                {
                    Register(HkMove + i, Native.MOD_CONTROL | Native.MOD_SHIFT, Arrows[i], false);
                    Register(HkSize + i, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, Arrows[i], false);
                }
            }
            else
            {
                Unregister(HkAnswer);
                Unregister(HkShot);
                for (int i = 0; i < 4; i++) { Unregister(HkMove + i); Unregister(HkSize + i); }
            }
        }

        private void Register(int id, uint mods, uint vk, bool noRepeat)
        {
            if (_registered.Contains(id)) return;
            uint m = noRepeat ? mods | Native.MOD_NOREPEAT : mods;
            if (Native.RegisterHotKey(_hwnd, id, m, vk)) _registered.Add(id);
        }

        private void Unregister(int id)
        {
            if (!_registered.Contains(id)) return;
            Native.UnregisterHotKey(_hwnd, id);
            _registered.Remove(id);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                OnHotkey(wParam.ToInt32());
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void OnHotkey(int id)
        {
            if (id == HkToggle) { ToggleWindow(); return; }
            if (id == HkAnswer) { AnswerNow(); return; }
            if (id == HkShot) { CaptureAndAnswer(); return; }
            if (id >= HkMove && id < HkMove + 4) { NudgeMove(id - HkMove); return; }
            if (id >= HkSize && id < HkSize + 4) { NudgeSize(id - HkSize); return; }
        }

        private void NudgeMove(int dir)
        {
            const double step = 40;
            switch (dir)
            {
                case 0: Left -= step; break;   // left
                case 1: Top -= step; break;    // up
                case 2: Left += step; break;   // right
                case 3: Top += step; break;    // down
            }
            SaveBounds();
        }

        private void NudgeSize(int dir)
        {
            const double step = 30;
            switch (dir)
            {
                case 0: Width = Math.Max(MinWidth, Width - step); break;   // narrower
                case 1: Height = Math.Max(MinHeight, Height - step); break; // shorter
                case 2: Width = Width + step; break;                       // wider
                case 3: Height = Height + step; break;                     // taller
            }
            SaveBounds();
        }

        private void OnPreviewKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_modalOpen) { CloseModal(); e.Handled = true; return; }
                if (Ctl.Answering) { Ctl.CancelAnswer(); e.Handled = true; }
                return;
            }
            if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                NewInterviewSetup();
                e.Handled = true;
            }
        }

        // =========================================================================================
        // Demo & external entry points
        // =========================================================================================

        /// <summary>Fills the window with a sample interview (used by `--demo` and `--render`), without any keys or listening.</summary>
        public void LoadDemo()
        {
            LoadDemo(false);
        }

        /// <param name="live">Show it as a live interview (paused, never listening or saved) instead of a past one (`--render`).</param>
        internal void LoadDemo(bool live)
        {
            var s = new Session { Title = "Backend interview — Acme", TitleSetByUser = true };
            var now = DateTime.Now;
            s.Lines.Add(new SessionLine { Speaker = "Them", Text = "Can you walk me through how you'd design a rate limiter?", Time = now.AddMinutes(-3) });
            s.Lines.Add(new SessionLine { Speaker = "Me", Text = "Sure, I'd start with a token bucket per API key.", Time = now.AddMinutes(-2) });
            s.Lines.Add(new SessionLine { Speaker = "Them", Text = "Thanks for joining. Tell me about a time you had to scale a system quickly.", Time = now });
            s.Qas.Add(new QaItem
            {
                Question = "Can you walk me through how you'd design a rate limiter?",
                Kind = "Auto",
                Time = now.AddMinutes(-3),
                Model = S.Model,
                Answer = "I'd use a **token bucket** per API key, kept in **Redis** so every server shares it.\n" +
                         "- Refill at the plan's rate; reject with **429** and a Retry-After header when empty."
            });
            s.Qas.Add(new QaItem
            {
                Question = "Tell me about a time you had to scale a system quickly.",
                Kind = "Auto",
                Time = now,
                Model = S.Model,
                Answer = "At my last team I scaled our **payments API** from 2k to 20k requests a second in six weeks.\n" +
                         "- Moved reads to **PostgreSQL read replicas**, cutting p95 latency by **40%**.\n" +
                         "- Put a **Redis cache** in front of hot keys, which kept database load flat.\n" +
                         "- Closing point: we hit Black Friday with zero downtime."
            });
            if (live) Ctl.PreviewLive(s);
            else Ctl.Open(s);
            _reviewing = !live;
            ShowView("session");
        }

        /// <summary>Opens the full transcript drop-down (`--render`).</summary>
        internal void PreviewTranscript()
        {
            _session.PreviewTranscript();
        }

        /// <summary>Shows the window on a specific settings page (used by `--open-settings`).</summary>
        public void OpenSettingsPage(string page)
        {
            ShowWindow();
            ShowSettings(page);
        }

        // =========================================================================================
        // Shutdown
        // =========================================================================================

        private void OnClosing()
        {
            _closed = true;
            _billingTimer.Stop();
            try { if (_calls != null) _calls.Dispose(); } catch { }
            try { Ctl.End(); } catch { }
            if (!Offscreen)
            {
                SaveBounds();
                S.Save();
            }
            if (_hwnd != IntPtr.Zero)
                foreach (var id in _registered.ToList()) Native.UnregisterHotKey(_hwnd, id);
            _registered.Clear();
            _tick.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            if (_callPrompt != null) { _callPrompt.Close(); _callPrompt = null; }
            var app = Application.Current;
            if (app != null && !Offscreen) app.Shutdown();
        }

        private bool _trayHintShown;
    }
}
