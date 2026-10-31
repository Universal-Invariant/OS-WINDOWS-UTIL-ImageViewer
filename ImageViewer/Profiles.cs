using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Xml.Serialization;
using ImageViewer.Properties;

namespace ImageViewer
{
	// ---- Persisted model (%LOCALAPPDATA%\ImageViewer\profiles.xml) ----------

	public class ProfileStore
	{
		public bool PerMonitor;
		public bool PerDesktop;
		public List<ProfileEntry> Profiles = new List<ProfileEntry>();
	}

	public class ProfileEntry
	{
		[XmlAttribute] public string Key;
		public string MonitorName;
		public string DesktopName;
		public DateTime LastUsed;
		public List<ProfileValue> Values = new List<ProfileValue>();

		/// <summary>A stored setting value (null when absent).</summary>
		public string Get(string name)
		{
			var v = Values.FirstOrDefault(x => x.Name == name);
			return v != null ? v.Value : null;
		}

		/// <summary>Human-readable name of this profile.</summary>
		public string DisplayName
		{
			get
			{
				bool mon = Key.Contains("monitor="), desk = Key.Contains("desktop=");
				if (!mon && !desk) return "All monitors and desktops";
				if (mon && desk) return MonitorName + "  ·  " + DesktopName;
				return mon ? MonitorName : DesktopName;
			}
		}
	}

	public class ProfileValue
	{
		[XmlAttribute] public string Name;
		[XmlText] public string Value;
	}

	/// <summary>
	/// Per-monitor / per-virtual-desktop settings profiles.
	///
	/// Every user setting (folder, recent folders, window geometry and state,
	/// scale, book view, scrolling, colors, ...) lives in a profile. Which
	/// profile is used depends on two options:
	///   neither       -> one profile for everything (the classic behavior)
	///   PerMonitor    -> one profile per physical monitor
	///   PerDesktop    -> one profile per Windows virtual desktop
	///   both          -> one per monitor + desktop combination
	/// At launch the profile of the launch point (monitor of the window the app
	/// was started from, current virtual desktop) is copied into
	/// Settings.Default, which the rest of the app keeps using as before. On
	/// save, Settings.Default is written to the profile of where the window IS
	/// at that moment, so moving the window to another desktop/monitor and
	/// closing it there teaches that place the new state.
	/// A place used for the first time starts as a copy of the most recently
	/// used profile, with the window centered on the launch monitor.
	/// </summary>
	internal static class Profiles
	{
		// IMAGEVIEWER_PROFILE_STORE overrides the location (tests use a scratch file
		// so they never touch the real profiles).
		static readonly string StorePath =
			Environment.GetEnvironmentVariable("IMAGEVIEWER_PROFILE_STORE") ??
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageViewer", "profiles.xml");

		// Serializes read-modify-write of the store across running instances
		// (e.g. one window per virtual desktop).
		static readonly Mutex StoreMutex = new Mutex(false, @"Local\ImageViewer.Profiles");

		static bool _perMonitor, _perDesktop;
		static bool _initialized;

		public static bool PerMonitor { get { return _perMonitor; } }
		public static bool PerDesktop { get { return _perDesktop; } }
		public static bool Enabled { get { return _perMonitor || _perDesktop; } }

		/// <summary>Where the app was launched (monitor + desktop).</summary>
		public static Place LaunchPlace { get; private set; }

		/// <summary>
		/// Which saved profile the settings came from at launch, for the
		/// Settings dialog: its key and a description ("" = not loaded yet).
		/// </summary>
		public static string LoadedKey { get; private set; }
		public static string LoadedDescription { get; private set; }

