using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ImageViewer
{
	// Full screen (F11), short confirmation toasts, the keyboard-shortcut
	// sheet (F1) and renaming (F2).
	public partial class MainWindow
	{
		// ------------------------------------------------------------------
		//  Full screen
		// ------------------------------------------------------------------

		bool _fullScreen;
		Rect _restoreBounds;
		WindowState _restoreState;
		Visibility _restoreStatusBar;
		bool _toolbarRevealed;

		bool IsFullScreen { get { return _fullScreen; } }

		/// <summary>The user's status bar choice (full screen hides it only temporarily).</summary>
		bool StatusBarWanted
		{
			get { return (_fullScreen ? _restoreStatusBar : SB_Main.Visibility) == Visibility.Visible; }
			set
			{
				var v = value ? Visibility.Visible : Visibility.Collapsed;
				if (_fullScreen) _restoreStatusBar = v; else SB_Main.Visibility = v;
			}
		}

		/// <summary>
		/// Covers the whole monitor (taskbar included) with only the images:
		/// no toolbar, no status bar. Moving the mouse to the top edge shows
		/// the toolbar while it is used.
		/// </summary>
		void ToggleFullScreen()
		{
			var chrome = WindowChrome.GetWindowChrome(this);
			if (!_fullScreen)
			{
				var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
				var src = PresentationSource.FromVisual(this);
				Rect px;
				if (hwnd == IntPtr.Zero || src == null || src.CompositionTarget == null || !Native.TryGetMonitorBounds(hwnd, out px)) return;
				var m = src.CompositionTarget.TransformFromDevice;
				var tl = m.Transform(px.TopLeft);
				var br = m.Transform(px.BottomRight);

				_restoreState = WindowState;
				_restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
				_restoreStatusBar = SB_Main.Visibility;
				_fullScreen = true;

				WindowState = WindowState.Normal;
				if (chrome != null) { chrome.ResizeBorderThickness = new Thickness(0); chrome.CaptionHeight = 0; }
				SetToolbarShown(false);
				SB_Main.Visibility = Visibility.Collapsed;
				Left = tl.X; Top = tl.Y; Width = br.X - tl.X; Height = br.Y - tl.Y;
				ShowToast("Full screen  ·  F11 or Esc to leave, mouse at the top edge for the toolbar");
			}
			else
			{
				_fullScreen = false;
				if (chrome != null) { chrome.ResizeBorderThickness = new Thickness(6); chrome.CaptionHeight = 40; }
				SetToolbarShown(true);
				SB_Main.Visibility = _restoreStatusBar;
				Left = _restoreBounds.Left; Top = _restoreBounds.Top;
				Width = _restoreBounds.Width; Height = _restoreBounds.Height;
				WindowState = _restoreState;
			}
			UpdateMaximizeGlyph();
		}

		void SetToolbarShown(bool show)
		{
			_toolbarRevealed = show && _fullScreen;
			RootGrid.RowDefinitions[0].Height = new GridLength(show ? 40 : 0);
			TB_Main.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		}

		// Full screen: reveal the toolbar at the top edge, hide it again once
		// the mouse moves well below it (not while a menu / the path box is in use).
		void FullScreenMouseMove(Point p)
		{
			if (!_fullScreen) return;
			if (!_toolbarRevealed && p.Y <= 2) SetToolbarShown(true);
			else if (_toolbarRevealed && p.Y > 80 && !TB_Main.IsKeyboardFocusWithin && !_viewMenu.IsOpen) SetToolbarShown(false);
		}

		private void MI_FullScreen_Click(object sender, RoutedEventArgs e) { ToggleFullScreen(); }


		// ------------------------------------------------------------------
		//  Toast
		// ------------------------------------------------------------------

		DispatcherTimer _toastTimer;

		/// <summary>A short confirmation at the top of the image area (also over the preview).</summary>
		internal void ShowToast(string text)
		{
			ToastText.Text = text;
			Toast.Visibility = Visibility.Visible;
			Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
			if (_toastTimer == null)
			{
				_toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
				_toastTimer.Tick += (s, e) =>
				{
					_toastTimer.Stop();
					var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
					fade.Completed += (s2, e2) => { if (!_toastTimer.IsEnabled) Toast.Visibility = Visibility.Collapsed; };
					Toast.BeginAnimation(OpacityProperty, fade);
				};
			}
			_toastTimer.Stop();
			_toastTimer.Start();
		}


		// ------------------------------------------------------------------
		//  Help: keyboard shortcuts and mouse
		// ------------------------------------------------------------------

		static readonly string[][] HelpSections =
		{
			new[] { "Folders",
				"Ctrl+O", "Open a folder (Ctrl+click the folder button: add one)",
				"A; B", "Several folders in the path box: shown as one view",
				"Ctrl+L", "Type a path (click empty breadcrumb space)",
				"Alt+← / Alt+→", "Back / forward (mouse back / forward buttons)",
				"Alt+↑", "Parent folder",
				"Ctrl+PgUp / Ctrl+PgDn", "Previous / next folder (next chapter)",
				"F5", "Reload",
				"Drop", "Folders or images onto the window open them (Ctrl: add)" },
			new[] { "Browsing",
				"Arrow keys", "Move the keyboard focus (selects)",
				"Shift+arrows", "Extend the selection",
				"Ctrl+arrows", "Move the focus only",
				"Insert", "Select / unselect the focused image",
				"Home / End", "First / last image",
				"PgUp / PgDn", "Scroll a screen (row scrolling: a row)",
				"Ctrl+F", "Filter by name / subfolder (*.png, img_0??)",
				"Esc", "Clear filter, then selection, then full screen" },
			new[] { "View",
				"Ctrl + / Ctrl − / Ctrl 0", "Larger / smaller / 100%  (Ctrl+wheel)",
				"F", "Book view: fit pages to the window width",
				"C", "Pack the grid (small images fill gaps) / center pages in book view",
				"Space / Shift+Space", "Book view: next / previous page",
				"Ctrl+Space", "Auto-advance: next screen / page every few seconds",
				"T", "Uniform tiles",
				"N", "File names",
				"B / Shift+B", "Next / previous color behind transparent images (swatch button; right-click it to choose)",
				"I", "Info panel (file and camera details)",
				"F11", "Full screen",
				"View button", "Sort, group by folder, two-page spread, right to left" },
			new[] { "Images",
				"Space", "Preview",
				"Enter / double-click", "Open in the default app (or preview: Settings)",
				"R / Shift+R", "Rotate right / left (Alt+click, Alt+Shift+click)",
				"F2", "Rename",
				"Del", "Move to the Recycle Bin",
				"Ctrl+C / Ctrl+Shift+C", "Copy image(s) / path(s)",
				"Ctrl+A", "Select all",
				"Click / Ctrl / Shift", "Select / toggle / range; drag out to other apps",
				"S", "Slideshow" },
			new[] { "Preview",
				"← → / wheel", "Previous / next image",
				"Home / End", "First / last",
				"Ctrl+wheel, + / −", "Zoom (at the mouse)",
				"Z / double-click", "Fit ↔ 100% (actual pixels)",
				"Drag, wheel", "Pan while zoomed in",
				"R, S, I, Del, F2", "Rotate, slideshow, info, delete, rename",
				"B / Shift+B", "Backdrop color of transparent images",
				"Esc / Space", "Close" },
		};

		bool _helpBuilt;

		bool IsHelpOpen { get { return HelpOverlay.Visibility == Visibility.Visible; } }

		void ShowHelp(bool show)
		{
			if (show && !_helpBuilt) BuildHelp();
			HelpOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			if (!show && !IsPreviewOpen) LV.Focus();
		}

		void BuildHelp()
		{
			_helpBuilt = true;
			foreach (var section in HelpSections)
			{
				var panel = new StackPanel { Width = 340, Margin = new Thickness(10, 6, 10, 10) };
				var title = new TextBlock { Text = section[0], FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
				title.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
				panel.Children.Add(title);
				for (int i = 1; i + 1 < section.Length; i += 2)
				{
					var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					var key = new TextBlock { Text = section[i], Width = 150, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
					DockPanel.SetDock(key, Dock.Left);
					var desc = new TextBlock { Text = section[i + 1], TextWrapping = TextWrapping.Wrap };
					desc.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
					row.Children.Add(key);
					row.Children.Add(desc);
					panel.Children.Add(row);
				}
				HelpColumns.Children.Add(panel);
			}
		}

		private void MI_Help_Click(object sender, RoutedEventArgs e) { ShowHelp(true); }
		private void HelpClose_Click(object sender, RoutedEventArgs e) { ShowHelp(false); }

		private void HelpOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (e.OriginalSource == HelpOverlay) { ShowHelp(false); e.Handled = true; }   // click outside the card
		}


		// ------------------------------------------------------------------
		//  Rename (F2)
		// ------------------------------------------------------------------

		void RenameImage(LazyImage img)
		{
			if (img == null || !File.Exists(img.Path)) return;
			string ext = Path.GetExtension(img.FileName);
			string name = PromptText("Rename", "New name for \"" + img.FileName + "\":", img.FileName,
				img.FileName.Length - ext.Length);
			if (name != null) RenameImageTo(img, name);
		}

		void RenameImageTo(LazyImage img, string name)
		{
			if (img == null || !File.Exists(img.Path)) return;
			string ext = Path.GetExtension(img.FileName);
			name = name.Trim();
			if (name.Length == 0 || name == img.FileName) return;
			if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				MessageBox.Show(this, "A file name can't contain any of these characters:\n\\ / : * ? \" < > |", "Rename",
					MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			if (!string.Equals(Path.GetExtension(name), ext, StringComparison.OrdinalIgnoreCase) && ImageFiles.IsImage(img.Path)
				&& !ImageFiles.IsImage(name)
				&& MessageBox.Show(this, "\"" + name + "\" doesn't have an image extension, so the viewer won't show it any more. Rename anyway?",
					"Rename", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
				return;

			string target = Path.Combine(img.Folder, name);
			bool caseOnly = string.Equals(target, img.Path, StringComparison.OrdinalIgnoreCase);
			if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
			{
				MessageBox.Show(this, "\"" + name + "\" already exists in this folder.", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			try
			{
				NoteSelfChange(img.Path);
				NoteSelfChange(target);
				File.Move(img.Path, target);
				AppData.Rotations.Rename(img.Path, target);
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "Could not rename:\n" + ex.Message, "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			// Swap in an item for the new path (path is the image's identity:
			// cache key, rotation, sort position).
			var header = ImageHeader.Read(new FileInfo(target));
			bool wasSelected = img.IsSelected, wasCurrent = img == _current, wasPreview = IsPreviewOpen && img == _previewItem;
			int at = _all.IndexOf(img);
			img.UnLoadImage();
			img.IsSelected = false;
			img.IsCurrent = false;
			_selected.Remove(img);
			if (_hovered == img) _hovered = null;
			if (_anchor == img) _anchor = null;

			LazyImage renamed = null;
			if (header.Ok && ImageFiles.IsImage(target) && at >= 0)
			{
				renamed = CreateImage(header);
				renamed.LoadIndex = img.LoadIndex;
				_all[at] = renamed;
			}
			else if (at >= 0) _all.RemoveAt(at);

			if (wasCurrent) _current = null;
			RebuildView();
			if (renamed != null && renamed.ViewIndex >= 0)
			{
				if (wasSelected) { renamed.IsSelected = true; _selected.Add(renamed); }
				if (wasCurrent) SetCurrent(renamed);
				if (wasPreview) { _previewItem = renamed; ShowPreviewAt(renamed.ViewIndex); }
				else Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => GridPanel.ScrollIntoView(renamed)));
			}
			else if (wasPreview) ShowPreviewAt(Math.Min(_previewIndex, _view.Count - 1));
			UpdateCountText();
			ShowToast("Renamed to " + name);
		}

		/// <summary>Small themed text prompt; null when cancelled.</summary>
		string PromptText(string title, string label, string initial, int selectLength)
		{
			var box = new TextBox { Text = initial, Width = 420, Margin = new Thickness(0, 8, 0, 16) };
			var ok = new Button { Content = "OK", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
			var cancel = new Button { Content = "Cancel", IsCancel = true };
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			buttons.Children.Add(ok);
			buttons.Children.Add(cancel);
			var content = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
			content.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
			content.Children.Add(box);
			content.Children.Add(buttons);

			var dlg = new Window
			{
				Title = title, Content = content, Owner = this, SizeToContent = SizeToContent.WidthAndHeight,
				ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
				ShowInTaskbar = false, FontFamily = FontFamily, FontSize = 12,
				UseLayoutRounding = true,
			};
			dlg.SetResourceReference(BackgroundProperty, "Bg.Window");
			dlg.SetResourceReference(ForegroundProperty, "Fg.Primary");
			TextOptions.SetTextFormattingMode(dlg, TextFormattingMode.Display);
			dlg.SourceInitialized += (s, e) => { if (Theme.IsDark) Native.UseDarkTitleBar(dlg); };
			dlg.Loaded += (s, e) => { box.Focus(); box.Select(0, Math.Max(0, Math.Min(selectLength, box.Text.Length))); };
			ok.Click += (s, e) => dlg.DialogResult = true;
			return dlg.ShowDialog() == true ? box.Text : null;
		}
	}
}
