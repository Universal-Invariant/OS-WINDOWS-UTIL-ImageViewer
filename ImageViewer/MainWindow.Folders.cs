using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace ImageViewer
{
	// Several folders as one view: "A; B" in the path box, dropping several
	// folders (Ctrl+drop adds), Ctrl+browse adds, Explorer multi-selection.
	public partial class MainWindow
	{
		/// <summary>The folders of the current view (one, or several).</summary>
		List<string> _roots = new List<string>();

		bool MultiRoot { get { return _roots.Count > 1; } }

		/// <summary>Index of the root folder holding a path (the longest match, for nested roots).</summary>
		int RootIndexOf(string path)
		{
			int best = 0, bestLen = -1;
			for (int i = 0; i < _roots.Count; i++)
			{
				string r = _roots[i];
				if (r.Length <= bestLen || !path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
				if (path.Length == r.Length || r.EndsWith("\\") || path[r.Length] == '\\') { best = i; bestLen = r.Length; }
			}
			return best;
		}

		string RootOf(LazyImage img)
		{
			return img.RootIndex >= 0 && img.RootIndex < _roots.Count ? _roots[img.RootIndex] : _rootFolder;
		}

		/// <summary>Adds folders to the ones shown (or opens them when nothing is open).</summary>
		void AddFolders(IEnumerable<string> folders)
		{
			var current = FolderSet.Split(oldPath);
			var added = folders.Where(Directory.Exists).Select(FolderSet.Normalize)
				.Where(f => !current.Contains(f, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (added.Count == 0) { ShowToast("Already shown"); return; }
			OpenFolder(FolderSet.Join(current.Concat(added)));
			ShowToast(added.Count == 1 ? "Added " + FolderSet.NameOf(added[0]) : "Added " + added.Count + " folders");
		}

		/// <summary>Window title for the folders shown: "Comics", "Comics + Scans", "Comics + Scans + 2 more".</summary>
		static string TitleFor(List<string> folders)
		{
			if (folders.Count == 0) return "Image Viewer";
			string names = string.Join(" + ", folders.Take(2).Select(FolderSet.NameOf));
			if (folders.Count > 2) names += " + " + (folders.Count - 2) + " more";
			return names + " - Image Viewer";
		}


		// ------------------------------------------------------------------
		//  Explorer multi-selection: paths forwarded by other launches
		// ------------------------------------------------------------------

		DispatcherTimer _gatherTimer;
		readonly List<string> _gathered = new List<string>();
		DateTime _lastGathered;

		/// <summary>This window collects the other items of an Explorer multi-selection (see LaunchGather).</summary>
		void StartGatherMerge()
		{
			_gatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
			_gatherTimer.Tick += (s, e) =>
			{
				var received = LaunchGather.TakeReceived();
				if (received.Count > 0) { _gathered.AddRange(received); _lastGathered = DateTime.UtcNow; }
				bool quiet = (DateTime.UtcNow - _lastGathered).TotalMilliseconds > 400;
				if (_gathered.Count > 0 && (quiet || !LaunchGather.Collecting))
				{
					string file;
					var folders = FolderSet.FoldersOf(_gathered, out file);
					_gathered.Clear();
					if (folders.Count > 0)
					{
						var all = FolderSet.Split(oldPath).Concat(folders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
						if (all.Count != FolderSet.Split(oldPath).Count)
						{
							// Explorer starts the launches in no particular order: sort them.
							all.Sort(Native.CompareNatural);
							// The reload closes the preview: reopen the image shown, else the first one selected.
							if (App.StartupFile == null)
								App.StartupFile = IsPreviewOpen && _previewItem != null ? _previewItem.Path : file;
							OpenFolder(FolderSet.Join(all));
						}
					}
				}
				if (!LaunchGather.Collecting && _gathered.Count == 0 && LaunchGather.Pending == 0) _gatherTimer.Stop();
			};
			_gatherTimer.Start();
		}
	}
}
