using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ImageViewer.Properties;

namespace ImageViewer
{
	// Built-in preview (Space / double-click / context menu): one image at a
	// time over the grid, arrows / wheel to step, R to rotate, S for a
	// slideshow, animated GIFs play.
	public partial class MainWindow
	{
		int _previewIndex = -1;
		LazyImage _previewItem;
		int _previewVersion;
		bool _previewHasAlpha;   // the previewed bitmap has transparency: show the backdrop
		GifAnimator _gif;

		bool IsPreviewOpen { get { return PreviewOverlay.Visibility == Visibility.Visible; } }

		void OpenPreview(LazyImage item)
		{
			if (item == null || item.ViewIndex < 0) return;
			PreviewOverlay.Visibility = Visibility.Visible;
			ShowPreviewAt(item.ViewIndex);
		}

		void ShowPreviewAt(int index)
		{
			int n = _view.Count;
			if (n == 0) { ClosePreview(); return; }
			index = Math.Max(0, Math.Min(n - 1, index));
			var item = _view[index];

			StopGif();
			_previewIndex = index;
			_previewItem = item;
			_zoom = 0;
			_previewHasAlpha = false;
			_pan = new Vector();
			_fullResRequested = false;

			// Instant feedback: the grid's (smaller) bitmap if it is decoded, then
			// swap in a sharp copy decoded off the UI thread at screen size.
			SizePreviewImage();   // also applies the (reset) zoom transform and the caption
			PreviewImage.Source = item.Source;
			UpdatePreviewBackdrop();
			int version = ++_previewVersion;
			ImageLoader.DecodeAsync(item.Path, item.CacheKey, PreviewDecodeWidth(item), item.Transform, Dispatcher, bmp =>
			{
				if (version != _previewVersion || !IsPreviewOpen || bmp == null) return;
				// Zoomed in meanwhile: the full-resolution decode supplies the bitmap
				// (this smaller one must not replace it if it lands later).
				_previewHasAlpha = LazyImage.HasAlpha(bmp);
				UpdatePreviewBackdrop();
				if (_gif == null && !_fullResRequested) PreviewImage.Source = ImageLoader.ForDisplay(bmp);
			});

			// Animated GIF: composite all frames in the background, then play.
			if (GifAnimator.IsGif(item.Path))
				GifAnimator.Load(item.Path, item.Transform, Dispatcher, anim =>
				{
					if (anim == null || version != _previewVersion || !IsPreviewOpen) return;
					_gif = anim;
					_previewHasAlpha = true;   // frames are composited with transparency
					UpdatePreviewBackdrop();
					anim.Start(frame => PreviewImage.Source = frame);
				});

			// Warm the cache for the next image so flipping forward is instant.
			if (index + 1 < n)
			{
				var next = _view[index + 1];
				ImageLoader.DecodeAsync(next.Path, next.CacheKey, PreviewDecodeWidth(next), next.Transform, Dispatcher, null);
			}
			RestartSlideshowTimer();
			UpdateInfo();
		}

		void UpdatePreviewCaption()
		{
			var item = _previewItem;
			if (item == null) return;
			PreviewCaption.Text = item.FileName + "     " + item.PixelWidth + " × " + item.PixelHeight
				+ (item.UserRotation != 0 ? "     rotated " + item.UserRotation + "°" : "")
				+ "     " + (_previewIndex + 1).ToString("N0") + " / " + _view.Count.ToString("N0")
				+ "     " + Math.Round(100 * (_zoom > 0 ? _zoom : _fitScale)) + "%"
				+ (_slideshow != null ? "     ▶ slideshow" : "");
		}

		void StopGif()
		{
			if (_gif != null) { _gif.Stop(); _gif = null; }
		}

		// Display box of the previewed image: 1:1 device pixels when it fits,
		// otherwise scaled down to the preview area (never blown up).
		void SizePreviewImage()
		{
			var item = _previewItem;
			if (item == null || item.PixelWidth <= 0 || item.PixelHeight <= 0)
			{
				PreviewImage.Width = PreviewImage.Height = double.NaN;
				return;
			}
			double s = LazyImage.DpiScale > 0 ? LazyImage.DpiScale : 1.0;
			double w = item.PixelWidth / s, h = item.PixelHeight / s;
			double boxW = Math.Max(1, PreviewAreaWidth - 112);
			double boxH = Math.Max(1, PreviewAreaHeight - 80);
			double fit = Math.Min(1.0, Math.Min(boxW / w, boxH / h));
			_fitScale = fit;
			PreviewImage.Width = Math.Max(1, Math.Floor(w * fit));
			PreviewImage.Height = Math.Max(1, Math.Floor(h * fit));
			ApplyPreviewTransform();
		}

