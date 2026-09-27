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
    /// that is excluded from screen capture. Hosts the setup, session, settings, browser and history views.
    /// </summary>
    internal sealed class OverlayWindow : Window
    {
        private const int HkToggle = 1, HkAnswer = 2, HkShot = 3, HkMove = 10, HkSize = 20;

        public readonly AppSettings S;
        public readonly SessionController Ctl;
        public readonly SubscriptionClient Billing;
        private readonly bool _forceCapturable;
        internal static bool StealthOn;

        private readonly Grid _root = new Grid();
        private RowDefinition _rowCard, _rowFill;
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
        private bool _dockHover, _dockExpanded, _moreMenuOpen;
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
        private bool _billingRefreshing, _closed;
        private bool _modalOpen;
        private readonly HashSet<int> _registered = new HashSet<int>();

        public OverlayWindow(AppSettings settings, bool forceCapturable)
        {
            S = settings;
            _forceCapturable = forceCapturable;
            StealthOn = S.HideFromCapture && !forceCapturable;
            Billing = new SubscriptionClient(S);
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
            Content = _root;
            ApplyBounds();
            ApplyAppearance();

            Ctl.StateChanged += OnSessionState;
            Ctl.QasChanged += OnQasChanged;
            Ctl.TranscriptChanged += () => _session.RefreshTranscript();
            Ctl.Status += OnSessionStatus;
            Ctl.NeedsKey += () => ShowSettings(S.UseSubscription ? "subscription" : "models");
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
                    _settings.RefreshSubscription();
                    OnKeysChanged();
                });
            };
            _billingTimer.Tick += async delegate
            {
                if (!S.UseSubscription && !Billing.PendingCheckout) return;
                var interval = Billing.PendingCheckout && DateTime.UtcNow < _checkoutWatchUntil ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(45);
                if (DateTime.UtcNow - _billingChecked >= interval) await RefreshBillingAsync();
            };
            _billingTimer.Start();
            Loaded += async delegate { if (S.UseSubscription || Billing.PendingCheckout) await RefreshBillingAsync(); };

            _tick.Tick += delegate { if (_view == "session") _session.Tick(); UpdateHover(); };
            _tick.Start();
            _saveTimer.Tick += delegate { _saveTimer.Stop(); S.Save(); };

            PreviewKeyDown += OnPreviewKey;
            SizeChanged += delegate { UpdateCardLayout(); };
            IsVisibleChanged += delegate { UpdateHotkeys(); };
            Closing += delegate { OnClosing(); };

            BuildTray();
            if (S.OfferOnCall) StartCallDetector();
            ShowView("setup");
            SetPanel(false); // start as just the capsule; the dock opens the panel
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
            _rowCard = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
            _root.RowDefinitions.Add(_rowCard);
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rowFill = new RowDefinition { Height = new GridLength(0) };
            _root.RowDefinitions.Add(_rowFill);

            // Title bar -------------------------------------------------------------------------
            _topBrush = new SolidColorBrush(U.C(0xFF1B1B1D));
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
            _composeBtn = U.Circle(U.GCompose, NewInterviewSetup, "New call  (Ctrl+N)", 14);
            _moreBtn = U.Btn("Btn.Ghost", U.Icon(U.GMore, 17, U.Blue), ShowMoreMenu, "More");
            _moreBtn.Padding = new Thickness(10, 8, 10, 8);
            _moreBtn.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(_composeBtn);
            right.Children.Add(_moreBtn);
            Grid.SetColumn(right, 2);
            topGrid.Children.Add(right);
            _top = new Border
            {
                Background = _topBrush,
                BorderBrush = U.B(0xFF2A2A2D),
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
            _cardBrush = new SolidColorBrush(U.C(0xFF161618));
            _cardGrid.Children.Add(_viewHost);
            _cardGrid.Children.Add(_toasts);
            _cardGrid.Children.Add(_modalLayer);
            _cardGrid.SizeChanged += delegate { _cardGrid.Clip = new RectangleGeometry(new Rect(0, 0, _cardGrid.ActualWidth, _cardGrid.ActualHeight), 20, 20); };
            _card = new Border
            {
                Background = _cardBrush,
                BorderBrush = U.B(0xFF28282B),
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
            _dockBrush = new SolidColorBrush(U.C(0xFF141416));
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
            var sep = new Border { Width = 1, Height = 28, Background = U.B(0xFF303034), Margin = new Thickness(8, 0, 12, 0) };
            _dockMore.Children.Add(sep);

            _dockAsk = new Grid();
            _dockAsk.ColumnDefinitions.Add(new ColumnDefinition());
            _dockAsk.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _ask = new TextBox { Style = U.Style("Text.Input"), Tag = "Ask anything", FontSize = 15, Padding = new Thickness(14, 9, 14, 9), Background = U.B(0xFF0D0D0F), BorderBrush = U.B(0xFF2A2A2E), VerticalAlignment = VerticalAlignment.Center };
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
            _navHome = NavButton(U.GMonitor, "Call", "Radio.Dock", () => NavTo("main"));
            _navWeb = NavButton(U.GGlobe, "Browser", "Radio.Dock", () => NavTo("browser"));
            _navSettings = NavButton(U.GPerson, "Settings", "Radio.DockAccent", () => NavTo("settings"));
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
                BorderBrush = U.B(0xFF2C2C30),
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
                Background = U.B(0xFF1B1B1E),
                BorderBrush = U.B(0xFF2E2E32),
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
            UpdateCardLayout();
        }

        /// <summary>Opens or closes the panel (title bar + card). Closed, only the dock capsule is left on screen.</summary>
        public void SetPanel(bool open)
        {
            _panelOpen = open;
            var vis = open ? Visibility.Visible : Visibility.Collapsed;
            _top.Visibility = vis;
            _card.Visibility = vis;
            _grip.Visibility = vis;
            if (open) U.Enter(_card, 12, 200);
            if (!open && _modalOpen) CloseModal();
            RefreshDock();
            UpdateCardLayout();
        }

        /// <summary>Dock buttons: open that view, or close the panel if it's already showing.</summary>
        private void NavTo(string target)
        {
            bool showing = _panelOpen && (target == "main" ? _view == "setup" || _view == "session" || _view == "history" : _view == target);
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

        private void RefreshTopBar()
        {
            switch (_view)
            {
                case "session": _title.Text = Ctl.Current != null ? Ctl.Current.Title : "Session"; break;
                case "settings": _title.Text = "Settings"; break;
                case "browser": _title.Text = "Browser"; break;
                case "history": _title.Text = "History"; break;
                default: _title.Text = "New call"; break;
            }
            // The compose ("new call") button only makes sense on the call screens.
            _composeBtn.Visibility = _view == "settings" || _view == "browser" ? Visibility.Collapsed : Visibility.Visible;
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
            _dock.HorizontalAlignment = ask ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            _dockMoreCol.Width = ask ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            UpdateDockExpansion(true);
        }

        /// <summary>The capsule is just the logo until you hover it (or the panel is open); then the rest slides out.</summary>
        private void UpdateDockExpansion(bool force)
        {
            bool expand = _panelOpen || _dockHover;
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
            bool overDock = Over(_dock, at, 6), overTop = Over(_top, at, 8), overGrip = Over(_grip, at, 6);

            // Short grace period so the capsule doesn't snap shut when the pointer grazes its edge.
            var now = DateTime.UtcNow;
            if (overDock) _dockHoverUntil = now.AddMilliseconds(350);
            bool hover = overDock || now < _dockHoverUntil;
            if (hover != _dockHover)
            {
                _dockHover = hover;
                UpdateDockExpansion(false);
            }

            // During a live call the title bar and the dock stay out of sight until you point at them.
            bool live = Ctl.Active;
            bool typing = _ask.IsKeyboardFocusWithin && _ask.Text.Length > 0;
            bool dockOn = !live || !_panelOpen || hover || typing;
            SetShown(_top, ref _topShown, !live || overTop || _moreMenuOpen || _modalOpen);
            SetShown(_dock, ref _dockShown, dockOn);
            SetShown(_grip, ref _gripShown, dockOn || overGrip);
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

        /// <summary>Sizes the content card: fills the window normally, hugs its header when the session is collapsed.</summary>
        public void UpdateCardLayout()
        {
            bool collapse = _panelOpen && _view == "session" && _session != null && _session.IsCollapsed;
            _rowCard.Height = collapse ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            _rowFill.Height = collapse ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }

        // =========================================================================================
        // Actions from the chrome
        // =========================================================================================

        private void OnLogoClick()
        {
            if (_panelOpen) SetPanel(false);
            else ShowView(MainView());
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
            var menu = new ContextMenu();
            if (Ctl.Current != null)
            {
                if (Ctl.Active && !Ctl.Paused) menu.Items.Add(Item("Pause call", delegate { Ctl.Pause(); }));
                else if (Ctl.Active && Ctl.Paused) menu.Items.Add(Item("Resume call", delegate { Ctl.Resume(); }));
                if (Ctl.Active) menu.Items.Add(Item("End call", delegate { Ctl.End(); }));
                else menu.Items.Add(Item("Continue call", ContinueSession));
                menu.Items.Add(Item("Rename session…", RenameSessionDialog));
                menu.Items.Add(Item("Export session…", ExportSession));
                menu.Items.Add(new Separator());
            }

            // Model (current one shown on the right) and the look of the window - the same controls as in Settings.
            var model = new MenuItem { Header = "Model", InputGestureText = ModelCatalog.Name(S.Model) };
            AddModelItems(model, null);
            menu.Items.Add(model);
            // What to listen to; switching mid-call reconnects the transcription right away.
            int audio = Math.Max(0, Array.IndexOf(AudioValues, S.AudioSource));
            var input = new MenuItem { Header = "Audio input", InputGestureText = AudioLabels[audio] };
            for (int i = 0; i < AudioValues.Length; i++)
            {
                var value = AudioValues[i];
                var mi = new MenuItem { Header = AudioLabels[i], IsCheckable = true, IsChecked = i == audio };
                mi.Click += delegate { SetAudioSource(value); };
                input.Items.Add(mi);
            }
            menu.Items.Add(input);
            menu.Items.Add(new Separator());
            bool looksChanged = false;
            menu.Items.Add(MenuSlider("Opacity", 30, 100, S.OpacityPct, n => { S.OpacityPct = n; looksChanged = true; ApplyAppearance(); SaveSettingsSoon(); }));
            menu.Items.Add(MenuSlider("Background", 20, 100, S.BackgroundPct, n => { S.BackgroundPct = n; looksChanged = true; ApplyAppearance(); SaveSettingsSoon(); }));
            menu.Items.Add(MenuSlider("Text size", 75, 200, S.TextSizePct, n => { S.TextSizePct = n; looksChanged = true; ApplyAppearance(); SaveSettingsSoon(); }));
            menu.Items.Add(new Separator());

            var focus = new MenuItem { Header = "Focus mode (latest answer only)", IsCheckable = true, IsChecked = S.FocusMode };
            focus.Click += delegate { S.FocusMode = focus.IsChecked; SaveSettingsSoon(); _session.RefreshQas(); };
            menu.Items.Add(focus);
            var trans = new MenuItem { Header = "Show live transcript", IsCheckable = true, IsChecked = S.ShowTranscript };
            trans.Click += delegate { SetShowTranscript(trans.IsChecked); };
            menu.Items.Add(trans);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("History", delegate { ShowView("history"); }));
            menu.Items.Add(Item("Browser", delegate { ShowView("browser"); }));
            menu.Items.Add(Item("Settings", delegate { ShowSettings(null); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Replay welcome tour", ShowWelcomeTour));
            menu.Items.Add(Item("Hide window", HideWithHint));
            menu.Items.Add(Item("Quit TheCloser", delegate { Close(); }));
            menu.Opened += delegate { _moreMenuOpen = true; };
            menu.Closed += delegate
            {
                _moreMenuOpen = false;
                if (looksChanged && _view == "settings") _settings.Refresh(); // its sliders show the old values
            };
            menu.PlacementTarget = _moreBtn;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private static MenuItem Item(string header, Action click)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += delegate { click(); };
            return mi;
        }

        /// <summary>A menu row with a label, a slider and the value, e.g. "Opacity ——o—— 90%". The menu stays open while you drag.</summary>
        private static MenuItem MenuSlider(string label, int min, int max, int value, Action<int> changed)
        {
            var g = new Grid { MinWidth = 270 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var sl = new Slider { Style = U.Style("Slider.Mini"), Minimum = min, Maximum = max, Value = value, SmallChange = 5, LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sl, 1);
            g.Children.Add(sl);
            var v = new TextBlock { Text = value + "%", FontFamily = U.Mono, FontSize = 13, Foreground = U.Text2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(v, 2);
            g.Children.Add(v);
            sl.ValueChanged += delegate
            {
                int n = (int)Math.Round(sl.Value);
                v.Text = n + "%";
                changed(n);
            };
            return new MenuItem { Style = U.Style("Menu.Slider"), Header = g };
        }

        // =========================================================================================
        // Model and language pickers (the ... menu, the setup screen and Settings share these)
        // =========================================================================================

        /// <summary>Adds the enabled models to a menu, the current one ticked, then "Manage models…".</summary>
        private void AddModelItems(ItemsControl parent, Action picked)
        {
            foreach (var slug in S.UseSubscription ? Billing.Models : S.EnabledModels)
            {
                var m = slug;
                var mi = new MenuItem
                {
                    Header = ModelCatalog.Name(m),
                    IsCheckable = true,
                    IsChecked = m == S.Model,
                    InputGestureText = ModelCatalog.Resolve(S, m).Provider == null ? (S.UseSubscription ? "check subscription" : "needs a key") : ""
                };
                mi.Click += delegate { SetModel(m); if (picked != null) picked(); };
                parent.Items.Add(mi);
            }
            parent.Items.Add(new Separator());
            parent.Items.Add(Item(S.UseSubscription ? "Subscription and models…" : "Manage models…", delegate { ShowSettings(S.UseSubscription ? "subscription" : "models"); }));
        }

        public void SetModel(string slug)
        {
            S.Model = slug;
            SaveSettingsSoon();
            OnKeysChanged();
            var route = ModelCatalog.Resolve(S, slug);
            if (route.Provider == null) Toast(route.Missing.Replace(" Click to open API keys.", ""), true);
            else Toast("Answers now use " + ModelCatalog.Name(slug) + ".", false);
        }

        /// <summary>Locks transcription to one language ("" = detect). Takes effect right away, even mid-call.</summary>
        public void SetSpeechLanguage(string code)
        {
            code = code ?? "";
            if (S.SpeechLanguage == code) return;
            S.SpeechLanguage = code;
            SaveSettingsSoon();
            Ctl.RestartSource();
            _setup.Refresh();
            Toast(code.Length == 0 ? "Transcribing any language." : "Transcribing " + SpeechLanguages.Name(code) + " only.", false);
        }

        private static readonly string[] AudioLabels = { "Both", "System audio", "Microphone" };
        private static readonly string[] AudioValues = { "Both", "System", "Mic" };

        /// <summary>What to listen to: Both (them + you), System audio (what the PC plays) or Microphone. Applies right away.</summary>
        public void SetAudioSource(string value)
        {
            if (S.AudioSource == value) return;
            S.AudioSource = value;
            SaveSettingsSoon();
            Ctl.RestartSource();
            _setup.Refresh();
        }

        /// <summary>Both / System audio / Microphone chips; `changed` runs after a pick (e.g. to update a hint).</summary>
        public FrameworkElement AudioSourcePicker(Action changed = null)
        {
            int current = Math.Max(0, Array.IndexOf(AudioValues, S.AudioSource));
            return U.Segmented(AudioLabels, AudioLabels[current], label =>
            {
                SetAudioSource(AudioValues[Array.IndexOf(AudioLabels, label)]);
                if (changed != null) changed();
            });
        }

        /// <summary>Pill showing the current model; opens the model menu.</summary>
        public Button ModelPicker()
        {
            Button b = null;
            b = U.Btn("Btn.Pill", null, delegate
            {
                var menu = new ContextMenu();
                AddModelItems(menu, delegate { b.Content = PickerLabel(U.Sparkle(15, null), ModelCatalog.Name(S.Model)); });
                OpenBelow(menu, b);
            }, "Model used for answers");
            b.Content = PickerLabel(U.Sparkle(15, null), ModelCatalog.Name(S.Model));
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
            }, "Language of the call");
            b.Content = PickerLabel(U.Icon(U.GGlobe, 15, null), SpeechLanguages.Name(S.SpeechLanguage));
            return b;
        }

        private static StackPanel PickerLabel(FrameworkElement icon, string text)
        {
            var sp = U.IconText(icon, text);
            var chevron = U.Icon(U.GDown, 10, U.Text2);
            chevron.Margin = new Thickness(10, 1, 0, 0);
            sp.Children.Add(chevron);
            return sp;
        }

        private static void OpenBelow(ContextMenu menu, UIElement target)
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
            if (ModelCatalog.Resolve(S, S.Model).Provider == null) { ShowSettings(S.UseSubscription ? "subscription" : "models"); return; }
            var session = previous ?? new Session { PromptId = S.PromptId };
            _reviewing = false;
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

        public void PickPreviousSession(SetupView setup)
        {
            var sessions = SessionStore.All();
            var list = new StackPanel();
            if (sessions.Count == 0)
                list.Children.Add(U.T("No saved sessions yet. Every call is saved automatically once it has content.", 14.5, U.Text2));
            foreach (var s in sessions)
            {
                var sess = s;
                var info = new StackPanel();
                info.Children.Add(new TextBlock { Text = sess.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = U.Text, TextTrimming = TextTrimming.CharacterEllipsis });
                var meta = sess.Updated.ToString("ddd, MMM d · h:mm tt") + "  ·  " + sess.Qas.Count + (sess.Qas.Count == 1 ? " answer" : " answers");
                info.Children.Add(new TextBlock { Text = meta, FontSize = 12.5, Foreground = U.Text3, Margin = new Thickness(0, 2, 0, 0) });
                var b = U.Btn("Btn.Ghost", info, delegate { setup.SetPrevious(sess); CloseModal(); }, "Use this session");
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Margin = new Thickness(0, 0, 0, 4);
                list.Children.Add(b);
            }
            var scroll = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
            var cancel = U.Btn("Btn.Pill", "Cancel", CloseModal);
            ShowModal(ModalShell("Continue a previous session", scroll, cancel));
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
            if (Ctl.Current == null) { ShowWindow(); Toast("Start a call first, then I can answer.", true); return; }
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
        }

        public void SaveSettingsSoon()
        {
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
            var instr = U.Input("Describe how TheCloser should respond during the call…", prompt != null ? prompt.Text : "", true);
            instr.Height = 200;
            var body = new StackPanel();
            body.Children.Add(Label2("Name"));
            body.Children.Add(name);
            body.Children.Add(Label2("Instructions"));
            body.Children.Add(instr);
            var cancel = U.Btn("Btn.Pill", "Cancel", CloseModal);
            var save = U.Btn("Btn.White", "Save prompt", delegate
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
            if (!Prompts.Delete(S, prompt.Id)) { Toast("Keep at least one prompt.", true); return; }
            SaveSettingsSoon();
            RefreshSetup();
            if (_view == "settings") _settings.Refresh();
            Toast("Deleted “" + prompt.Name + "”." + (Prompts.IsBuiltIn(prompt.Id) ? " Restore it from Settings > Prompts." : ""), false);
        }

        public void AddModelById(Action onAdded)
        {
            var input = U.Input("e.g. anthropic/claude-sonnet-5", "", false);
            input.Margin = new Thickness(0, 12, 0, 0);
            var body = new StackPanel();
            body.Children.Add(U.T("Paste an OpenRouter model ID. It's added to your model menu and selected.", 14, U.Text2));
            body.Children.Add(input);
            var cancel = U.Btn("Btn.Pill", "Cancel", CloseModal);
            var add = U.Btn("Btn.White", "Add model", delegate
            {
                var slug = ModelCatalog.NormalizeSlug(input.Text);
                if (slug.Length == 0) return;
                if (!S.EnabledModels.Contains(slug)) S.EnabledModels.Add(slug);
                S.Model = slug;
                SaveSettingsSoon();
                CloseModal();
                OnKeysChanged();
                if (onAdded != null) onAdded();
            });
            ShowModal(ModalShell("Add a model", body, cancel, add));
        }

        public void ShowWelcomeTour()
        {
            var body = new StackPanel();
            body.Children.Add(Bullet2("Choose your AI plan", "Open Settings > Subscription for Pro, or add your own provider keys in Models."));
            body.Children.Add(Bullet2("Set up your call", "Attach a reference file and notes, pick a prompt, then press Start."));
            body.Children.Add(Bullet2("Answers as you talk", "When the other person asks something, an answer streams into the card a second later."));
            body.Children.Add(Bullet2("Analyze the screen", "Press Ctrl+Shift+Enter to send a screenshot — a coding problem, a quiz, a slide."));
            body.Children.Add(Bullet2("Private by default", "The window is hidden from screen shares and recordings. Toggle it with the eye button."));
            var got = U.Btn("Btn.White", "Get started", delegate { S.OnboardingDone = true; SaveSettingsSoon(); CloseModal(); });
            ShowModal(ModalShell("Welcome to TheCloser", body, got));
        }

        // =========================================================================================
        // Appearance & bounds
        // =========================================================================================

        public void ApplyAppearance()
        {
            Opacity = Math.Max(0.3, S.OpacityPct / 100.0);
            byte a = (byte)Math.Round(255.0 * Math.Max(20, Math.Min(100, S.BackgroundPct)) / 100.0);
            SetBgAlpha(_topBrush, 0x1B1B1D, a);
            SetBgAlpha(_cardBrush, 0x161618, a);
            SetBgAlpha(_dockBrush, 0x141416, a);
            if (_session != null) _session.OnSettingsChanged();
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
                Background = U.B(0xFF161618),
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

        private static FrameworkElement Bullet2(string title, string desc)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(U.T(title, 15.5, U.Text, FontWeights.SemiBold));
            var d = U.T(desc, 13.5, U.Text2);
            d.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(d);
            return sp;
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

        private void OfferCall(string app)
        {
            if (Ctl.Active || !S.OfferOnCall) return;
            DateTime last;
            if (_callOffered.TryGetValue(app, out last) && (DateTime.UtcNow - last).TotalMinutes < 5) return;
            _callOffered[app] = DateTime.UtcNow;
            ShowWindow();
            ShowView("setup");
            Toast(app + " just started using your microphone. Press Start to have TheCloser listen in.", false);
        }

        private void TrySample()
        {
            ShowWindow();
            if (Ctl.Current == null) { Ctl.Open(new Session { Title = "Sample" }); _reviewing = true; }
            ShowView("session");
            Ctl.Answer(AnswerKind.Ask, "Give me a friendly one-paragraph sample answer, with a short bold headline and two bullet points, so I can see how TheCloser formats responses.", null);
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
            menu.Items.Add("New call", null, delegate { Dispatcher.Invoke((Action)delegate { ShowWindow(); NewInterviewSetup(); }); });
            menu.Items.Add("Answer now  (Ctrl+Enter)", null, delegate { Dispatcher.Invoke((Action)AnswerNow); });
            menu.Items.Add("Analyze screen  (Ctrl+Shift+Enter)", null, delegate { Dispatcher.Invoke((Action)CaptureAndAnswer); });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Try a sample question", null, delegate { Dispatcher.Invoke((Action)TrySample); });
            menu.Items.Add("Settings", null, delegate { Dispatcher.Invoke((Action)delegate { ShowWindow(); ShowSettings(null); }); });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Quit TheCloser", null, delegate { Dispatcher.Invoke((Action)delegate { Close(); }); });
            _tray.ContextMenuStrip = menu;
        }

        // =========================================================================================
        // Global hotkeys + window messages
        // =========================================================================================

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
            if (_hwnd == IntPtr.Zero) return;
            Register(HkToggle, Native.MOD_CONTROL | Native.MOD_ALT, VK_SPACE, true);
            if (IsVisible)
            {
                Register(HkAnswer, Native.MOD_CONTROL, VK_RETURN, true);
                Register(HkShot, Native.MOD_CONTROL | Native.MOD_SHIFT, VK_RETURN, true);
                for (int i = 0; i < 4; i++)
                {
                    Register(HkMove + i, Native.MOD_CONTROL | Native.MOD_ALT, Arrows[i], false);
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

        /// <summary>Fills the window with sample content (used by `--demo`), without any keys or listening.</summary>
        public void LoadDemo()
        {
            var s = new Session { Title = "Acme Pay — support call", TitleSetByUser = true };
            var now = DateTime.Now;
            s.Lines.Add(new SessionLine { Speaker = "Them", Text = "Hi, I think I was charged twice for my subscription this month.", Time = now });
            s.Lines.Add(new SessionLine { Speaker = "Me", Text = "I'm sorry about that — let me take a look at your account.", Time = now });
            s.Lines.Add(new SessionLine { Speaker = "Them", Text = "Can you refund the duplicate and make sure it doesn't happen again?", Time = now });
            s.Qas.Add(new QaItem
            {
                Question = "Can you refund the duplicate charge and prevent a repeat?",
                Kind = "Auto",
                Time = now,
                Model = S.Model,
                Answer = "**Yes — I can refund the duplicate charge today.**\n" +
                         "- I can see two charges on the same date; I'll reverse the extra one now (3–5 business days to land).\n" +
                         "- I'll switch the account to a single monthly invoice so it can't double-bill again.\n" +
                         "- Want me to email a confirmation once the refund is submitted?"
            });
            Ctl.Open(s);
            _reviewing = true;
            ShowView("session");
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
            SaveBounds();
            S.Save();
            if (_hwnd != IntPtr.Zero)
                foreach (var id in _registered.ToList()) Native.UnregisterHotKey(_hwnd, id);
            _registered.Clear();
            _tick.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            var app = Application.Current;
            if (app != null) app.Shutdown();
        }

        private bool _trayHintShown;
    }
}
