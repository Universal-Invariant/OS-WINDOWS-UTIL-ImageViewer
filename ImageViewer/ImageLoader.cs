using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageViewer
{
	/// <summary>
	/// Image dimensions read from the file header only (no pixel decode).
	/// Produced on the folder-loading worker thread and handed to the UI thread,
	/// where LazyImage is constructed from it without touching the disk.
	/// </summary>
	public sealed class ImageHeader
	{
		public string Path;
		public string CacheKey;      // path + last-write time: a file changed on disk never hits a stale cache entry
		public int PixelWidth;       // as stored in the file (before EXIF orientation)
		public int PixelHeight;
		public int Orientation = 1;  // EXIF orientation 1..8 (JPEG), 1 = as stored
		public long FileSize;
		public DateTime Modified;
		public bool Ok;

		public static ImageHeader Read(FileInfo file)
		{
			var h = Read(file.FullName, file.LastWriteTimeUtc);
			h.FileSize = file.Length;
			h.Modified = file.LastWriteTime;
			return h;
		}

		public static ImageHeader Read(string path, DateTime lastWriteUtc)
		{
			var h = new ImageHeader { Path = path, CacheKey = path + "|" + lastWriteUtc.Ticks };
			long t0 = Perf.Start();
			try
			{
				// FileStream rather than a Uri: paths containing '#' or '%' are
				// mangled by Uri parsing. DelayCreation + CacheOption.None parses
				// only the header; the decoder is dropped right away.
				using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
				{
					// PERF: parse the common formats ourselves (a few bytes at fixed
					// offsets / one marker scan). Creating a WPF BitmapDecoder per
					// file was the folder-load bottleneck (~0.1 ms each and it
					// serializes across threads); everything else - and anything
					// that doesn't parse - still goes through WIC.
					int w, hh, orientation;
					if (TryReadFast(fs, out w, out hh, out orientation))
					{
						h.PixelWidth = w;
						h.PixelHeight = hh;
						h.Orientation = orientation;
						h.Ok = true;
						Perf.Stop(Perf.C.HeaderRead, t0);
						return h;
					}
					fs.Position = 0;

					var dc = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
					if (dc.Frames.Count == 0) return h;
					var f = dc.Frames[0];
					h.PixelWidth = f.PixelWidth;
					h.PixelHeight = f.PixelHeight;
					h.Ok = h.PixelWidth > 0 && h.PixelHeight > 0;
				}
			}
			catch
			{
				// corrupt / unsupported file: Ok stays false
			}
			Perf.Stop(Perf.C.HeaderRead, t0);
			return h;
		}

		// Pixel size (and, for JPEG, the EXIF orientation) from the file header
		// for PNG, GIF, BMP and JPEG; false for anything else or anything
		// unexpected (the caller then asks WIC).
		static bool TryReadFast(Stream s, out int width, out int height, out int orientation)
		{
			width = height = 0;
			orientation = 1;
			var b = new byte[26];
			int n = ReadUpTo(s, b, 26);

			// PNG: signature, then the IHDR chunk with big-endian width/height.
			if (n >= 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G'
				&& b[12] == 'I' && b[13] == 'H' && b[14] == 'D' && b[15] == 'R')
			{
				width = BE32(b, 16); height = BE32(b, 20);
				return width > 0 && height > 0;
			}

			// GIF: logical screen size, little-endian.
			if (n >= 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8')
			{
				width = b[6] | b[7] << 8; height = b[8] | b[9] << 8;
				return width > 0 && height > 0;
			}

			// BMP: BITMAPCOREHEADER (12 bytes, 16-bit sizes) or a later header
			// (32-bit sizes; negative height = top-down).
			if (n >= 26 && b[0] == 'B' && b[1] == 'M')
			{
				int dib = LE32(b, 14);
				if (dib == 12) { width = b[18] | b[19] << 8; height = b[20] | b[21] << 8; }
				else if (dib >= 40) { width = LE32(b, 18); height = Math.Abs(LE32(b, 22)); }
				return width > 0 && height > 0;
			}

			// JPEG: walk the marker segments up to the first start-of-frame.
			if (n >= 4 && b[0] == 0xFF && b[1] == 0xD8)
			{
				s.Position = 2;
				var seg = new byte[7];
				for (int guard = 0; guard < 512; guard++)
				{
					int m = s.ReadByte();
					if (m != 0xFF) return false;
					do { m = s.ReadByte(); } while (m == 0xFF);   // fill bytes
					if (m < 0) return false;
					if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) continue;   // no length
					if (m == 0xD9 || m == 0xDA) return false;   // end / start of scan before any frame
					if (ReadUpTo(s, seg, 2) < 2) return false;
					int len = seg[0] << 8 | seg[1];
					if (len < 2) return false;
					if (m == 0xE1 && len > 16 && len <= 65535)   // APP1: may be Exif
					{
						var app1 = new byte[len - 2];
						if (ReadUpTo(s, app1, app1.Length) < app1.Length) return false;
						orientation = ExifOrientation(app1);
						continue;
					}
					bool sof = m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC;
					if (sof)
					{
						if (ReadUpTo(s, seg, 5) < 5) return false;   // precision, height, width
						height = seg[1] << 8 | seg[2];
						width = seg[3] << 8 | seg[4];
						return width > 0 && height > 0;
					}
					s.Seek(len - 2, SeekOrigin.Current);
				}
			}
			return false;
		}

		// Orientation tag (0x0112) of IFD0 in an APP1 "Exif" segment; 1 if absent.
		static int ExifOrientation(byte[] seg)
		{
			int len = seg.Length;
			if (len < 14 || seg[0] != 'E' || seg[1] != 'x' || seg[2] != 'i' || seg[3] != 'f' || seg[4] != 0 || seg[5] != 0) return 1;
			const int t = 6;   // TIFF header
			bool le = seg[t] == 'I' && seg[t + 1] == 'I';
			if (!le && !(seg[t] == 'M' && seg[t + 1] == 'M')) return 1;
			Func<int, int> u16 = i => le ? seg[i] | seg[i + 1] << 8 : seg[i] << 8 | seg[i + 1];
			Func<int, long> u32 = i => le ? (uint)(seg[i] | seg[i + 1] << 8 | seg[i + 2] << 16 | seg[i + 3] << 24)
			                              : (uint)(seg[i] << 24 | seg[i + 1] << 16 | seg[i + 2] << 8 | seg[i + 3]);
			long ifd = t + u32(t + 4);
			if (ifd < t || ifd + 2 > len) return 1;
			int count = u16((int)ifd);
			for (int k = 0; k < count; k++)
			{
				long e = ifd + 2 + k * 12L;
				if (e + 12 > len) break;
				if (u16((int)e) == 0x0112)
				{
					int v = u16((int)e + 8);
					return v >= 1 && v <= 8 ? v : 1;
				}
			}
			return 1;
		}

		static int ReadUpTo(Stream s, byte[] buf, int count)
		{
			int total = 0;
			while (total < count)
			{
				int r = s.Read(buf, total, count - total);
				if (r <= 0) break;
				total += r;
			}
			return total;
		}

		static int BE32(byte[] b, int i) { return b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]; }
		static int LE32(byte[] b, int i) { return b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24; }
	}

	/// <summary>Which files are images, and walking a folder tree for them.</summary>
	public static class ImageFiles
	{
		// Formats with a WIC codec. WebP/HEIC/AVIF/JPEG-XR depend on codecs that
		// are installed on most Windows 10/11 machines; files whose header can't
		// be read are simply skipped by the loader.
		static readonly HashSet<string> _extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".jpg", ".jpeg", ".jpe", ".jif", ".jfif", ".jfi", ".png", ".bmp", ".dib", ".ico", ".gif",
			".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp",
		};

		public static bool IsImage(string pathOrName)
		{
			if (string.IsNullOrEmpty(pathOrName)) return false;
			string ext;
			try { ext = System.IO.Path.GetExtension(pathOrName); } catch { return false; }
			return !string.IsNullOrEmpty(ext) && _extensions.Contains(ext);
		}

		/// <summary>
		/// Image files of a folder (and optionally its subfolders), each folder's
		/// files in natural (Explorer) order, depth-first. Unlike
		/// DirectoryInfo.EnumerateFiles(AllDirectories), an inaccessible
		/// subfolder is skipped instead of aborting the whole enumeration, and
		/// junctions/symlinks are not followed (no cycles).
		/// </summary>
		public static IEnumerable<FileInfo> Enumerate(DirectoryInfo root, bool recurse)
		{
			var pending = new Stack<DirectoryInfo>();
			pending.Push(root);
			while (pending.Count > 0)
			{
				var dir = pending.Pop();

				FileInfo[] files;
				try { files = dir.GetFiles(); }
				catch { continue; }   // access denied, vanished, ...
				Array.Sort(files, (a, b) => Native.CompareNatural(a.Name, b.Name));
				foreach (var f in files)
					if (IsImage(f.Name)) yield return f;

				if (!recurse) continue;
				DirectoryInfo[] subs;
				try { subs = dir.GetDirectories(); }
				catch { continue; }
				Array.Sort(subs, (a, b) => Native.CompareNatural(b.Name, a.Name));   // reversed: stack pops them in order
				foreach (var s in subs)
					if ((s.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(s);
			}
		}
	}

	/// <summary>
	/// Decodes bitmaps off the UI thread at (roughly) their displayed size and
	/// keeps a memory-bounded LRU cache of the results, so scrolling back over
	/// already-seen images is instant and never re-reads the disk.
	/// </summary>
	public static class ImageLoader
	{
		// Total decoded-pixel budget of the cache. Bitmaps currently on screen
		// are referenced by their Image.Source anyway; evicting them here only
		// means a later re-show has to decode again.
		public static long CacheBudgetBytes = 256L * 1024 * 1024;

		struct Request
		{
			public LazyImage Image;
			public int Version;
			public string Path;
			public string CacheKey;
			public int DecodeWidth;     // 0 = full resolution
			public int Transform;       // see KeyFor
		}

		sealed class Entry
		{
			public string Key;
			public BitmapSource Bitmap;
			public int DecodeWidth;
			public long Bytes;
		}

		static readonly BlockingCollection<Request> _queue = new BlockingCollection<Request>(new ConcurrentQueue<Request>());

		static readonly object _cacheLock = new object();
		static readonly Dictionary<string, LinkedListNode<Entry>> _cacheMap = new Dictionary<string, LinkedListNode<Entry>>();
		static readonly LinkedList<Entry> _lru = new LinkedList<Entry>();   // front = most recently used
		static long _cacheBytes;
		public static long CachedBytes { get { lock (_cacheLock) return _cacheBytes; } }
		public static int CachedCount { get { lock (_cacheLock) return _lru.Count; } }

		static ImageLoader()
		{
			// Decoding is CPU bound; leave a core for the UI thread.
			int workers = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
			for (int i = 0; i < workers; i++)
			{
				var t = new Thread(WorkerLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "ImageDecoder" + i };
				t.Start();
			}
		}

		/// <summary>
		/// UI thread: a cached bitmap at least decodeWidth wide (or full
		/// resolution), or null.
		/// </summary>
		public static BitmapSource TryGetCached(string key, int decodeWidth, out int cachedDecodeWidth)
		{
			cachedDecodeWidth = -1;
			lock (_cacheLock)
			{
				LinkedListNode<Entry> node;
				if (!_cacheMap.TryGetValue(key, out node)) return null;
				var e = node.Value;
				if (!Satisfies(e.DecodeWidth, decodeWidth)) return null;
				_lru.Remove(node);
				_lru.AddFirst(node);
				cachedDecodeWidth = e.DecodeWidth;
				return e.Bitmap;
			}
		}

		/// <summary>
		/// Queues a background decode. The result is delivered on the image's
		/// dispatcher via LazyImage.OnDecoded; requests that went stale in the
		/// meantime (image unloaded or re-requested) are skipped without decoding.
		/// </summary>
		/// <summary>
		/// Orientation applied after decoding, as one int: bit 2 = mirror
		/// horizontally (first), bits 0-1 = clockwise quarter turns (then).
		/// Covers all 8 EXIF orientations plus the user's 90° rotations.
		/// Each transform is cached separately.
		/// </summary>
		public static string KeyFor(string cacheKey, int transform)
		{
			return transform == 0 ? cacheKey : cacheKey + "|t" + transform;
		}

		/// <summary>
		/// Quarter turns the decoder applies (BitmapImage.Rotation). Mirrored
		/// orientations are drawn mirrored instead (IsMirrored): rotate(q) after a
		/// mirror equals a mirror after rotate(-q). Rotating in the decoder keeps
		/// every result a BitmapImage - a CachedBitmap built on a worker thread
		/// can't be cloned on the UI thread (ForDisplay), which crashed the app.
		/// </summary>
		public static int DecodeQuarters(int transform)
		{
			int q = transform & 3;
			return IsMirrored(transform) ? (4 - q) % 4 : q;
		}

		public static bool IsMirrored(int transform) { return (transform & 4) != 0; }

		/// <param name="cacheKey">Already combined with the transform (KeyFor).</param>
		public static void Enqueue(LazyImage image, int version, string path, string cacheKey, int decodeWidth, int transform)
		{
			Interlocked.Increment(ref Perf.PendingDecodes);
			_queue.Add(new Request { Image = image, Version = version, Path = path, CacheKey = cacheKey, DecodeWidth = decodeWidth, Transform = transform });
		}

		/// <summary>
		/// One-off decode on the thread pool (preview, clipboard). Uses its own
		/// cache key so a large preview decode never replaces the thumbnail-sized
		/// entry the grid uses. onDone (may be null) runs on the dispatcher with
		/// the bitmap, or null when the file can't be decoded.
		/// </summary>
		public static void DecodeAsync(string path, string cacheKey, int decodeWidth, int transform, Dispatcher dispatcher, Action<BitmapSource> onDone)
		{
			string key = KeyFor(cacheKey, transform) + "|full";
			System.Threading.Tasks.Task.Run(() =>
			{
				int have;
				var bmp = TryGetCached(key, decodeWidth, out have);
				if (bmp == null)
				{
					bmp = Decode(path, decodeWidth, transform);
					if (bmp != null) Put(key, bmp, decodeWidth);
				}
				if (onDone != null)
				{
					try { dispatcher.BeginInvoke(new Action(() => onDone(bmp))); }
					catch { /* shutting down */ }
				}
			});
		}

		/// <summary>
		/// The object to put in Image.Source: a frozen clone that shares the
		/// cached bitmap's pixels. MEMORY FIX: with hardware rendering, a bitmap
		/// that has been drawn keeps render/GPU resources for as long as the
		/// BitmapSource object lives - and cached ones live on after scrolling
		/// away. Benchmark (300 photos, 256 MB cache): every cached MB cost ~2.3 MB
		/// of private memory; displaying clones brings it to ~1.0 MB (1,094 ->
		/// 737 MB after a full scroll). Clones of a frozen bitmap share the
		/// decoded pixel buffer, so this costs no extra pixel memory.
		/// </summary>
		public static BitmapSource ForDisplay(BitmapSource cached)
		{
			if (cached == null) return null;
			var c = cached.Clone();
			c.Freeze();
			return c;
		}

		// A bitmap decoded at `have` pixels wide can serve a request for `want`.
		static bool Satisfies(int have, int want)
		{
			if (have == 0) return true;      // full resolution serves everything
			if (want == 0) return false;     // full resolution wanted
			return have >= want;
		}

		static void WorkerLoop()
		{
			foreach (var r in _queue.GetConsumingEnumerable())
			{
				// Scrolled away (or superseded) before we got to it: skip the decode.
				if (!r.Image.IsRequestCurrent(r.Version)) { Interlocked.Decrement(ref Perf.PendingDecodes); continue; }

				int width;
				var bmp = TryGetCached(r.CacheKey, r.DecodeWidth, out width);
				if (bmp == null)
				{
					width = r.DecodeWidth;
					bmp = Decode(r.Path, r.DecodeWidth, r.Transform);
					if (bmp != null) Put(r.CacheKey, bmp, width);
				}
				else if (Perf.Enabled) Interlocked.Increment(ref Perf.CacheHits);
				Interlocked.Decrement(ref Perf.PendingDecodes);

				var img = r.Image;
				int version = r.Version;
				var result = bmp;
				int resultWidth = width;
				try
				{
					img.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => img.OnDecoded(version, result, resultWidth)));
				}
				catch
				{
					// dispatcher shut down: app is exiting
				}
			}
		}

		static BitmapSource Decode(string path, int decodeWidth, int transform)
		{
			long t0 = Perf.Start();
			try
			{
				using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
				{
					var bm = new BitmapImage();
					bm.BeginInit();
					// OnLoad decodes everything during EndInit (here, off the UI
					// thread) and lets us close the file right away. DecodePixelWidth
					// makes WIC scale while decoding: a 6000px photo shown at 300px
					// costs ~0.4 MB instead of ~96 MB. Aspect ratio is preserved.
					bm.CacheOption = BitmapCacheOption.OnLoad;
					if (decodeWidth > 0) bm.DecodePixelWidth = decodeWidth;   // file width, before rotation
					bm.Rotation = (Rotation)DecodeQuarters(transform);
					bm.StreamSource = fs;
					bm.EndInit();
					bm.Freeze();   // frozen => usable from the UI thread

					BitmapSource result = bm;
					Perf.Stop(Perf.C.Decode, t0);
					if (Perf.Enabled) Interlocked.Add(ref Perf.DecodedPixels, (long)result.PixelWidth * result.PixelHeight);
					return result;
				}
			}
			catch
			{
				return null;
			}
		}

		static void Put(string key, BitmapSource bmp, int decodeWidth)
		{
			long bytes = (long)bmp.PixelWidth * bmp.PixelHeight * 4;
			lock (_cacheLock)
			{
				LinkedListNode<Entry> node;
				if (_cacheMap.TryGetValue(key, out node))
				{
					// Keep whichever decode is sharper.
					if (Satisfies(node.Value.DecodeWidth, decodeWidth)) return;
					_cacheBytes -= node.Value.Bytes;
					_lru.Remove(node);
					_cacheMap.Remove(key);
				}

				node = _lru.AddFirst(new Entry { Key = key, Bitmap = bmp, DecodeWidth = decodeWidth, Bytes = bytes });
				_cacheMap[key] = node;
				_cacheBytes += bytes;

				while (_cacheBytes > CacheBudgetBytes && _lru.Count > 1)
				{
					var last = _lru.Last;
					_lru.RemoveLast();
					_cacheMap.Remove(last.Value.Key);
					_cacheBytes -= last.Value.Bytes;
				}
			}
		}
	}
}
