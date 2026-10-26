using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Threading;
using System.ComponentModel;
using System.Diagnostics;


namespace ImageViewer
{
	public interface ILazyLoad
	{
		void Load();
		void UnLoad();
	}

	public class LazyImage : Image, ILazyLoad
	{
		BitmapImage bm;
		static BitmapImage notloaded = new BitmapImage(new Uri(@"/ImageViewer;component/Unloaded.png", UriKind.Relative));

		public new bool IsLoaded { get; set; }
		public bool IsDrawn { get; set; }
		public string Path = "";
		ListView host = null;

		// The ListView this image is displayed in (set by MainWindow when the
		// batch is handed to the UI thread). Used by LoadImage() to find the
		// correct Dispatcher for deferring Source assignment out of WPF's
		// layout pass. Falls back to SmartFlowPanel.Current.LVHost when null.
		public ListView LVHost { get; set; }

		// Set to true once the header read succeeded (real pixel dimensions
		// captured). Replaces the old `bitmap != null` decoder reference: we no
		// longer keep a BitmapDecoder alive per image (see constructor - it is
		// thread-affine and its DpiX/DpiY getters throw cross-thread unless
		// frozen, which silently zeroed every off-thread header read).
		bool _headerReadOk = false;

		public double ImageWidth = 0;
		public double ImageHeight = 0;


        // Store original (unscaled) dimensions
        private double _originalWidth;
        private double _originalHeight;

        // Layout parameters of the last UpdateSize() call, kept so the size
        // can be recomputed whenever the window is resized while a layout
        // mode (e.g. fit-to-width) is active.
        private double _lastScale = 1.0;
        private double _lastFitWidth = 0;
        private double _lastMaxSizeFactor = 1.0;
        private double _lastMinFitWidthPx = 0;

        // The (already clamped) maximum rendered width this image may ever
        // occupy - set from the constructor's MaxWidth. Used by UpdateSize as
        // a final safety clamp in fit-to-width mode so a page can never be
        // wider than the wrap area even if the caller asks for a target width
        // equal to (or above) the full extent width.
        private double _maxLayoutWidth = 0;

        public Rect? LayoutRect { get; set; }


