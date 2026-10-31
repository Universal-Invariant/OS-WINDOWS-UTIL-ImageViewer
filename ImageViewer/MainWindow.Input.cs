using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImageViewer.Properties;

namespace ImageViewer
{
	// Mouse and keyboard. The full list is in the help overlay (F1,
	// MainWindow.Extras.cs HelpSections) and in README.md.
	//
	// Which image a key acts on: after arrow-key navigation the keyboard
	// focus (_current); after the mouse moves, the image under the mouse.
	// Either way, an image that is part of the selection means "the whole
	// selection" (Targets).
	public partial class MainWindow
	{
		LazyImage _pressed;
		Point _dragStart;
		bool _pressedWasSelected;
		bool _draggingOut;

		bool _keyboardMode;        // last navigation was by keyboard
		Point _lastMouseScreen;

		/// <summary>The image keys and the info panel refer to (null: none).</summary>
		LazyImage ActionItem { get { return _keyboardMode && _current != null ? _current : _hovered; } }

		LazyImage KeyTarget { get { return ActionItem ?? Targets(null).FirstOrDefault(); } }

		// Real mouse movement (not content scrolling under a still cursor)
		// hands control back to the mouse.
		void Window_TrackMouse(object sender, MouseEventArgs e)
		{
			var p = e.GetPosition(this);
			FullScreenMouseMove(p);
			var screen = PointToScreen(p);
			if (Math.Abs(screen.X - _lastMouseScreen.X) + Math.Abs(screen.Y - _lastMouseScreen.Y) < 3) return;
			_lastMouseScreen = screen;
			if (_keyboardMode)
			{
				_keyboardMode = false;
				if (_hovered != null) SB_Size.Text = DescribeImage(_hovered);
				UpdateInfo();
			}
		}

		private void LV_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			// Images draw themselves, so the image is the hit element itself.
			var img = e.OriginalSource as LazyImage;
			if (img == null)
			{
				// Empty space between/after images (not the scrollbar): deselect.
				if ((e.OriginalSource == GridPanel || e.OriginalSource == LV) && Keyboard.Modifiers == ModifierKeys.None)
					ClearSelection();
				LV.Focus();
				return;
			}

			LV.Focus();
			var mods = Keyboard.Modifiers;
			e.Handled = true;
			SetCurrent(img);

			if (e.ClickCount == 2 && mods == ModifierKeys.None)
			{
				bool external = true;
				try { external = Settings.Default.OpenOnDoubleClick; } catch { }
				if (external) OpenExternal(img); else OpenPreview(img);
				return;
			}
			if ((mods & ModifierKeys.Alt) != 0)
			{
				RotateImages(Targets(img), (mods & ModifierKeys.Shift) != 0 ? -90 : 90, false);
				return;
			}
			if ((mods & ModifierKeys.Control) != 0) { ToggleSelection(img); return; }
			if ((mods & ModifierKeys.Shift) != 0) { SelectRange(img); return; }

			// Plain click: select it - but keep a multi-selection until mouse-up
			// so the whole selection can be dragged out.
			_pressed = img;
			_pressedWasSelected = img.IsSelected;
			_dragStart = e.GetPosition(LV);
			if (!img.IsSelected) SelectOnly(img);
			else _anchor = img;
		}

