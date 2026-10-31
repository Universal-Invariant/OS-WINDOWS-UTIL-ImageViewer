using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageViewer.Properties;

namespace ImageViewer
{
	// Info panel (I): file details of the image under the mouse / keyboard
	// focus / in the preview, plus camera data (EXIF) read off the UI thread.
	public partial class MainWindow
	{
		const double InfoPanelWidth = 300;
		LazyImage _infoShown;
		int _infoVersion;
		DispatcherTimer _infoTimer;

		bool IsInfoOpen { get { return InfoPanel.Visibility == Visibility.Visible; } }

		private void MI_Info_Click(object sender, RoutedEventArgs e) { ToggleInfo(); }

		void ToggleInfo()
		{
			Settings.Default.ShowInfoPanel = !Settings.Default.ShowInfoPanel;
			ApplyInfoPanel();
		}

		/// <summary>Shows/hides the panel; the image area (and preview) give up its width.</summary>
		void ApplyInfoPanel()
		{
			bool show = Settings.Default.ShowInfoPanel;
			InfoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			var margin = new Thickness(0, 0, show ? InfoPanelWidth : 0, 0);
			LV.Margin = margin;
			PreviewOverlay.Margin = margin;
			EmptyState.Margin = margin;
			_infoShown = null;
			if (show) RefreshInfoNow();
			if (IsPreviewOpen) SizePreviewImage();
		}

		LazyImage InfoTarget
		{
			get { return IsPreviewOpen ? _previewItem : ActionItem ?? _current ?? Targets(null).FirstOrDefault(); }
		}

		/// <summary>Hover / focus / preview changed: refresh shortly (hovering across a grid stays cheap).</summary>
		void UpdateInfo()
		{
			if (!IsInfoOpen) return;
			if (_infoTimer == null)
			{
				_infoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
				_infoTimer.Tick += (s, e) => { _infoTimer.Stop(); RefreshInfoNow(); };
			}
			_infoTimer.Stop();
			_infoTimer.Start();
		}

		void RefreshInfoNow(bool force = false)
		{
			if (!IsInfoOpen) return;
			var img = InfoTarget;
			if (img == _infoShown && !force) return;
			_infoShown = img;
			int version = ++_infoVersion;
			InfoRows.Children.Clear();

			if (img == null)
			{
				AddInfoNote(_view.Count == 0 ? "No image." : "Point at an image, or use the arrow keys.");
				return;
			}

			AddInfoTitle(img.FileName);
			AddInfoRow("Folder", img.Folder);
			double mp = (double)img.PixelWidth * img.PixelHeight / 1e6;
			AddInfoRow("Dimensions", img.PixelWidth.ToString("N0") + " × " + img.PixelHeight.ToString("N0") + " px"
				+ (mp >= 0.1 ? "  (" + mp.ToString("0.#") + " MP)" : ""));
			AddInfoRow("Type", (Path.GetExtension(img.FileName) ?? "").TrimStart('.').ToUpperInvariant());
			AddInfoRow("File size", FormatSize(img.FileSize));
			AddInfoRow("Modified", img.Modified.ToString("f"));
			if (img.UserRotation != 0) AddInfoRow("Rotation", img.UserRotation + "° (viewer only)");
			if (img.ViewIndex >= 0) AddInfoRow("Position", (img.ViewIndex + 1).ToString("N0") + " of " + _view.Count.ToString("N0"));

			string path = img.Path;
			Task.Run(() => ReadCameraInfo(path)).ContinueWith(t =>
			{
				if (version != _infoVersion || t.IsFaulted || t.Result == null || t.Result.Count == 0) return;
				AddInfoSection("Camera");
				foreach (var kv in t.Result) AddInfoRow(kv.Key, kv.Value);
			}, TaskScheduler.FromCurrentSynchronizationContext());
		}

		static string FormatSize(long bytes)
		{
			if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.0") + " MB  (" + bytes.ToString("N0") + " bytes)";
			if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB  (" + bytes.ToString("N0") + " bytes)";
			return bytes.ToString("N0") + " bytes";
		}

		void AddInfoTitle(string text)
		{
			InfoRows.Children.Add(new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
		}

		void AddInfoSection(string text)
		{
			var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) };
			t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
			InfoRows.Children.Add(t);
		}

		void AddInfoNote(string text)
		{
			var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
			t.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
			InfoRows.Children.Add(t);
		}

		void AddInfoRow(string label, string value)
		{
			if (string.IsNullOrEmpty(value)) return;
			var l = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
			l.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
			// Selectable, so values (paths, dates) can be copied.
			var v = new TextBox
			{
				Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
				Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(-2, 0, 0, 0),
			};
			InfoRows.Children.Add(l);
			InfoRows.Children.Add(v);
		}


		// ------------------------------------------------------------------
		//  EXIF (worker thread)
		// ------------------------------------------------------------------

