using System;
using System.Runtime.InteropServices;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// Explicit framework aliases
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.Brush;

namespace SinhalaTypeBypasser
{
    public partial class MainWindow : Window
    {
        // ==========================================
        // CONSTANTS
        // ==========================================

        private const double ExpandedHeight = 76;
        private const double CollapsedHeight = 14;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static readonly TimeSpan InitialHideDelay =
            TimeSpan.FromSeconds(3);

        private static readonly TimeSpan MouseLeaveHideDelay =
            TimeSpan.FromMilliseconds(800);

        private static readonly WpfBrush ActiveBrush =
            CreateFrozenBrush(74, 222, 128);

        private static readonly WpfBrush InactiveBrush =
            CreateFrozenBrush(113, 113, 122);

        private static readonly InputStyle[] InputStyles =
        {
            InputStyle.ModernWijesekara,
            InputStyle.TraditionalWijesekara,
            InputStyle.PhoneticSinglish
        };

        private static readonly OutputFontFamily[] OutputFonts =
        {
            OutputFontFamily.DL,
            OutputFontFamily.FM,
            OutputFontFamily.ISI,
            OutputFontFamily.Unicode
        };

        // ==========================================
        // CORE COMPONENTS
        // ==========================================

        private readonly TranslationEngine _engine = new();

        private readonly DispatcherTimer _autoHideTimer;

        private GlobalKeyboardHook? _hook;

        // System tray
        private Forms.NotifyIcon? _notifyIcon;
        private Forms.ContextMenuStrip? _trayMenu;
        private Forms.ToolStripMenuItem? _trayStatusItem;

        // Application state
        private bool _isClosing;

        private bool IsTypingAssistOn =>
            BtnToggle.IsChecked == true;

        private bool IsDropDownOpen =>
            CbInputStyle.IsDropDownOpen ||
            CbOutputFont.IsDropDownOpen;

        // ==========================================
        // CONSTRUCTOR
        // ==========================================

        public MainWindow()
        {
            InitializeComponent();

            // Position toolbar at top-center
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
            Height = ExpandedHeight;

            // Initialize auto-hide timer
            _autoHideTimer = new DispatcherTimer
            {
                Interval = InitialHideDelay
            };

            _autoHideTimer.Tick += AutoHideTimer_Tick;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        // ==========================================
        // WINDOW INITIALIZATION
        // ==========================================

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Prevent toolbar from stealing application focus
            MakeNoActivate(
                new WindowInteropHelper(this).Handle
            );
        }

        private void MainWindow_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                // Load Sinhala conversion dictionary
                _engine.LoadMasterDictionary();

                // Synchronize initial typing state
                _engine.IsActive = IsTypingAssistOn;

                UpdateNotchStatus();

                // Initialize global keyboard hook
                _hook = new GlobalKeyboardHook(_engine);

                // F12 toggles typing assistance
                _hook.OnToggleRequested += OnHookToggleRequested;

                // Initialize Windows system tray
                InitializeTrayIcon();

                // Show toolbar initially
                ExpandToolbar();

                // Automatically minimize after 3 seconds
                ScheduleCollapse(InitialHideDelay);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(
                    ex.Message,
                    "Initialization Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        // ==========================================
        // TOOLBAR EXPAND / COLLAPSE
        // ==========================================

        private void ExpandToolbar()
        {
            if (_isClosing)
                return;

            _autoHideTimer.Stop();

            Height = ExpandedHeight;

            FullToolbar.Visibility = Visibility.Visible;
            NotchPanel.Visibility = Visibility.Collapsed;
        }

        private void CollapseToolbar()
        {
            if (_isClosing)
                return;

            // Keep toolbar open while dropdown is being used
            if (IsDropDownOpen)
                return;

            FullToolbar.Visibility = Visibility.Collapsed;
            NotchPanel.Visibility = Visibility.Visible;

            Height = CollapsedHeight;
        }

        private void ScheduleCollapse(TimeSpan delay)
        {
            _autoHideTimer.Stop();
            _autoHideTimer.Interval = delay;
            _autoHideTimer.Start();
        }

        // ==========================================
        // MOUSE HOVER EVENTS
        // ==========================================

        private void Window_MouseEnter(
            object sender,
            System.Windows.Input.MouseEventArgs e)
        {
            ExpandToolbar();
        }

        private void Window_MouseLeave(
            object sender,
            System.Windows.Input.MouseEventArgs e)
        {
            if (_isClosing)
                return;

            ScheduleCollapse(MouseLeaveHideDelay);
        }

        // ==========================================
        // AUTO HIDE TIMER
        // ==========================================

        private void AutoHideTimer_Tick(
            object? sender,
            EventArgs e)
        {
            if (_isClosing)
                return;

            // Don't hide while dropdown is open
            if (IsDropDownOpen)
                return;

            _autoHideTimer.Stop();

            if (!IsMouseOver)
                CollapseToolbar();
        }

        // ==========================================
        // NOTCH CLICK
        // ==========================================

        private void Notch_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            ExpandToolbar();

            e.Handled = true;
        }

        // ==========================================
        // TOOLBAR DRAGGING
        // ==========================================

