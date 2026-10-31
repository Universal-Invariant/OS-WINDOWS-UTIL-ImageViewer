using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageViewer
{
	/// <summary>
	/// Plays animated GIFs in the preview. WPF only shows a GIF's first frame;
	/// here every frame is composited the way browsers do it (frame offsets,
	/// transparency, disposal methods) on a background thread, then shown on a
	/// timer with the file's frame delays.
	/// </summary>
	internal sealed class GifAnimator
	{
		// Composited frames are kept in memory; above this the GIF stays still.
		const long MaxBytes = 384L * 1024 * 1024;

		readonly List<BitmapSource> _frames = new List<BitmapSource>();
		readonly List<TimeSpan> _delays = new List<TimeSpan>();
		DispatcherTimer _timer;
		int _index;
		Action<BitmapSource> _show;

		public static bool IsGif(string path)
		{
			return string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Decodes and composites on the thread pool; calls onReady on the
		/// dispatcher with an animator (null when the file isn't animated or
		/// is too large). transform: see ImageLoader.KeyFor.
		/// </summary>
		public static void Load(string path, int transform, Dispatcher dispatcher, Action<GifAnimator> onReady)
		{
			Task.Run(() =>
			{
				GifAnimator anim = null;
				try { anim = Build(path, transform); } catch { anim = null; }
				try { dispatcher.BeginInvoke(new Action(() => onReady(anim))); } catch { }
			});
		}

		static GifAnimator Build(string path, int transform)
		{
			BitmapDecoder dec;
			using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				dec = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
			if (dec.Frames.Count < 2) return null;

			int w = MetaInt(dec.Metadata, "/logscrdesc/Width", dec.Frames[0].PixelWidth);
			int h = MetaInt(dec.Metadata, "/logscrdesc/Height", dec.Frames[0].PixelHeight);
			if (w <= 0 || h <= 0 || (long)w * h * 4 * dec.Frames.Count > MaxBytes) return null;

			int stride = w * 4;
			var canvas = new byte[stride * h];   // Pbgra32, starts fully transparent
			byte[] saved = null;
			var anim = new GifAnimator();
			int prevDisposal = 0;
			Int32Rect prevRect = Int32Rect.Empty;

			foreach (var frame in dec.Frames)
			{
				var meta = frame.Metadata as BitmapMetadata;
				int fx = MetaInt(meta, "/imgdesc/Left", 0), fy = MetaInt(meta, "/imgdesc/Top", 0);
				int fw = frame.PixelWidth, fh = frame.PixelHeight;
				int delay = MetaInt(meta, "/grctlext/Delay", 10);      // 1/100 s
				int disposal = MetaInt(meta, "/grctlext/Disposal", 0);

				// Undo the previous frame per its disposal method.
				if (prevDisposal == 2) Clear(canvas, stride, prevRect, w, h);
				else if (prevDisposal == 3 && saved != null) Buffer.BlockCopy(saved, 0, canvas, 0, canvas.Length);

				if (disposal == 3) saved = (byte[])canvas.Clone();

				// Draw this frame (GIF alpha is all-or-nothing: copy opaque pixels).
				var conv = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
				var px = new byte[fw * fh * 4];
				conv.CopyPixels(px, fw * 4, 0);
				for (int y = 0; y < fh; y++)
				{
					int cy = fy + y;
					if (cy < 0 || cy >= h) continue;
					for (int x = 0; x < fw; x++)
					{
						int cx = fx + x;
						if (cx < 0 || cx >= w) continue;
						int s = (y * fw + x) * 4;
						if (px[s + 3] == 0) continue;
						Buffer.BlockCopy(px, s, canvas, cy * stride + cx * 4, 4);
					}
				}

				var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, canvas, stride);
				if (transform != 0) bmp = Orient(bmp, transform);
				bmp.Freeze();
				anim._frames.Add(bmp);
				// Browsers treat 0/1 (= "as fast as possible") as 100 ms.
				anim._delays.Add(TimeSpan.FromMilliseconds(delay <= 1 ? 100 : delay * 10));

				prevDisposal = disposal;
				prevRect = new Int32Rect(fx, fy, fw, fh);
			}
			return anim;
		}

		static BitmapSource Orient(BitmapSource b, int transform)
		{
			// Same convention as the still decoder: quarter turns here, the
			// mirror (if any) is applied by the preview's RenderTransform.
			int q = ImageLoader.DecodeQuarters(transform);
			if (q == 0) return b;
			return new CachedBitmap(new TransformedBitmap(b, new RotateTransform(q * 90)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
		}

		static void Clear(byte[] canvas, int stride, Int32Rect r, int w, int h)
		{
			for (int y = Math.Max(0, r.Y); y < Math.Min(h, r.Y + r.Height); y++)
			{
				int x0 = Math.Max(0, r.X), x1 = Math.Min(w, r.X + r.Width);
				if (x1 > x0) Array.Clear(canvas, y * stride + x0 * 4, (x1 - x0) * 4);
			}
		}

		static int MetaInt(ImageMetadata metadata, string query, int fallback)
		{
			var m = metadata as BitmapMetadata;
			if (m == null) return fallback;
			try
			{
				var v = m.GetQuery(query);
				return v == null ? fallback : Convert.ToInt32(v);
			}
			catch { return fallback; }
		}

		/// <summary>Starts looping; show is called with each frame.</summary>
		public void Start(Action<BitmapSource> show)
		{
			_show = show;
			_index = 0;
			_show(_frames[0]);
			_timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = _delays[0] };
			_timer.Tick += (s, e) =>
			{
				_index = (_index + 1) % _frames.Count;
				_show(_frames[_index]);
				_timer.Interval = _delays[_index];
			};
			_timer.Start();
		}

		public void Stop()
		{
			if (_timer != null) _timer.Stop();
			_timer = null;
		}
	}
}