        // Scales the image and recomputes its layout size.
        //   scale           : user scale factor from the toolbar slider
        //   targetWidth     : > 0 enables "fit to width" (book format) mode -
        //                     images at least as wide as minWidth are scaled
        //                     so their rendered width equals this value.
        //   maxSizeFactor   : fraction of the available layout area used to
        //                     clamp oversized images before scaling.
        //   minWidthInPixels: fit-to-width threshold (original pixel width).
        //                     Images narrower than this keep their normal
        //                     scale so small icons don't get blown up to the
        //                     full window width; they then flow in between
        //                     the large pages.
        // NOTE: the size is ALWAYS derived from the original dimensions -
        // never chained onto previously computed sizes - so repeated calls
        // cannot accumulate rounding error (a source of transient overlaps).
        public void UpdateSize(double scale, double targetWidth = 0, double maxSizeFactor = 1.0, double minWidthInPixels = 0)
        {
            _lastScale = scale;
            _lastFitWidth = targetWidth;
            _lastMaxSizeFactor = maxSizeFactor;
            _lastMinFitWidthPx = minWidthInPixels;

            double w = _originalWidth;
            double h = _originalHeight;

            // Threshold is expressed in ORIGINAL PIXELS; _originalWidth/_originalHeight
            // are device-independent units (96 dpi), so convert px -> DIU by dividing.
            // (The old multiplication made the threshold ~9216 DIU, so effectively NO
            // image was ever excluded - every image got stretched to window width and
            // a single tall page could exceed the viewport, causing the book-view
            // scroll glitches.)
            bool fits = targetWidth > 0 && (_originalWidth >= minWidthInPixels / 96.0 || _originalHeight >= minWidthInPixels / 96.0);
            if (fits)
            {
                // Book format: derive the scale directly from the current
                // window width instead of chaining it onto previously
                // computed sizes (which would accumulate rounding error).
                var c = targetWidth / w;
                w *= c;
                h *= c;

                // SAFETY CLAMP (book view "sometimes works then breaks"): a
                // page may never be wider than the layout area it wraps
                // inside. If the fit target ever exceeds the available width -
                // e.g. MainWindow's FitToWidthFactor is 1.0 so pages are sized
                // to exactly the full extent width while WPF reserves no
                // gutter (horizontal scrollbar Disabled) - floating point
                // drift makes the wrap test (lineWidth + child > avail) fire
                // on the SECOND page instead of starting a new row, and two
                // window-width pages share one row and overlap: the book view
                // looks fine until some later relayout pass and collapses into
                // chaos after that ("works sometimes then doesn't"). Clamping
                // here guarantees a page always fits its row regardless of who
                // supplied the target width.
                if (_maxLayoutWidth > 0 && w > _maxLayoutWidth)
                {
                    var cc = _maxLayoutWidth / w;
                    w *= cc;
                    h *= cc;
                }
            }

            w *= scale;
            h *= scale;

            // Never let an image collapse to zero (or negative/NaN) extents -
            // degenerate sizes break the flow layout and cause overlaps.
            const double minDim = 8.0;
            var newW = double.IsNaN(w) ? minDim : Math.Max(minDim, w);
            var newH = double.IsNaN(h) ? minDim : Math.Max(minDim, h);

            // Only touch Width/Height (and dirty the layout system) when the
            // size actually changed. This keeps bulk relayouts cheap: most
            // images - e.g. all the small ones while fit-to-width is on -
            // are skipped entirely, preserving the snappy lazy behavior.
            // NOTE: Image.Width/Height are plain doubles (NOT Nullable), so
            // "unset" shows up as NaN rather than HasValue == false.
            // Compare against the CURRENT DesiredSize as well as the explicit
            // Width/Height: after a chrome-strip style change, an old row's
            // DesiredSize can still include ~20px of ListViewItem padding even
            // though Width/Height are already correct. If we skipped the dirty
            // in that case, SmartFlowPanel would lay the row out with the stale
            // (padded) DesiredSize and every image would overlap by ~20px.
            bool sizeChanged = double.IsNaN(this.Width) || double.IsNaN(this.Height) ||
                               Math.Abs(this.Width - newW) >= 0.01 ||
                               Math.Abs(this.Height - newH) >= 0.01 ||
                               (!double.IsNaN(DesiredSize.Width) && Math.Abs(DesiredSize.Width - newW) >= 0.01) ||
                               (!double.IsNaN(DesiredSize.Height) && Math.Abs(DesiredSize.Height - newH) >= 0.01);
            if (sizeChanged)
            {
                this.Width = newW;
                this.Height = newH;
                InvalidateMeasure();
                InvalidateVisual();   // re-render the bitmap at the new extent
            }
        }

        // Recompute the size using the last layout parameters. Called when
        // the viewport changes (window resize) so modes like fit-to-width
        // stay in sync with the actual usable width.
        public void RefreshSize(double targetWidth)
        {
            UpdateSize(_lastScale, targetWidth > 0 ? targetWidth : _lastFitWidth, _lastMaxSizeFactor, _lastMinFitWidthPx);
        }

