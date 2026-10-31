using System;
using System.Diagnostics;
using System.Threading;

namespace ImageViewer
{
	/// <summary>
	/// Cheap timing counters for the built-in benchmark (--benchmark). Disabled
	/// unless the benchmark runs; when disabled every call is a single bool test.
	/// Counters are updated from the UI thread and the worker/decoder threads.
	/// </summary>
	internal static class Perf
	{
		public static bool Enabled;

		public enum C { HeaderRead, Batch, Measure, Arrange, Visibility, Decode, ApplyScale, Count }

		static readonly long[] _ticks = new long[(int)C.Count];
		static readonly long[] _calls = new long[(int)C.Count];
		public static long DecodedPixels;
		public static long CacheHits;
		public static int PendingDecodes;   // queued or running

		public static long Start() { return Enabled ? Stopwatch.GetTimestamp() : 0; }

		public static void Stop(C counter, long start)
		{
			if (!Enabled || start == 0) return;
			Interlocked.Add(ref _ticks[(int)counter], Stopwatch.GetTimestamp() - start);
			Interlocked.Increment(ref _calls[(int)counter]);
		}

		public static void Reset()
		{
			for (int i = 0; i < _ticks.Length; i++) { _ticks[i] = 0; _calls[i] = 0; }
			DecodedPixels = 0;
			CacheHits = 0;
		}

		public static double Ms(C c) { return Interlocked.Read(ref _ticks[(int)c]) * 1000.0 / Stopwatch.Frequency; }
		public static long Calls(C c) { return Interlocked.Read(ref _calls[(int)c]); }

		public static string Line(C c)
		{
			long n = Calls(c);
			double ms = Ms(c);
			return string.Format("{0,-12} {1,10:N1} ms total {2,9:N0} calls {3,9:N3} ms/call",
				c, ms, n, n > 0 ? ms / n : 0);
		}
	}
}
