/*
 * Switcheroo - The incremental-search task switcher for Windows.
 * http://www.switcheroo.io/
 * Copyright 2009, 2010 James Sulak
 * Copyright 2014 Regin Larsen
 * 
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Switcheroo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with Switcheroo.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedWinapi;
using ManagedWinapi.Windows;
using Switcheroo.Core;
using Switcheroo.Core.Matchers;
using Switcheroo.Properties;
using System.Runtime.InteropServices;
using System.Text;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Forms.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace Switcheroo
{
    public partial class MainWindow : Window
    {
        private WindowCloser _windowCloser;
        private List<AppWindowViewModel> _unfilteredWindowList;
        private ObservableCollection<AppWindowViewModel> _filteredWindowList;
        private NotifyIcon _notifyIcon;
        private HotKey _hotkey, _curHotkey;

        public static readonly RoutedUICommand CloseWindowCommand = new RoutedUICommand();
        public static readonly RoutedUICommand SwitchToWindowCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListDownCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListUpCommand = new RoutedUICommand();
        private OptionsWindow _optionsWindow;
        private AboutWindow _aboutWindow;
        private AltTabHook _altTabHook;
        private SystemWindow _foregroundWindow;
        private bool _altTabAutoSwitch;
        private DispatcherTimer _hotkeyRetryTimer;
        private DispatcherTimer _curHotkeyRetryTimer;
        private int _hotkeyRetryCount;
        private int _curHotkeyRetryCount;
        private const int MaxHotkeyRetries = 5;

        private static void Log(string message)
        {
            Trace.WriteLine("[MainWindow] " + message);
        }

        public MainWindow()
        {
            InitializeComponent();
            Log("Constructor: InitializeComponent done.");

            ApplyColors();

            SetUpKeyBindings();
            SetUpNotifyIcon();
            Log("NotifyIcon set up.");

            SetUpHotKey();
            SetUpCurHotKey();
            Log("Hotkey setup done.");

            SetUpAltTabHook();
            Log("AltTabHook set up.");

            CheckForUpdates();

            Opacity = 0;
            Log("Constructor complete.");
        }

        /// =================================

        #region Private Methods

        /// =================================

        private void SetUpKeyBindings()
        {
            // Enter and Esc bindings are not executed before the keys have been released.
            // This is done to prevent that the window being focused after the key presses
            // to get 'KeyUp' messages.

            KeyDown += (sender, args) =>
            {
                // Opacity is set to 0 right away so it appears that action has been taken right away...
                if (args.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    Opacity = 0;
                }
                else if (args.Key == Key.Escape)
                {
                    Opacity = 0;
                }
                else if (args.SystemKey == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    _altTabAutoSwitch = false;
                    tb.Text = "";
                    tb.IsEnabled = true;
                    tb.Focus();
                }
            };

            KeyUp += (sender, args) =>
            {
                // ... But only when the keys are release, the action is actually executed
                if (args.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    Switch();
                }
                else if (args.Key == Key.Escape)
                {
                    HideWindow();
                }
                else if (args.SystemKey == Key.LeftAlt && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    Switch();
                }
                else if (args.Key == Key.LeftAlt && _altTabAutoSwitch)
                {
                    Switch();
                }
            };
        }

        private void SetUpHotKey()
        {
            _hotkey = new HotKey();
            _hotkey.LoadSettings();
            Log("HotKey settings loaded. Name='" + _hotkey.strName + "'");

            Application.Current.Properties["hotkey"] = _hotkey;

            _hotkey.HotkeyPressed += hotkey_HotkeyPressed;
            _hotkeyRetryCount = 0;
            TryEnableHotKey(_hotkey, Settings.Default.EnableHotKey, isCurHotKey: false);
        }

        private void SetUpCurHotKey()
        {
            _curHotkey = new HotKey();
            _curHotkey.curLoadSettings();
            Log("CurHotKey settings loaded. Name='" + _curHotkey.strName + "'");

            Application.Current.Properties["curHotkey"] = _curHotkey;

            _curHotkey.HotkeyPressed += hotkey_HotkeyPressed;
            _curHotkeyRetryCount = 0;
            TryEnableHotKey(_curHotkey, Settings.Default.CurEnableHotKey, isCurHotKey: true);
        }

        private void TryEnableHotKey(HotKey hotkey, bool enable, bool isCurHotKey)
        {
            string label = isCurHotKey ? "CurHotKey" : "HotKey";
            try
            {
                hotkey.Enabled = enable;
                Log(label + " enabled (Enabled=" + enable + ").");
            }
            catch (HotkeyAlreadyInUseException)
            {
                if (isCurHotKey)
                {
                    if (_curHotkeyRetryCount < MaxHotkeyRetries)
                    {
                        _curHotkeyRetryCount++;
                        Log(label + " registration failed (attempt " + _curHotkeyRetryCount + "/" +
                            MaxHotkeyRetries + "). Will retry in 3 seconds.");
                        if (_curHotkeyRetryTimer == null)
                        {
                            _curHotkeyRetryTimer = new DispatcherTimer
                            {
                                Interval = TimeSpan.FromSeconds(3)
                            };
                            _curHotkeyRetryTimer.Tick += (s, e) =>
                            {
                                _curHotkeyRetryTimer.Stop();
                                TryEnableHotKey(_curHotkey, Settings.Default.CurEnableHotKey, isCurHotKey: true);
                            };
                        }
                        _curHotkeyRetryTimer.Start();
                    }
                    else
                    {
                        Log(label + " registration failed after " + MaxHotkeyRetries +
                            " attempts. Giving up and notifying the user.");
                        var boxText = "The current hotkey for activating Switcheroo is in use by another program." +
                                      Environment.NewLine +
                                      Environment.NewLine +
                                      "You can change the hotkey by right-clicking the Switcheroo icon in the system tray and choosing 'Options'.";
                        MessageBox.Show(boxText, "Hotkey already in use", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    if (_hotkeyRetryCount < MaxHotkeyRetries)
                    {
                        _hotkeyRetryCount++;
                        Log(label + " registration failed (attempt " + _hotkeyRetryCount + "/" +
                            MaxHotkeyRetries + "). Will retry in 3 seconds.");
                        if (_hotkeyRetryTimer == null)
                        {
                            _hotkeyRetryTimer = new DispatcherTimer
                            {
                                Interval = TimeSpan.FromSeconds(3)
                            };
                            _hotkeyRetryTimer.Tick += (s, e) =>
                            {
                                _hotkeyRetryTimer.Stop();
                                TryEnableHotKey(_hotkey, Settings.Default.EnableHotKey, isCurHotKey: false);
                            };
                        }
                        _hotkeyRetryTimer.Start();
                    }
                    else
                    {
                        Log(label + " registration failed after " + MaxHotkeyRetries +
                            " attempts. Giving up and notifying the user.");
                        var boxText = "The current hotkey for activating Switcheroo is in use by another program." +
                                      Environment.NewLine +
                                      Environment.NewLine +
                                      "You can change the hotkey by right-clicking the Switcheroo icon in the system tray and choosing 'Options'.";
                        MessageBox.Show(boxText, "Hotkey already in use", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void SetUpAltTabHook()
        {
            _altTabHook = new AltTabHook();
            _altTabHook.Pressed += AltTabPressed;
        }

        private void SetUpNotifyIcon()
        {
            var icon = Properties.Resources.icon;

            var runOnStartupMenuItem = new MenuItem("Run on Startup", (s, e) => RunOnStartup(s as MenuItem))
            {
                Checked = new AutoStart().IsEnabled
            };

            _notifyIcon = new NotifyIcon
            {
                Text = "Switcheroo",
                Icon = icon,
                Visible = true,
                ContextMenu = new System.Windows.Forms.ContextMenu(new[]
                {
                    new MenuItem("Options", (s, e) => Options()),
                    runOnStartupMenuItem,
                    new MenuItem("About", (s, e) => About()),
                    new MenuItem("Exit", (s, e) => Quit())
                })
            };
        }

        private static void RunOnStartup(MenuItem menuItem)
        {
            try
            {
                var autoStart = new AutoStart
                {
                    IsEnabled = !menuItem.Checked
                };
                menuItem.Checked = autoStart.IsEnabled;
            }
            catch (AutoStartException e)
            {
                MessageBox.Show(e.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void CheckForUpdates()
        {
            var currentVersion = Assembly.GetEntryAssembly().GetName().Version;
            if (currentVersion == new Version(0, 0, 0, 0))
            {
                return;
            }

            var timer = new DispatcherTimer();

            timer.Tick += async (sender, args) =>
            {
                timer.Stop();
                var latestVersion = await GetLatestVersion();
                if (latestVersion != null && latestVersion > currentVersion)
                {
                    var result = MessageBox.Show(
                        string.Format(
                            "Switcheroo v{0} is available (you have v{1}).\r\n\r\nDo you want to download it?",
                            latestVersion, currentVersion),
                        "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        Process.Start("https://github.com/kvakulo/Switcheroo/releases/latest");
                    }
                }
                else
                {
                    timer.Interval = new TimeSpan(24, 0, 0);
                    timer.Start();
                }
            };

            timer.Interval = new TimeSpan(0, 0, 0);
            timer.Start();
        }

        private static async Task<Version> GetLatestVersion()
        {
            try
            {
                var versionAsString =
                    await
                        new WebClient().DownloadStringTaskAsync(
                            "https://raw.github.com/kvakulo/Switcheroo/update/version.txt");
                Version newVersion;
                if (Version.TryParse(versionAsString, out newVersion))
                {
                    return newVersion;
                }
            }
            catch (WebException)
            {
            }
            return null;
        }

        /// <summary>
        /// Populates the window list with the current running windows.
        /// </summary>
        private void LoadData(InitialFocus focus)
        {
            ApplyColors();
            _unfilteredWindowList = new WindowFinder().GetWindows().Select(window => new AppWindowViewModel(window)).ToList();

            var firstWindow = _unfilteredWindowList.FirstOrDefault();

            var foregroundWindowMovedToBottom = false;
            
            // Move first window to the bottom of the list if it's related to the foreground window
            if (firstWindow != null && AreWindowsRelated(firstWindow.AppWindow, _foregroundWindow))
            {
                _unfilteredWindowList.RemoveAt(0);
                _unfilteredWindowList.Add(firstWindow);
                foregroundWindowMovedToBottom = true;
            }

            _filteredWindowList = new ObservableCollection<AppWindowViewModel>(_unfilteredWindowList);
            _windowCloser = new WindowCloser();

            foreach (var window in _unfilteredWindowList)
            {
                window.FormattedTitle = new XamlHighlighter().Highlight(new[] {new StringPart(window.AppWindow.Title)});
                window.FormattedProcessTitle =
                    new XamlHighlighter().Highlight(new[] {new StringPart(window.AppWindow.ProcessTitle)});
            }

            lb.DataContext = null;
            lb.DataContext = _filteredWindowList;

            FocusItemInList(focus, foregroundWindowMovedToBottom);

            tb.Clear();
            tb.Focus();
            CenterWindow();
            ScrollSelectedItemIntoView();
        }


        private ObservableCollection<AppWindowViewModel> LoadCurData(InitialFocus focus, string AWinPrcsName)
        {

            ApplyColors();
            _unfilteredWindowList = new WindowFinder().GetWindows().Select(window => new AppWindowViewModel(window)).ToList();


            var firstWindow = _unfilteredWindowList.FirstOrDefault();

            var foregroundWindowMovedToBottom = false;

            // Move first window to the bottom of the list if it's related to the foreground window
            if (firstWindow != null && AreWindowsRelated(firstWindow.AppWindow, _foregroundWindow))
            {
                _unfilteredWindowList.RemoveAt(0);
                _unfilteredWindowList.Add(firstWindow);
                foregroundWindowMovedToBottom = true;
            }
                       

            foreach (var window in _unfilteredWindowList.ToList())
            {
                if (window.ProcessTitle != AWinPrcsName)
                { 
                    _unfilteredWindowList.Remove(window);
                    continue;
                }
                window.FormattedTitle = new XamlHighlighter().Highlight(new[] { new StringPart(window.AppWindow.Title) });
                window.FormattedProcessTitle =
                    new XamlHighlighter().Highlight(new[] { new StringPart(window.AppWindow.ProcessTitle) });
            }
            _filteredWindowList = new ObservableCollection<AppWindowViewModel>(_unfilteredWindowList);
            _windowCloser = new WindowCloser();

            lb.DataContext = null;
            lb.DataContext = _filteredWindowList;
            int WinNum = _filteredWindowList.Count;
            if (WinNum > 1)
            {
                FocusItemInList(focus, foregroundWindowMovedToBottom);

                tb.Clear();
                tb.Focus();
                CenterWindow();
                ScrollSelectedItemIntoView();
            }
            return _filteredWindowList;
        }

        private static bool AreWindowsRelated(SystemWindow window1, SystemWindow window2)
        {
            return window1.HWnd == window2.HWnd || window1.Process.Id == window2.Process.Id;
        }

        private void FocusItemInList(InitialFocus focus, bool foregroundWindowMovedToBottom)
        {
            if (focus == InitialFocus.PreviousItem)
            {
                var previousItemIndex = lb.Items.Count - 1;
                if (foregroundWindowMovedToBottom)
                {
                    previousItemIndex--;
                }

                lb.SelectedIndex = previousItemIndex > 0 ? previousItemIndex : 0;
            }
            else
            {
                lb.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Place the Switcheroo window in the center of the screen
        /// </summary>
        private void CenterWindow()
        {
            // Reset height every time to ensure that resolution changes take effect
            Border.MaxHeight = SystemParameters.PrimaryScreenHeight;

            double width = Settings.Default.WindowWidth;
            Width = width > 0 ? width : 542;

            double fontSize = Settings.Default.FontSize;
            if (fontSize >= 8)
            {
                tb.FontSize = fontSize;
                lb.FontSize = fontSize;
            }

            double height = Settings.Default.WindowHeight;
            if (height > 0)
            {
                // Fixed height: the window list scrolls inside the popup
                SizeToContent = SizeToContent.Manual;
                Height = height;
            }
            else
            {
                // Auto height: the popup grows to fit its content
                SizeToContent = SizeToContent.Height;
                Height = double.NaN;
            }

            // Force a rendering before repositioning the window
            UpdateLayout();

            // Position the window in the center of the screen
            Left = (SystemParameters.PrimaryScreenWidth/2) - (ActualWidth/2);
            Top = (SystemParameters.PrimaryScreenHeight/2) - (ActualHeight/2);
        }

        private void ApplyColors()
        {
            var bgRaw = Settings.Default.SelectedBackground;
            var fgRaw = Settings.Default.SelectedForeground;
            var bg = ParseColor(bgRaw, Color.FromRgb(0x2F, 0x7C, 0xD6));
            var fg = ParseColor(fgRaw, Colors.White);

            Log("ApplyColors: SelectedBackground='" + bgRaw + "' -> " + bg
                + ", SelectedForeground='" + fgRaw + "' -> " + fg);

            var bgBrush = new SolidColorBrush(bg);
            bgBrush.Freeze();
            var fgBrush = new SolidColorBrush(fg);
            fgBrush.Freeze();

            Resources["SelectedBackgroundBrush"] = bgBrush;
            Resources["SelectedForegroundBrush"] = fgBrush;
            Log("ApplyColors: replaced SelectedBackgroundBrush/SelectedForegroundBrush (custom ListBoxItem template binds directly to them)");

            var titleConv = Resources["TitleColorConverter"] as SelectionAwareColorConverter;
            var procConv = Resources["ProcessColorConverter"] as SelectionAwareColorConverter;
            Log("ApplyColors: TitleColorConverter=" + (titleConv == null ? "NULL" : "ok")
                + ", ProcessColorConverter=" + (procConv == null ? "NULL" : "ok"));
            if (titleConv != null) titleConv.SelectedColor = fg;
            if (procConv != null) procConv.SelectedColor = fg;
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                Log("ParseColor: empty/null hex, using fallback " + fallback);
                return fallback;
            }
            try
            {
                if (ColorConverter.ConvertFromString(hex.Trim()) is Color color)
                {
                    return color;
                }
                Log("ParseColor: '" + hex + "' did not parse to a Color, using fallback " + fallback);
            }
            catch (Exception ex)
            {
                Log("ParseColor: invalid hex '" + hex + "' (" + ex.Message + "), using fallback " + fallback);
            }
            return fallback;
        }

        /// <summary>
        /// Switches the window associated with the selected item.
        /// </summary>
        private void Switch()
        {
            foreach (var item in lb.SelectedItems)
            {
                var win = (AppWindowViewModel)item;
                win.AppWindow.SwitchToLastVisibleActivePopup();
            }

            HideWindow();
        }

        private void Preview()
        {
            this.Topmost = true;
            foreach (var item in lb.SelectedItems)
            {
                var win = (AppWindowViewModel)item;
                win.AppWindow.SwitchToLastVisibleActivePopup();
            }
        }

        private void HideWindow()
        {
            if (_windowCloser != null)
            {
                _windowCloser.Dispose();
                _windowCloser = null;
            }

            _altTabAutoSwitch = false;
            Opacity = 0;
            Dispatcher.BeginInvoke(new Action(Hide), DispatcherPriority.Input);
        }

        #endregion

        /// =================================

        #region Right-click menu functions

        /// =================================
        /// <summary>
        /// Show Options dialog.
        /// </summary>
        private void Options()
        {
            if (_optionsWindow == null)
            {
                _optionsWindow = new OptionsWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                _optionsWindow.Closed += (sender, args) => _optionsWindow = null;
                _optionsWindow.ShowDialog();
            }
            else
            {
                _optionsWindow.Activate();
            }
        }

        /// <summary>
        /// Show About dialog.
        /// </summary>
        private void About()
        {
            if (_aboutWindow == null)
            {
                _aboutWindow = new AboutWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                _aboutWindow.Closed += (sender, args) => _aboutWindow = null;
                _aboutWindow.ShowDialog();
            }
            else
            {
                _aboutWindow.Activate();
            }
        }

        /// <summary>
        /// Quit Switcheroo
        /// </summary>
        private void Quit()
        {
            if (_hotkeyRetryTimer != null)
            {
                _hotkeyRetryTimer.Stop();
                _hotkeyRetryTimer = null;
            }
            if (_curHotkeyRetryTimer != null)
            {
                _curHotkeyRetryTimer.Stop();
                _curHotkeyRetryTimer = null;
            }
            _notifyIcon.Dispose();
            _notifyIcon = null;
            _hotkey.Dispose();
            _curHotkey.Dispose();
            Application.Current.Shutdown();
        }

        #endregion

        /// =================================

        #region Event Handlers

        /// =================================
        private void OnClose(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            HideWindow();
        }

        private void hotkey_HotkeyPressed(object sender, EventArgs e)
        {
            string shortcutName = (sender as Switcheroo.HotKey).strName;
            Log("hotkey_HotkeyPressed fired. Name='" + shortcutName +
                "', Visibility=" + Visibility + ", EnableHotKey=" + Settings.Default.EnableHotKey +
                ", CurEnableHotKey=" + Settings.Default.CurEnableHotKey);

            if (!Settings.Default.EnableHotKey && !Settings.Default.CurEnableHotKey)
            {
                return;
            }

            if (Visibility != Visibility.Visible)
            {
                tb.IsEnabled = true;

                _foregroundWindow = SystemWindow.ForegroundWindow;
                string _aWinPrcsName = _foregroundWindow.Process.ProcessName;
                var _aWinHWnd = _foregroundWindow.HWnd;
                if (shortcutName == "CurHotKey")
                {
                    if (!Settings.Default.CurEnableHotKey)
                    {
                        return;
                    }
                    else
                    {
                        ObservableCollection<AppWindowViewModel> WindowList = LoadCurData(InitialFocus.NextItem, _aWinPrcsName);
                        if (WindowList.Count == 1) return;
                        if (WindowList.Count == 2)
                        {
                            // directly switched to the other window
                            var win = WindowList[0];
                            if (WindowList[0].HWnd == _aWinHWnd)
                            {
                                win = WindowList[1];
                            }
                            win.AppWindow.SwitchToLastVisibleActivePopup();
                            return;
                        }
                    }
                }
                else
                {
                    if (!Settings.Default.EnableHotKey)
                    {
                        return;
                    }
                    else
                    {
                        LoadData(InitialFocus.NextItem);
                    }
                }
                Show();
                Activate();
                Keyboard.Focus(tb);
                Opacity = 1;
            }
            else
            {
                HideWindow();
            }
        }

        private void AltTabPressed(object sender, AltTabHookEventArgs e)
        {
            Log("AltTabPressed fired. CtrlDown=" + e.CtrlDown + ", ShiftDown=" + e.ShiftDown +
                ", AltTabHook=" + Settings.Default.AltTabHook);

            if (!Settings.Default.AltTabHook)
            {
                // Ignore Alt+Tab presses if the hook is not activated by the user
                return;
            }

            _foregroundWindow = SystemWindow.ForegroundWindow;

            if (_foregroundWindow.ClassName == "MultitaskingViewFrame")
            {
                // If Windows' task switcher is on the screen then don't do anything
                return;
            }

            e.Handled = true;

            if (Visibility != Visibility.Visible)
            {
                tb.IsEnabled = true;

                ActivateAndFocusMainWindow();

                Keyboard.Focus(tb);
                if (e.ShiftDown)
                {
                    LoadData(InitialFocus.PreviousItem);
                }
                else
                {
                    LoadData(InitialFocus.NextItem);
                }

                if (Settings.Default.AutoSwitch && !e.CtrlDown)
                {
                    _altTabAutoSwitch = true;
                    tb.IsEnabled = false;
                    tb.Text = "Press Alt + S to search";
                }

                Opacity = 1;
            }
            else
            {
                if (e.ShiftDown)
                {
                    PreviousItem();
                }
                else
                {
                    NextItem();
                }
            }
        }

        private void ActivateAndFocusMainWindow()
        {
            // What happens below looks a bit weird, but for Switcheroo to get focus when using the Alt+Tab hook,
            // it is needed to simulate an Alt keypress will bring Switcheroo to the foreground. Otherwise Switcheroo
            // will become the foreground window, but the previous window will retain focus, and receive keep getting
            // the keyboard input.
            // http://www.codeproject.com/Tips/76427/How-to-bring-window-to-top-with-SetForegroundWindo

            var thisWindowHandle = new WindowInteropHelper(this).Handle;
            var thisWindow = new AppWindow(thisWindowHandle);

            var altKey = new KeyboardKey(Keys.Alt);
            var altKeyPressed = false;

            // Press the Alt key if it is not already being pressed
            if ((altKey.AsyncState & 0x8000) == 0)
            {
                altKey.Press();
                altKeyPressed = true;
            }

            // Bring the Switcheroo window to the foreground
            Show();
            SystemWindow.ForegroundWindow = thisWindow;
            Activate();

            // Release the Alt key if it was pressed above
            if (altKeyPressed)
            {
                altKey.Release();
            }
        }

        private void TextChanged(object sender, TextChangedEventArgs args)
        {
            if (!tb.IsEnabled)
            {
                return;
            }

            var query = tb.Text;

            var context = new WindowFilterContext<AppWindowViewModel>
            {
                Windows = _unfilteredWindowList,
                ForegroundWindowProcessTitle = new AppWindow(_foregroundWindow.HWnd).ProcessTitle
            };

            var filterResults = new WindowFilterer().Filter(context, query).ToList();

            foreach (var filterResult in filterResults)
            {
                filterResult.AppWindow.FormattedTitle =
                    GetFormattedTitleFromBestResult(filterResult.WindowTitleMatchResults);
                filterResult.AppWindow.FormattedProcessTitle =
                    GetFormattedTitleFromBestResult(filterResult.ProcessTitleMatchResults);
            }

            _filteredWindowList = new ObservableCollection<AppWindowViewModel>(filterResults.Select(r => r.AppWindow));
            lb.DataContext = _filteredWindowList;
            if (lb.Items.Count > 0)
            {
                lb.SelectedItem = lb.Items[0];
            }
        }

        private static string GetFormattedTitleFromBestResult(IList<MatchResult> matchResults)
        {
            var bestResult = matchResults.FirstOrDefault(r => r.Matched) ?? matchResults.First();
            return new XamlHighlighter().Highlight(bestResult.StringParts);
        }

        private void OnEnterPressed(object sender, ExecutedRoutedEventArgs e)
        {
            Switch();
            e.Handled = true;
        }

        private void ListBoxItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Switch();
            e.Handled = true;
        }

        private async void CloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            var windows = lb.SelectedItems.Cast<AppWindowViewModel>().ToList();
            foreach (var win in windows)
            {
                bool isClosed = await _windowCloser.TryCloseAsync(win);
                if(isClosed)
                    RemoveWindow(win);
            }

            if (lb.Items.Count == 0)
                HideWindow();

            e.Handled = true;
        }

        private void RemoveWindow(AppWindowViewModel window)
        {
            int index = _filteredWindowList.IndexOf(window);
            if (index < 0)
                return;

            if (lb.SelectedIndex == index)
            {
                if (_filteredWindowList.Count > index + 1)
                    lb.SelectedIndex++;
                else
                {
                    if (index > 0)
                        lb.SelectedIndex--;
                }
            }

            _filteredWindowList.Remove(window);
            _unfilteredWindowList.Remove(window);
        }

        private void ScrollListUp(object sender, ExecutedRoutedEventArgs e)
        {
            PreviousItem();
            e.Handled = true;
        }

        private void PreviousItem()
        {
            if (lb.Items.Count > 0)
            {
                if (lb.SelectedIndex != 0)
                {
                    lb.SelectedIndex--;
                }
                else
                {
                    lb.SelectedIndex = lb.Items.Count - 1;
                }

                ScrollSelectedItemIntoView();
                Preview();
                //SwitchPreview();
            }
        }

        private void ScrollListDown(object sender, ExecutedRoutedEventArgs e)
        {
            NextItem();
            e.Handled = true;
        }

        private void NextItem()
        {
            if (lb.Items.Count > 0)
            {
                if (lb.SelectedIndex != lb.Items.Count - 1)
                {
                    lb.SelectedIndex++;
                }
                else
                {
                    lb.SelectedIndex = 0;
                }

                ScrollSelectedItemIntoView();
            }
        }

        private void ScrollSelectedItemIntoView()
        {
            var selectedItem = lb.SelectedItem;
            if (selectedItem != null)
            {
                lb.ScrollIntoView(selectedItem);
            }
        }

        private void MainWindow_OnLostFocus(object sender, EventArgs e)
        {
            HideWindow();
        }

        private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            DisableSystemMenu();
        }

        private void DisableSystemMenu()
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            var window = new SystemWindow(windowHandle);
            window.Style = window.Style & ~WindowStyleFlags.SYSMENU;
        }

        private void ShowHelpTextBlock_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var duration = new Duration(TimeSpan.FromSeconds(0.150));
            var newHeight = HelpPanel.Height > 0 ? 0 : +17;
            HelpPanel.BeginAnimation(HeightProperty, new DoubleAnimation(HelpPanel.Height, newHeight, duration));
        }

        #endregion

        private enum InitialFocus
        {
            NextItem,
            PreviousItem
        }
    }
}