		// The preview area (window minus the info panel when it is open).
		double PreviewAreaWidth { get { return PreviewOverlay.ActualWidth > 10 ? PreviewOverlay.ActualWidth : RootGrid.ActualWidth; } }
		double PreviewAreaHeight { get { return PreviewOverlay.ActualHeight > 10 ? PreviewOverlay.ActualHeight : RootGrid.ActualHeight; } }


		// ------------------------------------------------------------------
		//  Zoom and pan
		// ------------------------------------------------------------------

		// _zoom: displayed size relative to actual pixels (1 = 100%), 0 = fit.
		// Rendered as a scale of the fitted box plus a pan offset, so the
		// layout never changes while zooming.
		double _zoom, _fitScale = 1;
		Vector _pan;
		bool _fullResRequested;
		bool _panning;
		Point _panStartMouse;
		Vector _panStartOffset;
		const double MaxZoom = 16;

		bool IsPreviewZoomed { get { return _zoom > 0; } }

		void ApplyPreviewTransform()
		{
			var item = _previewItem;
			double k = _zoom > 0 && _fitScale > 0 ? _zoom / _fitScale : 1;
			// Pan only as far as the zoomed image extends past the area.
			double w = (double.IsNaN(PreviewImage.Width) ? 0 : PreviewImage.Width) * k;
			double h = (double.IsNaN(PreviewImage.Height) ? 0 : PreviewImage.Height) * k;
			double maxX = Math.Max(0, (w - PreviewAreaWidth) / 2 + 8), maxY = Math.Max(0, (h - PreviewAreaHeight) / 2 + 8);
			_pan = new Vector(Math.Max(-maxX, Math.Min(maxX, _pan.X)), Math.Max(-maxY, Math.Min(maxY, _pan.Y)));

			bool mirror = item != null && ImageLoader.IsMirrored(item.Transform);
			var g = new System.Windows.Media.TransformGroup();
			g.Children.Add(new System.Windows.Media.ScaleTransform(mirror ? -k : k, k));
			g.Children.Add(new System.Windows.Media.TranslateTransform(_pan.X, _pan.Y));
			PreviewImage.RenderTransformOrigin = new Point(0.5, 0.5);
			PreviewImage.RenderTransform = g;
			PreviewImage.Cursor = _zoom > 0 ? (_panning ? Cursors.SizeAll : Cursors.Hand) : null;
			UpdatePreviewCaption();

			// Zoomed past the screen-size decode: fetch full resolution once.
			if (_zoom > _fitScale + 1e-6 && !_fullResRequested && item != null)
			{
				_fullResRequested = true;
				int version = _previewVersion;
				ImageLoader.DecodeAsync(item.Path, item.CacheKey, 0, item.Transform, Dispatcher, bmp =>
				{
					if (version != _previewVersion || !IsPreviewOpen || bmp == null || _gif != null) return;
					PreviewImage.Source = ImageLoader.ForDisplay(bmp);
				});
			}
		}

		/// <summary>Zooms by a factor, keeping the point under the mouse (overlay coordinates) in place.</summary>
		void ZoomPreviewBy(double factor, Point? at)
		{
			if (_previewItem == null) return;
			double current = _zoom > 0 ? _zoom : _fitScale;
			SetPreviewZoom(Math.Max(Math.Min(_fitScale, 1), Math.Min(MaxZoom, current * factor)), at);
		}

		void SetPreviewZoom(double target, Point? at)
		{
			if (_previewItem == null) return;
			if (target <= _fitScale + 1e-6) { ResetPreviewZoom(); return; }
			double current = _zoom > 0 ? _zoom : _fitScale;
			// Anchor offset from the center of the fitted box (margins 56,24,56,56).
			var c = new Point(PreviewAreaWidth / 2, 24 + (PreviewAreaHeight - 80) / 2);
			var m = at.HasValue ? at.Value - c : new Vector();
			_pan = m - (m - _pan) * (target / current);
			_zoom = target;
			ApplyPreviewTransform();
		}

		void ResetPreviewZoom()
		{
			_zoom = 0;
			_pan = new Vector();
			ApplyPreviewTransform();
		}

		/// <summary>Z / double-click: fit ↔ actual pixels (images that already fit: 200%).</summary>
		void ToggleActualSize(Point? at)
		{
			if (_zoom > 0) ResetPreviewZoom();
			else SetPreviewZoom(_fitScale >= 1 ? 2.0 : 1.0, at);
		}

		private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			var p = e.GetPosition(PreviewOverlay);
			if (e.ClickCount == 2) { ToggleActualSize(p); e.Handled = true; return; }
			if (_zoom <= 0) return;
			_panning = true;
			_panStartMouse = p;
			_panStartOffset = _pan;
			PreviewImage.CaptureMouse();
			PreviewImage.Cursor = Cursors.SizeAll;
			e.Handled = true;
		}

		private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
		{
			if (!_panning) return;
			_pan = _panStartOffset + (e.GetPosition(PreviewOverlay) - _panStartMouse);
			ApplyPreviewTransform();
		}