		private void LV_PreviewMouseMove(object sender, MouseEventArgs e)
		{
			if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
			var p = e.GetPosition(LV);
			if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
				Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

			var targets = Targets(_pressed);
			_pressed = null;
			var files = new StringCollection();
			foreach (var t in targets) files.Add(t.Path);
			var data = new DataObject();
			data.SetFileDropList(files);

			_draggingOut = true;
			try { DragDrop.DoDragDrop(LV, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
			catch { /* target app failed - nothing to clean up */ }
			finally { _draggingOut = false; }
		}

		private void LV_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			// Clicked (not dragged) inside a multi-selection: select just that one.
			if (_pressed != null && _pressedWasSelected && _selected.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
				SelectOnly(_pressed);
			_pressed = null;
		}


		// ------------------------------------------------------------------
		//  Keyboard
		// ------------------------------------------------------------------

		void Window_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			var key = e.Key == Key.System ? e.SystemKey : e.Key;
			var mods = Keyboard.Modifiers;

			if (IsHelpOpen)
			{
				if (key == Key.Escape || key == Key.F1 || key == Key.OemQuestion || key == Key.Enter || key == Key.Space) ShowHelp(false);
				e.Handled = true;   // the sheet is modal
				return;
			}

			// History works everywhere (also while typing).
			if (mods == ModifierKeys.Alt)
			{
				switch (key)
				{
					case Key.Left: NavigateHistory(-1); break;
					case Key.Right: NavigateHistory(+1); break;
					case Key.Up: OpenParentFolder(); break;
					default: return;
				}
				e.Handled = true;
				return;
			}

			// Window-level keys that work in every mode.
			if (mods == ModifierKeys.None && key == Key.F11) { ToggleFullScreen(); e.Handled = true; return; }
			if (mods == ModifierKeys.None && key == Key.F1) { ShowHelp(true); e.Handled = true; return; }

			if (IsPreviewOpen)
			{
				HandlePreviewKey(key, mods, e);
				return;
			}

			// Typing in the path or filter box (or selecting text in the info
			// panel): leave its keys alone.
			var tb = Keyboard.FocusedElement as TextBox;
			if (tb != null)
			{
				bool isFilter = tb == TB_Filter;
				if (key == Key.Escape)
				{
					if (isFilter) ClearFilter();
					else if (tb.IsReadOnly) LV.Focus();
					else { CB_Path.Text = oldPath ?? ""; LV.Focus(); UpdateCrumbVisibility(); }
					e.Handled = true;
				}
				else if (key == Key.Enter && isFilter) { if (_filterTimer != null) { _filterTimer.Stop(); SetFilter(TB_Filter.Text); } LV.Focus(); e.Handled = true; }
				else if (key == Key.Down && isFilter) { LV.Focus(); MoveFocus(Key.Down, ModifierKeys.None); e.Handled = true; }
				else if (mods == ModifierKeys.Control && key == Key.O) { BtnBrowse_Click(null, null); e.Handled = true; }
				else if (mods == ModifierKeys.Control && key == Key.F) { FocusFilter(); e.Handled = true; }
				else if (mods == ModifierKeys.Control && key == Key.L) { FocusPathBox(); e.Handled = true; }
				return;
			}

			// Arrow keys / Home / End: keyboard focus and selection.
			if ((key == Key.Left || key == Key.Right || key == Key.Up || key == Key.Down || key == Key.Home || key == Key.End)
				&& (mods & ModifierKeys.Alt) == 0)
			{
				MoveFocus(key, mods);
				e.Handled = true;
				return;
			}

			if (mods == ModifierKeys.Control)
			{
				switch (key)
				{
					case Key.O: BtnBrowse_Click(null, null); break;
					case Key.L: FocusPathBox(); break;
					case Key.F: FocusFilter(); break;
					case Key.A: SelectAll(); break;
					case Key.C: CopyToClipboard(Targets(ActionItem)); break;
					case Key.OemPlus: case Key.Add: StepScale(+1); break;
					case Key.OemMinus: case Key.Subtract: StepScale(-1); break;
					case Key.D0: case Key.NumPad0: SD_Scale.Value = 1.0; break;
					case Key.PageDown: OpenSiblingFolder(+1); break;
					case Key.PageUp: OpenSiblingFolder(-1); break;
					case Key.Space: ToggleAutoAdvance(); break;   // hands-free reading
					default: return;
				}
				e.Handled = true;
				return;
			}

			// Esc stops auto-advance first; any other key restarts its countdown.
			if (IsAutoAdvancing)
			{
				if (mods == ModifierKeys.None && key == Key.Escape) { StopAutoAdvance("Auto-advance off"); e.Handled = true; return; }
				if (!(mods == ModifierKeys.Control && key == Key.Space)) RestartAutoAdvanceCountdown();
			}

			if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C)
			{
				CopyPaths(Targets(ActionItem));
				e.Handled = true;
				return;
			}

			if (mods == ModifierKeys.Shift)
			{
				if (key == Key.R) { RotateImages(Targets(ActionItem), -90, false); e.Handled = true; }
				else if (key == Key.Space && IsFitToWidthEnabled) { PageStep(-1); e.Handled = true; }
				else if (key == Key.OemQuestion) { ShowHelp(true); e.Handled = true; }   // "?"
				else if (key == Key.B) { CycleBackdrop(-1); e.Handled = true; }
				return;
			}

			if (mods != ModifierKeys.None) return;
			switch (key)
			{
				case Key.F5: BtnRefresh_Click(null, null); break;
				case Key.F2: RenameImage(KeyTarget); break;
				case Key.F: TGB_FitToWidth.IsChecked = !IsFitToWidthEnabled; break;
				case Key.T: ToggleTiles(); break;
				case Key.N: ToggleNames(); break;
				case Key.I: ToggleInfo(); break;
				case Key.B: CycleBackdrop(+1); break;
				case Key.C: ToggleArrange(); break;
				case Key.R: RotateImages(Targets(ActionItem), 90, false); break;
				case Key.S: StartSlideshow(); break;
				case Key.Delete: DeleteImages(Targets(ActionItem)); break;
				case Key.Escape:
					if (IsFiltered) ClearFilter();
					else if (_selected.Count > 0) ClearSelection();
					else if (_fullScreen) ToggleFullScreen();
					else return;
					break;
				case Key.Space:
					// Book view reads like a book: Space turns the page.
					if (IsFitToWidthEnabled) PageStep(+1);
					else { if (KeyTarget == null) return; OpenPreview(KeyTarget); }
					break;
				case Key.Enter: if (KeyTarget == null) return; OpenExternal(KeyTarget); break;
				case Key.Insert: if (_current != null) ToggleSelection(_current); break;   // toggle the focused image
				default: return;
			}
			e.Handled = true;
		}

