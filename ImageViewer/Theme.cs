using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ImageViewer
{
	/// <summary>
	/// Dark / Light / System (follows the Windows app mode) color themes. The
	/// XAML uses DynamicResource for every themed brush, so replacing the
	/// brushes in Application.Resources restyles open windows live.
	/// </summary>
	internal static class Theme
	{
		static readonly Dictionary<string, string> Dark = new Dictionary<string, string>
		{
			{ "Bg.Window", "#FF1A1A1A" }, { "Bg.Chrome", "#FF202020" }, { "Bg.Control", "#FF2B2B2B" },
			{ "Bg.ControlHover", "#FF363636" }, { "Bg.ControlPressed", "#FF414141" }, { "Bg.Popup", "#FF262626" },
			{ "Border.Control", "#FF3A3A3A" }, { "Border.Subtle", "#FF2E2E2E" },
			{ "Fg.Primary", "#FFE8E8E8" }, { "Fg.Secondary", "#FF9E9E9E" }, { "Fg.Disabled", "#FF5E5E5E" },
			{ "Accent", "#FF4C9EEB" }, { "Accent.Subtle", "#334C9EEB" }, { "Danger", "#FFE5484D" },
		};

		static readonly Dictionary<string, string> Light = new Dictionary<string, string>
		{
			{ "Bg.Window", "#FFF3F3F3" }, { "Bg.Chrome", "#FFEBEBEB" }, { "Bg.Control", "#FFFFFFFF" },
			{ "Bg.ControlHover", "#FFE0E0E0" }, { "Bg.ControlPressed", "#FFD4D4D4" }, { "Bg.Popup", "#FFFAFAFA" },
			{ "Border.Control", "#FFCFCFCF" }, { "Border.Subtle", "#FFDDDDDD" },
			{ "Fg.Primary", "#FF1B1B1B" }, { "Fg.Secondary", "#FF5F5F5F" }, { "Fg.Disabled", "#FFA8A8A8" },
			{ "Accent", "#FF0F6CBD" }, { "Accent.Subtle", "#330F6CBD" }, { "Danger", "#FFC42B1C" },
		};

		public static bool IsDark { get; private set; } = true;

		/// <param name="name">"Dark", "Light" or "System".</param>
		public static void Apply(string name)
		{
			bool dark = name == "Light" ? false : name == "System" ? !WindowsUsesLightApps() : true;
			IsDark = dark;
			var res = Application.Current.Resources;
			foreach (var kv in dark ? Dark : Light)
			{
				var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
				b.Freeze();
				res[kv.Key] = b;
			}
			LazyImage.SetAccent((Color)ColorConverter.ConvertFromString((dark ? Dark : Light)["Accent"]));
		}

		static bool WindowsUsesLightApps()
		{
			try
			{
				using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
				{
					var v = k != null ? k.GetValue("AppsUseLightTheme") : null;
					return v is int && (int)v != 0;
				}
			}
			catch { return false; }
		}
	}
}