		private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (_panning) PreviewImage.ReleaseMouseCapture();
		}

		private void PreviewImage_LostMouseCapture(object sender, MouseEventArgs e)
		{
			_panning = false;
			PreviewImage.Cursor = _zoom > 0 ? Cursors.Hand : null;
		}

		// Decode width (file orientation) that fills the preview area (0 = full resolution).
		int PreviewDecodeWidth(LazyImage item)
		{
			if (item.PixelWidth <= 0 || item.PixelHeight <= 0) return 0;
			double s = LazyImage.DpiScale;
			double boxW = Math.Max(1, PreviewAreaWidth - 112) * s;
			double boxH = Math.Max(1, PreviewAreaHeight - 80) * s;
			double fit = Math.Min(boxW / item.PixelWidth, boxH / item.PixelHeight);
			if (fit >= 1) return 0;
			bool quarter = (item.Transform & 1) == 1;   // file width is the displayed height
			return (int)Math.Ceiling((quarter ? item.PixelHeight : item.PixelWidth) * fit);
		}

		void ClosePreview()
		{
			++_previewVersion;
			StopGif();
			StopSlideshow();
			if (_panning) PreviewImage.ReleaseMouseCapture();
			_zoom = 0;
			PreviewOverlay.Visibility = Visibility.Collapsed;
			PreviewImage.Source = null;
			PreviewBackdrop.Fill = null;

			// Land the grid on the image the preview ended on, and select it.
			var item = _previewItem;
			_previewItem = null;
			if (item != null && item.ViewIndex >= 0)
			{
				GridPanel.ScrollIntoView(item);
				SelectOnly(item);
				SetCurrent(item);
			}
			LV.Focus();
			UpdateInfo();
		}

		private void PreviewPrev_Click(object sender, RoutedEventArgs e) { ShowPreviewAt(_previewIndex - 1); }
		private void PreviewNext_Click(object sender, RoutedEventArgs e) { ShowPreviewAt(_previewIndex + 1); }
		private void PreviewClose_Click(object sender, RoutedEventArgs e) { ClosePreview(); }

		private void PreviewRotate_Click(object sender, RoutedEventArgs e)
		{
			if (_previewItem != null)
				RotateImages(new System.Collections.Generic.List<LazyImage> { _previewItem },
					Keyboard.Modifiers == ModifierKeys.Shift ? -90 : 90, false);
		}

		private void PreviewOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			// Click on the dark backdrop (not on the image or a button) closes.
			if (e.OriginalSource == PreviewOverlay) { ClosePreview(); e.Handled = true; }
		}

		// Wheel: next / previous image. Ctrl+wheel zooms at the mouse; while
		// zoomed in, the wheel pans (Shift: sideways).
		private void PreviewOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
		{
			e.Handled = true;
			if (e.Delta == 0) return;
			if (Keyboard.Modifiers == ModifierKeys.Control)
			{
				ZoomPreviewBy(Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(PreviewOverlay));
				return;
			}
			if (_zoom > 0)
			{
				double step = e.Delta;   // one notch = 120 DIP
				_pan += Keyboard.Modifiers == ModifierKeys.Shift ? new Vector(step, 0) : new Vector(0, step);
				ApplyPreviewTransform();
				return;
			}
			ShowPreviewAt(_previewIndex + (e.Delta < 0 ? 1 : -1));
		}


		// ------------------------------------------------------------------
		//  Slideshow
		// ------------------------------------------------------------------

		DispatcherTimer _slideshow;

		private void PreviewSlideshow_Click(object sender, RoutedEventArgs e) { ToggleSlideshow(); }

		void ToggleSlideshow()
		{
			if (_slideshow != null) StopSlideshow();
			else StartSlideshow();
			UpdatePreviewCaption();
		}

		void StartSlideshow()
		{
			if (!IsPreviewOpen) OpenPreview(KeyTarget ?? _view.FirstOrDefault());
			if (!IsPreviewOpen) return;
			int seconds = Math.Max(1, Math.Min(60, Settings.Default.SlideshowSeconds));
			_slideshow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
			_slideshow.Tick += (s, e) =>
			{
				// Wraps around at the end of the folder.
				ShowPreviewAt(_previewIndex + 1 < _view.Count ? _previewIndex + 1 : 0);
			};
			_slideshow.Start();
			BtnSlideshow.Content = "";   // pause
			BtnSlideshow.ToolTip = "Pause slideshow (S)";
			UpdatePreviewCaption();
		}

		void StopSlideshow()
		{
			if (_slideshow == null) return;
			_slideshow.Stop();
			_slideshow = null;
			BtnSlideshow.Content = "";   // play
			BtnSlideshow.ToolTip = "Slideshow (S)";
		}

		// Manual stepping restarts the interval so the new image gets its full time.
		void RestartSlideshowTimer()
		{
			if (_slideshow == null) return;
			_slideshow.Stop();
			_slideshow.Start();
		}
	}
}
