using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace ImageViewer
{
	/// <summary>Where a window is (or is being launched): monitor + virtual desktop.</summary>
	internal sealed class Place
	{
		public string MonitorId = "";     // stable: monitor device path (model + connection)
		public string MonitorName = "";   // e.g. "DELL U2720Q (2560 × 1440)"
		public Rect MonitorWorkArea;      // in WPF device-independent units
		public Guid DesktopId;            // Guid.Empty when unknown / virtual desktops unavailable
		public string DesktopName = "";   // the user's name for it, or "Desktop 2"

		public override string ToString()
		{
			if (string.IsNullOrEmpty(DesktopName)) return MonitorName;
			return MonitorName + "  ·  " + DesktopName;
		}
	}

	/// <summary>
	/// Detects the monitor and Windows virtual desktop of the app's launch point
	/// or of a window.
	/// </summary>
	internal static class Placement
	{
		/// <summary>
		/// Where the app is being started, monitor first:
		///  1. the monitor the shell asked for - taskbar, Start menu and
		///     Explorer launches pass the monitor they were clicked on;
		///  2. the monitor of the foreground window it was launched from
		///     (terminal, Explorer, an IDE) - but not the desktop window behind
		///     the icons, which spans every monitor and would always resolve
		///     to the primary one;
		///  3. the mouse position.
		/// Then the current virtual desktop.
		/// </summary>
		public static Place AtLaunch()
		{
			IntPtr monitor = ShellLaunchMonitor();
			var fg = GetForegroundWindow();
			bool fgUsable = fg != IntPtr.Zero && !IsShellDesktop(fg);
			if (monitor == IntPtr.Zero && fgUsable) monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONULL);
			if (monitor == IntPtr.Zero)
			{
				POINT pt;
				GetCursorPos(out pt);
				monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY);
			}

			var place = new Place();
			FillMonitor(place, monitor);
			FillDesktop(place, CurrentDesktopFromRegistry(), IntPtr.Zero);
			if (place.DesktopId == Guid.Empty && fgUsable) FillDesktop(place, WindowDesktop(fg), IntPtr.Zero);
			return place;
		}

		// ShellExecuteEx(SEE_MASK_HMONITOR) - used by the taskbar, Start and
		// Explorer - hands the new process the monitor in STARTUPINFO.hStdOutput,
		// flagged by STARTF_HASSHELLDATA (that is how windows created at the
		// default position open on the clicked taskbar's monitor). Validated
		// before use: anything that isn't a live monitor handle is ignored.
		static IntPtr ShellLaunchMonitor()
		{
			try
			{
				STARTUPINFO si;
				GetStartupInfo(out si);
				if ((si.dwFlags & STARTF_USESTDHANDLES) != 0 || (si.dwFlags & STARTF_HASSHELLDATA) == 0) return IntPtr.Zero;
				var mi = new MONITORINFOEX();
				mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
				return si.hStdOutput != IntPtr.Zero && GetMonitorInfo(si.hStdOutput, ref mi) ? si.hStdOutput : IntPtr.Zero;
			}
			catch { return IntPtr.Zero; }
		}

		// The desktop (icons) window: double-clicking a desktop shortcut leaves
		// it in the foreground.
		static bool IsShellDesktop(IntPtr hwnd)
		{
			var sb = new System.Text.StringBuilder(64);
			if (GetClassName(hwnd, sb, sb.Capacity) == 0) return false;
			string cls = sb.ToString();
			return cls == "Progman" || cls == "WorkerW";
		}

		/// <summary>Where the given top-level window currently is.</summary>
		public static Place OfWindow(IntPtr hwnd)
		{
			var place = new Place();
			FillMonitor(place, MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
			var desk = WindowDesktop(hwnd);
			if (desk == Guid.Empty) desk = CurrentDesktopFromRegistry();
			FillDesktop(place, desk, hwnd);
			return place;
		}


		/// <summary>Cheap: the monitor handle a window is on (to detect monitor changes).</summary>
		public static IntPtr MonitorHandleOf(IntPtr hwnd) { return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST); }

		// ------------------------------------------------------------------
		//  Monitors
		// ------------------------------------------------------------------

		static void FillMonitor(Place place, IntPtr monitor)
		{
			var mi = new MONITORINFOEX();
			mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
			if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi))
			{
				place.MonitorId = "unknown";
				place.MonitorName = "Unknown monitor";
				place.MonitorWorkArea = SystemParameters.WorkArea;
				return;
			}

			double scale = SystemDpiScale();
			place.MonitorWorkArea = new Rect(mi.rcWork.left / scale, mi.rcWork.top / scale,
				(mi.rcWork.right - mi.rcWork.left) / scale, (mi.rcWork.bottom - mi.rcWork.top) / scale);

			string gdiName = mi.szDevice;   // "\\.\DISPLAY2" - not stable across reboots/docking
			string friendly = null, devicePath = null;
			int pxW = mi.rcMonitor.right - mi.rcMonitor.left, pxH = mi.rcMonitor.bottom - mi.rcMonitor.top;
			bool portrait = pxH > pxW;
			try { LookupTarget(gdiName, out friendly, out devicePath, ref pxW, ref pxH); } catch { }
			if ((pxH > pxW) != portrait) { int t = pxW; pxW = pxH; pxH = t; }   // rotated display: mode is unrotated

			if (string.IsNullOrEmpty(devicePath))
			{
				// Fallback: the monitor's device interface path via EnumDisplayDevices.
				var dd = new DISPLAY_DEVICE();
				dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
				if (EnumDisplayDevices(gdiName, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) devicePath = dd.DeviceID;
			}

			string number = gdiName.Replace(@"\\.\DISPLAY", "");
			place.MonitorId = !string.IsNullOrEmpty(devicePath) ? devicePath : gdiName;
			place.MonitorName = (string.IsNullOrEmpty(friendly) ? "Display " + number : friendly)
				+ " (" + pxW + " × " + pxH + ")"
				+ ((mi.dwFlags & MONITORINFOF_PRIMARY) != 0 ? ", primary" : "");
		}

		static double SystemDpiScale()
		{
			IntPtr dc = GetDC(IntPtr.Zero);
			try { int dpi = GetDeviceCaps(dc, LOGPIXELSX); return dpi > 0 ? dpi / 96.0 : 1.0; }
			finally { ReleaseDC(IntPtr.Zero, dc); }
		}

		// GDI device name -> monitor friendly name, device path and real mode size
		// (QueryDisplayConfig). The rcMonitor size seen by a system-DPI-aware
		// process is scaled when the monitor's DPI differs from the system DPI.
		static void LookupTarget(string gdiName, out string friendly, out string devicePath, ref int pxW, ref int pxH)
		{
			friendly = devicePath = null;
			uint numPaths, numModes;
			if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes) != 0) return;
			IntPtr paths = Marshal.AllocHGlobal((int)numPaths * PATH_INFO_SIZE);
			IntPtr modes = Marshal.AllocHGlobal((int)numModes * MODE_INFO_SIZE);
			try
			{
				if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0) return;
				for (int i = 0; i < numPaths; i++)
				{
					IntPtr p = paths + i * PATH_INFO_SIZE;
					var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
					src.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
					src.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
					src.header.adapterId = new LUID { LowPart = (uint)Marshal.ReadInt32(p, 0), HighPart = Marshal.ReadInt32(p, 4) };
					src.header.id = (uint)Marshal.ReadInt32(p, 8);
					if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
					if (!string.Equals(src.viewGdiDeviceName, gdiName, StringComparison.OrdinalIgnoreCase)) continue;

					var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
					tgt.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
					tgt.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
					tgt.header.adapterId = new LUID { LowPart = (uint)Marshal.ReadInt32(p, 20), HighPart = Marshal.ReadInt32(p, 24) };
					tgt.header.id = (uint)Marshal.ReadInt32(p, 28);
					if (DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;
					friendly = tgt.monitorFriendlyDeviceName;
					devicePath = tgt.monitorDevicePath;

					// Source mode (desktop resolution): MODE_INFO.infoType @0 == 1,
					// DISPLAYCONFIG_SOURCE_MODE.width @16, height @20.
					int modeIdx = Marshal.ReadInt32(p, 12);
					if (modeIdx >= 0 && modeIdx < numModes)
					{
						IntPtr m = modes + modeIdx * MODE_INFO_SIZE;
						if (Marshal.ReadInt32(m, 0) == 1)
						{
							int w = Marshal.ReadInt32(m, 16), h = Marshal.ReadInt32(m, 20);
							if (w > 0 && h > 0) { pxW = w; pxH = h; }
						}
					}
					return;
				}
			}
			finally
			{
				Marshal.FreeHGlobal(paths);
				Marshal.FreeHGlobal(modes);
			}
		}


		// ------------------------------------------------------------------
		//  Virtual desktops
		// ------------------------------------------------------------------

		const string VdKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

		static Guid WindowDesktop(IntPtr hwnd)
		{
			try
			{
				var mgr = (IVirtualDesktopManager)new VirtualDesktopManagerCoClass();
				try
				{
					Guid id;
					return mgr.GetWindowDesktopId(hwnd, out id) == 0 ? id : Guid.Empty;
				}
				finally { Marshal.ReleaseComObject(mgr); }
			}
			catch { return Guid.Empty; }
		}

		// Undocumented but long-stable registry values (Windows 10 2004+ / 11).
		// Used before our own window exists; the documented API needs a window.
		static Guid CurrentDesktopFromRegistry()
		{
			try
			{
				using (var k = Registry.CurrentUser.OpenSubKey(VdKey))
				{
					var b = k != null ? k.GetValue("CurrentVirtualDesktop") as byte[] : null;
					if (b != null && b.Length == 16) return new Guid(b);
				}
				// Older Windows 10 builds keep it per session.
				using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\"
					+ System.Diagnostics.Process.GetCurrentProcess().SessionId + @"\VirtualDesktops"))
				{
					var b = k != null ? k.GetValue("CurrentVirtualDesktop") as byte[] : null;
					if (b != null && b.Length == 16) return new Guid(b);
				}
			}
			catch { }
			return Guid.Empty;
		}

		static void FillDesktop(Place place, Guid id, IntPtr hwnd)
		{
			place.DesktopId = id;
			if (id == Guid.Empty) { place.DesktopName = ""; return; }

			string name = null;
			int index = -1;
			try
			{
				using (var k = Registry.CurrentUser.OpenSubKey(VdKey + @"\Desktops\" + id.ToString("B")))
					name = k != null ? k.GetValue("Name") as string : null;
				using (var k = Registry.CurrentUser.OpenSubKey(VdKey))
				{
					var ids = k != null ? k.GetValue("VirtualDesktopIDs") as byte[] : null;
					if (ids != null)
						for (int i = 0; i + 16 <= ids.Length; i += 16)
						{
							var g = new byte[16];
							Array.Copy(ids, i, g, 0, 16);
							if (new Guid(g) == id) { index = i / 16; break; }
						}
				}
			}
			catch { }

			place.DesktopName = !string.IsNullOrEmpty(name) ? name
				: index >= 0 ? "Desktop " + (index + 1)
				: "Desktop " + id.ToString("D").Substring(0, 8);
		}


		// ------------------------------------------------------------------
		//  Interop
		// ------------------------------------------------------------------

		[ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		interface IVirtualDesktopManager
		{
			[PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
			[PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
			[PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
		}

		[ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
		class VirtualDesktopManagerCoClass { }

		[StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

		// String members as IntPtr: the memory belongs to the process, the
		// marshaler must not free it.
		[StructLayout(LayoutKind.Sequential)]
		struct STARTUPINFO
		{
			public int cb;
			public IntPtr lpReserved, lpDesktop, lpTitle;
			public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
			public short wShowWindow, cbReserved2;
			public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
		}
		const int STARTF_USESTDHANDLES = 0x100, STARTF_HASSHELLDATA = 0x400;
		[StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct MONITORINFOEX
		{
			public int cbSize;
			public RECT rcMonitor;
			public RECT rcWork;
			public int dwFlags;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct DISPLAY_DEVICE
		{
			public int cb;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
			public int StateFlags;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
		}

		[StructLayout(LayoutKind.Sequential)] struct LUID { public uint LowPart; public int HighPart; }

		[StructLayout(LayoutKind.Sequential)]
		struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
		{
			public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct DISPLAYCONFIG_TARGET_DEVICE_NAME
		{
			public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
			public uint flags;
			public uint outputTechnology;
			public ushort edidManufactureId;
			public ushort edidProductCodeId;
			public uint connectorInstance;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
		}

		const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
		const int MONITORINFOF_PRIMARY = 1;
		const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
		const int LOGPIXELSX = 88;
		const uint QDC_ONLY_ACTIVE_PATHS = 2;
		const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
		// sizeof(DISPLAYCONFIG_PATH_INFO) = 20 (source) + 48 (target) + 4 (flags);
		// sizeof(DISPLAYCONFIG_MODE_INFO) = 64. Paths are read by offset:
		// source adapterId @0, source id @8; target adapterId @20, target id @28.
		const int PATH_INFO_SIZE = 72, MODE_INFO_SIZE = 64;

		[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
		[DllImport("kernel32.dll", EntryPoint = "GetStartupInfoW")] static extern void GetStartupInfo(out STARTUPINFO si);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
		[DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
		[DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
		[DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX mi);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
		[DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
		[DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
		[DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);
		[DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
		[DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, IntPtr topology);
		[DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);
		[DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
	}
}
