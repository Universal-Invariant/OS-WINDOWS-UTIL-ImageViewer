using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ImageViewer
{
	/// <summary>
	/// The path box can hold one folder or several separated by ';'
	/// ("D:\Comics\A; E:\Scans"): they are shown together as one view, each
	/// folder in its own group. Helpers to split, validate and join such text.
	/// </summary>
	internal static class FolderSet
	{
		public const string Separator = "; ";

		/// <summary>The folders of a path text, trimmed, unquoted, without duplicates (in the given order).</summary>
		public static List<string> Split(string text)
		{
			var list = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrWhiteSpace(text)) return list;
			foreach (var raw in text.Split(';'))
			{
				string p = raw.Trim().Trim('"').Trim();
				if (p.Length == 0) continue;
				p = Normalize(p);
				if (seen.Add(p)) list.Add(p);
			}
			return list;
		}

		/// <summary>"C:\" stays as is, other folders lose a trailing '\'.</summary>
		public static string Normalize(string folder)
		{
			if (folder.Length > 3) folder = folder.TrimEnd('\\');
			if (folder.Length == 2 && folder[1] == ':') folder += "\\";
			return folder;
		}

		public static string Join(IEnumerable<string> folders)
		{
			return string.Join(Separator, folders);
		}

		/// <summary>True when the text names at least one folder and every one of them exists.</summary>
		public static bool IsValid(string text)
		{
			var list = Split(text);
			return list.Count > 0 && list.All(Exists);
		}

		public static bool IsMultiple(string text)
		{
			return Split(text).Count > 1;
		}

		static bool Exists(string folder)
		{
			try { return Directory.Exists(folder); }
			catch { return false; }
		}

		/// <summary>
		/// Folders for dropped / command-line paths: a folder as is, an image
		/// file's folder; duplicates and missing paths dropped, order kept.
		/// </summary>
		public static List<string> FoldersOf(IEnumerable<string> paths, out string firstFile)
		{
			firstFile = null;
			var list = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var raw in paths)
			{
				if (string.IsNullOrWhiteSpace(raw)) continue;
				string p;
				try { p = Path.GetFullPath(raw.Trim().Trim('"')); } catch { continue; }
				string folder = null;
				if (Exists(p)) folder = p;
				else if (File.Exists(p))
				{
					folder = Path.GetDirectoryName(p);
					if (firstFile == null && ImageFiles.IsImage(p)) firstFile = p;
				}
				if (folder == null) continue;
				folder = Normalize(folder);
				if (seen.Add(folder)) list.Add(folder);
			}
			return list;
		}

		/// <summary>Display name of a folder: its own name, or the drive / share for a root.</summary>
		public static string NameOf(string folder)
		{
			string name = Path.GetFileName(folder.TrimEnd('\\'));
			return string.IsNullOrEmpty(name) ? folder.TrimEnd('\\') : name;
		}
	}
}
