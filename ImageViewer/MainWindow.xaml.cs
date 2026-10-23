using ImageViewer.Properties;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Path = System.IO.Path;

namespace ImageViewer
{
    // Corrects WM_GETMINMAXINFO so that a WindowStyle=None window, when
    // maximized, exactly fills the monitor work area (no clipped taskbar
    // edge and no phantom margins).
    internal static class MinMaxInfoPatched
    {
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        };

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        class MONITORINFO
        {
            public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO lpmi);

        const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        public static void WMGetMinmaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var handle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (handle == IntPtr.Zero) return;

            var mi = new MONITORINFO();
            if (!GetMonitorInfo(handle, mi)) return;

            var info = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
            info.ptMaxPosition.X = mi.rcWork.left - mi.rcMonitor.left;
            info.ptMaxPosition.Y = mi.rcWork.top - mi.rcMonitor.top;
            info.ptMaxSize.X = mi.rcWork.right - mi.rcWork.left;
            info.ptMaxSize.Y = mi.rcWork.bottom - mi.rcWork.top;
            Marshal.StructureToPtr(info, lParam, true);
        }
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Single-instance handle so the custom layout panel (SmartFlowPanel)
        // can query the current toolbar/layout settings during measure.
        public static MainWindow Current { get; private set; }

        private const int MaxRecentPaths = 10;

        // "Fit to width" (book format) mode: every image is scaled so its
        // width equals the usable window width times this factor. Kept below
        // 1.0 so rounding drift can never push an image past the wrap
        // threshold in SmartFlowPanel (which would create overlapping rows).
        const double FitToWidthFactor = 0.985;

        // Scale slider drag throttle: ApplyScale() is O(all images), so we
        // coalesce rapid ValueChanged events into at most one relayout per
        // interval and always schedule a final apply for the last value.
        const double ScaleApplyIntervalMs = 60.0;
        DispatcherTimer _scaleApplyTimer = null;
        bool _scaleFinalApplyPending = false;

        // Native window messages needed for sane maximize behavior with a
        // custom (borderless) title bar.
        const int WM_GETMINMAXINFO = 0x0024;


        string fileTypes = "*.jpg; *.jpeg; *.jpe; *.jif; *.jfif; *.jfi; *.png; *.bmp; *.ico; *.gif; *.tiff";
        BackgroundWorker currentRecurser = null;
        



        public MainWindow()
        {
            InitializeComponent();

            Current = this;

            // Initialize events after settings are loaded
            SD_Scale.ValueChanged += SD_Scale_ValueChanged;
            CB_Path.KeyDown += CB_Path_KeyDown; // Handle Enter key in ComboBox


            string loadedPath = Settings.Default.LastUsedPath;
            if (!string.IsNullOrEmpty(loadedPath) && Directory.Exists(loadedPath))
            {
                CB_Path.Text = loadedPath;
                TextBox_TextChanged(CB_Path, null);
            }

            LoadSettings();

            // Keep the maximize/restore button glyph in sync with the window state
            StateChanged += (s, ev) => UpdateMaximizeGlyph();
            UpdateMaximizeGlyph();

            // Re-layout the images when the window is resized so that
            // fit-to-width / max-size clamping always tracks the window size.
            SizeChanged += MainWindow_SizeChanged;

            return;

        }


        private void LoadSettings()
        {
            // Load background color.
            // (The stored default value contains literal quotes - strip them.)
            var bgString = (Settings.Default.BackgroundColorARGB ?? "").Trim('"');
            if (!string.IsNullOrEmpty(bgString))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(bgString);
                    ApplyBackgroundColor(color);
                }
                catch (Exception ex)
                {
                    // Log or handle invalid color string if necessary
                    Console.WriteLine($"Error loading background color: {ex.Message}");
                }
            }

            // Load scale factor
            SD_Scale.Value = Math.Max(0.1, Math.Min(3.0, Settings.Default.ScaleFactor));

            // Load recurse setting (two-state toolbar toggle)
            TGB_Recurse.IsChecked = Settings.Default.RecurseSubdirectories;

            // Load layout settings
            TGB_FitToWidth.IsChecked = Settings.Default.FitToWidth;
            SB_Main.Visibility = Settings.Default.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;

            // Load recent paths into the ComboBox
            if (Settings.Default.RecentPaths != null)
            {
                foreach (string path in Settings.Default.RecentPaths)
                {
                    if (!string.IsNullOrEmpty(path) && !CB_Path.Items.Contains(path))
                    {
                        CB_Path.Items.Add(path);
                    }
                }
            }

            // Set the selected path (e.g., last used)
            if (!string.IsNullOrEmpty(Settings.Default.LastUsedPath))
            {
                // Check if the path is in the combobox items list
                int index = CB_Path.Items.IndexOf(Settings.Default.LastUsedPath);
                if (index >= 0)
                {
                    CB_Path.SelectedIndex = index; // Select the item if it exists
                }
                else
                {
                    // If path isn't in the list (e.g., was deleted), just set the text
                    CB_Path.Text = Settings.Default.LastUsedPath;
                }
                // Trigger path loading if needed, perhaps by calling TextBox_TextChanged logic
                // or ensuring the path is loaded after initialization if it's valid
                // For now, let's just set the text, and user can press Enter or change selection to load
            }
        }

        private void SaveSettings()
        {
            // Save background color (as ARGB hex string)
            var currentBgBrush = this.Background as SolidColorBrush;
            if (currentBgBrush != null)
            {
                Settings.Default.BackgroundColorARGB = currentBgBrush.Color.ToString();
            }

            // Save scale factor
            Settings.Default.ScaleFactor = Math.Max(0.1, Math.Min(3.0, SD_Scale.Value));

            // Save recurse setting (two-state toolbar toggle)
            Settings.Default.RecurseSubdirectories = TGB_Recurse.IsChecked ?? false;

            // Save layout settings
            Settings.Default.FitToWidth = TGB_FitToWidth.IsChecked ?? false;
            Settings.Default.ShowStatusBar = SB_Main.Visibility == Visibility.Visible;

            // Save recent paths
            var recentPaths = new StringCollection();
            // Add current path first if it's valid and not already the first item
            string currentPath = CB_Path.Text;
            if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath.TrimEnd('\\')))
            {
                if (recentPaths.Contains(currentPath))
                    recentPaths.Remove(currentPath);

                recentPaths.Insert(0, currentPath);
            }

            // Add the rest of the paths, up to MaxRecentPaths - 1
            for (int i = 0; i < CB_Path.Items.Count && recentPaths.Count < MaxRecentPaths; i++)
            {
                string item = CB_Path.Items[i] as string;
                if (item != null && item != currentPath && Directory.Exists(item)) // Ensure it's a string and not the current path
                {
                    if (!recentPaths.Contains(item) && !recentPaths.Contains(item.Trim('\\')) && !CB_Path.Items.Contains(item.Trim('\\')))
                        recentPaths.Add(item.Trim('\\'));
                }
            }
        
            Settings.Default.RecentPaths = recentPaths;

            // Save the last used path (the text in the combobox)
            Settings.Default.LastUsedPath = currentPath;

            Settings.Default.Save();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveSettings();
            base.OnClosing(e);
        }
        private void SD_Scale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Throttle slider drags: ApplyScale() touches every image and is
            // O(n), so running it for each intermediate value of a drag made
            // large folders stutter. Relayout at most once per interval and
            // always once more after the drag settles on the final value.
            if (_scaleApplyTimer == null)
            {
                _scaleApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(ScaleApplyIntervalMs)
                };
                _scaleApplyTimer.Tick += (s, ev) =>
                {
                    _scaleApplyTimer.Stop();
                    ApplyScale();
                    if (_scaleFinalApplyPending)
                    {
                        _scaleFinalApplyPending = false;
                        _scaleApplyTimer.Start();
                    }
                };
            }

            _scaleFinalApplyPending = true;
            if (!_scaleApplyTimer.IsEnabled)
            {
                // Immediate first apply keeps the UI responsive, then the
                // timer coalesces everything until the user stops dragging.
                _scaleFinalApplyPending = false;
                ApplyScale();
                _scaleApplyTimer.Start();
            }
        }

        // Re-applies the current layout settings (scale slider + fit-to-width
        // toggle) to every loaded image and forces a layout refresh.
        private void ApplyScale()
        {
            var ScaleFactor = Math.Max(0.1, Math.Min(3.0, SD_Scale.Value));
            var fitWidth = ComputeFitToWidthScale();
            var maxSizeFactor = MaxSizeFactor();
            var minFitPx = FitToWidthMinPixels();

            foreach (var item in LV.Items.OfType<LazyImage>())
            {
                // UpdateSize() only mutates Width/Height (and dirties layout)
                // when the computed size actually differs, so unchanged
                // images - e.g. all the small ones while fit-to-width is on -
                // cost almost nothing here.
                item.UpdateSize(ScaleFactor, fitWidth, maxSizeFactor, minFitPx);
            }

            // Force one relayout pass for the whole panel.
            var panel = SmartFlowPanel.Current;
            panel?.InvalidateLocations();

            LV.InvalidateMeasure();
            LV.InvalidateArrange();
        }

        void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Debounce: relayout once the user stopped resizing for a moment
            if (_sizeTimer == null)
            {
                _sizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _sizeTimer.Tick += (s, ev) => { _sizeTimer.Stop(); ApplyScale(); };
            }
            _sizeTimer.Stop();
            _sizeTimer.Start();
        }
        DispatcherTimer _sizeTimer = null;


        // --- New Methods for Features ---

        private void SD_Scale_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Reset scale to 1.0
            SD_Scale.Value = 1.0; // This will trigger SD_Scale_ValueChanged
        }

        private void SD_Scale_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Focus the slider so it receives keyboard input
            SD_Scale.Focus();
            // Show a prompt or just wait for keyboard input
            // For simplicity, we'll just focus and let the user type a number
            // A more robust solution might involve a small popup input dialog
            // For now, we'll handle a key combination like Ctrl+V to paste or just digits
            // However, handling specific key sequences directly on the slider might be tricky
            // A better approach might be a context menu or a specific key combination
            // For demonstration, let's assume the user will type a number and press Enter
            // This requires the slider to be focused and potentially handling KeyDown globally or via a specific mechanism
            // Let's use a simple approach: show a MessageBox asking for input
            // A more elegant solution would be a small input box overlay or context menu item.

            // Example using MessageBox (not ideal for continuous use):
            /*
            string input = Microsoft.VisualBasic.Interaction.InputBox("Enter scale factor (0.1 - 3.0):", "Set Scale", SD_Scale.Value.ToString("F2"));
            if (!string.IsNullOrEmpty(input))
            {
                if (double.TryParse(input, out double newScale))
                {
                    newScale = Math.Max(0.1, Math.Min(3.0, newScale));
                    SD_Scale.Value = newScale; // Triggers ValueChanged
                }
            }
            */
            // A better approach might be to show a small custom control or use a context menu.
            // For now, let's just focus the slider and assume a custom input method will be used.
            // Or, we could implement a key handler on the MainWindow or a specific mode.
            // For this example, we'll add a key handler to the slider itself.
            // Make sure the slider can receive focus.
            SD_Scale.Focusable = true; // Ensure it can be focused
            SD_Scale.Focus();
            // The actual input handling would need to be implemented in a KeyDown event.
            // See SD_Scale_PreviewKeyDown below.
        }

        private void SD_Scale_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle key input when slider is focused (e.g., after right-click)
            if (SD_Scale.IsFocused)
            {
                // Simple example: if user types a number and presses Enter
                // This is basic and might need refinement for a full user experience
                if (e.Key >= Key.D0 && e.Key <= Key.D9 || e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 || e.Key == Key.OemPeriod || e.Key == Key.Decimal)
                {
                    // Allow number input - typically handled by TextBox, but slider might need custom handling
                    // A more robust solution might involve a temporary TextBox overlay.
                    // For now, let's just capture the key if it's a number/dot and store it.
                    // A better way is to use a temporary TextBox or a custom control for input.
                    // Let's assume a temporary textbox approach isn't desired here directly on the slider.
                    // We could use a flag or a small input area.
                    // For this example, let's use a more standard approach like a context menu item
                    // that opens an input dialog, or handle Ctrl+K or similar.
                    // Let's add a simple check for a specific key combination like Ctrl+K to open an input dialog.
                    if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
                    {
                        string input = Microsoft.VisualBasic.Interaction.InputBox("Enter scale factor (0.1 - 3.0):", "Set Scale", SD_Scale.Value.ToString("F2"));
                        if (!string.IsNullOrEmpty(input))
                        {
                            if (double.TryParse(input, out double newScale))
                            {
                                newScale = Math.Max(0.1, Math.Min(3.0, newScale));
                                SD_Scale.Value = newScale; // Triggers ValueChanged
                            }
                        }
                        e.Handled = true; // Mark event as handled
                    }
                    // Alternatively, you could implement a more complex key sequence handler here
                    // if you want direct typing on the focused slider.
                }
                else if (e.Key == Key.Enter)
                {
                    // If enter was pressed after typing (requires more complex input buffering)
                    // Not implemented here for simplicity.
                }
            }
        }

        // Handle Enter key in the ComboBox to trigger path loading
        private void CB_Path_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // Trigger the path loading logic
                TextBox_TextChanged(CB_Path, null);
                oldPath = null;

            }
        }

        private void BtnBgColor_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new WinForms.ColorDialog();
            var current = (this.Background as SolidColorBrush)?.Color;
            if (current.HasValue)
                dialog.Color = System.Drawing.Color.FromArgb(
                    current.Value.A, current.Value.R, current.Value.G, current.Value.B);

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                ApplyBackgroundColor(Color.FromArgb(
                    dialog.Color.A, dialog.Color.R, dialog.Color.G, dialog.Color.B));
            }
        }

        // Applies a background color everywhere it is used and persists it.
        private void ApplyBackgroundColor(Color color)
        {
            var brush = new SolidColorBrush(color);
            this.Background = brush;
            BtnBgColor.Background = brush;
            LV.Background = brush;
            Settings.Default.BackgroundColorARGB = color.ToString();
        }


        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);

            SD_Scale.ValueChanged += SD_Scale_ValueChanged;

            this.InvalidateVisual();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => { })).Wait();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => { })).Wait();
        }

        // Hook the native window procedure so that maximizing a borderless
        // window covers the whole monitor (no clipped edges / off-screen margins).
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WndProc);
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                MinMaxInfoPatched.WMGetMinmaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        // --- Layout helpers -------------------------------------------------

        // Usable width of the image area (used by fit-to-width / max-size logic)
        double GetLayoutWidth()
        {
            var w = LV.ActualWidth;
            return w > 10 ? w : ActualWidth;
        }

        double GetLayoutHeight()
        {
            var h = LV.ActualHeight;
            return h > 10 ? h : ActualHeight;
        }

        // Whether the "fit images to window width" (book format) toggle is on.
        // Read by SmartFlowPanel during measure to keep image sizes in sync
        // with the real viewport width.
        public bool IsFitToWidthEnabled { get { return TGB_FitToWidth != null && (TGB_FitToWidth.IsChecked ?? false); } }

        // Fit-to-width ("book format"): scale every image so its width equals
        // the window width. Returns 0 to mean "not in fit-to-width mode".
        double ComputeFitToWidthScale()
        {
            if (!IsFitToWidthEnabled) return 0;
            var w = GetLayoutWidth();
            if (w < 10) return 0;
            return w * FitToWidthFactor;
        }

        // Minimum ORIGINAL pixel size for an image to be scaled up by
        // fit-to-width. Images below this keep their normal scale so icons
        // and other small images don't get blown up to the full window
        // width - they then flow in between the large "pages".
        double FitToWidthMinPixels()
        {
            return Math.Max(0, Settings.Default.FitToWidthMinImageWidth);
        }

        // Maximum allowed image dimension (from the settings dialog), used as
        // the pre-scale clamp for images larger than the window.
        double MaxSizeFactor()
        {
            return Math.Max(0.1, Math.Min(2.0, Settings.Default.MaxImageSizePercent / 100.0));
        }

        // --- New toolbar handlers -------------------------------------------

        // Folder-icon browse button: lets the user pick the image folder.
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new WinForms.FolderBrowserDialog
            {
                Description = "Select the folder containing your images:",
                ShowNewFolderButton = false
            };

            try
            {
                if (!string.IsNullOrEmpty(CB_Path.Text) && Directory.Exists(CB_Path.Text))
                    dlg.SelectedPath = CB_Path.Text;
            }
            catch { /* ignore invalid current text */ }

            if (dlg.ShowDialog() == WinForms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
            {
                CB_Path.Text = dlg.SelectedPath;
                oldPath = null;                       // force a reload
                TextBox_TextChanged(CB_Path, null);   // loads the images & remembers the path
                SaveSettings();                       // persist immediately so it is never lost
            }
        }

        // Gear button: pops up the simple settings dialog.
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var bg = (this.Background as SolidColorBrush)?.Color ?? Colors.AliceBlue;

            var dlg = new SettingsWindow(
                TGB_Recurse.IsChecked ?? false,
                TGB_FitToWidth.IsChecked ?? false,
                SB_Main.Visibility == Visibility.Visible,
                Settings.Default.MaxImageSizePercent,
                Settings.Default.FitToWidthMinImageWidth,
                SD_Scale.Value,
                bg)
            {
                Owner = this
            };

            if (dlg.ShowDialog() == true)
            {
                ApplyBackgroundColor(dlg.NewBackgroundColor);

                if (TGB_Recurse.IsChecked != (dlg.NewRecurse ? (bool?)true : (bool?)false))
                {
                    // Checked/Unchecked events fire CB_Recurse_Changed which reloads the images
                    TGB_Recurse.IsChecked = dlg.NewRecurse;
                }

                if (TGB_FitToWidth.IsChecked != (dlg.NewFitToWidth ? (bool?)true : (bool?)false))
                {
                    // Checked/Unchecked events fire TGB_FitToWidth_Changed which relayouts
                    TGB_FitToWidth.IsChecked = dlg.NewFitToWidth;
                }

                SB_Main.Visibility = dlg.NewShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
                Settings.Default.ShowStatusBar = dlg.NewShowStatusBar;

                Settings.Default.MaxImageSizePercent = dlg.NewMaxImageSizePercent;
                Settings.Default.FitToWidthMinImageWidth = dlg.NewFitToWidthMinImageWidth;

                var newScale = Math.Max(SD_Scale.Minimum, Math.Min(SD_Scale.Maximum, dlg.NewDefaultScale));
                if (Math.Abs(SD_Scale.Value - newScale) > 0.001)
                {
                    SD_Scale.Value = newScale;    // triggers ApplyScale()
                }
                else
                {
                    // The threshold only takes effect through a relayout, and
                    // the scale didn't change (so no ValueChanged fired).
                    ApplyScale();
                }

                Settings.Default.Save();
            }
        }

        // Two-state recursion toggle (replaces the old label + checkbox)
        private void CB_Recurse_Changed(object sender, RoutedEventArgs e)
        {
            oldPath = null;
            TextBox_TextChanged(CB_Path, null);
        }

        // Two-state "fit images to window width" toggle (book format)
        private void TGB_FitToWidth_Changed(object sender, RoutedEventArgs e)
        {
            Settings.Default.FitToWidth = TGB_FitToWidth.IsChecked ?? false;
            ApplyScale();
        }

        // --- Title-bar-less window handling ---------------------------------

        // Drag handler: grab any empty part of the toolbar to move the window.
        private void DragRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximizeRestore();
                return;
            }

            try { DragMove(); }
            catch (InvalidOperationException) { /* drag interrupted - ignore */ }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximizeRestore();
        }

        private void ToggleMaximizeRestore()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Segoe MDL2 glyphs: E922 = maximize, E923 = restore
        private void UpdateMaximizeGlyph()
        {
            if (BtnMaximize == null) return;
            BtnMaximize.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            BtnMaximize.ToolTip = WindowState == WindowState.Maximized ? "Restore down" : "Maximize";
        }



        Brush tbBrush = null;
        string oldPath = "";
        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

            var cb = sender as ComboBox;
            var t = cb.Text;
            if (tbBrush == null) tbBrush = cb.Foreground;

            if (!Directory.Exists(t.TrimEnd('\\')))
            {
                cb.Foreground = Brushes.Red;
                cb.FontWeight = FontWeights.Normal;
                cb.FontStyle = FontStyles.Normal;
                return;
            }
            if (oldPath == null || string.Compare(oldPath.TrimEnd('\\'), t.TrimEnd('\\')) == 0) return;

            cb.Foreground = tbBrush;
            oldPath = t;

            cb.Items.Remove(t); // Note when removing a value from the items it removes it from the Text element to since Text = Items[SelectedIndex]
            cb.Items.Insert(0, t);
            while (cb.Items.Count > MaxRecentPaths) cb.Items.RemoveAt(cb.Items.Count - 1);
            cb.SelectedIndex = 0;


            if (currentRecurser != null) currentRecurser.CancelAsync();



            currentRecurser = new BackgroundWorker() { WorkerSupportsCancellation = true };
            currentRecurser.DoWork += new DoWorkEventHandler(showImages);
            currentRecurser.RunWorkerAsync(t.TrimEnd('\\'));

            return;
        }

        // Handles when a user selects an item from the ComboBox dropdown
        private void CB_Path_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var cb = sender as ComboBox;
            var s = cb.SelectedItem as String;
            if (s == null) return;
            if (!Directory.Exists(s.TrimEnd('\\'))) return;            
            cb.SelectionChanged -= CB_Path_SelectionChanged;
            cb.Text = s;
            TextBox_TextChanged(sender, null);
            cb.SelectionChanged += CB_Path_SelectionChanged;
        }





        private void showImages(object sender, DoWorkEventArgs e)
        {
            LV.Dispatcher.BeginInvoke((Action)(() =>
            {
                LV.Items.Clear();
                CB_Path.FontWeight = FontWeights.Normal;
                CB_Path.FontStyle = FontStyles.Normal;
            }), null);

            GC.Collect();

            var dir = new DirectoryInfo(e.Argument as string);

            var recurse = (bool)LV.Dispatcher.Invoke((Func<object>)(() => { return (TGB_Recurse.IsChecked.HasValue) ? TGB_Recurse.IsChecked.Value : false; }), null);

            var numFilesRead = 0;
            var count = 0;
            var maxDelay = 250;
            foreach (var file in dir.EnumerateFiles("*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                if (e.Cancel) return;
                if (!fileTypes.Contains("*" + file.Extension.ToLower()) || file.Extension == "") continue;
                numFilesRead++;

                var ScaleFactor = 1.0;
                Dispatcher.Invoke(new Action(() =>
                {                    
                    ScaleFactor = Math.Max(0.1, Math.Min(3.0, SD_Scale.Value));
                }));



                double layoutW = 0, layoutH = 0, fitWidth = 0, maxSizeFactor = 1.0;
                Dispatcher.Invoke(new Action(() =>
                {
                    layoutW = GetLayoutWidth();
                    layoutH = GetLayoutHeight();
                    fitWidth = ComputeFitToWidthScale();
                    maxSizeFactor = MaxSizeFactor();
                }));

                LV.Dispatcher.Invoke((Action<string>)((path) => { var item = new LazyImage(path, LV, layoutW * maxSizeFactor, layoutH * maxSizeFactor);  LV.Items.Add(item); item.UpdateSize(ScaleFactor, fitWidth, maxSizeFactor); }), file.FullName);

                if (count++ > maxDelay)
                {
                    LV.Dispatcher.Invoke((Action<int>)((c) => { SB_Info1.Text = c.ToString(); }), numFilesRead);


                    Thread.Sleep(250);
                    count = 0;
                }


            }


            LV.Dispatcher.Invoke((Action)(() => { CB_Path.Foreground = Brushes.Green; CB_Path.FontWeight = FontWeights.Heavy; CB_Path.FontStyle = FontStyles.Oblique; SmartFlowPanel.Current.InvalidateLocations(); SB_Info1.Text = numFilesRead.ToString(); }), null);



            return;
        }

        private void BtnBgColor_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {

        }

        private void CB_Path_DropDownClosed(object sender, EventArgs e)
        {
            TextBox_TextChanged(CB_Path, null);
        }


    }

}
