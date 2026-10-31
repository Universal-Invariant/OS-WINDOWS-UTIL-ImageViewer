using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ImageViewer
{
	/// <summary>
	/// "Open in Image Viewer" on several folders or images selected in
	/// Explorer starts one process per item (each gets "--gather &lt;path&gt;").
	/// The first one becomes the collector: for a moment after it starts it
	/// listens on a named pipe, and the others send their path to it and exit,
	/// so all of them open in one window as one view. A launch after that
	/// moment opens its own window as usual.
	/// </summary>
	internal static class LaunchGather
	{
		// The collection window: from start, extended a little by every arrival.
		static readonly TimeSpan Window = TimeSpan.FromSeconds(2.5);
		static readonly TimeSpan Extension = TimeSpan.FromSeconds(1.5);
		static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(15);

		static string Name { get { return "ImageViewer.Gather." + Process.GetCurrentProcess().SessionId; } }

		static Mutex _marker;
		static readonly ConcurrentQueue<string> _received = new ConcurrentQueue<string>();
		static volatile bool _collecting;

		/// <summary>True while paths from other launches may still arrive.</summary>
		public static bool Collecting { get { return _collecting; } }

		/// <summary>Received paths not taken yet.</summary>
		public static int Pending { get { return _received.Count; } }

		/// <summary>Paths sent by other launches since the last call.</summary>
		public static List<string> TakeReceived()
		{
			var list = new List<string>();
			string p;
			while (_received.TryDequeue(out p)) list.Add(p);
			return list;
		}

		/// <summary>
		/// Called at startup with this launch's paths. True: another instance
		/// is collecting and took them (this process should exit). False: this
		/// instance is the collector (or forwarding failed) and opens a window.
		/// </summary>
		public static bool ForwardOrCollect(IList<string> paths)
		{
			bool created;
			try { _marker = new Mutex(false, @"Local\" + Name, out created); }
			catch { return false; }
			if (created)
			{
				StartCollecting();
				return false;
			}
			_marker.Dispose();
			_marker = null;

			// The collector may still be starting: its pipe appears shortly.
			var bytes = Encoding.UTF8.GetBytes(string.Join("\n", paths));
			var sw = Stopwatch.StartNew();
			while (sw.ElapsedMilliseconds < 2000)
			{
				try
				{
					using (var c = new NamedPipeClientStream(".", Name, PipeDirection.Out))
					{
						c.Connect(500);
						c.Write(bytes, 0, bytes.Length);
						c.Flush();
					}
					return true;
				}
				catch (TimeoutException) { }
				catch (IOException) { Thread.Sleep(50); }
				catch { return false; }
			}
			return false;   // nobody listening any more: open a window of our own
		}

		static void StartCollecting()
		{
			_collecting = true;
			var thread = new Thread(() =>
			{
				var start = DateTime.UtcNow;
				var until = start + Window;
				try
				{
					while (DateTime.UtcNow < until)
					{
						using (var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
						{
							var ar = server.BeginWaitForConnection(null, null);
							var left = until - DateTime.UtcNow;
							if (left <= TimeSpan.Zero || !ar.AsyncWaitHandle.WaitOne(left)) break;
							server.EndWaitForConnection(ar);
							using (var ms = new MemoryStream())
							{
								server.CopyTo(ms);
								foreach (var line in Encoding.UTF8.GetString(ms.ToArray()).Split('\n'))
									if (line.Trim().Length > 0) _received.Enqueue(line.Trim());
							}
							var later = DateTime.UtcNow + Extension;
							if (later > until) until = later < start + MaxWindow ? later : start + MaxWindow;
						}
					}
				}
				catch { /* collection is best effort */ }
				finally
				{
					_collecting = false;
					try { _marker.Dispose(); } catch { }
				}
			}) { IsBackground = true, Name = "LaunchGather" };
			thread.Start();
		}
	}
}