		/// <summary>
		/// Arrow keys: move the keyboard focus. Plain = select it, Shift =
		/// extend the selection from the anchor, Ctrl = move the focus only.
		/// The first press only shows the focus (on the hovered or first
		/// visible image).
		/// </summary>
		void MoveFocus(Key key, ModifierKeys mods)
		{
			if (_view.Count == 0) return;
			var from = _current != null && _current.ViewIndex >= 0 ? _current : null;
			LazyImage to;
			if (from == null)
			{
				double fraction;
				to = _hovered ?? GridPanel.FirstVisibleImage(out fraction) ?? _view[0];
				if (key == Key.Home) to = _view[0];
				else if (key == Key.End) to = _view[_view.Count - 1];
			}
			else
			{
				bool rtl = SmartFlowPanel.RightToLeft;
				switch (key)
				{
					// Display order: the same as the view's, except in packed mode.
					case Key.Left: to = GridPanel.FindImageHorizontal(from, rtl ? 1 : -1); break;
					case Key.Right: to = GridPanel.FindImageHorizontal(from, rtl ? -1 : 1); break;
					case Key.Up: to = GridPanel.FindImageVertical(from, -1); break;
					case Key.Down: to = GridPanel.FindImageVertical(from, +1); break;
					case Key.Home: to = _view[0]; break;
					case Key.End: to = _view[_view.Count - 1]; break;
					default: to = null; break;
				}
				if (to == null) return;   // edge
			}

			_keyboardMode = true;
			if ((mods & ModifierKeys.Shift) != 0)
			{
				if (_anchor == null || _anchor.ViewIndex < 0) _anchor = from ?? to;
				SetCurrent(to);
				SelectRange(to);
			}
			else if ((mods & ModifierKeys.Control) != 0) SetCurrent(to);
			else { SetCurrent(to); SelectOnly(to); }

			GridPanel.ScrollIntoView(to);
			SB_Size.Text = DescribeImage(to);
			UpdateInfo();
		}

		void HandlePreviewKey(Key key, ModifierKeys mods, KeyEventArgs e)
		{
			var current = _previewItem;
			if (mods == ModifierKeys.Control && key == Key.C) { if (current != null) CopyToClipboard(new System.Collections.Generic.List<LazyImage> { current }); }
			else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C) { if (current != null) CopyPaths(new System.Collections.Generic.List<LazyImage> { current }); }
			else if (mods == ModifierKeys.Shift && key == Key.R) { if (current != null) RotateImages(new System.Collections.Generic.List<LazyImage> { current }, -90, false); }
			else if (mods == ModifierKeys.Shift && key == Key.OemQuestion) ShowHelp(true);
			else if (mods == ModifierKeys.Shift && key == Key.B) CycleBackdrop(-1);
			else if (mods == ModifierKeys.Control && key == Key.Space) ToggleSlideshow();   // like auto-advance in the grid
			else if (mods == ModifierKeys.Control && (key == Key.OemPlus || key == Key.Add)) ZoomPreviewBy(1.25, null);
			else if (mods == ModifierKeys.Control && (key == Key.OemMinus || key == Key.Subtract)) ZoomPreviewBy(1 / 1.25, null);
			else if (mods == ModifierKeys.Control && (key == Key.D0 || key == Key.NumPad0)) ResetPreviewZoom();
			else if (mods != ModifierKeys.None) return;
			else switch (key)
			{
				case Key.Escape:
					if (_slideshow != null) ToggleSlideshow();
					else if (IsPreviewZoomed) ResetPreviewZoom();
					else ClosePreview();
					break;
				case Key.Space: ClosePreview(); break;
				case Key.Left: case Key.Up: case Key.PageUp: case Key.Back: ShowPreviewAt(_previewIndex - 1); break;
				case Key.Right: case Key.Down: case Key.PageDown: ShowPreviewAt(_previewIndex + 1); break;
				case Key.Home: ShowPreviewAt(0); break;
				case Key.End: ShowPreviewAt(_view.Count - 1); break;
				case Key.Enter: OpenExternal(current); break;
				case Key.S: ToggleSlideshow(); break;
				case Key.I: ToggleInfo(); break;
				case Key.B: CycleBackdrop(+1); break;
				case Key.F2: RenameImage(current); break;
				case Key.Z: ToggleActualSize(null); break;
				case Key.OemPlus: case Key.Add: ZoomPreviewBy(1.25, null); break;
				case Key.OemMinus: case Key.Subtract: ZoomPreviewBy(1 / 1.25, null); break;
				case Key.D0: case Key.NumPad0: ResetPreviewZoom(); break;
				case Key.R: if (current != null) RotateImages(new System.Collections.Generic.List<LazyImage> { current }, 90, false); break;
				case Key.Delete: if (current != null) DeleteImages(new System.Collections.Generic.List<LazyImage> { current }); break;
				default: return;
			}
			e.Handled = true;
		}

		void FocusFilter()
		{
			TB_Filter.Focus();
			TB_Filter.SelectAll();
		}
	}
}