        public LazyImage(string path, ListView host, double MaxWidth, double MaxHeight) : base() 
		{
			this.Path = path;
			this.host = host;
			IsLoaded = false; IsDrawn = false;

 
			// PERF REGRESSION FIX: this constructor used to run on the UI thread
			// for EVERY image in a folder (6k images -> thousands of synchronous
			// file opens + header reads inside Dispatcher batches -> frozen app and
			// quadratic measure passes). It is now invoked on a BACKGROUND thread by
			// MainWindow's batch loader. That is safe because nothing here creates
			// UI-affinity state:
			//   * BitmapDecoder.Create(..., DelayCreation, CacheOption.None) only
			//     opens the file and parses the IMAGE HEADER (pixel dims + DPI) - it
			//     never decodes pixels. The heavy decode still happens only in
			//     LoadImage(), i.e. when an image scrolls into view. Laziness kept.
			//   * We deliberately do NOT keep or freeze the decoder: WPF's
			//     BitmapFrame.DpiX/DpiY getters throw "freeze required" once the
			//     creating thread is gone, so freezing the decoder would make every
			//     off-thread header read fail (catch => return) and leave ALL images
			//     with zero size -> nothing ever renders ("no images shown").
			//     Instead we extract width/height/DPI INSIDE the try block while the
			//     decoder is still owned by this thread, then drop the reference.
			// If ImageWidth stays 0 (corrupt/unsupported file), MainWindow falls back
			// to a placeholder size on the UI thread and patches the real size later.
			var imageStream = new Uri(path, UriKind.Absolute); 
			double frameW = 0, frameH = 0, dpiX = 96, dpiY = 96;
			try
			{
				BitmapDecoder dc = BitmapDecoder.Create(imageStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
				if (dc.Frames.Count == 0) return;
				var frame = dc.Frames[0];
				frameW = frame.Width;
				frameH = frame.Height;
				dpiX = frame.DpiX;
				dpiY = frame.DpiY;
				// NOTE: no Freeze(). A frozen decoder CAN be referenced across
				// threads, but DpiX/DpiY are not dependency properties and still
				// demand thread affinity at read time - that was the bug that made
				// every background header read silently fail.
			} catch (Exception e)
			{
				return;
			}
			_headerReadOk = true;

			ImageWidth = frameW / 96.0 * dpiX;
            ImageHeight = frameH / 96.0 * dpiY;

			if ((ImageWidth > MaxWidth && MaxWidth > 0) || (ImageHeight > MaxHeight && MaxHeight > 0))
			{
				var c = (ImageWidth > ImageHeight) ? (MaxWidth/ImageWidth) : (MaxHeight/ImageHeight);
				ImageWidth = c*ImageWidth; ImageHeight = c*ImageHeight;
			}

            // Remember the clamped maximum rendered width for the fit-to-width
            // safety clamp in UpdateSize (see _maxLayoutWidth).
            if (MaxWidth > 0) _maxLayoutWidth = Math.Min(MaxWidth, double.MaxValue);

            // After computing ImageWidth/ImageHeight, save originals
            _originalWidth = ImageWidth;
            _originalHeight = ImageHeight;

            // Apply initial size (scale = 1.0)
            UpdateSize(1.0);
			//this.Source = notloaded;
			this.Source = null;
            

            //this.Stretch = System.Windows.Media.Stretch.Fill;
			this.Stretch = System.Windows.Media.Stretch.UniformToFill;
			Visibility = Visibility.Visible;

			
			
			return;

		}

		static LazyImage() { notloaded.Freeze(); }


		
		void ILazyLoad.Load() { LoadImage(); }
		void ILazyLoad.UnLoad() { UnLoadImage(); }

		public void LoadImage()
		{
			/*
			bm = new BitmapImage();
			bm.BeginInit();
			bm.CacheOption = BitmapCacheOption.None;
			bm.UriSource = new Uri(Path, UriKind.Absolute);
			bm.EndInit();
			bm.Freeze();
			 */
			// Guard on the header-read flag, NOT a decoder reference: keeping a
			// thread-affine BitmapDecoder per image was both a memory leak and a
			// cross-thread crash source. The actual decode happens here (only when
			// an image scrolls into view) via a fresh BitmapImage.
			//
			// CRITICAL FIX ("no images shown"): LoadImage() is invoked from the
			// ScrollChanged handler while WPF is inside its layout pass (Measure).
			// Assigning Source there re-triggers MeasureAsync on this element in
			// the middle of the ongoing pass - WPF swallows that silently, so the
			// Image keeps rendering with a Source that was never measured/arranged:
			// every LazyImage occupies its slot but paints NOTHING. Defer the
			// Source assignment to the next dispatcher turn (after layout completes)
			// at Input priority so the pixels still arrive within milliseconds.
			if (Source == null && _headerReadOk)
			{
				var host = LVHost ?? SmartFlowPanel.Current?.LVHost;
				var dispatcher = host != null ? host.Dispatcher : Application.Current?.Dispatcher;

				Action apply = () =>
				{
					// Re-check laziness flags: the image may have been unloaded
					// (scrolled away) between scheduling and execution.
					if (Source != null || !_headerReadOk) return;
					var bm = new BitmapImage();
					bm.BeginInit();
					bm.CacheOption = BitmapCacheOption.None;
					bm.UriSource = new Uri(Path, UriKind.Absolute);
					bm.EndInit();
					bm.Freeze();

					Source = bm;
					IsLoaded = true;
				};

				if (dispatcher != null && !dispatcher.HasShutdownStarted)
					dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, apply);
				else
					apply();   // designer / no dispatcher context: run inline
			}
		}