		/// <summary>Called once from App.OnStartup, before MainWindow exists.</summary>
		public static void Initialize()
		{
			try
			{
				LaunchPlace = Placement.AtLaunch();
				ProfileStore store;
				ProfileEntry entry;
				bool isNew = false;

				using (Lock())
				{
					store = Load();
					if (store == null)
					{
						// First run with profiles: migrate the existing settings.
						store = new ProfileStore();
						store.Profiles.Add(Capture("default", LaunchPlace));
						Write(store);
					}

					_perMonitor = store.PerMonitor;
					_perDesktop = store.PerDesktop;
					string key = KeyFor(LaunchPlace);
					entry = store.Profiles.FirstOrDefault(p => p.Key == key);
					if (entry == null)
					{
						isNew = true;
						entry = store.Profiles.OrderByDescending(p => p.LastUsed).FirstOrDefault();
					}
				}

				if (entry != null) Apply(entry);
				LoadedKey = entry != null ? entry.Key : null;
				LoadedDescription = entry == null ? "defaults"
					: isNew ? "new place - copied from " + entry.DisplayName
					: entry.DisplayName;

				// A monitor's profile must open on that monitor: first use (copied
				// from another place), or monitors rearranged since.
				if (_perMonitor && (isNew || !IsMostlyOn(SavedBounds(), LaunchPlace.MonitorWorkArea)))
					CenterOn(LaunchPlace.MonitorWorkArea);
			}
			catch
			{
				// Profiles are a convenience: never block startup over them.
			}
			_initialized = true;
		}

		/// <summary>
		/// Stores the current Settings.Default values into the profile of where
		/// the window is now. Called by MainWindow.SaveSettings.
		/// </summary>
		public static void Save(Window window)
		{
			if (!_initialized) return;
			try
			{
				var hwnd = window != null ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
				var place = hwnd != IntPtr.Zero ? Placement.OfWindow(hwnd) : LaunchPlace;
				if (place == null) return;

				using (Lock())
				{
					var store = Load() ?? new ProfileStore();
					store.PerMonitor = _perMonitor;
					store.PerDesktop = _perDesktop;
					string key = KeyFor(place);
					store.Profiles.RemoveAll(p => p.Key == key);
					store.Profiles.Add(Capture(key, place));
					Write(store);
				}
			}
			catch
			{
				// Settings.Default.Save() has still written user.config.
			}
		}

		/// <summary>Changes the profile options (Settings dialog); persisted immediately.</summary>
		public static void SetOptions(bool perMonitor, bool perDesktop)
		{
			_perMonitor = perMonitor;
			_perDesktop = perDesktop;
			try
			{
				using (Lock())
				{
					var store = Load() ?? new ProfileStore();
					store.PerMonitor = perMonitor;
					store.PerDesktop = perDesktop;
					Write(store);
				}
			}
			catch { }
		}

		/// <summary>Saved profiles, most recently used first.</summary>
		public static List<ProfileEntry> List()
		{
			try
			{
				using (Lock())
				{
					var store = Load();
					return store == null ? new List<ProfileEntry>()
						: store.Profiles.OrderByDescending(p => p.LastUsed).ToList();
				}
			}
			catch { return new List<ProfileEntry>(); }
		}

		public static void Forget(IEnumerable<string> keys)
		{
			var set = new HashSet<string>(keys);
			if (set.Count == 0) return;
			try
			{
				using (Lock())
				{
					var store = Load();
					if (store == null) return;
					store.Profiles.RemoveAll(p => set.Contains(p.Key));
					Write(store);
				}
			}
			catch { }
		}

		/// <summary>Key of the profile used for a place under the current options.</summary>
		public static string KeyFor(Place place)
		{
			if (!_perMonitor && !_perDesktop) return "default";
			var parts = new List<string>(2);
			if (_perMonitor) parts.Add("monitor=" + place.MonitorId);
			if (_perDesktop) parts.Add("desktop=" + place.DesktopId.ToString("B"));
			return string.Join("|", parts);
		}


		// ------------------------------------------------------------------
		//  Settings.Default <-> profile values
		// ------------------------------------------------------------------

		static ProfileEntry Capture(string key, Place place)
		{
			var e = new ProfileEntry
			{
				Key = key,
				MonitorName = place.MonitorName,
				DesktopName = place.DesktopName,
				LastUsed = DateTime.Now,
			};
			foreach (SettingsProperty p in Settings.Default.Properties)
			{
				try { e.Values.Add(new ProfileValue { Name = p.Name, Value = ToText(Settings.Default[p.Name], p.PropertyType) }); }
				catch { /* skip a value that can't be represented */ }
			}
			return e;
		}