		static List<KeyValuePair<string, string>> ReadCameraInfo(string path)
		{
			var list = new List<KeyValuePair<string, string>>();
			try
			{
				using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
					var md = dec.Frames.Count > 0 ? dec.Frames[0].Metadata as BitmapMetadata : null;
					if (md == null) return list;

					string make = Try(() => md.CameraManufacturer), model = Try(() => md.CameraModel);
					string camera = (model ?? "").StartsWith(make ?? "\u0001", StringComparison.OrdinalIgnoreCase) ? model
						: ((make ?? "") + " " + (model ?? "")).Trim();
					Add(list, "Camera", camera);

					string ifd = md.Format == "jpg" ? "/app1/ifd" : md.Format == "tiff" ? "/ifd" : null;
					if (ifd != null)
					{
						Add(list, "Lens", Query(md, ifd + "/exif/{ushort=42036}") as string);
						Add(list, "Date taken", FormatExifDate(Query(md, ifd + "/exif/{ushort=36867}") as string));
						double v;
						if (Rational(Query(md, ifd + "/exif/{ushort=33434}"), out v) && v > 0)
							Add(list, "Exposure", v >= 1 ? v.ToString("0.#", CultureInfo.CurrentCulture) + " s" : "1/" + Math.Round(1 / v).ToString(CultureInfo.CurrentCulture) + " s");
						if (Rational(Query(md, ifd + "/exif/{ushort=33437}"), out v) && v > 0)
							Add(list, "Aperture", "f/" + v.ToString("0.0#", CultureInfo.CurrentCulture));
						var iso = Query(md, ifd + "/exif/{ushort=34855}");
						if (iso is ushort[] && ((ushort[])iso).Length > 0) iso = ((ushort[])iso)[0];
						if (iso != null) Add(list, "ISO", Convert.ToString(iso, CultureInfo.CurrentCulture));
						if (Rational(Query(md, ifd + "/exif/{ushort=37386}"), out v) && v > 0)
						{
							var eq = Query(md, ifd + "/exif/{ushort=41989}");
							Add(list, "Focal length", v.ToString("0.#", CultureInfo.CurrentCulture) + " mm"
								+ (eq != null && Convert.ToInt32(eq) > 0 ? "  (" + Convert.ToInt32(eq) + " mm equiv.)" : ""));
						}
						var flash = Query(md, ifd + "/exif/{ushort=37385}");
						if (flash != null) Add(list, "Flash", (Convert.ToInt32(flash) & 1) != 0 ? "Fired" : "Off");
						string gps = FormatGps(md, ifd + "/gps");
						Add(list, "Location", gps);
					}
					else
					{
						Add(list, "Date taken", Try(() => md.DateTaken));
					}
					Add(list, "Software", Try(() => md.ApplicationName));
				}
			}
			catch
			{
				// unreadable / no metadata: show the file details only
			}
			return list;
		}

		static void Add(List<KeyValuePair<string, string>> list, string key, string value)
		{
			if (!string.IsNullOrWhiteSpace(value)) list.Add(new KeyValuePair<string, string>(key, value.Trim().TrimEnd('\0')));
		}

		static string Try(Func<string> f) { try { return f(); } catch { return null; } }

		static object Query(BitmapMetadata md, string q)
		{
			try { return md.ContainsQuery(q) ? md.GetQuery(q) : null; } catch { return null; }
		}

		// WIC returns EXIF (S)RATIONALs as 64-bit values: numerator in the low
		// 32 bits, denominator in the high 32 bits.
		static bool Rational(object o, out double value)
		{
			value = 0;
			long num, den;
			if (o is ulong) { var u = (ulong)o; num = (uint)(u & 0xFFFFFFFF); den = (uint)(u >> 32); }
			else if (o is long) { var l = (long)o; num = (int)(l & 0xFFFFFFFF); den = (int)(l >> 32); }
			else return false;
			if (den == 0) return false;
			value = (double)num / den;
			return true;
		}

		static string FormatExifDate(string s)
		{
			DateTime d;
			if (s != null && DateTime.TryParseExact(s.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
				return d.ToString("f");
			return s;
		}

		static string FormatGps(BitmapMetadata md, string gps)
		{
			double lat, lon;
			if (!GpsCoordinate(Query(md, gps + "/{ushort=2}"), out lat) || !GpsCoordinate(Query(md, gps + "/{ushort=4}"), out lon)) return null;
			if (string.Equals(Query(md, gps + "/{ushort=1}") as string, "S", StringComparison.OrdinalIgnoreCase)) lat = -lat;
			if (string.Equals(Query(md, gps + "/{ushort=3}") as string, "W", StringComparison.OrdinalIgnoreCase)) lon = -lon;
			return lat.ToString("0.00000", CultureInfo.InvariantCulture) + ", " + lon.ToString("0.00000", CultureInfo.InvariantCulture);
		}

		static bool GpsCoordinate(object o, out double deg)
		{
			deg = 0;
			var parts = o as ulong[];
			if (parts == null || parts.Length < 3) return false;
			double d, m, s;
			if (!Rational(parts[0], out d) || !Rational(parts[1], out m) || !Rational(parts[2], out s)) return false;
			deg = d + m / 60 + s / 3600;
			return true;
		}
	}
}
