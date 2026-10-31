using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageViewer.Properties;

namespace ImageViewer
{
	// The image model: everything loaded for the folder (_all), the sorted and
	// filtered view (_view) with optional folder headers, display options,
	// selection, file actions and the status bar.
	public partial class MainWindow
	{
		readonly List<LazyImage> _all = new List<LazyImage>();
		List<LazyImage> _view = new List<LazyImage>();
		readonly Dictionary<string, FolderHeader> _headers = new Dictionary<string, FolderHeader>(StringComparer.OrdinalIgnoreCase);
		string _rootFolder = "";
		int _loadCounter;

		void ClearImages(string root)
		{
			_all.Clear();
			_view = new List<LazyImage>();
			_headers.Clear();
			_selected.Clear();
			_anchor = null;
			_current = null;
			_roots = FolderSet.Split(root);
			_rootFolder = _roots.Count > 0 ? _roots[0] : "";
			_shownFolder = FolderSet.Join(_roots);   // reading-position key: normalized, the same however it was typed
			UpdateModeText();                        // "3 folders"
			_userScrolled = false;
			_positionText = null;
			_loadCounter = 0;
			GridPanel.NotifyItemsCleared();
			UpdateCountText();
		}

		/// <summary>A loaded batch: inserted at its sorted position if it passes the filter.</summary>
		void AddImages(List<LazyImage> batch)
		{
			var cmp = CreateComparer();
			foreach (var img in batch)
			{
				img.LoadIndex = _loadCounter++;
				_all.Add(img);
				if (!PassesFilter(img)) continue;
				int i = _view.BinarySearch(img, cmp);
				_view.Insert(i < 0 ? ~i : i, img);
			}
			PublishView();
		}

