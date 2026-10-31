using System;
using System.Reflection;
using Microsoft.Win32;

namespace ImageViewer
{
	/// <summary>
	/// Optional "Open in Image Viewer" entries in Explorer's right-click menu
	/// for folders, folder backgrounds and image files. Per user (HKCU), no
	/// admin rights needed. On Windows 11 they appear under "Show more options".
	/// </summary>
	internal static class ShellIntegration
	{
		const string MenuText = "Open in Image Viewer";
		static readonly string[] Keys =
		{
			@"Software\Classes\Directory\shell\ImageViewer",
			@"Software\Classes\Directory\Background\shell\ImageViewer",
			@"Software\Classes\SystemFileAssociations\image\shell\ImageViewer",
		};
		// --gather: several items selected in Explorer open in one window (LaunchGather).
		static readonly string[] Args = { "%1", "%V", "%1" };
		const string GatherSwitch = "--gather";

		public static string ExePath { get { return Assembly.GetEntryAssembly().Location; } }

		/// <summary>The exe the menu currently launches, or null when not registered.</summary>
		public static string RegisteredExe()
		{
			try
			{
				using (var k = Registry.CurrentUser.OpenSubKey(Keys[0] + @"\command"))
				{
					var cmd = k != null ? k.GetValue("") as string : null;
					if (string.IsNullOrEmpty(cmd)) return null;
					int end = cmd.IndexOf('"', 1);
					return cmd.StartsWith("\"") && end > 1 ? cmd.Substring(1, end - 1) : cmd;
				}
			}
			catch { return null; }
		}

		public static bool IsRegistered { get { return RegisteredExe() != null; } }

		public static void Register()
		{
			string exe = ExePath;
			for (int i = 0; i < Keys.Length; i++)
				using (var k = Registry.CurrentUser.CreateSubKey(Keys[i]))
				{
					k.SetValue("", MenuText);
					k.SetValue("Icon", "\"" + exe + "\",0");
					using (var c = k.CreateSubKey("command"))
						c.SetValue("", "\"" + exe + "\" " + GatherSwitch + " \"" + Args[i] + "\"");
				}
		}

		/// <summary>
		/// Menu entries written by an older version of this program (no
		/// --gather) are rewritten, so a multi-selection opens in one window.
		/// Only when they point to this very exe.
		/// </summary>
		public static void UpdateIfOutdated()
		{
			string exe = RegisteredExe();
			if (exe == null || !string.Equals(exe, ExePath, StringComparison.OrdinalIgnoreCase)) return;
			using (var k = Registry.CurrentUser.OpenSubKey(Keys[0] + @"\command"))
			{
				var cmd = k != null ? k.GetValue("") as string : null;
				if (cmd != null && cmd.Contains(GatherSwitch)) return;
			}
			Register();
		}

		public static void Unregister()
		{
			foreach (var key in Keys)
				Registry.CurrentUser.DeleteSubKeyTree(key, false);
		}
	}
}
