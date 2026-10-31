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
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Single-instance handle so the custom layout panel (SmartFlowPanel)
        // can query the current toolbar/layout settings during measure.
        public static MainWindow Current { get; private set; }

        private const int MaxRecentPaths = 10;

        // Book view page width: SmartFlowPanel.FitTarget (keeps pages a little
        // under the viewport width so rounding can never wrap two pages into one row).

        // Scale slider drag throttle: ApplyScale() is O(all images), so we
        // coalesce rapid ValueChanged events into at most one relayout per
        // interval and always schedule a final apply for the last value.
        const double ScaleApplyIntervalMs = 60.0;
        DispatcherTimer _scaleApplyTimer = null;
        bool _scaleFinalApplyPending = false;

        // Scale slider range (slider, settings, persistence and every clamp
        // agree; the slider used to allow 4.0 while the code clamped to 3.0).
        const double MinScale = 0.1;
        const double MaxScale = 4.0;
        static double ClampScale(double v) { return Math.Max(MinScale, Math.Min(MaxScale, v)); }

        BackgroundWorker currentRecurser = null;


        public MainWindow()
        {
            InitializeComponent();

            Current = this;

            // Device-pixel rounding of image sizes (see LazyImage.UpdateSize).
            // Refreshed in Loaded once the window is on its monitor.
            UpdateDpiScale();

            // Display options (tiles, names, transparency, book layout, theme
            // colors) before anything is laid out.
            ApplyDisplayOptions();
            InitViewMenu();

            // Initialize events after settings are loaded
            SD_Scale.ValueChanged += SD_Scale_ValueChanged;
            CB_Path.KeyDown += CB_Path_KeyDown; // Handle Enter key in ComboBox


            // One folder or several ("A; B"); folders that no longer exist are left out.
            string loadedPath = FolderSet.Join(FolderSet.Split(App.StartupFolder ?? Settings.Default.LastUsedPath).Where(Directory.Exists));
            if (!App.IsBenchmark && !string.IsNullOrEmpty(loadedPath))
            {
                CB_Path.Text = loadedPath;
                LoadPathFromCombo();
            }

            LoadSettings();

            // Keep the maximize/restore button glyph (and maximized padding) in sync
            StateChanged += (s, ev) => UpdateMaximizeGlyph();
            UpdateMaximizeGlyph();

            UpdateScaleLabel();
            UpdateModeText();
            UpdateEmptyState(false);
            ApplyInfoPanel();

            // Global shortcuts, built-in preview keys (see Window_PreviewKeyDown).
            PreviewKeyDown += Window_PreviewKeyDown;
            PreviewMouseMove += Window_TrackMouse;   // keyboard vs mouse target, full-screen toolbar reveal

            // Drop a folder (or an image file) anywhere on the window to open it.
            DragEnter += Window_DragOver;
            DragOver += Window_DragOver;
            Drop += Window_Drop;

            // Right-click menu only appears over an image.
            LV.ContextMenuOpening += LV_ContextMenuOpening;

            // Apply the remembered Normal/Maximized state after Show() has run.
            // Geometry was already restored in OnSourceInitialized; deferring
            // the state to Loaded is what makes it stick (see RestoreWindowState
            // docs) - WPF's own show-time handling of the XAML default used to
            // preempt restores done earlier and re-pick the primary monitor.
            Loaded += (s, ev) =>
            {
                _loadedAt = DateTime.Now;
                ApplySavedWindowState();
                if (UpdateDpiScale()) ApplyScale();
                if (App.IsBenchmark) RunBenchmark();
                RefreshPlace();
            };
            Activated += (s, ev) => RefreshPlace();
            LocationChanged += (s, ev) =>
            {
                // Fires continuously while dragging: only re-query on a monitor change.
                if (!Profiles.PerMonitor) return;
                var mon = Placement.MonitorHandleOf(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (mon != _placeMonitor) { _placeMonitor = mon; RefreshPlace(); }
            };

            // Re-layout the images when the window is resized so that
            // fit-to-width / max-size clamping always tracks the window size.
            SizeChanged += MainWindow_SizeChanged;

            // "Scroll by image" mode: intercept the mouse wheel over the list
            // and jump whole rows instead of letting WPF scroll 3 lines/tick.
            // PreviewMouseWheel tunnels to us first, so e.Handled stops the
            // default pixel scrolling entirely (no double-scroll).
            LV.PreviewMouseWheel += LV_PreviewMouseWheel;
            LV.ScrollChanged += LV_ScrollChanged;   // position indicator, "user scrolled" (resume)

            // PageUp/PageDown/Home/End mirror the wheel's row stepping when
            // scroll-by-image is on (see LV_PreviewKeyDown).
            LV.PreviewKeyDown += LV_PreviewKeyDown;

            // Clicks on images: selection, double-click preview/open, Alt+click
            // rotation, drag-out to other apps (see MainWindow.Input.cs).
            LV.PreviewMouseLeftButtonDown += LV_MouseLeftButtonDown;
            LV.PreviewMouseMove += LV_PreviewMouseMove;
            LV.PreviewMouseLeftButtonUp += LV_MouseLeftButtonUp;
            PreviewMouseDown += Window_PreviewMouseDown;   // mouse back/forward buttons

            // File-system watcher for the current folder: when images are
            // added/deleted/renamed on disk we flag the refresh button red
            // (we never auto-reload - a full reload is expensive and would
            // throw away the lazy state / scroll position).
            // Explorer multi-selection: the other selected items arrive from their own launches.
            if (LaunchGather.Collecting) StartGatherMerge();


            // Auto-hiding scrollbar. The ListView's ScrollViewer uses an overlay
            // template (MainWindow.xaml) in which the scrollbar floats over the
            // content, so showing/hiding it is a pure Opacity fade.
            // RELAYOUT FIX: this used to toggle VerticalScrollBarVisibility between
            // Hidden and Auto. Auto reserves a ~17px gutter, so every time the
            // mouse approached (and 250ms after it left) the right edge the viewport
            // width changed and the whole list re-flowed (every image resized in
            // fit-to-width mode). It also walked the entire visual tree (every
            // ListViewItem) on each MouseMove to find the scrollbar.
            MouseMove += (s, e) =>
            {
                Point M = e.GetPosition(this);

                var sb = GetVerticalScrollBar();
                if (sb == null) return;

                double sliverWidth = Math.Max(sb.ActualWidth, 17) * 1.35; // Slightly wider zone so it's easy to hit
                bool keep = M.X >= (this.ActualWidth - sliverWidth) || sb.IsMouseCaptureWithin;
                if (keep)
                {
                    _scrollBarHideTimer.Stop();
                    SetScrollBarShown(true);
                }
                else if (_scrollBarShown && !_scrollBarHideTimer.IsEnabled)
                {
                    _scrollBarHideTimer.Start();
                }
            };
            MouseLeave += (s, e) => { if (_scrollBarShown) _scrollBarHideTimer.Start(); };

            _scrollBarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; // Grace period before hiding
            _scrollBarHideTimer.Tick += (s, e) =>
            {
                _scrollBarHideTimer.Stop();
                var sb = GetVerticalScrollBar();
                if (sb != null && sb.IsMouseCaptureWithin) return;   // still dragging the thumb
                SetScrollBarShown(false);
            };


            return;

        }

        // --- Overlay scrollbar fade --------------------------------------------
        DispatcherTimer _scrollBarHideTimer;
        bool _scrollBarShown;
        ScrollBar _vScrollBar;

        ScrollBar GetVerticalScrollBar()
        {
            if (_vScrollBar != null) return _vScrollBar;
            var sv = GetScrollViewer();
            if (sv == null || sv.Template == null) return null;
            _vScrollBar = sv.Template.FindName("PART_VerticalScrollBar", sv) as ScrollBar;
            return _vScrollBar;
        }

        void SetScrollBarShown(bool show)
        {
            if (_scrollBarShown == show) return;
            var sb = GetVerticalScrollBar();
            if (sb == null) return;
            _scrollBarShown = show;
            // Invisible scrollbar must not swallow clicks on the images below it.
            sb.IsHitTestVisible = show;
            sb.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(show ? 1.0 : 0.0, TimeSpan.FromMilliseconds(show ? 120 : 250)));
        }

        // --- DPI ------------------------------------------------------------------
        // Returns true when the scale changed (callers then re-apply image sizes).
        bool UpdateDpiScale()
        {
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (s <= 0 || Math.Abs(s - LazyImage.DpiScale) < 1e-9) return false;
            LazyImage.DpiScale = s;
            return true;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (UpdateDpiScale()) ApplyScale();
        }

        // --- Directory change detection --------------------------------------
        readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();   // one per folder shown
        bool _pendingChanges = false;      // changes seen since last (re)load
        int _watchStamp = 0;               // guards against stale async snapshots

        void UpdateFolderWatch()
        {
            // Detach first so a failed re-attach can't leave a watcher on the old path.
            foreach (var w in _watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _watchers.Clear();

            if (string.IsNullOrEmpty(oldPath)) return;
            ++_watchStamp;   // invalidate any in-flight bookkeeping from an older path

            bool recurse = TGB_Recurse.IsChecked ?? false;

            // Attaching the FileSystemWatcher itself is cheap (it just registers
            // with the OS) - do it synchronously on the UI thread. We do NOT
            // enumerate the directory here: for huge/network trees that would be
            // as slow as a full reload, defeating the point. The watcher's own
            // kernel events tell us when something actually changed.
            foreach (var folder in FolderSet.Split(oldPath))
            {
                try
                {
                    var w = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = recurse };
                    w.Created += FolderWatcher_Changed;
                    w.Deleted += FolderWatcher_Changed;
                    w.Renamed += FolderWatcher_Changed;
                    w.Error += (s, ev) => { try { ((FileSystemWatcher)s).EnableRaisingEvents = true; } catch { } };
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch
                {
                    // Some paths (e.g. certain network shares) can't be watched;
                    // the refresh button simply stays hidden then.
                }
            }
        }

        void FolderWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            // Ignore non-image files outright, and ignore events that only
            // touched metadata inside subfolders we don't care about.
            // (The old check was a substring test on "*.jpg; *.jpeg; ..." so e.g.
            // ".jp" or ".pn" counted as images.) A rename that turns an image
            // into a non-image (or vice versa) matters too.
            var renamed = e as RenamedEventArgs;
            if (!ImageFiles.IsImage(e.Name) && (renamed == null || !ImageFiles.IsImage(renamed.OldName))) return;
            // Our own copy / move / delete: the view is already up to date.
            if (IsSelfChange(e.FullPath) || (renamed != null && IsSelfChange(renamed.OldFullPath))) return;

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
            BtnRefresh.ToolTip = dirty
                ? "Files were added/removed in this folder - click to reload"
                : "Reload the folder";
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _pendingChanges = false;
            SetRefreshIndicator(false);
            oldPath = null;                     // force LoadPathFromCombo to reload
            LoadPathFromCombo();
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
            SD_Scale.Value = ClampScale(Settings.Default.ScaleFactor);

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
            if (string.IsNullOrEmpty(oldPath) && !string.IsNullOrEmpty(Settings.Default.LastUsedPath))
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
            if (App.IsBenchmark) return;   // benchmark runs on in-memory defaults

            // Save background color (as ARGB hex string)
            var currentBgBrush = LV.Background as SolidColorBrush;
            if (currentBgBrush != null)
            {
                Settings.Default.BackgroundColorARGB = currentBgBrush.Color.ToString();
            }

            // Save scale factor
            Settings.Default.ScaleFactor = ClampScale(SD_Scale.Value);

            // Save recurse setting (two-state toolbar toggle)
            Settings.Default.RecurseSubdirectories = TGB_Recurse.IsChecked ?? false;

            // Save layout settings
            Settings.Default.FitToWidth = TGB_FitToWidth.IsChecked ?? false;
            Settings.Default.ShowStatusBar = StatusBarWanted;   // not the full-screen state

            // Save recent paths: current folder first, then the dropdown's other
            // entries, de-duplicated case-insensitively ignoring a trailing '\'.
            // BUG FIX: the old filter skipped every entry that was already in
            // CB_Path.Items - i.e. all of them - so only the current folder was
            // ever saved; and Trim('\\') turned UNC paths (\\server\share) into
            // "server\share".
            var recentPaths = new StringCollection();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string currentPath = CB_Path.Text;
            Action<string> addRecent = path =>
            {
                if (string.IsNullOrEmpty(path) || recentPaths.Count >= MaxRecentPaths) return;
                var key = path.Length > 3 ? path.TrimEnd('\\') : path;   // keep "C:\" intact
                if (!seen.Add(key) || !FolderSet.IsValid(key)) return;
                recentPaths.Add(path);
            };
            addRecent(currentPath);
            foreach (var item in CB_Path.Items) addRecent(item as string);

            Settings.Default.RecentPaths = recentPaths;

            // Save the last used path (the text in the combobox)
            Settings.Default.LastUsedPath = currentPath;

            Settings.Default.Save();

            // ...and into the profile of the monitor / virtual desktop the
            // window is on right now (see Profiles).
            Profiles.Save(this);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_fullScreen) ToggleFullScreen();   // remember the normal window, not the full-screen one
            SaveReadingPosition();
            if (!App.IsBenchmark)
                SaveWindowState(); // capture geometry before SaveSettings() persists
            SaveSettings();
            base.OnClosing(e);
        }
        private void SD_Scale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // In book view the slider is pinned to 1.0 (pages are sized from the
            // window width; any extra scale would push them past the wrap edge).
            // Zooming by hand therefore leaves book view. Remember the new value
            // as the one to "restore" so TGB_FitToWidth_Changed doesn't undo it.
            if (IsFitToWidthEnabled && Math.Abs(e.NewValue - 1.0) > 0.001)
            {
                _scaleBeforeFitToWidth = e.NewValue;
                TGB_FitToWidth.IsChecked = false;
            }
            UpdateScaleLabel();

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
            long perfStart = Perf.Start();
            var panel = SmartFlowPanel.Current;
            if (panel != null) panel.InvalidateMeasures = true;
            var ScaleFactor = ClampScale(SD_Scale.Value);
            var fitWidth = ComputeFitToWidthScale();
            var maxSizeFactor = MaxSizeFactor();
            var minFitPx = FitToWidthMinPixels();

            // Pure arithmetic per image (no WPF layout for off-screen images:
            // the panel virtualizes), so this stays cheap for thousands.
            // The "largest image" limit follows the window size and the
            // setting live (it used to be fixed when the folder was loaded).
            double maxW = GetLayoutWidth() * maxSizeFactor, maxH = GetLayoutHeight() * maxSizeFactor;
            foreach (var item in _all)
            {
                item.SetMaxSize(maxW, maxH);
                item.UpdateSize(ScaleFactor, fitWidth, maxSizeFactor, minFitPx);
            }

            // Force one relayout pass for the whole panel.            
            panel?.InvalidateLocations();

            LV.InvalidateMeasure();
            LV.InvalidateArrange();
            Perf.Stop(Perf.C.ApplyScale, perfStart);
        }

        void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsPreviewOpen) SizePreviewImage();

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
            SD_Scale.Value = 1.0; // This will trigger SD_Scale_ValueChanged
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
                LoadPathFromCombo();
                e.Handled = true;   // don't let the combo treat Enter as dropdown toggle
            }
        }

        // Applies a background color everywhere it is used and persists it.
        private void ApplyBackgroundColor(Color color)
        {
            // Only the image area: the window chrome follows the theme.
            LV.Background = new SolidColorBrush(color);
            Settings.Default.BackgroundColorARGB = color.ToString();
            LazyImage.SetBackdrop(color);
            // "Same as background" backdrop follows the new color.
            Backdrop.BackgroundColor = color;
            LazyImage.TransparencyBrush = Backdrop.BrushFor(Settings.Default.TransparencyBackground);
            UpdateBackdropUi();
            FolderHeader.SetBackdrop(color);
            GridPanel.RefreshRealizedVisuals();
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
            if (!IsFitToWidthEnabled || LazyImage.TileMode) return 0;
            return SmartFlowPanel.FitTarget(GetLayoutWidth());
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
            string initial = null;
            try { initial = FolderSet.Split(CB_Path.Text).FirstOrDefault(Directory.Exists); }
            catch { /* ignore invalid current text */ }

            // Ctrl+click (Ctrl+O with Ctrl still held): add the folder to the ones shown.
            bool add = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && sender != null && !string.IsNullOrEmpty(oldPath);
            var picked = Native.PickFolder(this, initial, add ? "Add a folder to the ones shown" : "Select the folder containing your images");
            if (string.IsNullOrEmpty(picked) || !Directory.Exists(picked)) return;
            if (add) AddFolders(new[] { picked });
            else OpenFolder(picked);
        }

        // Shows `path` (browse, drag & drop): loads it and remembers it.
        void OpenFolder(string path)
        {
            CB_Path.Text = path;
            oldPath = null;                       // force a reload
            LoadPathFromCombo();
            SaveSettings();                       // persist immediately so it is never lost
        }

        // Gear button: pops up the simple settings dialog.
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var bg = (LV.Background as SolidColorBrush)?.Color ?? Colors.Black;

            var dlg = new SettingsWindow(
                TGB_Recurse.IsChecked ?? false,
                TGB_FitToWidth.IsChecked ?? false,
                StatusBarWanted,
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
            var place = CurrentPlace();
            dlg.InitProfiles(Profiles.PerMonitor, Profiles.PerDesktop, place.ToString(), Profiles.KeyFor(place),
                Profiles.Enabled ? Profiles.LoadedDescription : null, Profiles.List());
            bool explorerMenu = ShellIntegration.IsRegistered;
            dlg.InitExtras(Settings.Default.Theme, Settings.Default.TransparencyBackground,
                Settings.Default.RememberScrollPosition, explorerMenu, Settings.Default.SlideshowSeconds,
                Settings.Default.AutoAdvanceSeconds);

            if (dlg.ShowDialog() == true)
            {
                Profiles.Forget(dlg.ForgottenProfiles);
                if (dlg.NewPerMonitor != Profiles.PerMonitor || dlg.NewPerDesktop != Profiles.PerDesktop)
                    Profiles.SetOptions(dlg.NewPerMonitor, dlg.NewPerDesktop);

                ApplyBackgroundColor(dlg.NewBackgroundColor);

                if (dlg.NewTheme != Settings.Default.Theme)
                {
                    Settings.Default.Theme = dlg.NewTheme;
                    Theme.Apply(dlg.NewTheme);
                    GridPanel.RefreshRealizedVisuals();
                }
                Settings.Default.TransparencyBackground = dlg.NewTransparency;
                Settings.Default.RememberScrollPosition = dlg.NewRememberPosition;
                Settings.Default.SlideshowSeconds = dlg.NewSlideshowSeconds;
                Settings.Default.AutoAdvanceSeconds = dlg.NewAutoAdvanceSeconds;
                ApplyAutoAdvanceInterval();
                ApplyDisplayOptions();
                if (dlg.NewExplorerMenu != explorerMenu)
                {
                    try
                    {
                        if (dlg.NewExplorerMenu) ShellIntegration.Register(); else ShellIntegration.Unregister();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Could not change the Explorer menu:\n" + ex.Message, "Image Viewer",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

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

                StatusBarWanted = dlg.NewShowStatusBar;
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

                UpdateModeText();
                SaveSettings();   // also stores the profile for this monitor / desktop
            }
        }

        // Two-state recursion toggle (replaces the old label + checkbox)
        private void CB_Recurse_Changed(object sender, RoutedEventArgs e)
        {
            UpdateModeText();
            oldPath = null;
            LoadPathFromCombo();
        }

        // Two-state "fit images to window width" toggle (book format)
        private void TGB_FitToWidth_Changed(object sender, RoutedEventArgs e)
        {
            var on = TGB_FitToWidth.IsChecked ?? false;
            Settings.Default.FitToWidth = on;
            UpdateModeText();
            UpdateArrangeButton();   // its meaning follows the layout

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
            UpdateScaleLabel();
        }
        double _scaleBeforeFitToWidth = 1.0;

        // Two-state "scroll one image per wheel tick" toggle (persistent).
        private void TGB_ScrollByImage_Changed(object sender, RoutedEventArgs e)
        {

            Settings.Default.ScrollByImage = TGB_ScrollByImage.IsChecked ?? false;
            UpdateModeText();
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
            RestartAutoAdvanceCountdown();   // reading by hand: a full interval from here
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Delta != 0) StepScale(e.Delta > 0 ? +1 : -1);
                e.Handled = true;
                return;
            }
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

            // Row rectangles are in the panel's coordinate space. The panel is the
            // ScrollViewer's direct content (nothing above it), so panel space and
            // scroll offsets coincide; chromeOffset stays 0 but keeps the
            // conversion explicit should anything ever sit above the panel.
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
                case Key.PageDown: ScrollToRowStep(+perNotch); break;
                case Key.PageUp: ScrollToRowStep(-perNotch); break;
                case Key.Home: sv.ScrollToVerticalOffset(0); break;
                case Key.End: sv.ScrollToVerticalOffset(sv.ScrollableHeight); break;
                default: return;                                  // let other keys bubble on
            }
            e.Handled = true;
        }

        // The image area is a plain ScrollViewer around the virtualizing panel.
        ScrollViewer GetScrollViewer() { return LV; }

        // --- Title-bar-less window handling ---------------------------------

        // Drag handler: grab any empty part of the toolbar to move the window.
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

            // A maximized WindowChrome window extends past the monitor edges by
            // the resize frame; pad the content so nothing is cut off.
            if (RootGrid != null)
                RootGrid.Margin = WindowState == WindowState.Maximized
                    ? SystemParameters.WindowResizeBorderThickness
                    : new Thickness(0);
        }



        string oldPath = "";

        // Typing only validates (red text = no such folder). It used to load on
        // every keystroke that formed a valid path, so typing "C:\Photos" with
        // subfolders on started a recursive scan of the whole drive at "C:\".
        // Loading happens on Enter, picking a recent folder, browse or drop.
        private void CB_Path_TextChanged(object sender, TextChangedEventArgs e)
        {
            var t = CB_Path.Text ?? "";
            bool ok = t.Length > 0 && FolderSet.IsValid(t);   // one folder, or several separated by ';' 
            CB_Path.SetResourceReference(ForegroundProperty, ok || t.Length == 0 ? "Fg.Primary" : "Danger");   // follows theme changes
        }

        private void LoadPathFromCombo()
        {
            var cb = CB_Path;
            var t = cb.Text;

            if (!FolderSet.IsValid(t))
            {
                cb.SetResourceReference(ForegroundProperty, "Danger");   // follows theme changes
                oldPath = null;   // coming back to the same valid path reloads it
                return;
            }

            // Already showing this folder: nothing to do. Callers that want a
            // forced reload (Enter, refresh, browse, recurse toggle) null oldPath
            // first. This guard was missing, so the re-entrant TextChanged raised
            // by the Items/SelectedIndex/Text churn below, LoadSettings' second
            // CB_Path.Text assignment and every DropDownClosed each started a
            // complete reload (clear + re-list everything).
            if (t == oldPath) return;

            cb.SetResourceReference(ForegroundProperty, "Fg.Primary");
            SaveReadingPosition();
            StopAutoAdvance(null);
            RecordHistory(t);

            // Set oldPath BEFORE touching the items collection: Items.Remove /
            // Items.Insert / SelectedIndex=0 mutate ComboBox.Text and re-raise
            // TextChanged on an editable ComboBox. With the old order the
            // re-entrant call saw oldPath==null ("first load" guard) and kicked
            // off a SECOND BackgroundWorker enumerating the same folder - two
            // workers racing Clear/Add on LV.Items (the crashes), and the churn
            // around Remove/Insert is also why typed paths sometimes vanished
            // from the recent list instead of being remembered.
            oldPath = t;
            BuildBreadcrumb(t);   // after oldPath: it shows only while the box holds the open folder
            // Taskbar / Alt+Tab show which folder this window is on.
            Title = TitleFor(FolderSet.Split(t));
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

            // Every load gets a generation number; UI-side batch work of an older
            // load is discarded even if it was already queued on the dispatcher.
            int generation = ++_loadGeneration;

            currentRecurser = new BackgroundWorker() { WorkerSupportsCancellation = true };
            currentRecurser.DoWork += new DoWorkEventHandler(showImages);
            currentRecurser.RunWorkerAsync(Tuple.Create(t, generation));

            return;
        }

        // Handles when a user selects an item from the ComboBox dropdown
        private void CB_Path_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var cb = sender as ComboBox;
            var s = cb.SelectedItem as String;
            if (s == null) return;
            if (!FolderSet.IsValid(s)) return;
            cb.SelectionChanged -= CB_Path_SelectionChanged;
            cb.Text = s;
            LoadPathFromCombo();
            cb.SelectionChanged += CB_Path_SelectionChanged;
        }



        int _loadGeneration = 0;

        private void showImages(object sender, DoWorkEventArgs e)
        {
            var worker = (BackgroundWorker)sender;
            var args = (Tuple<string, int>)e.Argument;
            int generation = args.Item2;

            // CANCELLATION FIX: this used to test e.Cancel, which is an OUTPUT
            // flag the worker sets - CancelAsync() sets CancellationPending. So a
            // folder switch never stopped the previous load and both folders'
            // images ended up mixed in the list.
            Func<bool> cancelled = () =>
            {
                if (!worker.CancellationPending) return false;
                e.Cancel = true;
                return true;
            };

            // Clear the ListView on the UI thread first (unless a newer load has
            // already taken over).
            LV.Dispatcher.Invoke((Action)(() =>
            {
                if (generation != _loadGeneration) return;
                ClearImages(args.Item1);
                if (IsPreviewOpen) ClosePreview();
                _hovered = null;
                SB_Size.Text = "";
                SetLoading(true);
            }));
            if (cancelled()) return;

            GC.Collect();

            var roots = FolderSet.Split(args.Item1);
            // Several folders: one inside another (with subfolders on) would list files twice.
            var seenFiles = roots.Count > 1 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            var recurse = (bool)LV.Dispatcher.Invoke((Func<bool>)(() => TGB_Recurse.IsChecked ?? false));

            var headerBatch = new List<ImageHeader>();
            int numFilesRead = 0;

            // Bounded backlog: the WORKER acquires a permit before posting a batch
            // and the UI releases it after adding the batch, so the enumeration
            // pauses whenever PendingBatchLimit batches are waiting on the UI.
            // (It used to be acquired on the UI thread itself, which bounded
            // nothing.)
            var pendingBatches = new SemaphoreSlim(PendingBatchLimit, PendingBatchLimit);
            Func<bool> acquire = () =>
            {
                while (!pendingBatches.Wait(100))
                    if (cancelled()) return false;
                return true;
            };

            // Flush on size OR time, so the first images show up quickly even on
            // slow (network) drives.
            var sinceFlush = System.Diagnostics.Stopwatch.StartNew();

            // Natural (Explorer) order per folder; inaccessible subfolders are
            // skipped instead of silently aborting the whole scan.
            foreach (var root in roots)
            foreach (var file in ImageFiles.Enumerate(new DirectoryInfo(root), recurse))
            {
                if (cancelled()) return;
                if (seenFiles != null && !seenFiles.Add(file.FullName)) continue;

                // Header read (file open + parse, no pixel decode) happens HERE on
                // the worker thread. The UI thread only builds the LazyImage from
                // the result. Unreadable/corrupt files are skipped: they could
                // never be displayed anyway. (Reading headers in parallel was
                // measured to be SLOWER - WPF's decoder creation serializes - so
                // ImageHeader.Read parses common formats itself instead.)
                var header = ImageHeader.Read(file);
                if (!header.Ok) continue;

                numFilesRead++;
                headerBatch.Add(header);

                if (headerBatch.Count >= 100 || sinceFlush.ElapsedMilliseconds >= 150)
                {
                    if (!acquire()) return;
                    var chunk = headerBatch;
                    headerBatch = new List<ImageHeader>();
                    ProcessBatchOnUIThread(chunk, numFilesRead, pendingBatches, generation);
                    sinceFlush.Restart();
                }
            }

            // Flush any remaining paths in the final batch
            if (headerBatch.Count > 0)
            {
                if (!acquire()) return;
                ProcessBatchOnUIThread(headerBatch, numFilesRead, pendingBatches, generation);
            }

            // Wait until the UI has consumed every batch: all permits back.
            for (int i = 0; i < PendingBatchLimit; i++)
                if (!acquire()) return;

            // Finalize on the UI thread
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (generation != _loadGeneration) return;
                SetLoading(false);
                OnLoadCompleted();
            }));
        }

        public const int PendingBatchLimit = 8;   // max batches queued ahead of the UI thread

        // UI stage of a batch: pure in-memory work (headers were read on the
        // worker), then one incremental layout pass for the appended items.
        private void ProcessBatchOnUIThread(List<ImageHeader> headers, int totalFilesSoFar, SemaphoreSlim pendingBatches, int generation)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                long perfStart = Perf.Start();
                try
                {
                    if (generation != _loadGeneration) return;   // a newer folder load took over

                    // Read the layout parameters now rather than once per load, so
                    // a resize or slider change during a long load still applies to
                    // the images added afterwards.
                    double scaleFactor   = ClampScale(SD_Scale.Value);
                    double layoutW       = GetLayoutWidth();
                    double layoutH       = GetLayoutHeight();
                    double fitWidth      = ComputeFitToWidthScale();
                    double maxSizeFactor = MaxSizeFactor();
                    double minFitPx      = FitToWidthMinPixels();

                    var batch = new List<LazyImage>(headers.Count);
                    foreach (var header in headers)
                    {
                        var item = new LazyImage(header, layoutW * maxSizeFactor, layoutH * maxSizeFactor, SavedRotation(header.Path));
                        item.RootIndex = RootIndexOf(header.Path);
                        item.UpdateSize(scaleFactor, fitWidth, maxSizeFactor, minFitPx);
                        batch.Add(item);
                    }

                    // Sorted/filtered insertion + one arithmetic relayout.
                    AddImages(batch);
                }
                finally
                {
                    // Always release, even for a stale generation or if the UI
                    // stage throws, otherwise the producer blocks in Wait().
                    pendingBatches.Release();
                    Perf.Stop(Perf.C.Batch, perfStart);
                }
            }));
        }
        /// <summary>One image outside a folder load (rename): same sizing as a loaded batch.</summary>
        LazyImage CreateImage(ImageHeader header)
        {
            double f = MaxSizeFactor();
            var item = new LazyImage(header, GetLayoutWidth() * f, GetLayoutHeight() * f, SavedRotation(header.Path));
            item.RootIndex = RootIndexOf(header.Path);
            item.UpdateSize(ClampScale(SD_Scale.Value), ComputeFitToWidthScale(), f, FitToWidthMinPixels());
            return item;
        }

        private void CB_Path_DropDownClosed(object sender, EventArgs e)
        {
            LoadPathFromCombo();
        }


        // ==========================================================================
        //  Scale controls
        // ==========================================================================

        // Multiplicative steps feel even across the whole 10%-400% range.
        void StepScale(int direction)
        {
            double v = SD_Scale.Value * (direction > 0 ? 1.15 : 1 / 1.15);
            if (Math.Abs(v - 1.0) < 0.07) v = 1.0;   // snap through 100%
            SD_Scale.Value = ClampScale(Math.Round(v, 2));
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) { StepScale(+1); }
        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) { StepScale(-1); }
        private void BtnScalePct_Click(object sender, RoutedEventArgs e) { SD_Scale.Value = 1.0; }

        void UpdateScaleLabel()
        {
            if (BtnScalePct == null) return;
            BtnScalePct.Content = IsFitToWidthEnabled ? "Fit" : Math.Round(SD_Scale.Value * 100) + "%";
        }


        // ==========================================================================
        //  Drag & drop a folder (or an image file -> its folder)
        // ==========================================================================

        static string[] GetDroppedPaths(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new string[0];
            return e.Data.GetData(DataFormats.FileDrop) as string[] ?? new string[0];
        }

        // Ctrl while dropping: add to the folders shown instead of replacing them.
        bool IsAddDrop(DragEventArgs e)
        {
            return (e.KeyStates & DragDropKeyStates.ControlKey) != 0 && !string.IsNullOrEmpty(oldPath);
        }

        void Window_DragOver(object sender, DragEventArgs e)
        {
            if (_draggingOut) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            string file;
            bool any = FolderSet.FoldersOf(GetDroppedPaths(e), out file).Count > 0;
            e.Effects = !any ? DragDropEffects.None : IsAddDrop(e) ? DragDropEffects.Copy : DragDropEffects.Link;
            e.Handled = true;
        }

        void Window_Drop(object sender, DragEventArgs e)
        {
            if (_draggingOut) return;   // our own drag-out landed back on the window
            var paths = GetDroppedPaths(e);
            string file;
            var folders = FolderSet.FoldersOf(paths, out file);
            if (folders.Count == 0) return;
            e.Handled = true;
            Activate();

            if (IsAddDrop(e)) { AddFolders(folders); return; }

            // One dropped image: open its folder and preview it (directly when
            // it is already shown).
            if (paths.Length == 1 && file != null)
            {
                var shown = _view.FirstOrDefault(v => string.Equals(v.Path, file, StringComparison.OrdinalIgnoreCase));
                if (shown != null && !_isLoading) { SelectOnly(shown); SetCurrent(shown); GridPanel.ScrollIntoView(shown); OpenPreview(shown); return; }
            }
            if (file != null) App.StartupFile = file;   // picked up by OnLoadCompleted
            // Several folders (or images from several folders): shown together.
            OpenFolder(FolderSet.Join(folders));
        }


    }

}
