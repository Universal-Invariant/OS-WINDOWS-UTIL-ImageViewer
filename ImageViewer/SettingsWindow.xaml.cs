using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ImageViewer
{
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml
    /// A small, simple modal dialog exposing the basic viewer settings.
    /// The dialog does NOT save directly to Properties.Settings; instead it
    /// exposes the chosen values as properties and lets MainWindow apply and
    /// persist them (so everything goes through one code path).
    /// </summary>
    public partial class SettingsWindow : Window
    {
        // Working copies of the current settings
        public bool NewRecurse { get; private set; }
        public bool NewFitToWidth { get; private set; }
        public bool NewShowStatusBar { get; private set; }
        public int NewMaxImageSizePercent { get; private set; }
        public double NewDefaultScale { get; private set; }
        public Color NewBackgroundColor { get; private set; }
        public int NewFitToWidthMinImageWidth { get; private set; }
        public int NewScrollUndershootPercent { get; private set; }
        public bool NewAutoRefreshOnFolderChange { get; private set; }
        public int NewScrollNotchRows { get; private set; }
        public bool NewOpenOnDoubleClick { get; private set; }

        Brush _bgBrush;

        public SettingsWindow(bool recurse, bool fitToWidth, bool showStatusBar,
                              int maxSizePercent, double defaultScale, Color backgroundColor,
                              int minFitWidthPx, int scrollUndershootPercent,
                              bool autoRefreshOnFolderChange, int scrollNotchRows,
                              bool openOnDoubleClick)
        {
            InitializeComponent();
            SourceInitialized += (s, e) => { if (Theme.IsDark) Native.UseDarkTitleBar(this); };
            MaxHeight = SystemParameters.WorkArea.Height * 0.92;   // sections scroll, buttons stay

            NewRecurse = recurse;
            NewFitToWidth = fitToWidth;
            NewShowStatusBar = showStatusBar;
            NewMaxImageSizePercent = maxSizePercent;
            NewDefaultScale = defaultScale;
            NewBackgroundColor = backgroundColor;
            NewFitToWidthMinImageWidth = minFitWidthPx;
            NewScrollUndershootPercent = Math.Max(0, Math.Min(50, scrollUndershootPercent));
            NewAutoRefreshOnFolderChange = autoRefreshOnFolderChange;
            NewScrollNotchRows = Math.Max(1, Math.Min(5, scrollNotchRows));
            NewOpenOnDoubleClick = openOnDoubleClick;

            CHK_Recurse.IsChecked = recurse;
            CHK_FitToWidth.IsChecked = fitToWidth;
            CHK_ShowStatusBar.IsChecked = showStatusBar;
            CHK_AutoRefresh.IsChecked = autoRefreshOnFolderChange;
            CHK_OpenOnDblClick.IsChecked = openOnDoubleClick;

            SL_MaxSize.Value = Math.Max(10, Math.Min(200, maxSizePercent));
            TB_MaxSize.Text = ((int)SL_MaxSize.Value).ToString() + " %";

            SL_MinFitPx.Value = Math.Max(0, Math.Min(2000, minFitWidthPx));
            TB_MinFitPx.Text = ((int)SL_MinFitPx.Value).ToString() + " px";

            SL_Undershoot.Value = NewScrollUndershootPercent;
            TB_Undershoot.Text = NewScrollUndershootPercent.ToString() + " %";

            SL_NotchRows.Value = NewScrollNotchRows;
            TB_NotchRows.Text = NewScrollNotchRows.ToString();

            TB_Scale.Text = defaultScale.ToString("F2");

            _bgBrush = new SolidColorBrush(backgroundColor);
            BDR_Color.Background = _bgBrush;
        }

        private void SL_MaxSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TB_MaxSize != null)
                TB_MaxSize.Text = ((int)e.NewValue).ToString() + " %";
        }

        private void SL_MinFitPx_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TB_MinFitPx != null)
                TB_MinFitPx.Text = ((int)e.NewValue).ToString() + " px";
        }

        private void SL_Undershoot_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TB_Undershoot != null)
                TB_Undershoot.Text = ((int)e.NewValue).ToString() + " %";
        }

        private void SL_NotchRows_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TB_NotchRows != null)
                TB_NotchRows.Text = ((int)e.NewValue).ToString();
        }

        private void TB_Scale_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Only accept digits, separators and sign characters while typing
            var t = TB_Scale.Text;
            foreach (var c in t)
            {
                if (!char.IsDigit(c) && c != '.' && c != ',' && c != '-' && c != '+' && !char.IsWhiteSpace(c))
                {
                    // strip invalid chars silently
                    TB_Scale.Text = new string(t.ToCharArray(), 0, t.Length - 1);
                    TB_Scale.CaretIndex = TB_Scale.Text.Length;
                    return;
                }
            }
        }

        private void BtnPickColor_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.ColorDialog();
            dialog.Color = System.Drawing.Color.FromArgb(
                NewBackgroundColor.A, NewBackgroundColor.R, NewBackgroundColor.G, NewBackgroundColor.B);

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                NewBackgroundColor = Color.FromArgb(dialog.Color.A, dialog.Color.R, dialog.Color.G, dialog.Color.B);
                _bgBrush = new SolidColorBrush(NewBackgroundColor);
                BDR_Color.Background = _bgBrush;
            }
        }

        // --- Theme, transparency, reading, Explorer, slideshow --------------------
        public string NewTheme { get; private set; }
        public string NewTransparency { get; private set; }
        string _originalTransparency;
        public bool NewRememberPosition { get; private set; }
        public bool NewExplorerMenu { get; private set; }
        public int NewSlideshowSeconds { get; private set; }
        public int NewAutoAdvanceSeconds { get; private set; }

        public void InitExtras(string theme, string transparency, bool rememberPosition, bool explorerMenu, int slideshowSeconds,
            int autoAdvanceSeconds)
        {
            (theme == "Light" ? RB_ThemeLight : theme == "System" ? RB_ThemeSystem : RB_ThemeDark).IsChecked = true;
            // A color picked with the toolbar swatch (black, white, dark red...)
            // matches none of these: no segment is selected and it is kept
            // unless one is chosen here.
            _originalTransparency = transparency;
            var seg = transparency == "None" ? RB_TransNone : transparency == "Light" ? RB_TransLight
                : transparency == "Dark" ? RB_TransDark
                : string.IsNullOrEmpty(transparency) || transparency == "Checker" ? RB_TransChecker : null;
            if (seg != null) seg.IsChecked = true;
            CHK_RememberPosition.IsChecked = rememberPosition;
            CHK_ExplorerMenu.IsChecked = explorerMenu;
            SL_Slideshow.Value = Math.Max(1, Math.Min(30, slideshowSeconds));
            TB_Slideshow.Text = (int)SL_Slideshow.Value + " s";
            SL_AutoAdvance.Value = Math.Max(1, Math.Min(60, autoAdvanceSeconds));
            TB_AutoAdvance.Text = (int)SL_AutoAdvance.Value + " s";
        }

        private void SL_AutoAdvance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TB_AutoAdvance != null) TB_AutoAdvance.Text = (int)e.NewValue + " s";
        }

        private void SL_Slideshow_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TB_Slideshow != null) TB_Slideshow.Text = (int)e.NewValue + " s";
        }

        // --- Profiles -----------------------------------------------------------
        public bool NewPerMonitor { get; private set; }
        public bool NewPerDesktop { get; private set; }
        public List<string> ForgottenProfiles { get; } = new List<string>();

        /// <param name="currentKey">Profile key of the window's place now (marked in the list).</param>
        /// <param name="loadedFrom">Which profile the settings were loaded from at start, or null.</param>
        public void InitProfiles(bool perMonitor, bool perDesktop, string currentPlace, string currentKey,
            string loadedFrom, IEnumerable<ProfileEntry> profiles)
        {
            NewPerMonitor = perMonitor;
            NewPerDesktop = perDesktop;
            CHK_PerMonitor.IsChecked = perMonitor;
            CHK_PerDesktop.IsChecked = perDesktop;
            TB_CurrentPlace.Text = "This window: " + currentPlace
                + (string.IsNullOrEmpty(loadedFrom) ? "" : "\nSettings at start came from: " + loadedFrom);
            var secondary = (System.Windows.Media.Brush)FindResource("Fg.Secondary");
            foreach (var p in profiles)
            {
                bool here = p.Key == currentKey;
                string path = p.Get("LastUsedPath");
                string window = DescribeWindow(p);
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = p.DisplayName + (here ? "   (this window)" : ""),
                    FontWeight = here ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                content.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(path) ? "No folder" : ShortPath(path, 60),
                    FontSize = 11, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis,
                });
                content.Children.Add(new TextBlock
                {
                    Text = window + "  ·  last used " + p.LastUsed.ToString("g"),
                    FontSize = 11, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis,
                });
                LB_Profiles.Items.Add(new ListBoxItem
                {
                    Content = content,
                    Tag = p.Key,
                    ToolTip = p.DisplayName + "\n" + (string.IsNullOrEmpty(path) ? "No folder" : path) + "\n" + window,
                });
            }
        }

        // "Normal · 1052 × 600 · position 850, 377" / "Maximized · position 850, 377"
        // (a maximized window's size is its monitor's; the position is where it restores to).
        static string DescribeWindow(ProfileEntry p)
        {
            string state = p.Get("WindowState");
            if (string.IsNullOrEmpty(state)) state = "Normal";
            double l, t, w, h;
            bool hasPos = TryNumber(p.Get("WindowLeft"), out l) & TryNumber(p.Get("WindowTop"), out t);
            bool hasSize = TryNumber(p.Get("WindowWidth"), out w) & TryNumber(p.Get("WindowHeight"), out h);
            var parts = new List<string> { state };
            if (state == "Normal" && hasSize) parts.Add(Math.Round(w) + " × " + Math.Round(h));
            if (hasPos) parts.Add("position " + Math.Round(l) + ", " + Math.Round(t));
            if (!hasPos && !hasSize) parts.Add("default size and position");
            return string.Join("  ·  ", parts);
        }

        // "Z:\Temp\…\scratchpad\testtree": drive / share kept, the middle elided,
        // so the folder's own name (the end) stays readable.
        static string ShortPath(string path, int max)
        {
            if (path.Length <= max) return path;
            var parts = path.TrimEnd('\\').Split('\\');
            // Root: "C:" or, for a UNC path (\\server\share\...), "\\server\share".
            int rootParts = path.StartsWith(@"\\") ? Math.Min(4, parts.Length - 1) : 1;
            string root = string.Join("\\", parts, 0, rootParts);
            string tail = parts[parts.Length - 1];
            for (int i = parts.Length - 2; i >= rootParts; i--)
            {
                string longer = parts[i] + "\\" + tail;
                if (root.Length + longer.Length + 3 > max) break;
                tail = longer;
            }
            string result = root + "\\…\\" + tail;
            return result.Length < path.Length ? result : path;
        }

        static bool TryNumber(string text, out double value)
        {
            return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) && !double.IsNaN(value);
        }

        private void Btn_ForgetProfile_Click(object sender, RoutedEventArgs e)
        {
            var item = LB_Profiles.SelectedItem as ListBoxItem;
            if (item == null) return;
            ForgottenProfiles.Add((string)item.Tag);
            LB_Profiles.Items.Remove(item);
        }

        private void Btn_OK_Click(object sender, RoutedEventArgs e)
        {
            double scale;
            if (!double.TryParse(TB_Scale.Text.Replace(',', '.'),
                                 System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out scale)
                || scale < 0.1 || scale > 4.0)
            {
                MessageBox.Show(this, "Default scale must be a number between 0.1 and 4.0.",
                                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NewRecurse = CHK_Recurse.IsChecked ?? false;
            NewFitToWidth = CHK_FitToWidth.IsChecked ?? false;
            NewShowStatusBar = CHK_ShowStatusBar.IsChecked ?? false;
            NewAutoRefreshOnFolderChange = CHK_AutoRefresh.IsChecked ?? false;
            NewOpenOnDoubleClick = CHK_OpenOnDblClick.IsChecked ?? false;
            NewMaxImageSizePercent = (int)SL_MaxSize.Value;
            NewDefaultScale = scale;
            NewFitToWidthMinImageWidth = (int)SL_MinFitPx.Value;
            NewScrollUndershootPercent = (int)SL_Undershoot.Value;
            NewScrollNotchRows = (int)SL_NotchRows.Value;
            NewTheme = RB_ThemeLight.IsChecked == true ? "Light" : RB_ThemeSystem.IsChecked == true ? "System" : "Dark";
            NewTransparency = RB_TransNone.IsChecked == true ? "None" : RB_TransLight.IsChecked == true ? "Light"
                : RB_TransDark.IsChecked == true ? "Dark" : RB_TransChecker.IsChecked == true ? "Checker"
                : _originalTransparency;
            NewRememberPosition = CHK_RememberPosition.IsChecked ?? false;
            NewExplorerMenu = CHK_ExplorerMenu.IsChecked ?? false;
            NewSlideshowSeconds = (int)SL_Slideshow.Value;
            NewAutoAdvanceSeconds = (int)SL_AutoAdvance.Value;
            NewPerMonitor = CHK_PerMonitor.IsChecked ?? false;
            NewPerDesktop = CHK_PerDesktop.IsChecked ?? false;

            DialogResult = true;
            Close();
        }

        private void Btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
