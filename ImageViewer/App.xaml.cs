using System;
using System.Configuration;
using System.IO;
using System.Windows;
using ImageViewer.Properties;

namespace ImageViewer
{
	/// <summary>
	/// Interaction logic for App.xaml
	/// </summary>
	public partial class App : Application
	{
		// --benchmark <folder> [--out <file>]: see MainWindow.Benchmark.cs
		public static string BenchmarkFolder;
		public static string BenchmarkOut;
		public static bool IsBenchmark { get { return BenchmarkFolder != null; } }

		// ImageViewer.exe <folder>  or  ImageViewer.exe <image>  (also used by the
		// optional Explorer "Open in Image Viewer" menu): open that folder, and
		// for an image, preview it once the folder has loaded.
		static bool _errorShown;
		public static string StartupFolder;
		public static string StartupFile;

		protected override void OnStartup(StartupEventArgs e)
		{
			// Never vanish silently: log unexpected UI-thread errors, tell the
			// user once, keep running.
			DispatcherUnhandledException += (s, ev) =>
			{
				try
				{
					var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageViewer");
					Directory.CreateDirectory(dir);
					File.AppendAllText(Path.Combine(dir, "crash.log"), DateTime.Now.ToString("s") + "  " + ev.Exception + Environment.NewLine + Environment.NewLine);
				}
				catch { }
				if (!IsBenchmark && !_errorShown)
				{
					_errorShown = true;
					MessageBox.Show("Something went wrong:\n" + ev.Exception.Message + "\n\nDetails were written to %LOCALAPPDATA%\\ImageViewer\\crash.log.",
						"Image Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
				}
				ev.Handled = true;
			};

			var args = e.Args;
			var pathArgs = new System.Collections.Generic.List<string>();
			bool gather = false;
			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] == "--benchmark" && i + 1 < args.Length) BenchmarkFolder = Path.GetFullPath(args[++i]);
				else if (args[i] == "--out" && i + 1 < args.Length) BenchmarkOut = args[++i];
				else if (args[i] == "--cache-mb" && i + 1 < args.Length) ImageLoader.CacheBudgetBytes = long.Parse(args[++i]) * 1024 * 1024;
				else if (args[i] == "--software") System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
				else if (args[i] == "--gather") gather = true;   // Explorer menu: see LaunchGather
				else if (!args[i].StartsWith("--")) pathArgs.Add(args[i]);
			}

			// Folders and/or images: all their folders open together; the first
			// image is previewed. Unusable paths are ignored.
			if (pathArgs.Count > 0)
			{
				string file;
				var folders = FolderSet.FoldersOf(pathArgs, out file);
				if (folders.Count > 0) { StartupFolder = FolderSet.Join(folders); StartupFile = file; }

				// Explorer multi-selection: hand the path to the window collecting them.
				if (gather && BenchmarkFolder == null && LaunchGather.ForwardOrCollect(pathArgs))
				{
					Environment.Exit(0);
					return;
				}
			}

			if (IsBenchmark)
			{
				if (BenchmarkOut == null) BenchmarkOut = Path.Combine(Path.GetTempPath(), "ImageViewer-benchmark.txt");
				// Deterministic run: default settings, in memory only (never saved).
				foreach (SettingsProperty p in Settings.Default.Properties)
					Settings.Default[p.Name] = new SettingsPropertyValue(p).PropertyValue;
			}
			else
			{
				// Pick the settings profile for the monitor / virtual desktop the
				// app is being launched on, before MainWindow reads Settings.Default.
				Profiles.Initialize();
				try { ShellIntegration.UpdateIfOutdated(); } catch { }
			}
			Theme.Apply(Settings.Default.Theme);

			base.OnStartup(e);
		}
	}
}
