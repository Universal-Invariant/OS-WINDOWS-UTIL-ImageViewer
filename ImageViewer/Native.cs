using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ImageViewer
{
	/// <summary>Small Win32 / shell helpers.</summary>
	internal static class Native
	{
		// --- Natural ("Explorer") file name ordering: img2 < img10 ---------------
		[DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
		static extern int StrCmpLogicalW(string x, string y);

		public static int CompareNatural(string x, string y) { return StrCmpLogicalW(x, y); }


		// --- Dark title bar for standard windows (Windows 10 20H1+ / 11) ---------
		[DllImport("dwmapi.dll")]
		static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

		public static void UseDarkTitleBar(Window w)
		{
			try
			{
				var hwnd = new WindowInteropHelper(w).EnsureHandle();
				int on = 1;
				// DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on current builds, 19 on early Win10 builds.
				if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
					DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
			}
			catch
			{
				// cosmetic only
			}
		}


		// --- Monitor rectangle (device pixels) for full screen -----------------
		[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
		[StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
		[DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
		[DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO mi);

		/// <summary>Full bounds (taskbar included) of the monitor the window is on, in device pixels.</summary>
		public static bool TryGetMonitorBounds(IntPtr hwnd, out Rect bounds)
		{
			bounds = Rect.Empty;
			var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
			var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
			if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return false;
			bounds = new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
			return true;
		}


		// --- Modern folder picker (IFileOpenDialog + FOS_PICKFOLDERS) -------------
		// .NET Framework's WinForms FolderBrowserDialog is the old tree-view dialog;
		// this is the same Explorer-style picker newer apps use. Falls back to the
		// WinForms dialog if the COM dialog is unavailable.

		/// <summary>The chosen folder, or null when cancelled.</summary>
		public static string PickFolder(Window owner, string initialFolder, string title)
		{
			IFileDialog dlg = null;
			try
			{
				dlg = (IFileDialog)new FileOpenDialogCoClass();
				uint options;
				dlg.GetOptions(out options);
				dlg.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
				if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);

				if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
				{
					IShellItem folder;
					if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, typeof(IShellItem).GUID, out folder) == 0)
						dlg.SetFolder(folder);
				}

				var hwnd = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
				if (dlg.Show(hwnd) != 0) return null;   // cancelled (or failed)

				IShellItem result;
				dlg.GetResult(out result);
				string path;
				result.GetDisplayName(SIGDN_FILESYSPATH, out path);
				return path;
			}
			catch (COMException)
			{
				return PickFolderLegacy(initialFolder, title);
			}
			catch (InvalidCastException)
			{
				return PickFolderLegacy(initialFolder, title);
			}
			finally
			{
				if (dlg != null) Marshal.ReleaseComObject(dlg);
			}
		}

		static string PickFolderLegacy(string initialFolder, string title)
		{
			using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = title, ShowNewFolderButton = false })
			{
				if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)) dlg.SelectedPath = initialFolder;
				return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
			}
		}

		const uint FOS_PICKFOLDERS = 0x20;
		const uint FOS_FORCEFILESYSTEM = 0x40;
		const uint FOS_PATHMUSTEXIST = 0x800;
		const uint SIGDN_FILESYSPATH = 0x80058000;

		[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
		static extern int SHCreateItemFromParsingName(
			[MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
			[MarshalAs(UnmanagedType.LPStruct)] Guid riid,
			[MarshalAs(UnmanagedType.Interface, IidParameterIndex = 2)] out IShellItem ppv);

		[ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
		class FileOpenDialogCoClass { }

		// Only the vtable prefix up to GetResult is declared; order matters.
		[ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		interface IFileDialog
		{
			[PreserveSig] int Show(IntPtr parent);
			void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
			void SetFileTypeIndex(uint iFileType);
			void GetFileTypeIndex(out uint piFileType);
			void Advise(IntPtr pfde, out uint pdwCookie);
			void Unadvise(uint dwCookie);
			void SetOptions(uint fos);
			void GetOptions(out uint pfos);
			void SetDefaultFolder(IShellItem psi);
			void SetFolder(IShellItem psi);
			void GetFolder(out IShellItem ppsi);
			void GetCurrentSelection(out IShellItem ppsi);
			void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
			void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
			void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
			void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
			void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
			void GetResult(out IShellItem ppsi);
		}

		[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		interface IShellItem
		{
			void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
			void GetParent(out IShellItem ppsi);
			void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
			void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
			void Compare(IShellItem psi, uint hint, out int piOrder);
		}
	}
}