		public void UnLoadImage()
		{
			Source = null;
			IsLoaded = false;

		}
		

		protected override void OnRender(DrawingContext dc)
		{
			base.OnRender(dc);
			return;
			
			if (IsDrawn)
			{
				if (Source == null)
				{
					bm = new BitmapImage();
					bm.BeginInit();
					bm.CacheOption = BitmapCacheOption.None;
					bm.UriSource = new Uri(Path, UriKind.Absolute);
					bm.EndInit();
					bm.Freeze();
					Source = bm;
				} else
				IsDrawn = true;
				base.OnRender(dc);
			} 
			
 
			

		}

		// PERF FIX: ToString() showed up as a top CPU consumer in the VS profiler
		// even though it "should be free". Two reasons:
		//   1) WPF calls ToString() implicitly all over the place (trace logging,
		//      content-string coercion, ListView item containers, debugger/data
		//      inspection), so it runs far more often than just mouse-hover.
		//   2) The old body did a TransformToVisual matrix computation plus TWO
		//      interpolated-string allocations with eight Math.Ceiling boxing ops
		//      per call - expensive work being done thousands of times for a string
		//      nobody was reading.
		// LayoutRect is already cached by SmartFlowPanel during arrange, so we use
		// it directly (O(1), no transforms) and build the string with StringBuilder
		// (no intermediate format strings). Same information, ~zero cost.
		public override string ToString()
		{
			var sb = new StringBuilder(64);
			if (LayoutRect.HasValue)
			{
				var lr = LayoutRect.Value;
				sb.Append('(').Append((Int32)Math.Ceiling(lr.X)).Append(',')
				  .Append((Int32)Math.Ceiling(lr.Y)).Append(", ")
				  .Append((Int32)Math.Ceiling(lr.Width)).Append('x')
				  .Append((Int32)Math.Ceiling(lr.Height)).Append(") - ");
			}
			sb.Append((Int32)Math.Ceiling(ActualWidth > 0 ? ActualWidth : Width))
			  .Append('x').Append((Int32)Math.Ceiling(ActualHeight > 0 ? ActualHeight : Height))
			  .Append(" - ").Append((Int32)Math.Ceiling(ImageWidth))
			  .Append('x').Append((Int32)Math.Ceiling(ImageHeight))
			  .Append(" -- ").Append(Path);
			return sb.ToString();
		}


		protected override void OnMouseEnter(MouseEventArgs e)
		{ 
			
			base.OnMouseEnter(e);			
            (App.Current.MainWindow as MainWindow).SB_Size.Text = ToString();
        }


	}
}
