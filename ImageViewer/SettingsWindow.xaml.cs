using System;
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

        Brush _bgBrush;

        public SettingsWindow(bool recurse, bool fitToWidth, bool showStatusBar,
                              int maxSizePercent, double defaultScale, Color backgroundColor,
                              int minFitWidthPx, int scrollUndershootPercent,
                              bool autoRefreshOnFolderChange, int scrollNotchRows)
        {
            InitializeComponent();

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

            CHK_Recurse.IsChecked = recurse;
            CHK_FitToWidth.IsChecked = fitToWidth;
            CHK_ShowStatusBar.IsChecked = showStatusBar;
            CHK_AutoRefresh.IsChecked = autoRefreshOnFolderChange;

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
            NewMaxImageSizePercent = (int)SL_MaxSize.Value;
            NewDefaultScale = scale;
            NewFitToWidthMinImageWidth = (int)SL_MinFitPx.Value;
            NewScrollUndershootPercent = (int)SL_Undershoot.Value;
            NewScrollNotchRows = (int)SL_NotchRows.Value;

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
