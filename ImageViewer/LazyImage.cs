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
		
		BitmapDecoder bitmap;

		public new bool IsLoaded { get; set; }
		public bool IsDrawn { get; set; }
		public string Path = "";
		ListView host = null;

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

 
			var imageStream = new Uri(path, UriKind.Absolute); 
			BitmapDecoder dc = null;
			try
			{
				dc = BitmapDecoder.Create(imageStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
				if (dc.Frames.Count == 0) return;
			} catch (Exception e)
			{
				return;
			}
			bitmap = dc;
			
			ImageWidth = dc.Frames[0].Width / 96.0 * dc.Frames[0].DpiX;
            ImageHeight = dc.Frames[0].Height / 96.0 * dc.Frames[0].DpiY;

			if ((ImageWidth > MaxWidth && MaxWidth > 0) || (ImageHeight > MaxHeight && MaxHeight > 0))
			{
				var c = (ImageWidth > ImageHeight) ? (MaxWidth/ImageWidth) : (MaxHeight/ImageHeight);
				ImageWidth = c*ImageWidth; ImageHeight = c*ImageHeight;
			}

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
			if (Source == null && bitmap != null)
			{
				var bm = new BitmapImage();
				bm.BeginInit();
				bm.CacheOption = BitmapCacheOption.None;
				bm.UriSource = new Uri(Path, UriKind.Absolute);
				bm.EndInit();
				bm.Freeze();
				
				Source = bm;
				//this.InvalidateVisual();
				IsLoaded = true;
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

		public override string ToString()
		{
			var rect = GetBoundingBox(this, this.VisualParent as Visual);

			string P = "";
            if (LayoutRect.HasValue) {
				P = $"({(Int32)Math.Ceiling(LayoutRect.Value.X)},{(Int32)Math.Ceiling(LayoutRect.Value.Y)}, {(Int32)Math.Ceiling(LayoutRect.Value.Width)}x{(Int32)Math.Ceiling(LayoutRect.Value.Height)}) - ";
            }
            return $"{P}{(Int32)Math.Ceiling(rect.Width)}x{(Int32)Math.Ceiling(rect.Height)} - {(Int32)Math.Ceiling(ImageWidth)}x{(Int32)Math.Ceiling(ImageHeight)} -- {Path}";
		}

		private static Rect GetBoundingBox(FrameworkElement child, Visual parent)
		{
			if (parent == null) return new Rect();
			//var transform = child.TransformToAncestor(parent);
			var transform = child.TransformToVisual(parent);
			var topLeft = transform.Transform(new Point(0, 0));
			var bottomRight = transform.Transform(new Point(child.Width, child.Height));
			return new Rect(topLeft, bottomRight);
		}


		protected override void OnMouseEnter(MouseEventArgs e)
		{ 
			
			base.OnMouseEnter(e);			
            (App.Current.MainWindow as MainWindow).SB_Size.Text = ToString();
        }


	}
}
