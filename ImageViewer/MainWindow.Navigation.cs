using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ImageViewer.Properties;

namespace ImageViewer
{
	// Folder navigation: back/forward history, clickable breadcrumb, sibling
	// and parent folders, remembered reading position, position indicator.
	public partial class MainWindow
	{
		// ------------------------------------------------------------------
		//  History
		// ------------------------------------------------------------------

		readonly List<string> _history = new List<string>();
		int _historyPos = -1;
		bool _navigatingHistory;

		void RecordHistory(string path)
		{
			if (_navigatingHistory) return;
			if (_historyPos >= 0 && string.Equals(_history[_historyPos], path, StringComparison.OrdinalIgnoreCase)) return;
			if (_historyPos < _history.Count - 1) _history.RemoveRange(_historyPos + 1, _history.Count - _historyPos - 1);
			_history.Add(path);
			if (_history.Count > 100) _history.RemoveAt(0);
			_historyPos = _history.Count - 1;
			UpdateHistoryButtons();
		}

		void UpdateHistoryButtons()
		{
			BtnBack.IsEnabled = _historyPos > 0;
			BtnForward.IsEnabled = _historyPos >= 0 && _historyPos < _history.Count - 1;
		}

		void NavigateHistory(int step)
		{
			int target = _historyPos + step;
			if (target < 0 || target >= _history.Count) return;
			_historyPos = target;
			_navigatingHistory = true;
			try { OpenFolder(_history[target]); }
			finally { _navigatingHistory = false; }
			UpdateHistoryButtons();
		}

		private void BtnBack_Click(object sender, RoutedEventArgs e) { NavigateHistory(-1); }
		private void BtnForward_Click(object sender, RoutedEventArgs e) { NavigateHistory(+1); }

		// Mouse back / forward buttons anywhere in the window.
		void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
		{
			if (e.ChangedButton == MouseButton.XButton1) { NavigateHistory(-1); e.Handled = true; }
			else if (e.ChangedButton == MouseButton.XButton2) { NavigateHistory(+1); e.Handled = true; }
		}

		void OpenParentFolder()
		{
			var current = oldPath ?? CB_Path.Text;
			if (string.IsNullOrEmpty(current)) return;
			if (FolderSet.IsMultiple(current)) { ShowToast("Several folders are shown - click one in the path bar to open it alone"); return; }
			var parent = Path.GetDirectoryName(current.TrimEnd('\\'));
			if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) OpenFolder(parent);
		}

		/// <summary>Next / previous folder next to the current one (natural order) - e.g. the next chapter.</summary>
		void OpenSiblingFolder(int step)
		{
			if (FolderSet.IsMultiple(oldPath)) { ShowToast("Several folders are shown - click one in the path bar to open it alone"); return; }
			var current = (oldPath ?? "").TrimEnd('\\');
			var parent = string.IsNullOrEmpty(current) ? null : Path.GetDirectoryName(current);
			if (string.IsNullOrEmpty(parent)) return;
			string[] siblings;
			try { siblings = Directory.GetDirectories(parent); } catch { return; }
			Array.Sort(siblings, Native.CompareNatural);
			int i = Array.FindIndex(siblings, d => string.Equals(d, current, StringComparison.OrdinalIgnoreCase));
			int j = i + step;
			if (i < 0 || j < 0 || j >= siblings.Length)
			{
				SB_Size.Text = step > 0 ? "This is the last folder here" : "This is the first folder here";
				return;
			}
			OpenFolder(siblings[j]);
		}


		// ------------------------------------------------------------------
		//  Breadcrumb over the path box
		// ------------------------------------------------------------------

