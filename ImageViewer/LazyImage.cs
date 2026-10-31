using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


namespace ImageViewer
{
	/// <summary>An element laid out by SmartFlowPanel.</summary>
	public interface IGridItem
	{
		double LayoutWidth { get; }
		double LayoutHeight { get; }
		/// <summary>Occupies a whole row of its own (folder headers).</summary>
		bool IsFullRow { get; }
	}

	/// <summary>
	/// One image in the grid. A lightweight element that draws itself: the
	/// transparency backdrop, the bitmap, an optional file-name caption and
	/// the hover / selection outline. Its size is computed arithmetically
	/// (UpdateSize) so SmartFlowPanel can lay out thousands of them without
	/// creating any visuals; only images near the viewport are in the visual
	/// tree (UI virtualization).
	/// </summary>
	public class LazyImage : FrameworkElement, IGridItem
	{
		// ---- display options shared by all images (set by MainWindow) -------
		public static double DpiScale = 1.0;   // device pixels per DIP
		public static bool ShowNames;
		public static bool TileMode;
		public static Brush TransparencyBrush = Backdrop.BrushFor("Checker");   // behind transparent images; null = nothing
		public const double CaptionHeight = 20;
		const double TileBase = 160;           // tile edge at 100% scale
		const double MinNamedCellWidth = 72;   // room for a readable name under tiny icons

		// ---- file -----------------------------------------------------------
		public readonly string Path = "";
		public readonly string CacheKey = "";
		public readonly string FileName = "";
		public readonly string Folder = "";
		public readonly long FileSize;
		public readonly DateTime Modified;
		public int LoadIndex;          // order of arrival (stable sort tie-break)
		public int RootIndex;          // which of the shown folders it belongs to (several folders)
		public int ViewIndex = -1;     // position in the current (sorted/filtered) view

		public BitmapSource Source
		{
			get { return _source; }
			private set
			{
				_source = value;
				// The drawing is mutable and owned by this element: clearing its
				// image releases the bitmap even when the element is NOT in the
				// visual tree. MEMORY FIX for virtualization: an element removed
				// from the panel keeps its last render data, and WPF never
				// re-renders a disconnected element - with DrawImage(bitmap) that
				// render data kept every decoded image ever shown alive (300
				// photos: 3.5 GB private memory after one scroll through).
				if (_drawing != null) _drawing.ImageSource = value;
			}
		}
		BitmapSource _source;
		ImageDrawing _drawing;
		public Rect? LayoutRect { get; set; }

		readonly bool _headerReadOk;
		readonly int _fileWidth, _fileHeight;   // as stored in the file
		readonly int _exifOrientation;
		int _userRotation;                      // 0, 90, 180, 270 clockwise
		int _transform;                         // ImageLoader.KeyFor transform code
		bool _swapAxes;                         // displayed width = file height
		bool _hasAlpha;

		/// <summary>Displayed (oriented) pixel size.</summary>
		public int PixelWidth { get { return _swapAxes ? _fileHeight : _fileWidth; } }
		public int PixelHeight { get { return _swapAxes ? _fileWidth : _fileHeight; } }
		public int UserRotation { get { return _userRotation; } }
		public int Transform { get { return _transform; } }

		// Oriented size before user scale, clamped to the "largest image" limit.
		public double ImageWidth, ImageHeight;
		double _maxWidth, _maxHeight;

		/// <summary>
		/// "Largest image" limit (window size x setting). Applied live by
		/// MainWindow.ApplyScale; the caller recomputes the size afterwards.
		/// </summary>
		public void SetMaxSize(double maxWidth, double maxHeight)
		{
			if (Math.Abs(maxWidth - _maxWidth) < 0.5 && Math.Abs(maxHeight - _maxHeight) < 0.5) return;
			_maxWidth = maxWidth;
			_maxHeight = maxHeight;
			if (_headerReadOk) ApplyOrientation();
		}

		// ---- layout ---------------------------------------------------------
		public double LayoutWidth { get; private set; }
		public double LayoutHeight { get; private set; }
		public bool IsFullRow { get { return false; } }
		/// <summary>Scaled to the book-view page width by the last UpdateSize.</summary>
		public bool IsPage { get; private set; }
		Rect _imageRect;

		// Layout parameters of the last UpdateSize() call, kept so the size
		// can be recomputed when the window is resized while a layout mode
		// (e.g. fit-to-width) is active, or when the image is rotated.
		double _lastScale = 1.0, _lastFitWidth, _lastMaxSizeFactor = 1.0, _lastMinFitWidthPx;

