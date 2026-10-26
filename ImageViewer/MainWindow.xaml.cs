using ImageViewer.Properties;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Utility;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Path = System.IO.Path;
using System.Windows.Controls.Primitives;

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
        // Safety margin below the raw layout width. Must EXCEED the vertical
        // scrollbar width (~17 DIU): with HorizontalScrollBarVisibility=Disabled
        // WPF reserves no gutter, so a page sized to exactly 100% of the extent
        // width plus a row containing an image taller than the viewport would
        // trigger the scrollbar appearing mid-layout -> extent shrinks ~17px ->
        // wrap test fails -> two window-width pages share one row and overlap
        // ("book view" chaos). 1.5% covers it for any sane window width.
        // BUG FIX (book view "works sometimes then breaks"): this used to be 1,
        // i.e. pages were sized to EXACTLY the full extent width while the panel
        // internally reserves a safety margin of 0.985 (SmartFlowPanel.
        // FitToWidthFraction). A page at exactly 100% width hits the wrap test
        // (lineWidth + child > avail) on the SECOND page instead of starting a
        // new row, so two window-width pages share one row and overlap - the
        // layout looked fine until some later relayout pass and then collapsed.
        // Keep the factor in sync with the panel fraction so both agree.
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


        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T target && target is ScrollBar sb && sb.Orientation == Orientation.Vertical)
                    return target;

                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        /// <summary>
        /// Strips the default ListViewItem chrome (16px L/R padding, border,
        /// MinHeight) so each item container's DesiredSize equals the LazyImage's
        /// exact size. Without this the row math used by scroll-by-image snapping
        /// is off by ~20px per row. Done in code because this project targets an
        /// older framework version whose XAML compiler rejects attached-property
        /// element syntax placed outside a target element (MC3015).
        /// </summary>
        private static Style CreateImageContainerStyle()
        {
            var s = new Style(typeof(ListViewItem));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            s.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
            s.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
            s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
            s.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            s.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch));
            s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            return s;
        }

        public MainWindow()
        {
            InitializeComponent();
            // Expose the ListView to the flow panel so LazyImage instances
            // created off-thread (batch loader passes host=null) can still find
            // the UI Dispatcher when deferring bitmap loads out of layout passes.
            SmartFlowPanel.Current.LVHost = LV;

            Current = this;

            // Strip item-container chrome programmatically (see method docs).
            LV.ItemContainerStyle = CreateImageContainerStyle();

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

            // Apply the remembered Normal/Maximized state after Show() has run.
            // Geometry was already restored in OnSourceInitialized; deferring
            // the state to Loaded is what makes it stick (see RestoreWindowState
            // docs) - WPF's own show-time handling of the XAML default used to
            // preempt restores done earlier and re-pick the primary monitor.
            Loaded += (s, ev) => ApplySavedWindowState();

            // Re-layout the images when the window is resized so that
            // fit-to-width / max-size clamping always tracks the window size.
            SizeChanged += MainWindow_SizeChanged;

            // "Scroll by image" mode: intercept the mouse wheel over the list
            // and jump whole rows instead of letting WPF scroll 3 lines/tick.
            // PreviewMouseWheel tunnels to us first, so e.Handled stops the
            // default pixel scrolling entirely (no double-scroll).
            LV.PreviewMouseWheel += LV_PreviewMouseWheel;

            // PageUp/PageDown/Home/End mirror the wheel's row stepping when
            // scroll-by-image is on (see LV_PreviewKeyDown).
            LV.PreviewKeyDown += LV_PreviewKeyDown;

            // Double-click an image to open it with the system default viewer.
            LV.MouseLeftButtonDown += LV_MouseLeftButtonDown;

            // File-system watcher for the current folder: when images are
            // added/deleted/renamed on disk we flag the refresh button red
            // (we never auto-reload - a full reload is expensive and would
            // throw away the lazy state / scroll position).
            Watcher.Created += FolderWatcher_Changed;
            Watcher.Deleted += FolderWatcher_Changed;
            Watcher.Renamed += FolderWatcher_Changed;
            Watcher.Error += (s, ev) => { try { Watcher.EnableRaisingEvents = true; } catch { }; };


            // Define a timer for a smooth fade/delay hide
            DispatcherTimer _scrollBarHideTimer = null;
            
            // Inside your constructor where you set up MouseMove:
            MouseMove += (s, e) =>
            {

                Point M = Mouse.GetPosition(this);
                var mainWindow = App.Current.MainWindow as MainWindow;
                if (mainWindow != null && mainWindow.SB_Mouse != null)
                {
                    mainWindow.SB_Mouse.Text = $"({(Int32)Math.Ceiling(M.X)}, {(Int32)Math.Ceiling(M.Y)})";
                }
                               

                var ScrollOwner = this.GetScrollViewer();
                if (ScrollOwner == null) return;
                
                                 
                // 1. Initialize the hide timer once
                if (_scrollBarHideTimer == null)
                {
                    _scrollBarHideTimer = new DispatcherTimer();
                    _scrollBarHideTimer.Interval = TimeSpan.FromMilliseconds(250); // Grace period before hiding
                    _scrollBarHideTimer.Tick += (sender, args) =>
                    {
                        _scrollBarHideTimer.Stop();
                        var scrollOwner = ScrollOwner ?? VisualHelper.TryFindParent<ScrollViewer>(this);
                        if (scrollOwner != null)
                        {
                            scrollOwner.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                        }
                    };
                }



                // 3. Handle Scrollbar Proximity
                var verticalScrollBar = FindVisualChild<ScrollBar>(ScrollOwner);
                double sliverWidth = (verticalScrollBar?.Width ?? 30)*1.35; // Slightly wider zone so it's easy to hit
                bool isNearRightEdge = M.X >= (this.ActualWidth - sliverWidth);
                var activeScrollOwner = ScrollOwner ?? VisualHelper.TryFindParent<ScrollViewer>(this);

                if (activeScrollOwner != null)
                {
                    if (isNearRightEdge)
                    {
                        
                        // Cancel any pending hide action because the mouse is in the zone
                        _scrollBarHideTimer.Stop();

                        // Reveal the scrollbar
                        if (activeScrollOwner.VerticalScrollBarVisibility != ScrollBarVisibility.Auto)
                        {
                            activeScrollOwner.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                        }
                    }
                    else
                    {
                        // If the scrollbar is visible and the hide timer isn't already running, start it
                        if (activeScrollOwner.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && !_scrollBarHideTimer.IsEnabled)
                        {
                            _scrollBarHideTimer.Start();
                        }
                    }
                }
            };


            return;

        }

        // --- Directory change detection --------------------------------------
        FileSystemWatcher Watcher = new FileSystemWatcher() { NotifyFilter = NotifyFilters.FileName };
        bool _pendingChanges = false;      // changes seen since last (re)load
        int _watchStamp = 0;               // guards against stale async snapshots

        void UpdateFolderWatch()
        {
            // Detach first so a failed re-attach can't leave a watcher on the old path.
            try { Watcher.EnableRaisingEvents = false; } catch { }
            
            try { Watcher.Path = ""; } catch { }

            var dir = oldPath;
            if (string.IsNullOrEmpty(dir)) return;
            string trimmed = dir.TrimEnd('\\');
            if (!Directory.Exists(trimmed)) return;

            ++_watchStamp;   // invalidate any in-flight bookkeeping from an older path

            bool recurse = TGB_Recurse.IsChecked ?? false;

            // Attaching the FileSystemWatcher itself is cheap (it just registers
            // with the OS) - do it synchronously on the UI thread. We do NOT
            // enumerate the directory here: for huge/network trees that would be
            // as slow as a full reload, defeating the point. The watcher's own
            // kernel events tell us when something actually changed.
            try
            {
                Watcher.Path = trimmed;
                Watcher.IncludeSubdirectories = recurse;
                Watcher.EnableRaisingEvents = true;
            }
            catch
            {
                // Some paths (e.g. certain network shares) can't be watched;
                // the refresh button simply stays hidden then.
            }
        }

        void FolderWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            // Ignore non-image files outright, and ignore events that only
            // touched metadata inside subfolders we don't care about.
            string ext = null;
            try { ext = Path.GetExtension(e.Name).ToLower(); } catch { }
            if (ext == null || !fileTypes.Contains(ext)) return;

            // Marshal to the UI thread once per event burst (the raw event may
            // fire dozens of times while Windows copies a folder).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_pendingChanges)
                {
                    _pendingChanges = true;
                    SetRefreshIndicator(true);
                }

                // Optional auto-reload: restart a quiet-period timer on every
                // event so we reload only after the disk activity settles down
                // (a folder copy fires hundreds of events; we want one reload).
                if (Settings.Default.AutoRefreshOnFolderChange)
                {
                    if (_autoRefreshTimer == null)
                    {
                        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                        _autoRefreshTimer.Tick += (ts, te) =>
                        {
                            _autoRefreshTimer.Stop();
                            if (_pendingChanges) BtnRefresh_Click(null, null);
                        };
                    }
                    _autoRefreshTimer.Stop();
                    _autoRefreshTimer.Start();
                }
            }), DispatcherPriority.Background);
        }
        DispatcherTimer _autoRefreshTimer;

        void SetRefreshIndicator(bool dirty)
        {
            BtnRefresh.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            RefreshIcon.Stroke = dirty ? Brushes.Red : new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1));
            BtnRefresh.ToolTip = dirty
                ? "Files were added/removed in this folder - click to reload"
                : "Reload the folder";
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _pendingChanges = false;
            SetRefreshIndicator(false);
            oldPath = null;                     // force TextBox_TextChanged to reload
            TextBox_TextChanged(CB_Path, null);
        }


        // Saved window state from the last run; consumed once by
        // RestoreWindowState() in SourceInitialized. -999 = "nothing saved".
        System.Windows.WindowState _savedWindowState = System.Windows.WindowState.Normal;


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

            RestoreWindowState();


            // Load the persistent "scroll by image" toolbar toggle
            TGB_ScrollByImage.IsChecked = Settings.Default.ScrollByImage;
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

            // Set the selected path (e.g., last used). NOTE: do NOT set
            // SelectedIndex here - on an editable ComboBox that rewrites Text
            // asynchronously and clobbers whatever the user typed next. Assign
            // .Text only; the recent-paths items above remain selectable from
            // the dropdown. (The ctor's early load, if LastUsedPath exists,
            // already populated oldPath so this assignment won't skip a reload.)
            if (!string.IsNullOrEmpty(Settings.Default.LastUsedPath))
                CB_Path.Text = Settings.Default.LastUsedPath;
        }

        // ---- Window geometry persistence -------------------------------------
        // "Set it and forget it": the window remembers where it was (and whether
        // it was maximized) between runs. Handy on multi-monitor setups, e.g.
        // keeping this app docked to a side monitor while Visual Studio owns
        // the center one - no OS/IDE setting can do that for you.
        private void RestoreWindowState()
        {
            try
            {
                double left = Settings.Default.WindowLeft;
                double top = Settings.Default.WindowTop;
                double width = Settings.Default.WindowWidth;
                double height = Settings.Default.WindowHeight;

                // NaN means "never saved yet" - keep the XAML defaults.
                if (!double.IsNaN(left) && !double.IsNaN(top) &&
                    !double.IsNaN(width) && !double.IsNaN(height) &&
                    width >= 100 && height >= 50)
                {
                    // Only apply if some part of the remembered rectangle is
                    // still visible: monitors come and go (docking stations,
                    // projectors), and a window at Left=3200 on a single-screen
                    // setup would be unreachable. If nothing intersects, we
                    // fall back to the OS default position instead.
                    var rc = new Rect(left, top, width, height);
                    if (IsRectOnSomeScreen(rc))
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = left; Top = top; Width = width; Height = height;
                    }
                }

                // Remember the saved state; it is applied in Loaded below.
                // Setting WindowState to Maximized from a constructor or even
                // SourceInitialized gets preempted by WPF's own show-time
                // maximization (which also re-picks the monitor), while
                // setting Normal there is overridden back to the XAML default.
                // By Loaded time, Show() has finished and our value sticks -
                // including restoring down onto the side monitor when the
                // window was last used in Normal state there.
                _savedWindowState = Settings.Default.WindowState;
            }
            catch (Exception ex)
            {
                // Geometry restore must never crash startup; log and continue.
                Console.WriteLine("Error restoring window state: " + ex.Message);
            }
        }

        // Applies the saved Normal/Maximized state once, after Show() has
        // completed (called from Loaded). -999 / 2 (minimized) are ignored:
        // we never restore minimized, and "nothing saved" keeps XAML defaults.
        private void ApplySavedWindowState()
        {
            try
            {
                if (_savedWindowState == System.Windows.WindowState.Normal ||
                    _savedWindowState == System.Windows.WindowState.Maximized)
                    WindowState = (System.Windows.WindowState)_savedWindowState;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error applying saved window state: " + ex.Message);
            }
            finally
            {
                _savedWindowState = System.Windows.WindowState.Normal; 
            }
        }

        // Conservative on-screen check: the remembered rectangle (WPF DIPs)
        // must intersect at least one monitor's working area. We compare in
        // virtual-desktop coordinates and then clamp with a generous margin,
        // which is DPI-approximate but sufficient to catch the common failure
        // mode - a window remembered on a monitor that has since been
        // unplugged (e.g. Left=3200 on a single 1920px screen).
        private static bool IsRectOnSomeScreen(Rect r)
        {
            var vd = WinForms.SystemInformation.VirtualScreen;
            const int margin = 500; // tolerate DPI scaling differences
            return r.Right > vd.Left - margin && r.Left < vd.Right + margin &&
                   r.Bottom > vd.Top - margin && r.Top < vd.Bottom + margin;
        }

        private void SaveWindowState()
        {
            try
            {
                // While maximized (or minimized), Left/Top/Width/Height report
                // the *current* (screen-filling) bounds. RestoreBounds gives
                // the normal-state rectangle we want to return to next run.
                var b = WindowState == System.Windows.WindowState.Normal
                        ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                if (b.Width < 100 || b.Height < 50) return; // ignore degenerate rects

                Settings.Default.WindowLeft = b.X;
                Settings.Default.WindowTop = b.Y;
                Settings.Default.WindowWidth = b.Width;
                Settings.Default.WindowHeight = b.Height;
                Settings.Default.WindowState = WindowState;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error saving window state: " + ex.Message);
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
            SaveWindowState(); // capture geometry before SaveSettings() persists
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
        private void ApplyScale(bool forceInvalidate = false)
        {
            var panel = SmartFlowPanel.Current;
            if (panel != null) panel.InvalidateMeasures = true;
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
                // Force a reload of the typed path even when it equals the
                // currently-loaded one (the old order - load first, then null
                // oldPath - left oldPath==null after Enter: the next real path
                // change was treated as "first load", skipped remembering, and
                // re-entrancy during the item churn could spawn a second
                // concurrent folder worker -> LV.Items race / crash).
                oldPath = null;
                TextBox_TextChanged(CB_Path, null);
                e.Handled = true;   // don't let the combo treat Enter as dropdown toggle
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
        public double FitToWidthMinPixels()
        {
            return Math.Max(0, Settings.Default.FitToWidthMinImageWidth);
        }

        // Maximum allowed image dimension (from the settings dialog), used as
        // the pre-scale clamp for images larger than the window.
        public double MaxSizeFactor()
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
                SD_Scale.Value,
                bg,
                Settings.Default.FitToWidthMinImageWidth,
                Settings.Default.ScrollUndershootPercent,
                Settings.Default.AutoRefreshOnFolderChange,
                Settings.Default.ScrollNotchRows,
                Settings.Default.OpenOnDoubleClick)
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
                // Scroll undershoot is read live by the wheel handler on every
                // notch, so persisting it here is all that's needed (no relayout).
                Settings.Default.ScrollUndershootPercent = dlg.NewScrollUndershootPercent;
                Settings.Default.AutoRefreshOnFolderChange = dlg.NewAutoRefreshOnFolderChange;
                // Rows-per-notch is read live by the wheel handler on every
                // notch, so persisting it here is all that's needed (no relayout).
                Settings.Default.ScrollNotchRows = dlg.NewScrollNotchRows;
                // Double-click-to-open is read live by LV_MouseLeftButtonDown,
                // so persisting it here is all that's needed.
                Settings.Default.OpenOnDoubleClick = dlg.NewOpenOnDoubleClick;

                // If auto-refresh was just enabled and changes are already pending,
                // reload now so the red button doesn't sit there misleadingly.
                if (dlg.NewAutoRefreshOnFolderChange && _pendingChanges)
                    BtnRefresh_Click(null, null);

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
            var on = TGB_FitToWidth.IsChecked ?? false;
            Settings.Default.FitToWidth = on;

            // In "book format" mode every image is sized from the window width
            // alone - the user scale slider must not multiply on top of that or
            // images spill past the wrap edge (overlaps). Snap the slider to 1.0
            // when activating so the layout math stays exact; restore the last
            // user scale when deactivating.
            if (on)
            {
                _scaleBeforeFitToWidth = SD_Scale.Value;   // remember, in case it != 1
                if (Math.Abs(SD_Scale.Value - 1.0) > 0.001)
                    SD_Scale.Value = 1.0;                  // triggers ApplyScale() via ValueChanged
            }
            else
            {
                if (Math.Abs(SD_Scale.Value - _scaleBeforeFitToWidth) > 0.001 &&
                    Math.Abs(_scaleBeforeFitToWidth - 1.0) > 0.001)
                    SD_Scale.Value = _scaleBeforeFitToWidth; // triggers ApplyScale()
            }            
            ApplyScale();
        }
        double _scaleBeforeFitToWidth = 1.0;

        // Two-state "scroll one image per wheel tick" toggle (persistent).
        private void TGB_ScrollByImage_Changed(object sender, RoutedEventArgs e)
        {

            Settings.Default.ScrollByImage = TGB_ScrollByImage.IsChecked ?? false;
            // Leaving scroll-by-image mode: drop any half-accumulated fractional
            // delta so re-enabling the mode doesn't fire a stale snap.
            _wheelAccum = 0;
        }

        // Fractional wheel-delta accumulator (touchpads / high-precision wheels).
        int _wheelAccum = 0;
        int _wheelNotches = 0;

        // --- Scroll-by-image -------------------------------------------------
        // When the toggle is on, one wheel notch moves one image ROW - but never
        // more than one viewport height. A row taller than the window (fit-to-
        // width "book pages") would otherwise jump its full height off-screen in
        // a single tick; instead such rows are paged: each notch scrolls exactly
        // the viewport height until the row's bottom edge passes the viewport
        // top, at which point the NEXT row snaps to the top and normal per-row
        // paging resumes. Because a previous scroll can leave a partial row at
        // the top, the first notch only snaps that sliver away (aligns to the
        // next row's top edge). SmartFlowPanel keeps every laid-out rectangle in
        // ElementLocations keyed by Y, so row lookup is cheap - no visual-tree
        // walking and nothing here touches lazy loading.
        void LV_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(TGB_ScrollByImage.IsChecked ?? false)) return;   // pixel mode: let WPF handle it
            var panel = SmartFlowPanel.Current;
            if (panel == null || e.Delta == 0) return;

            var sv = GetScrollViewer();
            if (sv == null) return;
            if (sv.ScrollableHeight <= 0) { e.Handled = true; return; }   // nothing to scroll at all

            bool down = e.Delta < 0;
            // High-precision devices (touchpads, free-spinning mouse wheels) send
            // |delta| far below the classic 120 notch. Treat a full notch as one
            // row/page step and accumulate smaller deltas so a trackpad swipe
            // pages through book views proportionally instead of firing a snap on
            // every sub-notch event.
            int mag = Math.Abs(e.Delta);
            // Rows advanced per full wheel notch (1-5, from the settings dialog).
            int perNotch = 1;
            try { perNotch = Settings.Default.ScrollNotchRows; } catch { }
            perNotch = Math.Max(1, Math.Min(5, perNotch));
            // Sign-tracked accumulator: flipping scroll direction resets it so a
            // leftover partial delta from the opposite swipe can't eat into the
            // next notch's worth of movement.
            if (mag >= 120)
            {
                _wheelAccum = 0;
                _wheelNotches = Math.Max(1, (int)Math.Round(mag / 120.0)) * perNotch;
            }
            else if (_wheelAccum == 0 || Math.Sign(_wheelAccum) == e.Delta)
            {
                _wheelAccum += e.Delta;
                _wheelNotches = 0;
                while (Math.Abs(_wheelAccum) >= 120)
                {
                    _wheelAccum -= Math.Sign(_wheelAccum) * 120;
                    ++_wheelNotches;
                }
                if (_wheelNotches == 0)
                {
                    e.Handled = true;   // consumed: suppress pixel scrolling but don't snap yet
                    return;
                }
            }
            else
            {
                _wheelAccum = e.Delta;  // direction flipped: restart accumulation cleanly
                e.Handled = true;
                return;
            }
            int notches = _wheelNotches;
            ScrollToRowStep(down ? notches : -notches);
            e.Handled = true;                                      // suppress default 3-lines-per-tick scrolling
        }

        // Shared row/page stepping used by both the mouse wheel and the
        // PageUp/PageDown/Home/End keys. A positive `signedNotches` scrolls down
        // that many notch steps, a negative value scrolls up. Honours the
        // rows-per-notch and scroll-undershoot ("scroll overlap") settings.
        //
        // This mirrors the exact per-notch geometry of the last known-good build
        // (commit 0215e123), restored because the intermediate refactor changed
        // the row-occupancy test from GetLastRowAbove(yTop + eps) to a start-only
        // lookup (GetRowAtOrAbove). With fit-to-width book view, consecutive
        // window-height pages share edges EXACTLY (page N+1's Y == page N's
        // Bottom), so the start-only lookup made the CURRENT page win whenever
        // yTop sat on its own top edge; combined with a paging gate that compared
        // against the full multi-notch step, every aligned notch then skipped
        // ahead by (rowsPerNotch * (100-overlap%))% of a viewport while ignoring
        // the overlap setting entirely - images' top-left corners no longer
        // landed at the viewport top-left plus the configured overlap.
        void ScrollToRowStep(int signedNotches)
        {
            var panel = SmartFlowPanel.Current;
            if (panel == null || signedNotches == 0) return;

            var sv = GetScrollViewer();
            if (sv == null) return;
            if (sv.ScrollableHeight <= 0) return;   // nothing to scroll at all

            // NOTE: row rectangles in SmartFlowPanel.ElementLocations are stored in
            // the panel's OWN coordinate space. The ScrollViewer's VerticalOffset
            // includes any fixed chrome ABOVE the panel inside the scroll content
            // (e.g. the ListView header row), so we derive the panel-space offset
            // from the panel's actual rendered position (TransformToVisual is
            // authoritative and self-correcting) and convert targets back with
            // chromeOffset before scrolling.
            //GeneralTransform gt = panel.TransformToVisual(sv);
            //double panelTopInSv = gt != null ? gt.Transform(new Point(0, 0)).Y : 0.0;
            var panelTopInSv = 0.0;

            double offset = sv.VerticalOffset - panelTopInSv;      // viewport top in PANEL space
            double chromeOffset = sv.VerticalOffset - offset;      // fixed chrome above panel
            double offsetOfEntry = offset;                         // remember entry position for the failsafe
            const double eps = 2.0;                                // slop for layout rounding
            bool down = signedNotches > 0;
            int notches = Math.Abs(signedNotches);
            double vpH = Math.Max(1.0, sv.ViewportHeight);         // app-window usable height

            // "Undershoot" / scroll-overlap setting (0-50 % of the viewport height):
            // each notch moves ONE viewport height MINUS this overlap, so a strip of
            // the previous screen stays visible AND so the next image's top-left
            // corner lands at the viewport top-left shifted up by this percentage.
            // Applied PER NOTCH (never multiplied into the comparison threshold),
            // matching the pre-regression behaviour.
            int usPct = 0;
            try { usPct = Settings.Default.ScrollUndershootPercent; } catch { }
            usPct = Math.Max(0, Math.Min(50, usPct));
            double pageStep = Math.Max(1.0, vpH * (1.0 - usPct / 100.0));

            // Snap-to-row logic: if the viewport top is cut through the middle of a
            // row (from a previous partial/pixel scroll or a resize), the first notch
            // only snaps that sliver away so the next whole row sits at the very top;
            // when already aligned, each notch moves exactly one row - clamped to one
            // viewport (minus overlap) height for oversized rows (paged scroll).
            // IMPORTANT: `offset` here is in PANEL coordinate space; every target
            // computed below is also panel-space and gets converted back to
            // ScrollViewer offset space via chromeOffset at the ScrollTo call.
            double target;
            if (down)
            {
                var cur = panel.GetLastRowAbove(offset + eps);     // row occupying the viewport top
                bool hasCur = !cur.IsEmpty || cur.Height > 0;
                bool rowOversized = hasCur && cur.Height > vpH + eps;

                if (!panel.HasRows)
                    target = offset + sv.ScrollableHeight;         // no layout data -> bottom (panel-space approx)
                else if (rowOversized && offset + pageStep < cur.Bottom - eps)
                {
                    // Tall row still extends below the viewport (whether we are
                    // aligned at its top or mid-way through it): page down by ONE
                    // (viewport - overlap) step, but never past the row's bottom
                    // edge - landing exactly there makes the NEXT notch snap to the
                    // following row's top cleanly. Multi-notch events simply repeat
                    // the same single-notch step (bounded by cur.Bottom) rather than
                    // multiplying the step, which is what keeps the overlap setting
                    // honoured and prevents skipping content inside tall pages.
                    for (int i = 0; i < notches; i++)
                    {
                        if (offset + pageStep < cur.Bottom - eps)
                            offset = Math.Min(offset + pageStep, cur.Bottom);
                        else { offset = cur.Bottom; break; }       // bottom reached: next iteration snaps to next row
                    }
                    target = offset;
                }
                else
                {
                    var r = panel.GetFirstRowBelow(offset + eps);  // first row starting below the top edge
                    if (r.IsEmpty && r.Height <= 0)
                        target = offset + sv.ScrollableHeight;     // no rows below -> bottom
                    else
                        target = r.Y;                              // snap that row to the viewport top
                }
            }
            else
            {
                var cur = panel.GetLastRowAbove(offset + eps);     // row occupying the viewport top
                bool hasCur = !cur.IsEmpty || cur.Height > 0;
                if (!panel.HasRows || !hasCur)
                    target = 0;                                    // nothing above -> top
                else if (offset - cur.Y > eps)
                {
                    // Mid-row. Two cases:
                    if (cur.Height > vpH + eps && offset - pageStep >= cur.Y + eps)
                        target = offset - pageStep;                // oversized row, still inside it: page up one (undershot) step
                    else
                        target = cur.Y;                            // ordinary sliver -> snap back to this row's top
                    // Multi-notch events: repeat the single-notch step.
                    for (int i = 1; i < notches && target > cur.Y; i++)
                    {
                        double t = target;
                        if (cur.Height > vpH + eps && t - pageStep >= cur.Y + eps)
                            target = t - pageStep;
                        else
                            target = cur.Y;
                    }
                }
                else
                {
                    // Aligned at cur's top. If the PREVIOUS row is oversized it
                    // cannot fit in the viewport anyway, so land near ITS bottom
                    // edge (one screenful of it fills the view) rather than its
                    // unreachable top; otherwise snap to its top (clean row step).
                    var prev = panel.GetLastRowAbove(cur.Y - eps);
                    if (prev.IsEmpty && prev.Height <= 0)
                        target = 0;                                // nothing above -> top
                    else if (prev.Height > vpH + eps)
                        target = Math.Max(0.0, prev.Bottom - pageStep);
                    else
                        target = prev.Y;
                }
            }

            // Convert panel-space target back to ScrollViewer offset space and clamp:
            // never past the ends, never negative.
            double svTarget = target + chromeOffset;
            if (svTarget < 0) svTarget = 0;
            if (svTarget > sv.ScrollableHeight) svTarget = sv.ScrollableHeight;

            sv.ScrollToVerticalOffset(svTarget);

            // Failsafe: stale layout right after resize/scale could compute target ==
            // current; fall back to plain line scrolling for THIS tick so the wheel can
            // never feel dead (next tick retries once MeasureOverride has re-run).
            if (Math.Abs(sv.VerticalOffset - svTarget) < 0.5 && Math.Abs(target - offsetOfEntry) >= 0.5)
            {
                if (down) sv.LineDown();
                else sv.LineUp();
            }
        }

        // PageUp/PageDown/Home/End in scroll-by-image mode: step whole rows
        // exactly like the wheel does, instead of WPF's default viewport jump.
        void LV_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!(TGB_ScrollByImage.IsChecked ?? false)) return;   // pixel mode: let WPF handle keys
            if (Keyboard.Modifiers != ModifierKeys.None) return;   // leave Ctrl/Alt combos alone

            var sv = GetScrollViewer();
            if (sv == null || sv.ScrollableHeight <= 0) return;

            int perNotch = 1;
            try { perNotch = Settings.Default.ScrollNotchRows; } catch { }
            perNotch = Math.Max(1, Math.Min(5, perNotch));

            switch (e.Key)
            {
                case Key.Home: sv.ScrollToVerticalOffset(0); break;
                case Key.End: sv.ScrollToVerticalOffset(sv.ScrollableHeight); break;
                case Key.F:
                    // Book-view convenience: toggle fit-to-width without reaching
                    // for the mouse while paging through scans/comics.
                    TGB_FitToWidth.IsChecked = !(TGB_FitToWidth.IsChecked ?? false);
                    break;
                default: return;                                  // let other keys bubble on
            }
            e.Handled = true;
        }

        // Double-click an image to open it with the OS default viewer/editor.
        // Wired unconditionally in the constructor but gated by the
        // OpenOnDoubleClick setting (default on) so single/drag behaviour is
        // untouched when the user opts out.
        private void LV_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            bool enabled = false;
            try { enabled = Settings.Default.OpenOnDoubleClick; } catch { }
            if (!enabled) return;

            // Hit-test against the laid-out rectangles instead of walking the
            // visual tree: cheap, exact, and reuses the same ElementLocations
            // data the scroll-by-image logic already relies on.
            var panel = SmartFlowPanel.Current;
            if (panel == null) return;
            var path = panel.GetImagePathAtPoint(e.GetPosition(panel));
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            e.Handled = true;   // don't start a drag/selection change under the dbl-click
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                WinForms.MessageBox.Show("Could not open image:\n" + ex.Message,
                    "ImageViewer", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
            }
        }

        ScrollViewer _svCache;
        ScrollViewer GetScrollViewer()
        {
            // The ListView's internal ScrollViewer is stable once templated;
            // cache it instead of walking the tree on every wheel tick.
            if (_svCache != null) return _svCache;
            _svCache = FindScrollViewer(LV);
            return _svCache;
        }

        static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var sv = child as ScrollViewer;
                if (sv != null) return sv;
                sv = FindScrollViewer(child);
                if (sv != null) return sv;
            }
            return null;
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
            if (cb == null) return;
            var t = cb.Text;
            if (tbBrush == null) tbBrush = cb.Foreground;

            if (!Directory.Exists(t.TrimEnd('\\')))
            {
                cb.Foreground = Brushes.Red;
                cb.FontWeight = FontWeights.Normal;
                cb.FontStyle = FontStyles.Normal;
                return;
            }

            cb.Foreground = tbBrush;
            cb.FontWeight = FontWeights.Normal;
            cb.FontStyle = FontStyles.Normal;
            

            // Set oldPath BEFORE touching the items collection: Items.Remove /
            // Items.Insert / SelectedIndex=0 mutate ComboBox.Text and re-raise
            // TextChanged on an editable ComboBox. With the old order the
            // re-entrant call saw oldPath==null ("first load" guard) and kicked
            // off a SECOND BackgroundWorker enumerating the same folder - two
            // workers racing Clear/Add on LV.Items (the crashes), and the churn
            // around Remove/Insert is also why typed paths sometimes vanished
            // from the recent list instead of being remembered.
            oldPath = t;
            //SaveSettings();
            // New folder => re-aim the file-system watcher and clear any stale
            // "files changed" indicator from the previous folder.
            _pendingChanges = false;
            SetRefreshIndicator(false);
            UpdateFolderWatch();

            if (!cb.Items.Contains(t))
                cb.Items.Insert(0, t);
            else if (cb.Items.IndexOf(t) != 0)
            {
                cb.Items.Remove(t);
                cb.Items.Insert(0, t);
            }
            while (cb.Items.Count > MaxRecentPaths) cb.Items.RemoveAt(cb.Items.Count - 1);
            cb.SelectedIndex = 0;
            cb.Text = t;    // restore exactly what the user typed


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
            // Clear the ListView on the UI thread first
            LV.Dispatcher.Invoke((Action)(() =>
            {
                LV.Items.Clear();
                CB_Path.FontWeight = FontWeights.Normal;
                CB_Path.FontStyle = FontStyles.Normal;
            }));

            GC.Collect();

            var dir = new DirectoryInfo(e.Argument as string);
            var recurse = (bool)LV.Dispatcher.Invoke((Func<bool>)(() => TGB_Recurse.IsChecked ?? false));

            var pathBatch = new List<string>();
            int numFilesRead = 0;

            // Shared bounded backlog across ALL batches of this load. Without it,
            // the worker would race ahead of the UI and queue thousands of
            // pending LazyImage graphs at once (memory spike + the UI thread
            // still choking on the sheer volume of queued adds). The semaphore is
            // acquired inside ProcessBatchOnUIThread's background stage BEFORE it
            // hands a batch to the dispatcher, and released when the UI has done
            // its (tiny, IO-free) add pass - so at most PendingBatchLimit batches
            // are ever in flight and the enumeration pauses automatically while
            // the UI catches up.
            var pendingBatches = new SemaphoreSlim(PendingBatchLimit, PendingBatchLimit);

            // Snapshot the layout parameters ONCE for the whole folder load.
            // Previously every batch read these DPs/settings from the UI thread;
            // with hundreds of batches that was hundreds of needless dispatcher
            // round-trips competing with rendering. The values only change through
            // user interaction, which cannot happen mid-batch anyway.
            var layout = (double[])LV.Dispatcher.Invoke(() => new double[]
            {
                Math.Max(0.1, Math.Min(3.0, SD_Scale.Value)),
                GetLayoutWidth(),
                GetLayoutHeight(),
                ComputeFitToWidthScale(),
                MaxSizeFactor(),
                FitToWidthMinPixels(),
            });

            foreach (var file in dir.EnumerateFiles("*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                if (e.Cancel) return;
                string ext = file.Extension?.ToLower();
                if (string.IsNullOrEmpty(ext) || !fileTypes.Contains("*" + ext)) continue;

                numFilesRead++;
                pathBatch.Add(file.FullName);

                // When a batch reaches 100 paths, hand it to the async pipeline
                if (pathBatch.Count >= 100)
                {
                    var chunk = pathBatch;
                    pathBatch = new List<string>(); // Start a new batch
                    ProcessBatchOnUIThread(chunk, numFilesRead, pendingBatches, layout);
                }
            }

            // Flush any remaining paths in the final batch
            if (pathBatch.Count > 0)
            {
                ProcessBatchOnUIThread(pathBatch, numFilesRead, pendingBatches, layout);
            }

            // Wait for every queued batch to finish being added before we declare
            // the load complete (the status-bar finalize below must not beat the
            // last batches to the dispatcher queue).
            for (int i = 0; i < PendingBatchLimit; i++) pendingBatches.Wait();

            // Finalize on the UI thread
            LV.Dispatcher.Invoke(() =>
            {
                SB_NumFilesRead.Text = numFilesRead.ToString();
                SmartFlowPanel.Current?.InvalidateLocations();

                CB_Path.Foreground = Brushes.Green;
                CB_Path.FontWeight = FontWeights.Heavy;
                CB_Path.FontStyle = FontStyles.Oblique;
            });
        }

        // PERF REGRESSION FIX (the "not actually lazy / 6k images load everything"
        // complaint): this used to be a synchronous Dispatcher.Invoke that built
        // every LazyImage ON THE UI THREAD. Each LazyImage constructor opens the
        // file and reads its image header, so a 6k-image folder meant 6k
        // synchronous IO+parse operations blocking the UI inside each batch ->
        // the app stalled, and afterwards WPF still had to measure/arrange all
        // 6k visual children even though only ~20 are ever visible.
        //
        // New pipeline:
        //   1) The BACKGROUND worker builds the LazyImages itself. That is safe:
        //      the constructor only creates Freezable decoders (now Freeze()'d)
        //      and plain fields - no UI-affinity state - and UpdateSize only
        //      touches DependencyProperties on elements that are not yet parented
        //      or bound, which any creating thread may do.
        //   2) Items are handed to the UI thread with BeginInvoke at Background
        //      priority (non-blocking). While the user scrolls or interacts, the
        //      dispatcher stays responsive; batches drain when idle. This is the
        //      piece the old Invoke could never deliver: Invoke ran the whole
        //      batch (IO included) as one uninterruptible UI work item.
        //   3) A bounded backlog keeps memory sane: we never let more than
        //      PendingBatchLimit*batchSize items queue up ahead of the UI.
        //   4) Fallback for any image whose header read failed off-thread
        //      (ImageWidth == 0): re-run the read on a pool thread and patch the
        //      real size in via ReloadDimensionsAsync once it arrives.
        public const int PendingBatchLimit = 8;   // max batches queued ahead of the UI thread

        private void ProcessBatchOnUIThread(List<string> filePaths, int totalFilesSoFar, SemaphoreSlim pendingBatches, double[] layout)
        {
            // ---- background stage: construct + size everything off the UI thread ----
            var ui = LV.Dispatcher;

            Task.Run(() =>
            {
                double scaleFactor   = layout[0];
                double layoutW       = layout[1];
                double layoutH       = layout[2];
                double fitWidth      = layout[3];
                double maxSizeFactor = layout[4];
                double minFitPx      = layout[5];

                var items = new List<LazyImage>(filePaths.Count);
                var needDims = new List<LazyImage>();

                foreach (var path in filePaths)
                {
                    // Safe off-thread: constructor reads only the image header
                    // (BitmapDecoder.Create with DelayCreation - no pixel decode)
                    // and UpdateSize touches DPs on an unparented element.
                    var item = new LazyImage(path, null, layoutW * maxSizeFactor, layoutH * maxSizeFactor);
                    if (item.ImageWidth == 0) needDims.Add(item);   // header read failed
                    else item.UpdateSize(scaleFactor, fitWidth, maxSizeFactor, minFitPx);
                    items.Add(item);
                }

                // ---- UI stage: pure add pass, no IO, non-blocking handoff ----
                pendingBatches.Wait();
                ui.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    foreach (var item in items)
                    {
                        item.LVHost = LV;   // dispatcher source for deferred LoadImage
                        LV.Items.Add(item);
                    }

                    SB_NumFilesRead.Text = totalFilesSoFar.ToString();

                    // Tell the flow panel new children exist so it relayouts once per
                    // batch instead of stalling until something external dirties it.
                    SmartFlowPanel.Current?.NotifyItemsChanged();

                    pendingBatches.Release();

                    // Patch placeholder-sized images without blocking the UI.
                    foreach (var item in needDims)
                        ReloadDimensionsAsync(item, scaleFactor, fitWidth, maxSizeFactor, minFitPx);
                }));
            });
        }

        // Re-reads a failed header read on a pool thread, then applies the true
        // size on the UI thread. Only runs for corrupt/unsupported files, so the
        // cost is negligible in the common case.
        private void ReloadDimensionsAsync(LazyImage item, double scale, double fitWidth, double maxSizeFactor, double minFitPx)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var fs = new FileStream(item.Path, FileMode.Open, FileAccess.Read))
                    {
                        var dc = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        if (dc.Frames.Count > 0)
                        {
                            var f = dc.Frames[0];
                            double w = f.Width / 96.0 * f.DpiX;
                            double h = f.Height / 96.0 * f.DpiY;
                            LV.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                item.ImageWidth = w; item.ImageHeight = h;
                                item.UpdateSize(scale, fitWidth, maxSizeFactor, minFitPx);
                                SmartFlowPanel.Current?.NotifyItemsChanged();
                            }));
                        }
                    }
                }
                catch { /* leave placeholder size */ }
            });
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
