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

        // Scales the image and recomputes its layout size.
        //   scale          : user scale factor from the toolbar slider
        //   targetWidth    : > 0 enables "fit to width" (book format) mode -
        //                    every image is scaled so its rendered width
        //                    equals this value.
        //   maxSizeFactor  : fraction of the available layout area used to
        //                    clamp oversized images before scaling.
        public void UpdateSize(double scale, double targetWidth = 0, double maxSizeFactor = 1.0)
        {
            _lastScale = scale;
            _lastFitWidth = targetWidth;
            _lastMaxSizeFactor = maxSizeFactor;

            double w = _originalWidth;
            double h = _originalHeight;

            if (targetWidth > 0)
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
            this.Width = Math.Max(minDim, w);
            this.Height = Math.Max(minDim, h);

            // Force WPF to re-measure this element so DesiredSize tracks the
            // explicit Width/Height we just set.
            InvalidateMeasure();
        }

        // Recompute the size using the last layout parameters. Called when
        // the viewport changes (window resize) so modes like fit-to-width
        // stay in sync with the actual usable width.
        public void RefreshSize(double targetWidth)
        {
            UpdateSize(_lastScale, targetWidth > 0 ? targetWidth : _lastFitWidth, _lastMaxSizeFactor);
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
			return rect.Format() + " - {" + ImageWidth + ", " + ImageHeight + "} - " + (IsLoaded ? "O" : "X") + " - " + Path.ToString();
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
			(App.Current.MainWindow as MainWindow).SB_Info2.Text = Path;
		}


	}
}