		// The file header was already read on the folder-loading worker thread
		// (ImageHeader.Read); this runs on the UI thread and does no IO.
		public LazyImage(ImageHeader header, double maxWidth, double maxHeight, int userRotation)
		{
			Path = header.Path;
			CacheKey = header.CacheKey;
			FileSize = header.FileSize;
			Modified = header.Modified;
			try { FileName = System.IO.Path.GetFileName(Path); Folder = System.IO.Path.GetDirectoryName(Path); } catch { FileName = Path; }
			Focusable = false;

			_maxWidth = maxWidth;
			_maxHeight = maxHeight;
			if (!header.Ok) return;
			_headerReadOk = true;
			_fileWidth = header.PixelWidth;
			_fileHeight = header.PixelHeight;
			_exifOrientation = header.Orientation;
			_userRotation = Normalize(userRotation);
			ApplyOrientation();
			UpdateSize(1.0);
		}

		static int Normalize(int degrees) { return ((degrees / 90 % 4) + 4) % 4 * 90; }

		// EXIF orientation -> (mirror first, then clockwise quarter turns).
		static readonly bool[] ExifFlip = { false, false, true, false, true, true, false, true, false };
		static readonly int[] ExifQuarter = { 0, 0, 0, 2, 2, 3, 1, 1, 3 };

		void ApplyOrientation()
		{
			int o = _exifOrientation >= 1 && _exifOrientation <= 8 ? _exifOrientation : 1;
			int quarters = (ExifQuarter[o] + _userRotation / 90) % 4;
			_transform = (ExifFlip[o] ? 4 : 0) | quarters;
			_swapAxes = (quarters & 1) == 1;

			double w = PixelWidth, h = PixelHeight;
			double c = 1;
			if (_maxWidth > 0 && w > _maxWidth) c = Math.Min(c, _maxWidth / w);
			if (_maxHeight > 0 && h > _maxHeight) c = Math.Min(c, _maxHeight / h);
			ImageWidth = w * c;
			ImageHeight = h * c;
		}

		/// <summary>Sets the user's rotation (multiple of 90°, clockwise).</summary>
		public void SetUserRotation(int degrees)
		{
			degrees = Normalize(degrees);
			if (!_headerReadOk || degrees == _userRotation) return;
			int delta = (degrees - _userRotation + 360) % 360;
			_userRotation = degrees;
			ApplyOrientation();

			// Instant feedback: turn the bitmap already on screen; a properly
			// oriented decode (own cache entry) replaces it when needed.
			if (Source != null)
			{
				// A mirrored image's bitmap is turned the other way (see DecodeQuarters).
				int turn = ImageLoader.IsMirrored(_transform) ? (360 - delta) % 360 : delta;
				var turned = new TransformedBitmap(Source, new RotateTransform(turn));
				turned.Freeze();
				Source = turned;
				_sourceNeedsReorient = true;
			}
			if (_loadPending) { Interlocked.Increment(ref _loadVersion); _loadPending = false; }
			UpdateSize(_lastScale, _lastFitWidth, _lastMaxSizeFactor, _lastMinFitWidthPx);
			InvalidateVisual();
			if (_wanted) LoadImage();
		}