		void BuildBreadcrumb(string path)
		{
			CrumbPanel.Children.Clear();
			var folders = FolderSet.Split(path);
			if (folders.Count > 1)
			{
				// Several folders: one button each (opens it alone), joined by "+".
				for (int i = 0; i < folders.Count; i++)
				{
					if (i > 0)
					{
						var plus = new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
						plus.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
						CrumbPanel.Children.Add(plus);
					}
					var fb = new Button
					{
						Content = FolderSet.NameOf(folders[i]), Style = (Style)FindResource("CrumbButton"), Tag = folders[i],
						ToolTip = folders[i] + "\nClick to show only this folder", FontWeight = FontWeights.SemiBold,
					};
					fb.Click += (s, e) => OpenFolder((string)((Button)s).Tag);
					CrumbPanel.Children.Add(fb);
				}
				UpdateCrumbVisibility();
				Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AlignCrumbs));
				return;
			}
			var parts = new List<string>();
			string p = path.TrimEnd('\\');
			while (!string.IsNullOrEmpty(p))
			{
				parts.Add(p);
				string parent;
				try { parent = Path.GetDirectoryName(p); } catch { break; }
				if (string.IsNullOrEmpty(parent) || parent == p) break;
				p = parent.Length > 3 ? parent.TrimEnd('\\') : parent;
			}
			parts.Reverse();

			for (int i = 0; i < parts.Count; i++)
			{
				string full = parts[i];
				string name = Path.GetFileName(full.TrimEnd('\\'));
				if (string.IsNullOrEmpty(name)) name = full.TrimEnd('\\');   // "C:" or "\\server\share"
				if (i > 0)
				{
					var sep = new TextBlock
					{
						Text = "\uE76C", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 8,
						VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 1, 1, 0),
					};
					sep.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");   // follows theme changes
					CrumbPanel.Children.Add(sep);
				}
				var b = new Button { Content = name, Style = (Style)FindResource("CrumbButton"), Tag = full, ToolTip = full };
				if (i == parts.Count - 1) b.FontWeight = FontWeights.SemiBold;
				b.Click += (s, e) =>
				{
					var target = (string)((Button)s).Tag;
					if (!string.Equals(target.TrimEnd('\\'), (oldPath ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
						OpenFolder(target.Length == 2 && target[1] == ':' ? target + "\\" : target);
				};
				CrumbPanel.Children.Add(b);
			}
			UpdateCrumbVisibility();
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AlignCrumbs));
		}

		// Long paths: keep the end (current folder) visible.
		void AlignCrumbs()
		{
			CrumbPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			CrumbPanel.HorizontalAlignment = CrumbPanel.DesiredSize.Width > Crumbs.ActualWidth - 6
				? HorizontalAlignment.Right : HorizontalAlignment.Left;
		}

		private void Crumbs_SizeChanged(object sender, SizeChangedEventArgs e) { AlignCrumbs(); }

		void UpdateCrumbVisibility()
		{
			bool show = !CB_Path.IsKeyboardFocusWithin && CrumbPanel.Children.Count > 0
				&& string.Equals((CB_Path.Text ?? "").TrimEnd('\\'), (oldPath ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
			Crumbs.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		}

		private void CB_Path_FocusChanged(object sender, DependencyPropertyChangedEventArgs e) { UpdateCrumbVisibility(); }

		// Click on empty breadcrumb space: edit the path as text.
		private void Crumbs_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (e.OriginalSource is Button || (e.OriginalSource is FrameworkElement && ((FrameworkElement)e.OriginalSource).TemplatedParent is Button)) return;
			FocusPathBox();
		}

		void FocusPathBox()
		{
			Crumbs.Visibility = Visibility.Collapsed;
			CB_Path.Focus();
			var tb = CB_Path.Template.FindName("PART_EditableTextBox", CB_Path) as TextBox;
			if (tb != null) { tb.Focus(); tb.SelectAll(); }
		}


		// ------------------------------------------------------------------
		//  Reading position and position indicator
		// ------------------------------------------------------------------

		string _shownFolder;        // folder whose images are in the grid
		bool _userScrolled;         // since the current load started

		/// <summary>Remembers where the reader is in the folder being left (or on close).</summary>
		void SaveReadingPosition()
		{
			if (App.IsBenchmark || string.IsNullOrEmpty(_shownFolder) || !Settings.Default.RememberScrollPosition) return;
			double fraction;
			var top = GridPanel.FirstVisibleImage(out fraction);
			if (top == null) return;
			// Top of the folder: nothing worth remembering (and forget older positions).
			bool atStart = top.ViewIndex == 0 && fraction < 0.01;
			AppData.Positions.Set(_shownFolder, atStart ? null : top.Path + "|" + fraction.ToString("R", CultureInfo.InvariantCulture));
		}

		/// <summary>Load finished: open the image given on the command line, or restore the reading position.</summary>
		void OnLoadCompleted()
		{
			if (App.StartupFile != null)
			{
				var file = App.StartupFile;
				App.StartupFile = null;
				var item = _view.FirstOrDefault(v => string.Equals(v.Path, file, StringComparison.OrdinalIgnoreCase));
				if (item != null)
				{
					SelectOnly(item);
					Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { GridPanel.ScrollIntoView(item, true); OpenPreview(item); }));
				}
				return;
			}

			if (_userScrolled || !Settings.Default.RememberScrollPosition || string.IsNullOrEmpty(_shownFolder)) return;
			var saved = AppData.Positions.Get(_shownFolder);
			if (string.IsNullOrEmpty(saved)) return;
			int bar = saved.LastIndexOf('|');
			double fraction;
			string path = bar > 0 ? saved.Substring(0, bar) : saved;
			if (bar <= 0 || !double.TryParse(saved.Substring(bar + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out fraction)) fraction = 0;
			var target = _view.FirstOrDefault(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase));
			if (target == null) return;

			// After the layout pass that includes the last batch.
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
			{
				double offset;
				if (!_userScrolled && GridPanel.TryGetOffsetOf(target, fraction, out offset))
				{
					LV.ScrollToVerticalOffset(offset);
					SB_Size.Text = "Resumed where you left off - " + target.FileName;
				}
			}));
		}

		void LV_ScrollChanged(object sender, ScrollChangedEventArgs e)
		{
			// While loading, content only grows below - an offset change is the
			// user scrolling, so don't jump to the remembered position afterwards.
			if (e.VerticalChange != 0 && _isLoading) _userScrolled = true;
			UpdatePositionText();
		}

		void UpdatePositionText()
		{
			double fraction;
			var top = GridPanel.FirstVisibleImage(out fraction);
			string text = top != null && top.ViewIndex >= 0 ? (top.ViewIndex + 1).ToString("N0") : null;
			if (text == _positionText) return;
			_positionText = text;
			UpdateCountText();
		}

		/// <summary>Book view reading: Space / Shift+Space page like a reader.</summary>
		void PageStep(int direction)
		{
			if (GridPanel.HasRows) ScrollToRowStep(direction);
			else if (direction > 0) LV.PageDown();
			else LV.PageUp();
		}
	}
}
