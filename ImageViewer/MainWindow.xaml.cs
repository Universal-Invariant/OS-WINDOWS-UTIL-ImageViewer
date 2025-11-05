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
using System.Windows.Threading;
using Path = System.IO.Path;

namespace ImageViewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int MaxRecentPaths = 10;





        string fileTypes = "*.jpg; *.png; *.bmp; *.ico; *.gif";
        BackgroundWorker currentRecurser = null;
        



        public MainWindow()
        {
            InitializeComponent();

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

            var L = new TreeCollection.AVLTree<int, int>();
            L.ItemKey = (i) => { return i; };
            var LL = new List<int>();
            for (var i = 0; i < 20; i++) L.Add(i);

            L.Traversal.TraversalDir = TreeCollection.TraversalEnumeratorDir.Normal;
            L.Traversal.TraversalMethod = TreeCollection.TraversalEnumeratorMethod.In;
            L.Traversal.TraversalType = TreeCollection.TraversalEnumeratorType.Recursive;
            L.Traversal.CompleteValueIteration = true;

            L.EnumeratorRange = null; L.Traversal.TraversalDir = TreeCollection.TraversalEnumeratorDir.Normal;
            LL.Clear(); L.Traversal.Traverse((i) => { LL.Add(i); return true; });

            L.EnumeratorRange = null; L.Traversal.TraversalDir = TreeCollection.TraversalEnumeratorDir.Reverse;
            LL.Clear(); L.Traversal.Traverse((i) => { LL.Add(i); return true; });

            L.EnumeratorRange = new TreeCollection.KeyInterval<int>(5, 10); L.Traversal.TraversalDir = TreeCollection.TraversalEnumeratorDir.Normal;
            LL.Clear(); L.Traversal.Traverse((i) => { LL.Add(i); return true; });

            L.EnumeratorRange = new TreeCollection.KeyInterval<int>(5, 10); L.Traversal.TraversalDir = TreeCollection.TraversalEnumeratorDir.Reverse;
            LL.Clear(); L.Traversal.Traverse((i) => { LL.Add(i); return true; });



            return;

        }


        private void LoadSettings()
        {
            // Load background color
            if (!string.IsNullOrEmpty(Settings.Default.BackgroundColorARGB))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(Settings.Default.BackgroundColorARGB);
                    var brush = new SolidColorBrush(color);
                    this.Background = brush;
                    BtnBgColor.Background = brush;
                    LV.Background = brush;
                }
                catch (Exception ex)
                {
                    // Log or handle invalid color string if necessary
                    Console.WriteLine($"Error loading background color: {ex.Message}");
                }
            }

            // Load scale factor
            SD_Scale.Value = Math.Max(0.1, Math.Min(3.0, Settings.Default.ScaleFactor));

            // Load recurse setting
            CB_Recurse.IsChecked = Settings.Default.RecurseSubdirectories;

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

            // Save recurse setting
            Settings.Default.RecurseSubdirectories = CB_Recurse.IsChecked ?? false;

            // Save recent paths
            var recentPaths = new StringCollection();
            // Add current path first if it's valid and not already the first item
            string currentPath = CB_Path.Text;
            if (recentPaths.Contains(currentPath))
                recentPaths.Remove(currentPath);                
           
            recentPaths.Insert(0, currentPath);

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
            var ScaleFactor = Math.Max(0.1, Math.Min(3.0, e.NewValue));

            
            foreach (var item in LV.Items.OfType<LazyImage>())
            {
                item.UpdateSize(ScaleFactor);
                item.InvalidateMeasure();
                item.InvalidateArrange();
                item.InvalidateVisual();
            }

            // Force layout update
            SmartFlowPanel.Current?.InvalidateLocations();

            var panel = SmartFlowPanel.Current;
            panel?.InvalidateMeasure();
            panel?.InvalidateArrange();
            panel?.InvalidateVisual();
            panel?.InvalidateLocations();

            LV.InvalidateArrange();
            LV.InvalidateVisual();
            LV.InvalidateMeasure();

        }


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
            var dialog = new System.Windows.Forms.ColorDialog();
            var current = (this.Background as SolidColorBrush)?.Color;
            if (current.HasValue)
                dialog.Color = System.Drawing.Color.FromArgb(
                    current.Value.A, current.Value.R, current.Value.G, current.Value.B);

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var wpfColor = Color.FromArgb(
                    dialog.Color.A, dialog.Color.R, dialog.Color.G, dialog.Color.B);
                var brush = new SolidColorBrush(wpfColor);
                this.Background = brush;
                BtnBgColor.Background = brush;
                LV.Background = brush;
            }
        }


        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);

            CB_Path.Text = @"L:\Bar";
            SD_Scale.ValueChanged += SD_Scale_ValueChanged;
            // In your Window code-behind

            this.InvalidateVisual();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => { })).Wait();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => { })).Wait();
        }



        private void CB_Recurse_Changed(object sender, RoutedEventArgs e)
        {
            oldPath = null;
            TextBox_TextChanged(CB_Path, null);
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

            var recurse = (bool)LV.Dispatcher.Invoke((Func<object>)(() => { return (CB_Recurse.IsChecked.HasValue) ? CB_Recurse.IsChecked.Value : false; }), null);

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



                LV.Dispatcher.Invoke((Action<string>)((path) => { var item = new LazyImage(path, LV, this.ActualWidth, this.ActualHeight);  LV.Items.Add(item); item.UpdateSize(ScaleFactor); }), file.FullName);

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