		// Scales the image and recomputes its cell size.
		//   scale           : user scale factor from the toolbar slider
		//   targetWidth     : > 0 enables "fit to width" (book view) - images at
		//                     least minWidthInPixels wide (or tall) are scaled so
		//                     their rendered width equals this value.
		//   minWidthInPixels: book-view threshold in original pixels; smaller
		//                     images (icons) keep their normal scale.
		// The size is always derived from the original dimensions - never
		// chained onto earlier results - so repeated calls can't drift.
		public void UpdateSize(double scale, double targetWidth = 0, double maxSizeFactor = 1.0, double minWidthInPixels = 0)
		{
			_lastScale = scale;
			_lastFitWidth = targetWidth;
			_lastMaxSizeFactor = maxSizeFactor;
			_lastMinFitWidthPx = minWidthInPixels;

			double w = Math.Max(1, ImageWidth), h = Math.Max(1, ImageHeight);
			double imgW, imgH, cellW, cellH, caption = ShowNames ? CaptionHeight : 0;
			Rect imageRect;

			if (TileMode)
			{
				// Uniform tiles: every image fits a square, never enlarged past
				// its own size at this scale.
				double t = Math.Max(32, Round(TileBase * scale));
				double f = Math.Min(Math.Min(t / w, t / h), scale);
				imgW = Round(Math.Max(4, w * f));
				imgH = Round(Math.Max(4, h * f));
				cellW = t;
				cellH = t + caption;
				IsPage = false;
				imageRect = new Rect(Round((t - imgW) / 2), Round((t - imgH) / 2), imgW, imgH);
			}
			else
			{
				// BUG FIX: the threshold used to be compared against
				// minWidthInPixels / 96 (500 px -> ~5 px), so book view stretched
				// every image - icons included - to the window width.
				bool fits = targetWidth > 0 && (PixelWidth >= minWidthInPixels || PixelHeight >= minWidthInPixels);
				IsPage = fits;
				if (fits)
				{
					var c = targetWidth / w;
					w *= c;
					h *= c;
					// Safety clamp: a page may never be wider than the area it wraps in.
					if (_maxWidth > 0 && w > _maxWidth) { var cc = _maxWidth / w; w *= cc; h *= cc; }
				}
				w *= scale;
				h *= scale;

				// Never collapse to zero (or NaN) extents - that breaks the layout.
				imgW = Round(double.IsNaN(w) ? 8 : Math.Max(8, w));
				imgH = Round(double.IsNaN(h) ? 8 : Math.Max(8, h));
				cellW = ShowNames ? Math.Max(imgW, MinNamedCellWidth) : imgW;
				cellH = imgH + caption;
				imageRect = new Rect(Round((cellW - imgW) / 2), 0, imgW, imgH);
			}

			bool changed = Math.Abs(LayoutWidth - cellW) >= 0.01 || Math.Abs(LayoutHeight - cellH) >= 0.01 || imageRect != _imageRect;
			if (!changed) return;
			LayoutWidth = cellW;
			LayoutHeight = cellH;
			_imageRect = imageRect;
			_caption = null;
			if (VisualParent != null) { InvalidateMeasure(); InvalidateVisual(); }

			// Grown past the resolution we decoded at: fetch a sharper copy.
			// The current bitmap stays on screen until it arrives (no blank).
			if (_wanted && Source != null && !_loadPending && NeedsSharperDecode(NeededDecodeWidthPx()))
				RequestDecode();
		}

		/// <summary>Recompute with the last parameters (viewport / book-view width changed).</summary>
		public void RefreshSize(double targetWidth)
		{
			UpdateSize(_lastScale, targetWidth > 0 ? targetWidth : _lastFitWidth, _lastMaxSizeFactor, _lastMinFitWidthPx);
		}

		// Same rounding as WPF layout rounding, so positions/sizes land on device pixels.
		static double Round(double v)
		{
			var s = DpiScale;
			if (s <= 0 || double.IsNaN(s) || Math.Abs(s - 1.0) < 1e-9) return Math.Round(v);
			return Math.Round(v * s) / s;
		}

		protected override Size MeasureOverride(Size availableSize) { return new Size(LayoutWidth, LayoutHeight); }
		protected override Size ArrangeOverride(Size finalSize) { return finalSize; }


		// ---- drawing --------------------------------------------------------
		bool _hovered, _selected, _current;
		FormattedText _caption;

		public bool IsSelected
		{
			get { return _selected; }
			set { if (_selected == value) return; _selected = value; InvalidateVisual(); }
		}

		/// <summary>Keyboard focus (arrow-key navigation): drawn as a dashed outline.</summary>
		public bool IsCurrent
		{
			get { return _current; }
			set { if (_current == value) return; _current = value; InvalidateVisual(); }
		}