		/// <summary>Sort or filter changed: rebuild the view from everything loaded.</summary>
		void RebuildView()
		{
			var keepTop = GridPanel.FirstVisibleImage(out _);
			foreach (var img in _all) img.ViewIndex = -1;
			_view = _all.Where(PassesFilter).ToList();
			_view.Sort(CreateComparer());
			PublishView();
			_selected.RemoveWhere(x => { if (x.ViewIndex >= 0) return false; x.IsSelected = false; return true; });
			if (_current != null && _current.ViewIndex < 0) SetCurrent(null);   // filtered out
			UpdateCountText();
			if (keepTop != null && keepTop.ViewIndex >= 0)
				Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => GridPanel.ScrollIntoView(keepTop, true)));
			else
				LV.ScrollToVerticalOffset(0);
		}

		void PublishView()
		{
			bool groups = GroupingOn && _view.Count > 0
			              && _view.Any(v => !string.Equals(v.Folder, _view[0].Folder, StringComparison.OrdinalIgnoreCase));
			var items = new List<FrameworkElement>(_view.Count + 16);
			string lastFolder = null;
			FolderHeader header = null;
			int count = 0;
			for (int i = 0; i < _view.Count; i++)
			{
				var img = _view[i];
				img.ViewIndex = i;
				if (groups && !string.Equals(img.Folder, lastFolder, StringComparison.OrdinalIgnoreCase))
				{
					if (header != null) header.Count = count;
					header = GetHeader(img.Folder);
					items.Add(header);
					lastFolder = img.Folder;
					count = 0;
				}
				items.Add(img);
				count++;
			}
			if (header != null) header.Count = count;
			GridPanel.SetItems(items);
			UpdateCountText();
			UpdateEmptyState(_isLoading);
		}

		FolderHeader GetHeader(string folder)
		{
			FolderHeader h;
			if (_headers.TryGetValue(folder, out h)) return h;
			// Relative to its root folder; with several root folders, prefixed by the root's name.
			string title = folder;
			string root = _roots.Count > 0 ? _roots[RootIndexOf(folder)] : _rootFolder;
			if (root.Length > 0 && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
			{
				var rel = folder.Substring(root.Length).TrimStart('\\');
				string rootName = FolderSet.NameOf(root);
				title = rel.Length == 0 ? rootName : MultiRoot ? rootName + "\\" + rel : rel;
			}
			h = new FolderHeader(folder, title);
			_headers[folder] = h;
			return h;
		}

		internal void OpenFolderFromHeader(string folder)
		{
			if (Directory.Exists(folder)) OpenFolder(folder);
		}


		// ------------------------------------------------------------------
		//  Sorting
		// ------------------------------------------------------------------

		IComparer<LazyImage> CreateComparer()
		{
			return new ViewOrder
			{
				By = Settings.Default.SortBy ?? "Name",
				Descending = Settings.Default.SortDescending,
				Group = GroupingOn,
			};
		}

		// Folder groups: subfolders included, or several folders shown.
		bool GroupingOn { get { return Settings.Default.GroupByFolder && (TGB_Recurse.IsChecked == true || MultiRoot); } }

		sealed class ViewOrder : IComparer<LazyImage>
		{
			public string By;
			public bool Descending;
			public bool Group;

			public int Compare(LazyImage a, LazyImage b)
			{
				if (ReferenceEquals(a, b)) return 0;
				int c;
				// Folder groups: root folders in the order given, then natural
				// folder order (same as the scan).
				if (Group && (c = a.RootIndex.CompareTo(b.RootIndex)) != 0) return c;
				if (Group && (c = Native.CompareNatural(a.Folder, b.Folder)) != 0) return c;
				switch (By)
				{
					case "Date": c = a.Modified.CompareTo(b.Modified); break;
					case "Size": c = a.FileSize.CompareTo(b.FileSize); break;
					case "Dimensions": c = ((long)a.PixelWidth * a.PixelHeight).CompareTo((long)b.PixelWidth * b.PixelHeight); break;
					default:
						c = Native.CompareNatural(a.FileName, b.FileName);
						if (c == 0) c = Native.CompareNatural(a.Folder, b.Folder);
						break;
				}
				if (Descending) c = -c;
				return c != 0 ? c : a.LoadIndex.CompareTo(b.LoadIndex);
			}
		}


		// ------------------------------------------------------------------
		//  Filter
		// ------------------------------------------------------------------

		string[] _filterTerms = new string[0];
		Regex[] _filterRegex = new Regex[0];
		DispatcherTimer _filterTimer;

		bool IsFiltered { get { return _filterTerms.Length > 0; } }

		bool PassesFilter(LazyImage img)
		{
			if (_filterTerms.Length == 0) return true;
			string root = RootOf(img);
			string rel = root.Length > 0 && img.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
				? img.Path.Substring(root.Length).TrimStart('\\') : img.Path;
			if (MultiRoot) rel = FolderSet.NameOf(root) + "\\" + rel;   // the root's name filters too
			for (int i = 0; i < _filterTerms.Length; i++)
			{
				var rx = _filterRegex[i];
				if (rx != null)
				{
					string target = _filterTerms[i].IndexOf('\\') >= 0 || _filterTerms[i].IndexOf('/') >= 0 ? rel.Replace('/', '\\') : img.FileName;
					if (!rx.IsMatch(target)) return false;
				}
				else if (rel.IndexOf(_filterTerms[i], StringComparison.OrdinalIgnoreCase) < 0) return false;
			}
			return true;
		}

		void SetFilter(string text)
		{
			var terms = (text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			_filterTerms = terms;
			_filterRegex = terms.Select(t => t.IndexOf('*') >= 0 || t.IndexOf('?') >= 0
				? new Regex("^" + Regex.Escape(t.Replace('/', '\\')).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
				: null).ToArray();
			RebuildView();
		}

		private void TB_Filter_TextChanged(object sender, TextChangedEventArgs e)
		{
			bool empty = string.IsNullOrEmpty(TB_Filter.Text);
			FilterHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
			BtnClearFilter.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
			if (_filterTimer == null)
			{
				_filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
				_filterTimer.Tick += (s, ev) => { _filterTimer.Stop(); SetFilter(TB_Filter.Text); };
			}
			_filterTimer.Stop();
			_filterTimer.Start();
		}

		private void BtnClearFilter_Click(object sender, RoutedEventArgs e) { ClearFilter(); }

		void ClearFilter()
		{
			TB_Filter.Text = "";
			if (_filterTimer != null) _filterTimer.Stop();
			if (IsFiltered) SetFilter("");
			LV.Focus();
		}


		// ------------------------------------------------------------------
		//  Display options and the View menu
		// ------------------------------------------------------------------

		ContextMenu _viewMenu;
		MenuItem MI(string name) { return (MenuItem)LogicalTreeHelper.FindLogicalNode(_viewMenu, name); }

		void InitViewMenu()
		{
			_viewMenu = (ContextMenu)FindResource("ViewMenu");
		}

		void SyncViewMenu()
		{
			var s = Settings.Default;
			MI("MI_SortName").IsChecked = s.SortBy == "Name" || string.IsNullOrEmpty(s.SortBy);
			MI("MI_SortDate").IsChecked = s.SortBy == "Date";
			MI("MI_SortSize").IsChecked = s.SortBy == "Size";
			MI("MI_SortDims").IsChecked = s.SortBy == "Dimensions";
			MI("MI_SortAsc").IsChecked = !s.SortDescending;
			MI("MI_SortDesc").IsChecked = s.SortDescending;
			MI("MI_Group").IsChecked = s.GroupByFolder;
			MI("MI_Group").IsEnabled = TGB_Recurse.IsChecked == true || MultiRoot;
			MI("MI_Tiles").IsChecked = s.TileMode;
			MI("MI_Names").IsChecked = s.ShowFileNames;
			MI("MI_Spread").IsChecked = s.TwoPageSpread;
			MI("MI_Rtl").IsChecked = s.RightToLeft;
			MI("MI_Cover").IsChecked = s.SpreadCoverAlone;
			MI("MI_Cover").IsEnabled = s.TwoPageSpread;
			MI("MI_Info").IsChecked = IsInfoOpen;
			MI("MI_FullScreen").IsChecked = IsFullScreen;
		}

		private void BtnView_Click(object sender, RoutedEventArgs e)
		{
			SyncViewMenu();
			_viewMenu.PlacementTarget = BtnView;
			_viewMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
			_viewMenu.IsOpen = true;
		}

		private void MI_Sort_Click(object sender, RoutedEventArgs e)
		{
			Settings.Default.SortBy = (string)((MenuItem)sender).Tag;
			SyncViewMenu();
			RebuildView();
		}

		private void MI_SortDir_Click(object sender, RoutedEventArgs e)
		{
			Settings.Default.SortDescending = (string)((MenuItem)sender).Tag == "Desc";
			SyncViewMenu();
			RebuildView();
		}

		private void MI_Group_Click(object sender, RoutedEventArgs e)
		{
			Settings.Default.GroupByFolder = !Settings.Default.GroupByFolder;
			SyncViewMenu();
			RebuildView();
		}

		private void MI_Tiles_Click(object sender, RoutedEventArgs e) { ToggleTiles(); }
		private void MI_Names_Click(object sender, RoutedEventArgs e) { ToggleNames(); }

		void ToggleTiles()
		{
			Settings.Default.TileMode = !Settings.Default.TileMode;
			ApplyDisplayOptions();
			ApplyScale();
			UpdateModeText();
		}

		void ToggleNames()
		{
			Settings.Default.ShowFileNames = !Settings.Default.ShowFileNames;
			ApplyDisplayOptions();
			ApplyScale();
		}

		private void MI_Spread_Click(object sender, RoutedEventArgs e)
		{
			Settings.Default.TwoPageSpread = !Settings.Default.TwoPageSpread;
			ApplyDisplayOptions();
			ApplyScale();
			UpdateModeText();
		}

		private void MI_Cover_Click(object sender, RoutedEventArgs e)
		{
			Settings.Default.SpreadCoverAlone = !Settings.Default.SpreadCoverAlone;
			ApplyDisplayOptions();
			ApplyScale();
		}

		private void MI_Rtl_Click(object sender, RoutedEventArgs e)
		{
			Settings.Default.RightToLeft = !Settings.Default.RightToLeft;
			ApplyDisplayOptions();
			ApplyScale();
			UpdateModeText();
		}

		/// <summary>Pushes display settings into the image / panel statics.</summary>
		void ApplyDisplayOptions()
		{
			var s = Settings.Default;
			LazyImage.ShowNames = s.ShowFileNames;
			LazyImage.TileMode = s.TileMode;
			LazyImage.TransparencyBrush = Backdrop.BrushFor(s.TransparencyBackground);
			UpdateBackdropUi();
			SmartFlowPanel.TwoPageSpread = s.TwoPageSpread;
			SmartFlowPanel.RightToLeft = s.RightToLeft;
			SmartFlowPanel.CoverAlone = s.SpreadCoverAlone;
			SmartFlowPanel.CenterPages = s.CenterPages;
			SmartFlowPanel.Pack = s.PackImages;
			UpdateArrangeButton();
			GridPanel.RefreshRealizedVisuals();
		}


		// ------------------------------------------------------------------
		//  Arrange button (C): center pages in book view, pack the grid otherwise
		// ------------------------------------------------------------------

		bool IsBookLayout { get { return IsFitToWidthEnabled && !LazyImage.TileMode; } }

		/// <summary>
		/// One button, two options: in book view it centers the pages
		/// (CenterPages); in the grid it packs images densely (PackImages),
		/// stacking small ones beside and under large ones.
		/// </summary>
		void ToggleArrange()
		{
			bool on;
			if (IsBookLayout) on = Settings.Default.CenterPages = !Settings.Default.CenterPages;
			else on = Settings.Default.PackImages = !Settings.Default.PackImages;

			double fraction;
			var top = GridPanel.FirstVisibleImage(out fraction);
			ApplyDisplayOptions();
			GridPanel.InvalidateLocations();
			var keep = _current ?? top;
			if (keep != null)
				Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => GridPanel.ScrollIntoView(keep, keep == top)));
			ShowToast(IsBookLayout ? (on ? "Pages centered" : "Pages aligned left")
				: (on ? "Packed: small images fill the gaps (order changes locally)" : "Images in order"));
		}

		bool _syncingArrange;
		void UpdateArrangeButton()
		{
			if (TGB_Arrange == null) return;
			bool book = IsBookLayout;
			_syncingArrange = true;
			TGB_Arrange.IsChecked = book ? Settings.Default.CenterPages : Settings.Default.PackImages;
			_syncingArrange = false;
			TGB_Arrange.Content = book ? "\uE8E3" : "\uF0E2";   // AlignCenter / GridView
			TGB_Arrange.ToolTip = book
				? "Center the pages in book view (C)"
				: "Pack images: fill the space beside and under large images with smaller ones, reordering them locally (C)";
		}

		void TGB_Arrange_Click(object sender, RoutedEventArgs e)
		{
			if (!_syncingArrange) ToggleArrange();
		}


		// ------------------------------------------------------------------
		//  Backdrop behind transparent images (toolbar swatch, B / Shift+B)
		// ------------------------------------------------------------------

		/// <summary>
		/// Next (+1) / previous (-1) backdrop: checkerboard, same as the image
		/// background, black, white, dark red, dark green, dark blue.
		/// </summary>
		void CycleBackdrop(int step)
		{
			SetBackdrop(Backdrop.Step(Settings.Default.TransparencyBackground, step));
		}

		void SetBackdrop(string value)
		{
			Settings.Default.TransparencyBackground = value;
			ApplyDisplayOptions();   // redraws the realized images and the preview
			ShowToast("Behind transparent images: " + Backdrop.NameOf(value));
		}

		void UpdateBackdropUi()
		{
			var value = Settings.Default.TransparencyBackground;
			if (BackdropSwatch != null) BackdropSwatch.Background = Backdrop.BrushFor(value, swatch: true);
			if (BtnBackdrop != null)
				BtnBackdrop.ToolTip = "Behind transparent images: " + Backdrop.NameOf(value)
					+ "\nClick (B): next color   Shift+click (Shift+B): previous   Right-click: choose";
			UpdatePreviewBackdrop();
		}

		void UpdatePreviewBackdrop()
		{
			if (PreviewBackdrop == null) return;
			var item = _previewItem;
			bool alpha = IsPreviewOpen && item != null && (_previewHasAlpha || item.HasTransparency);
			PreviewBackdrop.Fill = alpha ? LazyImage.TransparencyBrush : null;
		}

		private void BtnBackdrop_Click(object sender, RoutedEventArgs e)
		{
			CycleBackdrop(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : +1);
		}

		// Right-click: every choice with its swatch, the current one checked.
		private void BtnBackdrop_ContextMenuOpening(object sender, ContextMenuEventArgs e) { FillBackdropMenu(); }

		void FillBackdropMenu()
		{
			var menu = BtnBackdrop.ContextMenu;
			menu.Items.Clear();
			string current = Settings.Default.TransparencyBackground;
			var choices = Backdrop.Choices().Select(c => c.Value).Concat(new[] { "None" });
			foreach (var value in choices)
			{
				var v = value;
				var swatch = new Border
				{
					Width = 14, Height = 14, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
					Background = Backdrop.BrushFor(v, swatch: true),
				};
				swatch.SetResourceReference(Border.BorderBrushProperty, "Fg.Secondary");
				// The swatch takes the icon column (where a check mark would go),
				// so the current choice is marked on the right and in bold.
				bool isCurrent = string.Equals(Backdrop.NameOf(current), Backdrop.NameOf(v), StringComparison.Ordinal);
				var item = new MenuItem
				{
					Header = Backdrop.NameOf(v), Icon = swatch,
					InputGestureText = isCurrent ? "✓" : "",
					FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
				};
				item.Click += (s2, e2) => SetBackdrop(v);
				menu.Items.Add(item);
			}
		}


		// ------------------------------------------------------------------
		//  Status bar
		// ------------------------------------------------------------------

		string _positionText;

		void UpdateCountText()
		{
			if (SB_NumFilesRead == null) return;
			int n = _view.Count, total = _all.Count;
			string text;
			// "12 / 240 images" while reading; "18 of 37 images" when filtered.
			string pos = !_isLoading && _positionText != null && n > 0 ? _positionText + " / " : "";
			if (_isLoading) text = "Loading… " + total.ToString("N0");
			else if (n == total) text = pos + n.ToString("N0") + (n == 1 ? " image" : " images");
			else if (pos.Length > 0) text = pos + n.ToString("N0") + "  ·  filtered from " + total.ToString("N0");
			else text = n.ToString("N0") + " of " + total.ToString("N0") + " images";
			if (_selected.Count > 0) text += "  ·  " + _selected.Count.ToString("N0") + " selected";
			SB_NumFilesRead.Text = text;
		}

		void UpdateModeText()
		{
			if (SB_Mode == null) return;
			var parts = new List<string>(5);
			if (IsFitToWidthEnabled && !LazyImage.TileMode)
				parts.Add("Book view" + (SmartFlowPanel.TwoPageSpread ? ", 2 pages" : "") + (SmartFlowPanel.RightToLeft ? ", right to left" : ""));
			else if (SmartFlowPanel.RightToLeft) parts.Add("Right to left");
			if (LazyImage.TileMode) parts.Add("Tiles");
			if (TGB_ScrollByImage.IsChecked == true) parts.Add("Row scrolling");
			if (TGB_Recurse.IsChecked == true) parts.Add("Subfolders");
			if (MultiRoot) parts.Add(_roots.Count + " folders");
			if (IsAutoAdvancing) parts.Add("Auto-advance " + AutoAdvanceSeconds + " s");
			if (Profiles.Enabled && _placeText != null) parts.Add(_placeText);
			SB_Mode.Text = string.Join("  ·  ", parts);
		}

		// Monitor / virtual desktop of this window, for the status bar when
		// profiles are on. Refreshed when the window is (re)activated - e.g.
		// after switching virtual desktops - or moved to another monitor.
		string _placeText;
		IntPtr _placeMonitor;

		Place CurrentPlace()
		{
			var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			return hwnd != IntPtr.Zero ? Placement.OfWindow(hwnd) : Profiles.LaunchPlace ?? new Place();
		}

		void RefreshPlace()
		{
			if (!Profiles.Enabled) { _placeText = null; UpdateModeText(); return; }
			var p = CurrentPlace();
			var parts = new List<string>(2);
			if (Profiles.PerMonitor) parts.Add(p.MonitorName);
			if (Profiles.PerDesktop && !string.IsNullOrEmpty(p.DesktopName)) parts.Add(p.DesktopName);
			_placeText = "Profile: " + string.Join(" · ", parts);
			UpdateModeText();
		}

		// The image under the mouse: status bar info and the target of the
		// keyboard / context-menu actions.
		LazyImage _hovered;

		internal void OnImageHover(LazyImage img, bool entered)
		{
			if (entered)
			{
				_hovered = img;
				if (!_keyboardMode) SB_Size.Text = DescribeImage(img);
			}
			else if (_hovered == img)
			{
				_hovered = null;
			}
			if (!_keyboardMode) UpdateInfo();
		}

		static string DescribeImage(LazyImage img)
		{
			string size = img.FileSize >= 1048576 ? (img.FileSize / 1048576.0).ToString("0.0") + " MB"
				: Math.Max(1, img.FileSize / 1024).ToString("N0") + " KB";
			string rot = img.UserRotation != 0 ? "     rotated " + img.UserRotation + "°" : "";
			return img.FileName + "     " + img.PixelWidth + " × " + img.PixelHeight + " px     " + size
				+ "     " + img.Modified.ToString("g") + rot + "     " + img.Folder;
		}


		// ------------------------------------------------------------------
		//  Loading / empty states
		// ------------------------------------------------------------------

		bool _isLoading;

		void SetLoading(bool loading)
		{
			_isLoading = loading;
			if (loading)
			{
				LoadingBar.Visibility = Visibility.Visible;
				LoadingBarShift.BeginAnimation(TranslateTransform.XProperty,
					new System.Windows.Media.Animation.DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.1))
					{ RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
			}
			else
			{
				LoadingBarShift.BeginAnimation(TranslateTransform.XProperty, null);   // stop: no idle rendering
				LoadingBar.Visibility = Visibility.Collapsed;
			}
			UpdateCountText();
			UpdateEmptyState(loading);
		}

		void UpdateEmptyState(bool loading)
		{
			bool empty = !loading && _view.Count == 0;
			EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
			if (!empty) return;

			if (_all.Count > 0 && IsFiltered)
			{
				EmptyTitle.Text = "No images match the filter";
				EmptyHint.Text = "\"" + string.Join(" ", _filterTerms) + "\" matches none of the " + _all.Count.ToString("N0") + " images.";
				EmptyAction.Content = "Clear filter";
			}
			else if (string.IsNullOrEmpty(oldPath))
			{
				EmptyTitle.Text = "No folder open";
				EmptyHint.Text = "Choose a folder (Ctrl+O) or drop one onto this window.";
				EmptyAction.Content = "Open folder…";
			}
			else
			{
				EmptyTitle.Text = MultiRoot ? "No images in these folders" : "No images in this folder";
				EmptyHint.Text = TGB_Recurse.IsChecked == true
					? "Neither this folder nor its subfolders contain supported images."
					: "Turn on \"Include subfolders\" in the toolbar to look deeper.";
				EmptyAction.Content = "Open folder…";
			}
		}

		private void EmptyAction_Click(object sender, RoutedEventArgs e)
		{
			if (_all.Count > 0 && IsFiltered) ClearFilter();
			else BtnBrowse_Click(sender, e);
		}


		// ------------------------------------------------------------------
		//  Selection
		// ------------------------------------------------------------------

		readonly HashSet<LazyImage> _selected = new HashSet<LazyImage>();
		LazyImage _anchor;
		LazyImage _current;   // keyboard focus (arrow keys); drawn as a dashed outline

		void SetCurrent(LazyImage img)
		{
			if (_current == img) return;
			if (_current != null) _current.IsCurrent = false;
			_current = img;
			if (img != null) img.IsCurrent = true;
		}

		void ClearSelection()
		{
			foreach (var s in _selected) s.IsSelected = false;
			_selected.Clear();
			UpdateCountText();
		}

		void SelectOnly(LazyImage img)
		{
			ClearSelection();
			if (img == null) return;
			img.IsSelected = true;
			_selected.Add(img);
			_anchor = img;
			UpdateCountText();
		}

		void ToggleSelection(LazyImage img)
		{
			if (_selected.Remove(img)) img.IsSelected = false;
			else { _selected.Add(img); img.IsSelected = true; }
			_anchor = img;
			UpdateCountText();
		}

		void SelectRange(LazyImage to)
		{
			var from = _anchor != null && _anchor.ViewIndex >= 0 ? _anchor : to;
			foreach (var s in _selected) s.IsSelected = false;
			_selected.Clear();
			// As laid out on screen (packing reorders images locally).
			foreach (var img in GridPanel.ImagesBetween(from, to)) { img.IsSelected = true; _selected.Add(img); }
			UpdateCountText();
		}

		void SelectAll()
		{
			foreach (var img in _view) { img.IsSelected = true; _selected.Add(img); }
			UpdateCountText();
		}

		/// <summary>
		/// What an action applies to: the selection when the clicked/hovered
		/// image is part of it (or nothing was clicked), else just that image.
		/// </summary>
		List<LazyImage> Targets(LazyImage clicked)
		{
			if (clicked != null && !_selected.Contains(clicked)) return new List<LazyImage> { clicked };
			if (_selected.Count > 0) return _selected.OrderBy(x => x.ViewIndex).ToList();
			return clicked != null ? new List<LazyImage> { clicked } : new List<LazyImage>();
		}


		// ------------------------------------------------------------------
		//  Actions
		// ------------------------------------------------------------------

		LazyImage _contextTarget;

		void LV_ContextMenuOpening(object sender, ContextMenuEventArgs e)
		{
			_contextTarget = _hovered;
			var targets = Targets(_contextTarget);
			if (targets.Count == 0) { e.Handled = true; return; }   // empty space, nothing selected: no menu

			string n = targets.Count == 1 ? "" : " " + targets.Count.ToString("N0") + " images";
			MI_CopyImage.Header = "Copy" + n;
			MI_CopyTo.Header = "Copy" + n + " to folder…";
			MI_MoveTo.Header = "Move" + n + " to folder…";
			MI_Delete.Header = "Delete" + n;
			MI_ResetRotation.IsEnabled = targets.Any(t => t.UserRotation != 0);
		}

		LazyImage ContextItem { get { return _contextTarget ?? Targets(null).FirstOrDefault(); } }

		private void MI_Preview_Click(object sender, RoutedEventArgs e) { OpenPreview(ContextItem); }
		private void MI_OpenExternal_Click(object sender, RoutedEventArgs e) { OpenExternal(ContextItem); }
		private void MI_ShowInExplorer_Click(object sender, RoutedEventArgs e) { ShowInExplorer(ContextItem); }
		private void MI_Slideshow_Click(object sender, RoutedEventArgs e) { OpenPreview(ContextItem ?? _view.FirstOrDefault()); StartSlideshow(); }
		private void MI_CopyImage_Click(object sender, RoutedEventArgs e) { CopyToClipboard(Targets(_contextTarget)); }
		private void MI_CopyPath_Click(object sender, RoutedEventArgs e) { CopyPaths(Targets(_contextTarget)); }
		private void MI_CopyTo_Click(object sender, RoutedEventArgs e) { CopyOrMoveTo(Targets(_contextTarget), false); }
		private void MI_MoveTo_Click(object sender, RoutedEventArgs e) { CopyOrMoveTo(Targets(_contextTarget), true); }
		private void MI_Delete_Click(object sender, RoutedEventArgs e) { DeleteImages(Targets(_contextTarget)); }
		private void MI_SelectAll_Click(object sender, RoutedEventArgs e) { SelectAll(); }

		private void MI_Rotate_Click(object sender, RoutedEventArgs e)
		{
			var tag = (string)((MenuItem)sender).Tag;
			if (tag == "reset") RotateImages(Targets(_contextTarget), 0, true);
			else RotateImages(Targets(_contextTarget), int.Parse(tag), false);
		}

		static int SavedRotation(string path)
		{
			int deg;
			return int.TryParse(AppData.Rotations.Get(path), out deg) ? deg : 0;
		}

		/// <summary>Rotates by delta degrees (or resets); remembered per file, the file is never modified.</summary>
		void RotateImages(List<LazyImage> targets, int delta, bool reset)
		{
			if (targets.Count == 0) return;
			var changes = new Dictionary<string, string>();
			foreach (var img in targets)
			{
				int deg = reset ? 0 : ((img.UserRotation + delta) % 360 + 360) % 360;
				img.SetUserRotation(deg);
				changes[img.Path] = deg == 0 ? null : deg.ToString();
			}
			AppData.Rotations.SetMany(changes);
			GridPanel.InvalidateLocations();
			if (IsPreviewOpen && _previewItem != null && targets.Contains(_previewItem)) ShowPreviewAt(_previewIndex);
			if (_hovered != null) SB_Size.Text = DescribeImage(_hovered);
		}

		void OpenExternal(LazyImage img)
		{
			if (img == null || !File.Exists(img.Path)) return;
			try
			{
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(img.Path) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "Could not open the image:\n" + ex.Message, "Image Viewer",
					MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		}

		void ShowInExplorer(LazyImage img)
		{
			if (img == null) return;
			try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + img.Path + "\""); }
			catch { /* Explorer unavailable - nothing sensible to do */ }
		}

		void CopyPaths(List<LazyImage> targets)
		{
			if (targets.Count == 0) return;
			try
			{
				Clipboard.SetText(string.Join(Environment.NewLine, targets.Select(t => t.Path)));
				ShowToast(targets.Count == 1 ? "Path copied" : targets.Count + " paths copied");
			}
			catch { /* clipboard busy */ }
		}

		/// <summary>
		/// Copies the files (paste in Explorer, attach in mail...). A single
		/// image also goes on the clipboard as a bitmap (paste into editors).
		/// </summary>
		void CopyToClipboard(List<LazyImage> targets)
		{
			if (targets.Count == 0) return;
			var files = new StringCollection();
			foreach (var t in targets) files.Add(t.Path);

			if (targets.Count > 1)
			{
				try { var d = new DataObject(); d.SetFileDropList(files); Clipboard.SetDataObject(d, true); } catch { }
				ShowToast(targets.Count + " images copied");
				return;
			}
			var img = targets[0];
			ImageLoader.DecodeAsync(img.Path, img.CacheKey, 0, img.Transform, Dispatcher, bmp =>
			{
				try
				{
					var d = new DataObject();
					d.SetFileDropList(files);
					if (bmp != null)
						d.SetImage(ImageLoader.IsMirrored(img.Transform)
							? (System.Windows.Media.Imaging.BitmapSource)new System.Windows.Media.Imaging.TransformedBitmap(bmp, new ScaleTransform(-1, 1))
							: bmp);
					Clipboard.SetDataObject(d, true);
					ShowToast("Image copied");
				}
				catch { /* clipboard busy */ }
			});
		}

		// File operations we did ourselves: the folder watcher ignores them.
		readonly Dictionary<string, DateTime> _selfChanges = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

		void NoteSelfChange(string path) { lock (_selfChanges) _selfChanges[path] = DateTime.Now.AddSeconds(5); }

		bool IsSelfChange(string path)
		{
			lock (_selfChanges)
			{
				DateTime until;
				return path != null && _selfChanges.TryGetValue(path, out until) && DateTime.Now < until;
			}
		}

		void CopyOrMoveTo(List<LazyImage> targets, bool move)
		{
			if (targets.Count == 0) return;
			var dest = Native.PickFolder(this, Path.GetDirectoryName(targets[0].Path),
				(move ? "Move " : "Copy ") + (targets.Count == 1 ? targets[0].FileName : targets.Count + " images") + " to…");
			if (string.IsNullOrEmpty(dest)) return;

			var done = new List<LazyImage>();
			var errors = new List<string>();
			foreach (var img in targets)
			{
				try
				{
					string target = UniqueName(Path.Combine(dest, img.FileName));
					if (string.Equals(target, img.Path, StringComparison.OrdinalIgnoreCase)) continue;
					NoteSelfChange(img.Path);
					NoteSelfChange(target);
					if (move)
					{
						File.Move(img.Path, target);
						AppData.Rotations.Rename(img.Path, target);
					}
					else
					{
						File.Copy(img.Path, target);
						if (img.UserRotation != 0) AppData.Rotations.Set(target, img.UserRotation.ToString());
					}
					done.Add(img);
				}
				catch (Exception ex) { errors.Add(img.FileName + ": " + ex.Message); }
			}

			if (move) RemoveFromView(done);
			ShowToast((move ? "Moved " : "Copied ") + (done.Count == targets.Count ? done.Count.ToString() : done.Count + " of " + targets.Count) + " to " + dest);
			if (errors.Count > 0) ShowErrors(errors);
		}

		static string UniqueName(string path)
		{
			if (!File.Exists(path)) return path;
			string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
			for (int i = 2; ; i++)
			{
				var p = Path.Combine(dir, name + " (" + i + ")" + ext);
				if (!File.Exists(p)) return p;
			}
		}

		void DeleteImages(List<LazyImage> targets)
		{
			if (targets.Count == 0) return;
			string what = targets.Count == 1 ? "\"" + targets[0].FileName + "\"" : targets.Count.ToString("N0") + " images";
			if (MessageBox.Show(this, "Move " + what + " to the Recycle Bin?", "Delete",
				MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

			var done = new List<LazyImage>();
			var errors = new List<string>();
			foreach (var img in targets)
			{
				try
				{
					NoteSelfChange(img.Path);
					Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(img.Path,
						Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
						Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
					done.Add(img);
				}
				catch (OperationCanceledException) { break; }
				catch (Exception ex) { errors.Add(img.FileName + ": " + ex.Message); }
			}
			// Forget their rotations in one store write.
			var forget = done.Where(d => d.UserRotation != 0).ToDictionary(d => d.Path, d => (string)null, StringComparer.OrdinalIgnoreCase);
			if (forget.Count > 0) AppData.Rotations.SetMany(forget);
			RemoveFromView(done);
			if (done.Count > 0) ShowToast(done.Count == 1 ? "Moved to the Recycle Bin" : done.Count + " images moved to the Recycle Bin");
			if (errors.Count > 0) ShowErrors(errors);
		}

		void RemoveFromView(List<LazyImage> removed)
		{
			if (removed.Count == 0) return;
			var set = new HashSet<LazyImage>(removed);
			foreach (var img in removed)
			{
				img.UnLoadImage();
				img.IsSelected = false;
				_selected.Remove(img);
				if (_hovered == img) _hovered = null;
				if (_anchor == img) _anchor = null;
			}
			// Keep a keyboard focus: the image that took the first removed one's place.
			LazyImage nextCurrent = null;
			if (_current != null && set.Contains(_current))
			{
				int at = _current.ViewIndex;
				nextCurrent = _view.Skip(Math.Max(0, at)).FirstOrDefault(v => !set.Contains(v))
				              ?? _view.Take(Math.Max(0, at)).LastOrDefault(v => !set.Contains(v));
				SetCurrent(null);
			}
			_all.RemoveAll(set.Contains);
			_view.RemoveAll(set.Contains);
			if (IsPreviewOpen && _previewItem != null && set.Contains(_previewItem))
			{
				if (_view.Count == 0) ClosePreview();
				else ShowPreviewAt(Math.Min(_previewIndex, _view.Count - 1));
			}
			PublishView();
			if (nextCurrent != null && _keyboardMode) { SetCurrent(nextCurrent); SelectOnly(nextCurrent); }
		}

		void ShowErrors(List<string> errors)
		{
			MessageBox.Show(this, string.Join("\n", errors.Take(12)) + (errors.Count > 12 ? "\n…" : ""), "Image Viewer",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}
}
