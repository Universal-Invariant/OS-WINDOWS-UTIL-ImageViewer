using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ImageViewer
{
	// Built-in benchmark:  ImageViewer.exe --benchmark <folder> [--out <report.txt>]
	// Loads the folder, scrolls through it frame by frame, changes the scale and
	// book view, and writes a timing report. Runs with default settings and never
	// saves settings. No input is simulated - everything is driven from code.
	public partial class MainWindow
	{
		DateTime _loadedAt;

		async void RunBenchmark()
		{
			var r = new StringBuilder();
			var proc = Process.GetCurrentProcess();
			var panel = SmartFlowPanel.Current;
			Perf.Enabled = true;

			WindowState = WindowState.Normal;
			Width = 1280; Height = 800;
			await Task.Delay(500);

			r.AppendLine("ImageViewer benchmark   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			r.AppendLine("Folder:   " + App.BenchmarkFolder);
			r.AppendLine(string.Format("Window:   {0:F0} x {1:F0} DIP, DPI scale {2:F2}, {3} CPUs",
				ActualWidth, ActualHeight, LazyImage.DpiScale, Environment.ProcessorCount));
			r.AppendLine(string.Format("Startup:  {0:N0} ms (process start -> window loaded)",
				(_loadedAt - proc.StartTime).TotalMilliseconds));
			r.AppendLine();

			// ---------------- 1. Folder load ----------------
			Perf.Reset();
			var sw = Stopwatch.StartNew();
			CB_Path.Text = App.BenchmarkFolder;
			oldPath = null;
			LoadPathFromCombo();

			await WaitUntil(() => _view.Count > 0, 120000);
			double tFirstItem = sw.Elapsed.TotalMilliseconds;
			await WaitUntil(() => { int t, w; panel.CountVisible(out t, out w); return t > 0 && w == t; }, 120000);
			double tFirstScreen = sw.Elapsed.TotalMilliseconds;
			await WaitUntil(() => !_isLoading, 1800000);
			double tLoaded = sw.Elapsed.TotalMilliseconds;
			await WaitUntil(() => Perf.PendingDecodes <= 0, 60000);
			await Task.Delay(300);

			int count = _view.Count;
			r.AppendLine("== Folder load ==");
			r.AppendLine(string.Format("Images:                    {0:N0}", count));
			r.AppendLine(string.Format("First image in list:       {0,8:N0} ms", tFirstItem));
			r.AppendLine(string.Format("First screen fully drawn:  {0,8:N0} ms", tFirstScreen));
			r.AppendLine(string.Format("Load complete:             {0,8:N0} ms   ({1:N0} images/s)", tLoaded, count / Math.Max(0.001, tLoaded / 1000)));
			AppendCounters(r);
			AppendMemory(r, proc);
			r.AppendLine();

			// ---------------- 2. Scrolling ----------------
			var sv = GetScrollViewer();
			sv.ScrollToVerticalOffset(0);
			await Task.Delay(800);
			r.AppendLine("== Scroll down, fast (1/4 screen per frame, whole folder) ==");
			Perf.Reset();
			await ScrollPass(r, sv, panel, sv.ViewportHeight * 0.25, int.MaxValue);
			AppendCounters(r);
			AppendMemory(r, proc);
			r.AppendLine();

			await WaitUntil(() => Perf.PendingDecodes <= 0, 60000);
			r.AppendLine("== Scroll up, moderate (1/20 screen per frame, 300 frames, warm cache) ==");
			Perf.Reset();
			await ScrollPass(r, sv, panel, -sv.ViewportHeight * 0.05, 300);
			AppendCounters(r);
			r.AppendLine();

			// ---------------- 3. Relayout ----------------
			r.AppendLine("== Relayout (all images) ==");
			foreach (var scale in new[] { 0.5, 1.5, 1.0 })
			{
				var t = Stopwatch.StartNew();
				SD_Scale.Value = scale;
				UpdateLayout();
				r.AppendLine(string.Format("Scale -> {0,4:P0}:      {1,8:N1} ms", scale, t.Elapsed.TotalMilliseconds));
				await Task.Delay(300);
			}
			foreach (var on in new[] { true, false })
			{
				var t = Stopwatch.StartNew();
				TGB_FitToWidth.IsChecked = on;
				UpdateLayout();
				r.AppendLine(string.Format("Book view {0,-3}:       {1,8:N1} ms", on ? "on" : "off", t.Elapsed.TotalMilliseconds));
				await Task.Delay(300);
			}
			{
				var t = Stopwatch.StartNew();
				Width = 1000;
				UpdateLayout();
				r.AppendLine(string.Format("Resize 1280->1000:  {0,8:N1} ms", t.Elapsed.TotalMilliseconds));
			}
			AppendMemory(r, proc);

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(App.BenchmarkOut)));
				File.WriteAllText(App.BenchmarkOut, r.ToString());
			}
			catch { /* nowhere to report */ }
			Close();
		}

		async Task ScrollPass(StringBuilder r, System.Windows.Controls.ScrollViewer sv, SmartFlowPanel panel, double step, int maxFrames)
		{
			var frames = new List<double>(4096);
			long visible = 0, blank = 0;
			int framesWithBlank = 0;
			double layoutMs = 0;
			TimeSpan last = TimeSpan.Zero;
			var done = new TaskCompletionSource<bool>();
			var sw = Stopwatch.StartNew();

			EventHandler onRender = (s, e) =>
			{
				var now = ((RenderingEventArgs)e).RenderingTime;
				if (now == last) return;                 // Rendering may fire more than once per frame
				if (last != TimeSpan.Zero) frames.Add((now - last).TotalMilliseconds);
				last = now;

				int t, w;
				panel.CountVisible(out t, out w);
				visible += t; blank += t - w;
				if (w < t) framesWithBlank++;

				bool atEnd = step > 0 ? sv.VerticalOffset >= sv.ScrollableHeight - 0.5 : sv.VerticalOffset <= 0.5;
				if (atEnd || frames.Count >= maxFrames) { done.TrySetResult(true); return; }
				// Force this frame's layout now so its cost can be separated from rendering.
				long t0 = Stopwatch.GetTimestamp();
				sv.ScrollToVerticalOffset(sv.VerticalOffset + step);
				UpdateLayout();
				layoutMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
			};
			CompositionTarget.Rendering += onRender;
			await Task.WhenAny(done.Task, Task.Delay(600000));
			CompositionTarget.Rendering -= onRender;

			if (frames.Count == 0) { r.AppendLine("(no frames)"); return; }
			var sorted = frames.OrderBy(x => x).ToList();
			Func<double, double> pct = p => sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
			r.AppendLine(string.Format("Frames: {0:N0} in {1:N0} ms   mean {2:N1} ms   p50 {3:N1}   p95 {4:N1}   p99 {5:N1}   max {6:N1}",
				frames.Count, sw.Elapsed.TotalMilliseconds, frames.Average(), pct(0.5), pct(0.95), pct(0.99), sorted[sorted.Count - 1]));
			r.AppendLine(string.Format("UI layout per frame (scroll + layout + ScrollChanged): mean {0:N2} ms", layoutMs / frames.Count));
			r.AppendLine(string.Format("Slow frames: {0:N0} > 33 ms, {1:N0} > 100 ms",
				frames.Count(x => x > 33), frames.Count(x => x > 100)));
			r.AppendLine(string.Format("Blank (not yet decoded) visible images: {0:P1} of image-frames; {1:P1} of frames had any",
				visible > 0 ? (double)blank / visible : 0, (double)framesWithBlank / Math.Max(1, frames.Count + 1)));
		}

		static void AppendCounters(StringBuilder r)
		{
			foreach (Perf.C c in Enum.GetValues(typeof(Perf.C)))
				if (c != Perf.C.Count && Perf.Calls(c) > 0) r.AppendLine("  " + Perf.Line(c));
			r.AppendLine(string.Format("  Decoded {0:N1} Mpixels, {1:N0} cache hits; cache now {2:N0} bitmaps, {3:N0} MB (budget {4:N0} MB)",
				Perf.DecodedPixels / 1e6, Perf.CacheHits, ImageLoader.CachedCount, ImageLoader.CachedBytes / 1048576.0, ImageLoader.CacheBudgetBytes / 1048576.0));
		}

		static void AppendMemory(StringBuilder r, Process proc)
		{
			proc.Refresh();
			r.AppendLine(string.Format("  Memory: working set {0:N0} MB, private {1:N0} MB, managed heap {2:N0} MB",
				proc.WorkingSet64 / 1048576.0, proc.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0));
			// How much of that is only waiting for finalizers (unmanaged bitmap memory)?
			GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
			proc.Refresh();
			r.AppendLine(string.Format("  After full GC:  working set {0:N0} MB, private {1:N0} MB",
				proc.WorkingSet64 / 1048576.0, proc.PrivateMemorySize64 / 1048576.0));
		}

		static async Task WaitUntil(Func<bool> condition, int timeoutMs)
		{
			var sw = Stopwatch.StartNew();
			while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
				await Task.Delay(10);
		}
	}
}