		// Theme-dependent brushes, refreshed by SetBackdrop / theme changes.
		static Brush _captionBrush = Brushes.Gainsboro;
		static Brush _accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9E, 0xEB));
		static Pen _hoverPen, _selectPen, _currentPen;
		static Brush _selectFill;
		static readonly Typeface CaptionFace = new Typeface("Segoe UI");

		static LazyImage() { SetAccent(Color.FromRgb(0x4C, 0x9E, 0xEB)); }

		static Brush Frozen(Brush b) { b.Freeze(); return b; }

		public static void SetAccent(Color accent)
		{
			_accent = Frozen(new SolidColorBrush(accent));
			_hoverPen = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xE6, accent.R, accent.G, accent.B))), 2); _hoverPen.Freeze();
			_selectPen = new Pen(_accent, 3); _selectPen.Freeze();
			_currentPen = new Pen(Brushes.White, 2) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) }; _currentPen.Freeze();
			_selectFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)));
		}

		/// <summary>Caption color follows the image-area background (not the UI theme).</summary>
		public static void SetBackdrop(Color background)
		{
			double luma = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B;
			_captionBrush = Frozen(new SolidColorBrush(luma < 140 ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x30, 0x30, 0x30)));
		}

		protected override void OnRender(DrawingContext dc)
		{
			// Transparent fill: the whole cell is hit-testable even before the
			// bitmap arrives (hover, clicks, context menu).
			dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, LayoutWidth, LayoutHeight));

			if (Source != null)
			{
				if (_hasAlpha && TransparencyBrush != null)
					dc.DrawRectangle(TransparencyBrush, null, _imageRect);
				if (_drawing == null) _drawing = new ImageDrawing();
				_drawing.ImageSource = Source;
				_drawing.Rect = _imageRect;
				bool mirror = ImageLoader.IsMirrored(_transform);
				if (mirror) dc.PushTransform(new ScaleTransform(-1, 1, _imageRect.X + _imageRect.Width / 2, 0));
				dc.DrawDrawing(_drawing);
				if (mirror) dc.Pop();
			}

			if (ShowNames && LayoutWidth > 8)
			{
				if (_caption == null)
				{
#pragma warning disable CS0618 // pixelsPerDip overload: DpiScale is applied via TextFormattingMode
					_caption = new FormattedText(FileName, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
						CaptionFace, 11, _captionBrush, DpiScale)
#pragma warning restore CS0618
					{
						MaxTextWidth = Math.Max(1, LayoutWidth - 4),
						MaxLineCount = 1,
						Trimming = TextTrimming.CharacterEllipsis,
						TextAlignment = TextAlignment.Center,
					};
				}
				double top = TileMode ? LayoutHeight - CaptionHeight : _imageRect.Bottom;
				dc.DrawText(_caption, new Point(2, top + 3));
			}

			var r = _imageRect;
			if (_selected)
			{
				dc.DrawRectangle(_selectFill, null, r);
				if (r.Width > 4 && r.Height > 4) r.Inflate(-1.5, -1.5);
				dc.DrawRectangle(null, _selectPen, r);
			}
			else if (_hovered)
			{
				if (r.Width > 2 && r.Height > 2) r.Inflate(-1, -1);
				dc.DrawRectangle(null, _hoverPen, r);
			}
			if (_current)
			{
				// Inside the selection outline, visible on any image content.
				var c = _imageRect;
				if (c.Width > 10 && c.Height > 10) c.Inflate(-4, -4);
				dc.DrawRectangle(null, _currentPen, c);
			}
		}

		/// <summary>Theme or backdrop changed: rebuild cached text.</summary>
		public void RefreshVisual() { _caption = null; InvalidateVisual(); }


		// ---- lazy loading ---------------------------------------------------

		// The panel calls LoadImage() for images inside its load band and
		// UnLoadImage() once they leave its keep band. Decoding runs on
		// ImageLoader's worker threads; every request carries a version number
		// and results for an outdated version are dropped (a late result can
		// never re-populate an image that has scrolled away).
		bool _wanted;               // the panel wants this image shown
		bool _loadPending;          // a decode (or cache hit) is on its way
		bool _decodeFailed;         // corrupt file: stop retrying
		int _loadVersion;           // bumped by every request and every unload
		int _decodedWidthPx = -1;   // decode width (file orientation) of the current Source (0 = full)

		public void LoadImage()
		{
			if (!_headerReadOk || _decodeFailed) return;
			_wanted = true;
			if (_loadPending) return;
			if (Source != null && !NeedsSharperDecode(NeededDecodeWidthPx()) && !_sourceNeedsReorient) return;
			RequestDecode();
		}

		public void UnLoadImage()
		{
			_wanted = false;
			_loadPending = false;
			Interlocked.Increment(ref _loadVersion);   // cancels anything in flight
			if (Source != null) { Source = null; InvalidateVisual(); }
			_decodedWidthPx = -1;
			_sourceNeedsReorient = false;
		}

		// Set when the on-screen bitmap was turned in place (SetUserRotation);
		// the next LoadImage replaces it with a properly decoded one.
		bool _sourceNeedsReorient;

		void RequestDecode()
		{
			// Round up to a multiple of 64 px so small size changes (slider
			// ticks, window resizes) reuse the decode; capped at full resolution.
			int need = NeededDecodeWidthPx();
			if (need > 0)
			{
				need = (need + 63) / 64 * 64;
				if (need >= _fileWidth) need = 0;
			}

			int version = Interlocked.Increment(ref _loadVersion);
			_loadPending = true;
			string key = ImageLoader.KeyFor(CacheKey, _transform);

			// Cache hit: still deferred to the next dispatcher turn (we may be
			// inside a layout pass), but at Normal priority - before the next
			// render, so no blank frame is shown.
			int cachedWidth;
			var cached = ImageLoader.TryGetCached(key, need, out cachedWidth);
			if (cached != null)
			{
				if (Perf.Enabled) Interlocked.Increment(ref Perf.CacheHits);
				Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal,
					new Action(() => OnDecoded(version, cached, cachedWidth)));
				return;
			}

			ImageLoader.Enqueue(this, version, Path, key, need, _transform);
		}

		// Called by ImageLoader worker threads before decoding.
		internal bool IsRequestCurrent(int version) { return Volatile.Read(ref _loadVersion) == version; }

		// UI thread: a decode (or cache hit) finished.
		internal void OnDecoded(int version, BitmapSource bitmap, int decodeWidth)
		{
			if (version != _loadVersion) return;   // superseded or unloaded meanwhile
			_loadPending = false;
			if (bitmap == null) { _decodeFailed = true; return; }
			if (!_wanted) return;

			Source = ImageLoader.ForDisplay(bitmap);
			_hasAlpha = HasAlpha(bitmap);
			_decodedWidthPx = decodeWidth;
			_sourceNeedsReorient = false;
			InvalidateVisual();

			// Resized again while this decode was in flight.
			if (NeedsSharperDecode(NeededDecodeWidthPx())) RequestDecode();
		}

		/// <summary>Whether the last decoded bitmap has an alpha channel (or transparent palette entries).</summary>
		public bool HasTransparency { get { return _hasAlpha; } }

		internal static bool HasAlpha(BitmapSource b)
		{
			var f = b.Format;
			if (f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
				|| f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float)
				return true;
			var p = b.Palette;
			return p != null && p.Colors.Any(c => c.A < 255);
		}

		// Decode width needed in FILE orientation (a quarter-turned image's file
		// width is its displayed height), or 0 for full resolution.
		int NeededDecodeWidthPx()
		{
			if (_fileWidth <= 0) return 0;
			double shown = _swapAxes ? _imageRect.Height : _imageRect.Width;
			int px = (int)Math.Ceiling(shown * DpiScale);
			return px >= _fileWidth ? 0 : px;
		}

		bool NeedsSharperDecode(int need)
		{
			if (_decodedWidthPx < 0) return true;     // nothing decoded yet
			if (_decodedWidthPx == 0) return false;   // already full resolution
			return need == 0 || need > _decodedWidthPx;
		}


		// ---- hover / status -------------------------------------------------
		protected override void OnMouseEnter(MouseEventArgs e)
		{
			base.OnMouseEnter(e);
			_hovered = true;
			InvalidateVisual();
			MainWindow.Current?.OnImageHover(this, true);
		}

		protected override void OnMouseLeave(MouseEventArgs e)
		{
			base.OnMouseLeave(e);
			_hovered = false;
			InvalidateVisual();
			MainWindow.Current?.OnImageHover(this, false);
		}

		// Cheap (no transforms, StringBuilder): WPF calls ToString implicitly
		// in many places (tracing, debugger, automation).
		public override string ToString()
		{
			var sb = new StringBuilder(64);
			if (LayoutRect.HasValue)
			{
				var lr = LayoutRect.Value;
				sb.Append('(').Append((int)Math.Ceiling(lr.X)).Append(',')
				  .Append((int)Math.Ceiling(lr.Y)).Append(", ")
				  .Append((int)Math.Ceiling(lr.Width)).Append('x')
				  .Append((int)Math.Ceiling(lr.Height)).Append(") - ");
			}
			sb.Append((int)Math.Ceiling(LayoutWidth)).Append('x').Append((int)Math.Ceiling(LayoutHeight))
			  .Append(" - ").Append(PixelWidth).Append('x').Append(PixelHeight)
			  .Append(" -- ").Append(Path);
			return sb.ToString();
		}
	}
}