		static void Apply(ProfileEntry entry)
		{
			foreach (var v in entry.Values)
			{
				var p = Settings.Default.Properties[v.Name];
				if (p == null) continue;   // setting no longer exists
				try { Settings.Default[v.Name] = FromText(v.Value, p.PropertyType); }
				catch { /* keep the current value */ }
			}
		}

		static string ToText(object value, Type type)
		{
			if (value == null) return null;
			var sc = value as StringCollection;
			if (sc != null) return string.Join("\n", sc.Cast<string>());
			return TypeDescriptor.GetConverter(type).ConvertToInvariantString(value);
		}

		static object FromText(string text, Type type)
		{
			if (type == typeof(StringCollection))
			{
				var sc = new StringCollection();
				if (!string.IsNullOrEmpty(text))
					foreach (var s in text.Split('\n')) if (s.Length > 0) sc.Add(s);
				return sc;
			}
			if (text == null) return type == typeof(string) ? null : Activator.CreateInstance(type);
			return TypeDescriptor.GetConverter(type).ConvertFromInvariantString(text);
		}


		// ------------------------------------------------------------------
		//  Window placement on the launch monitor
		// ------------------------------------------------------------------

		static Rect SavedBounds()
		{
			var s = Settings.Default;
			if (double.IsNaN(s.WindowLeft) || double.IsNaN(s.WindowTop) || double.IsNaN(s.WindowWidth) || double.IsNaN(s.WindowHeight))
				return Rect.Empty;
			return new Rect(s.WindowLeft, s.WindowTop, Math.Max(0, s.WindowWidth), Math.Max(0, s.WindowHeight));
		}

		static bool IsMostlyOn(Rect r, Rect work)
		{
			if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return false;
			var i = Rect.Intersect(r, work);
			return !i.IsEmpty && i.Width * i.Height >= 0.5 * r.Width * r.Height;
		}

		static void CenterOn(Rect work)
		{
			var s = Settings.Default;
			double w = double.IsNaN(s.WindowWidth) || s.WindowWidth < 300 ? 1000 : s.WindowWidth;
			double h = double.IsNaN(s.WindowHeight) || s.WindowHeight < 200 ? 700 : s.WindowHeight;
			w = Math.Min(w, work.Width * 0.9);
			h = Math.Min(h, work.Height * 0.9);
			s.WindowWidth = w;
			s.WindowHeight = h;
			s.WindowLeft = work.Left + (work.Width - w) / 2;
			s.WindowTop = work.Top + (work.Height - h) / 2;
		}


		// ------------------------------------------------------------------
		//  Store file
		// ------------------------------------------------------------------

		static IDisposable Lock()
		{
			bool taken;
			try { taken = StoreMutex.WaitOne(3000); }
			catch (AbandonedMutexException) { taken = true; }   // previous owner crashed: fine
			return new Releaser(taken);
		}

		sealed class Releaser : IDisposable
		{
			bool _held;
			public Releaser(bool held) { _held = held; }
			public void Dispose() { if (_held) { _held = false; StoreMutex.ReleaseMutex(); } }
		}

		static ProfileStore Load()
		{
			if (!File.Exists(StorePath)) return null;
			try
			{
				using (var f = File.OpenRead(StorePath))
					return (ProfileStore)new XmlSerializer(typeof(ProfileStore)).Deserialize(f);
			}
			catch
			{
				return null;   // unreadable: start over (the old file is replaced on next save)
			}
		}

		static void Write(ProfileStore store)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
			string tmp = StorePath + ".tmp";
			using (var f = File.Create(tmp))
				new XmlSerializer(typeof(ProfileStore)).Serialize(f, store);
			if (File.Exists(StorePath)) File.Replace(tmp, StorePath, null);
			else File.Move(tmp, StorePath);
		}
	}
}
