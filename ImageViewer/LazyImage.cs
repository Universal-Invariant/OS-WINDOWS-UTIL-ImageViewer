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


        // Store the scale factor for debugging
        private double _currentScale = 1.0;

        // Modified UpdateSize with debug output
        public void UpdateSize(double scale)
        {
            _currentScale = scale; // Store the scale factor
            this.Width = _originalWidth * scale;
            this.Height = _originalHeight * scale;
            Debug.WriteLine($"[LazyImage UpdateSize] Path: {Path}, Scale: {scale:F2}, New Width: {this.Width:F2}, New Height: {this.Height:F2}");

            // Invalidate measure/layout
            this.InvalidateMeasure();
        }

        // Modified MeasureOverride with debug output
        protected override Size MeasureOverride(Size constraint)
        {
            var desiredSize = new Size(this.Width, this.Height);
            Debug.WriteLine($"[LazyImage MeasureOverride] Path: {Path}, Scale: {_currentScale:F2}, DesiredSize: ({desiredSize.Width:F2}, {desiredSize.Height:F2})");
            return desiredSize;
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

            // Apply initial scale (default = 1.0)
            UpdateSize(1.0);


            this.Width = ImageWidth;
			this.Height = ImageHeight;
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