        private void Toolbar_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            // Drag only from empty toolbar background
            if (e.OriginalSource != FullToolbar)
                return;

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Ignore interrupted drag operations
            }
        }

        // ==========================================
        // TYPING ASSIST TOGGLE
        // ==========================================

        private void ToggleTypingAssist()
        {
            BtnToggle.IsChecked = !IsTypingAssistOn;
        }

        private void OnHookToggleRequested()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_isClosing)
                    return;

                ToggleTypingAssist();
            });
        }

        private void BtnToggle_Changed(
            object sender,
            RoutedEventArgs e)
        {
            if (_engine == null || BtnToggle == null)
                return;

            bool active = IsTypingAssistOn;

            // Update translation engine
            _engine.IsActive = active;

            // Clear pending conversion buffer
            _engine.ResetBuffer();

            // Update minimized notch indicator
            UpdateNotchStatus();

            // Synchronize tray menu
            if (_trayStatusItem != null)
                _trayStatusItem.Checked = active;
        }

        private void UpdateNotchStatus()
        {
            if (NotchStatus == null || BtnToggle == null)
                return;

            NotchStatus.Fill = IsTypingAssistOn
                ? ActiveBrush
                : InactiveBrush;
        }

        // ==========================================
        // INPUT STYLE / OUTPUT FONT
        // ==========================================

        private void CbInputStyle_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_engine == null || CbInputStyle == null)
                return;

            _engine.CurrentInputStyle =
                PickOrDefault(
                    InputStyles,
                    CbInputStyle.SelectedIndex
                );

            _engine.ResetBuffer();
        }

        private void CbOutputFont_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_engine == null || CbOutputFont == null)
                return;

            _engine.CurrentOutputFont =
                PickOrDefault(
                    OutputFonts,
                    CbOutputFont.SelectedIndex
                );

            _engine.ResetBuffer();
        }

        private void ComboBox_DropDownClosed(
            object? sender,
            EventArgs e)
        {
            if (_isClosing)
                return;

            if (!IsMouseOver && !IsDropDownOpen)
            {
                ScheduleCollapse(MouseLeaveHideDelay);
            }
        }

        // ==========================================
        // SYSTEM TRAY
        // ==========================================

        private void InitializeTrayIcon()
        {
            // Show Toolbar
            var showItem = new Forms.ToolStripMenuItem(
                "Show Toolbar"
            );

            showItem.Click += (s, e) =>
                Dispatcher.Invoke(ExpandToolbar);

            // Typing Assist
            _trayStatusItem = new Forms.ToolStripMenuItem(
                "Typing Assist"
            )
            {
                Checked = IsTypingAssistOn
            };

            _trayStatusItem.Click += (s, e) =>
                Dispatcher.Invoke(ToggleTypingAssist);

            // Exit
            var exitItem = new Forms.ToolStripMenuItem(
                "Exit"
            );

            exitItem.Click += (s, e) =>
                Dispatcher.Invoke(Close);

            // Context menu
            _trayMenu = new Forms.ContextMenuStrip();

            _trayMenu.Items.Add(showItem);
            _trayMenu.Items.Add(_trayStatusItem);

            _trayMenu.Items.Add(
                new Forms.ToolStripSeparator()
            );

            _trayMenu.Items.Add(exitItem);

            // Notification icon
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "Sinhala Typing Assistant",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            // Double-click to reveal toolbar
            _notifyIcon.DoubleClick += (s, e) =>
                Dispatcher.Invoke(ExpandToolbar);
        }

        // ==========================================
        // CLOSE / CLEANUP
        // ==========================================

        private void BtnClose_Click(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }

        private void MainWindow_Closed(
            object? sender,
            EventArgs e)
        {
            _isClosing = true;

            // Stop timer
            _autoHideTimer.Stop();

            // Dispose keyboard hook
            if (_hook != null)
            {
                _hook.OnToggleRequested -= OnHookToggleRequested;

                _hook.Dispose();
                _hook = null;
            }

            // Remove system tray icon
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            // Dispose tray menu
            _trayMenu?.Dispose();
            _trayMenu = null;

            _trayStatusItem = null;
        }

        // ==========================================
        // HELPERS
        // ==========================================

        private static T PickOrDefault<T>(
            T[] values,
            int index)
        {
            return index >= 0 && index < values.Length
                ? values[index]
                : values[0];
        }

        private static WpfBrush CreateFrozenBrush(
            byte r,
            byte g,
            byte b)
        {
            var brush = new SolidColorBrush(
                WpfColor.FromRgb(r, g, b)
            );

            brush.Freeze();

            return brush;
        }

        // ==========================================
        // WINDOWS API
        // ==========================================

        private static void MakeNoActivate(IntPtr hwnd)
        {
            long style = GetWindowLongPtr(
                hwnd,
                GWL_EXSTYLE
            ).ToInt64();

            SetWindowLongPtr(
                hwnd,
                GWL_EXSTYLE,
                new IntPtr(style | WS_EX_NOACTIVATE)
            );
        }

        // 32-bit / 64-bit compatible Windows API

        private static IntPtr GetWindowLongPtr(
            IntPtr hWnd,
            int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(
            IntPtr hWnd,
            int nIndex,
            IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, value)
                : new IntPtr(
                    SetWindowLong32(
                        hWnd,
                        nIndex,
                        value.ToInt32()
                    )
                );
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(
            IntPtr hWnd,
            int nIndex
        );

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(
            IntPtr hWnd,
            int nIndex,
            int dwNewLong
        );

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(
            IntPtr hWnd,
            int nIndex
        );

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(
            IntPtr hWnd,
            int nIndex,
            IntPtr dwNewLong
        );
    }
}