using System;
using System.Windows.Threading;
using ImageViewer.Properties;

namespace ImageViewer
{
	// Hands-free reading (Ctrl+Space): every few seconds the grid scrolls one
	// screen, or book view turns one page. Scrolling or paging by hand gives
	// you a full interval from there; Esc or Ctrl+Space stops; it stops by
	// itself at the end and when another folder opens.
	public partial class MainWindow
	{
		DispatcherTimer _autoTimer;

		bool IsAutoAdvancing { get { return _autoTimer != null && _autoTimer.IsEnabled; } }

		static int AutoAdvanceSeconds { get { return Math.Max(1, Math.Min(120, Settings.Default.AutoAdvanceSeconds)); } }

		void ToggleAutoAdvance()
		{
			if (IsAutoAdvancing) StopAutoAdvance("Auto-advance off");
			else StartAutoAdvance();
		}

		void StartAutoAdvance()
		{
			if (_view.Count == 0) return;
			if (_autoTimer == null)
			{
				_autoTimer = new DispatcherTimer();
				_autoTimer.Tick += AutoAdvance_Tick;
			}
			_autoTimer.Interval = TimeSpan.FromSeconds(AutoAdvanceSeconds);
			_autoTimer.Start();
			ShowToast("Auto-advance: " + (IsBookLayout ? "next page" : "next screen") + " every " + AutoAdvanceSeconds + " s  (Ctrl+Space or Esc stops)");
			UpdateModeText();
		}

		void StopAutoAdvance(string message)
		{
			if (!IsAutoAdvancing) return;
			_autoTimer.Stop();
			if (message != null) ShowToast(message);
			UpdateModeText();
		}

		/// <summary>The reader moved by hand: count the interval from now.</summary>
		void RestartAutoAdvanceCountdown()
		{
			if (!IsAutoAdvancing) return;
			_autoTimer.Stop();
			_autoTimer.Start();
		}

		/// <summary>Settings changed the interval.</summary>
		void ApplyAutoAdvanceInterval()
		{
			if (_autoTimer != null) _autoTimer.Interval = TimeSpan.FromSeconds(AutoAdvanceSeconds);
			UpdateModeText();
		}

		void AutoAdvance_Tick(object sender, EventArgs e)
		{
			if (IsPreviewOpen || IsHelpOpen) return;   // paused meanwhile
			var sv = GetScrollViewer();
			if (sv.ScrollableHeight <= 0 || sv.VerticalOffset >= sv.ScrollableHeight - 1)
			{
				StopAutoAdvance(_isLoading ? null : "End reached - auto-advance stopped");
				return;
			}
			if (IsBookLayout) PageStep(+1);
			else ScrollOneScreen();
		}

		// The next screen starts with the first row not yet fully visible (the
		// one cut by the bottom edge, or the one right below the screen). A row
		// taller than the screen - or one that would barely move the view -
		// pages by a screen minus the "overlap when paging" setting instead.
		void ScrollOneScreen()
		{
			var sv = GetScrollViewer();
			double top = sv.VerticalOffset, vp = Math.Max(1, sv.ViewportHeight), bottom = top + vp;
			int overlap = Math.Max(0, Math.Min(50, Settings.Default.ScrollUndershootPercent));
			double page = Math.Max(1, vp * (1 - overlap / 100.0));

			double target = top + page;
			var row = GridPanel.GetRowAtOrAbove(bottom - 1);   // the row at the bottom edge
			if (row.Height > 0)
			{
				double next = row.Bottom <= bottom + 1 ? row.Bottom : row.Y;
				if (next > top + vp * 0.25 && next <= top + vp) target = next;
			}
			sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, target));
		}
	}
}
