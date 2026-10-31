using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace ImageViewer
{
	/// <summary>
	/// Small per-user stores next to profiles.xml (%LOCALAPPDATA%\ImageViewer):
	/// manual image rotations and remembered reading positions per folder.
	/// </summary>
	internal static class AppData
	{
		public static string Folder
		{
			get
			{
				var overridePath = Environment.GetEnvironmentVariable("IMAGEVIEWER_PROFILE_STORE");
				return overridePath != null
					? Path.GetDirectoryName(Path.GetFullPath(overridePath))
					: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageViewer");
			}
		}

		/// <summary>Image path -> clockwise rotation in degrees (90, 180, 270). Never modifies the image file.</summary>
		public static readonly KeyValueStore Rotations = new KeyValueStore("rotations.xml", 20000);

		/// <summary>Folder path -> "first visible image path|fraction of its row scrolled past".</summary>
		public static readonly KeyValueStore Positions = new KeyValueStore("positions.xml", 1000);

		public static string Normalize(string path)
		{
			if (string.IsNullOrEmpty(path)) return "";
			var p = path.Length > 3 ? path.TrimEnd('\\') : path;
			return p.ToLowerInvariant();
		}
	}

	public class KeyValueEntry
	{
		[XmlAttribute] public string Key;
		[XmlAttribute] public DateTime Touched;
		[XmlText] public string Value;
	}

	/// <summary>
	/// A string map persisted as XML. Reads come from memory (loaded on first
	/// use); every write re-reads the file under a named mutex, applies the
	/// change and replaces the file atomically, so concurrent instances merge.
	/// Keeps at most maxEntries (least recently touched dropped).
	/// </summary>
	internal sealed class KeyValueStore
	{
		readonly string _fileName;
		readonly int _maxEntries;
		readonly Mutex _mutex;
		Dictionary<string, KeyValueEntry> _map;

		public KeyValueStore(string fileName, int maxEntries)
		{
			_fileName = fileName;
			_maxEntries = maxEntries;
			_mutex = new Mutex(false, @"Local\ImageViewer." + fileName);
		}

		string FilePath { get { return Path.Combine(AppData.Folder, _fileName); } }

		public string Get(string key)
		{
			if (_map == null) _map = Load();
			KeyValueEntry e;
			return _map.TryGetValue(AppData.Normalize(key), out e) ? e.Value : null;
		}

		/// <summary>Sets (or with value == null removes) an entry and persists.</summary>
		public void Set(string key, string value)
		{
			SetMany(new Dictionary<string, string> { { key, value } });
		}

		/// <summary>Several changes, one file write.</summary>
		public void SetMany(IDictionary<string, string> changes)
		{
			if (App.IsBenchmark || changes.Count == 0) return;
			bool taken = false;
			try
			{
				try { taken = _mutex.WaitOne(3000); } catch (AbandonedMutexException) { taken = true; }
				var map = Load();
				foreach (var c in changes)
				{
					var key = AppData.Normalize(c.Key);
					if (c.Value == null) map.Remove(key);
					else map[key] = new KeyValueEntry { Key = key, Value = c.Value, Touched = DateTime.Now };
				}
				if (map.Count > _maxEntries)
					foreach (var old in map.Values.OrderBy(v => v.Touched).Take(map.Count - _maxEntries).ToList())
						map.Remove(old.Key);
				Write(map);
				_map = map;
			}
			catch
			{
				// a convenience store: never fail the caller
			}
			finally
			{
				if (taken) _mutex.ReleaseMutex();
			}
		}

		/// <summary>Moves an entry to a new key (file renamed/moved).</summary>
		public void Rename(string oldKey, string newKey)
		{
			var v = Get(oldKey);
			if (v == null) return;
			Set(oldKey, null);
			Set(newKey, v);
		}

		Dictionary<string, KeyValueEntry> Load()
		{
			var map = new Dictionary<string, KeyValueEntry>();
			try
			{
				if (File.Exists(FilePath))
					using (var f = File.OpenRead(FilePath))
						foreach (var e in (List<KeyValueEntry>)new XmlSerializer(typeof(List<KeyValueEntry>)).Deserialize(f))
							if (e.Key != null) map[e.Key] = e;
			}
			catch { /* unreadable: start empty, replaced on next write */ }
			return map;
		}

		void Write(Dictionary<string, KeyValueEntry> map)
		{
			Directory.CreateDirectory(AppData.Folder);
			string tmp = FilePath + ".tmp";
			using (var f = File.Create(tmp))
				new XmlSerializer(typeof(List<KeyValueEntry>)).Serialize(f, map.Values.ToList());
			if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
			else File.Move(tmp, FilePath);
		}
	}
}
