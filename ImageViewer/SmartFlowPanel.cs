using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Utility;

namespace ImageViewer
{
	/// <summary>
	/// Flow ("wrap") layout of images and folder headers with UI virtualization.
	///
	/// The panel owns the full ordered item list (SetItems) but only the items
	/// in rows near the viewport are its visual children; the rest are plain
	/// objects. Every position is computed arithmetically from the items' own
	/// sizes (IGridItem), so a relayout of thousands of images is a single
	/// pass over an array - no WPF Measure/Arrange per image - and scrolling
	/// adds/removes a handful of children per frame.
	///
	/// Rows are produced top to bottom and stack without gaps, so _rows is
	/// sorted by Y and by Bottom: every lookup is a binary search.
	///
	/// Rows hold DISPLAY positions; _order maps a display position to an item
	/// index. It is the identity except in packed mode (Pack), where images of
	/// a folder group are reordered locally so small ones fill the space next
	/// to and under larger ones (see PackGroup).
	/// Hosted in a ScrollViewer (CanContentScroll = false): the panel reports
	/// its full height and realizes children from the scroll offset.
	/// </summary>
	public class SmartFlowPanel : Panel
	{
		public static SmartFlowPanel Current = null;

		// ---- layout options (set by MainWindow, then InvalidateLocations) -----
		public static bool TwoPageSpread;   // book view: two pages side by side
		public static bool RightToLeft;     // rows fill from the right (manga)
		public static bool CoverAlone;      // two-page spread: first page of each folder on its own row
		public static bool CenterPages = true;   // book view: rows centered horizontally
		public static bool Pack;            // grid (not book view): pack images densely, reordering locally

		// Book view: fraction of the usable width a page is scaled to. Below 1.0
		// so rounding can never push a page past the wrap threshold.
		const double FitToWidthFraction = 0.985;
		const double SpreadGap = 6;

		/// <summary>Book-view page width for a given viewport width (0 = unusable).</summary>
		public static double FitTarget(double availableWidth)
		{
			if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth) || availableWidth < 10) return 0;
			double t = availableWidth * FitToWidthFraction;
			return TwoPageSpread ? Math.Floor((t - SpreadGap) / 2) : t;
		}

		struct Row
		{
			public double Y, Height, Width;
			public int First, Count;          // item index range of this row
			public double Bottom { get { return Y + Height; } }
		}

		List<FrameworkElement> _items = new List<FrameworkElement>();
		readonly Dictionary<FrameworkElement, int> _indexOf = new Dictionary<FrameworkElement, int>();
		readonly List<Row> _rows = new List<Row>();
		readonly List<Rect> _slots = new List<Rect>();   // final rect of every item, by index
		int[] _order = new int[0];   // display position -> item index
		int[] _pos = new int[0];     // item index -> display position
		double _totalWidth, _totalHeight;
		bool _centerRows;   // book view (set per Flow)
		bool _packed;       // the last Flow packed the grid
		Size _lastAvailableSize;
		bool _layoutDirty = true;

		// One-shot request (MainWindow.ApplyScale) to re-run viewport-dependent
		// sizing on the next measure; cleared as soon as a pass consumes it.
		public bool InvalidateMeasures = false;

		public SmartFlowPanel() { Current = this; }

		public IReadOnlyList<FrameworkElement> Items { get { return _items; } }

		/// <summary>Replaces the item list (images and folder headers, in display order).</summary>
		public void SetItems(List<FrameworkElement> items)
		{
			_items = items;
			_indexOf.Clear();
			for (int i = 0; i < items.Count; i++) _indexOf[items[i]] = i;
			InvalidateLocations();
		}

		/// <summary>Full relayout on the next measure (sizes or options changed).</summary>
		public void InvalidateLocations() { _layoutDirty = true; InvalidateMeasure(); }

		/// <summary>Folder switch: release every loaded bitmap and drop all items.</summary>
		internal void NotifyItemsCleared()
		{
			for (int i = 0; i < _loaded.Count; i++) _loaded[i].UnLoadImage();
			_loaded.Clear();
			_loadedSet.Clear();
			SetItems(new List<FrameworkElement>());
		}

		/// <summary>Theme or background changed: redraw realized items.</summary>
		public void RefreshRealizedVisuals()
		{
			foreach (UIElement c in InternalChildren)
			{
				var img = c as LazyImage;
				if (img != null) img.RefreshVisual();
				var hdr = c as FolderHeader;
				if (hdr != null) hdr.RefreshVisual();
			}
		}


		// ------------------------------------------------------------------
		//  Measure / arrange
		// ------------------------------------------------------------------

		protected override Size MeasureOverride(Size availableSize)
		{
			if (ScrollOwner == null) ScrollOwner = VisualHelper.TryFindParent<ScrollViewer>(this);
			long perfStart = Perf.Start();

			double width = double.IsInfinity(availableSize.Width) ? Math.Max(400, _totalWidth) : availableSize.Width;
			if (!availableSize.Width.Equals(_lastAvailableSize.Width) || InvalidateMeasures)
			{
				// Viewport width changed (window resized) or scale settings
				// changed: book-view pages must be resized BEFORE flowing.
				_lastAvailableSize = availableSize;
				InvalidateMeasures = false;
				_layoutDirty = true;
				if (FitToWidthActive())
				{
					var target = FitTarget(width);
					if (target > 0)
						foreach (var item in _items)
						{
							var img = item as LazyImage;
							if (img != null) img.RefreshSize(target);
						}
				}
			}

			if (_layoutDirty)
			{
				Flow(width);
				_layoutDirty = false;
				ScheduleVisibilityUpdate();
			}

			Realize();
			Perf.Stop(Perf.C.Measure, perfStart);
			return new Size(double.IsInfinity(availableSize.Width) ? _totalWidth : width, _totalHeight);
		}

		void Flow(double width)
		{
			_rows.Clear();
			_slots.Clear();
			int n = _items.Count;
			for (int i = 0; i < n; i++) _slots.Add(Rect.Empty);
			if (_order.Length != n) { _order = new int[n]; _pos = new int[n]; }
			_totalWidth = _totalHeight = 0;
			bool book = FitToWidthActive();
			_centerRows = book && CenterPages;
			bool pack = Pack && !book;
			_packed = pack;

			// Folder headers take a full row; the images between two headers
			// (a group) flow, or are packed, on their own.
			for (int i = 0; i < n; )
			{
				var gi = (IGridItem)_items[i];
				if (gi.IsFullRow)
				{
					double h = gi.LayoutHeight;
					_slots[i] = new Rect(0, _totalHeight, width, h);
					_order[i] = i;
					_rows.Add(new Row { Y = _totalHeight, Height = h, Width = width, First = i, Count = 1 });
					_totalHeight += h;
					i++;
					continue;
				}
				int end = i + 1;
				while (end < n && !((IGridItem)_items[end]).IsFullRow) end++;
				if (pack) PackGroup(i, end, width);
				else FlowGroup(i, end, width, book);
				i = end;
			}
			for (int p = 0; p < n; p++) _pos[_order[p]] = p;
		}

		// Plain flow: items in order, left to right, wrapping into rows.
		void FlowGroup(int start, int end, double width, bool book)
		{
			double lineWidth = 0, lineHeight = 0;
			int rowFirst = start;
			// Book-style spreads: the first page of the folder (the cover) gets
			// a row of its own, so pages 2+3, 4+5... face each other as printed.
			bool coverRule = CoverAlone && TwoPageSpread && book;
			bool coverPending = true, breakAfter = false;

			for (int i = start; i < end; i++)
			{
				_order[i] = i;
				var gi = (IGridItem)_items[i];
				double w = gi.LayoutWidth, ih = gi.LayoutHeight;
				var page = _items[i] as LazyImage;
				bool isCover = coverRule && coverPending && page != null && page.IsPage;
				if (page != null && page.IsPage) coverPending = false;

				// Wrap only when the item would extend past the row and the row
				// already holds something (a lone oversized item keeps its row).
				if (i > rowFirst && (lineWidth + w > width || isCover || breakAfter))
				{
					CloseRow(rowFirst, i, lineWidth, lineHeight, width);
					lineWidth = lineHeight = 0;
					rowFirst = i;
				}
				breakAfter = isCover;
				_slots[i] = new Rect(lineWidth, 0, w, ih);   // Y patched in CloseRow
				lineWidth += w;
				lineHeight = Math.Max(lineHeight, ih);
			}
			if (end > rowFirst) CloseRow(rowFirst, end, lineWidth, lineHeight, width);
		}

		// How far ahead (in sort order) packing may pull an image forward.
		const int PackLookahead = 48;

		/// <summary>
		/// Packed layout of one group. Each row ("band") is as tall as the row
		/// a plain flow would build from the next images. The band is then
		/// filled guillotine-style: an image goes into the top-left corner of a
		/// free rectangle, leaving the column under it and the full-height
		/// strip to its right as new free rectangles. A free rectangle takes
		/// the earliest (in sort order) of the next PackLookahead images that
		/// fits, so smaller images stack in the space a taller neighbor would
		/// otherwise leave empty while the order changes only locally.
		/// </summary>
		void PackGroup(int start, int end, double width)
		{
			int m = end - start;
			var w = new double[m];
			var h = new double[m];
			var next = new int[m];
			var prev = new int[m];
			double minW = double.MaxValue, minH = double.MaxValue;
			for (int k = 0; k < m; k++)
			{
				var gi = (IGridItem)_items[start + k];
				w[k] = gi.LayoutWidth;
				h[k] = gi.LayoutHeight;
				minW = Math.Min(minW, w[k]);
				minH = Math.Min(minH, h[k]);
				next[k] = k + 1 < m ? k + 1 : -1;
				prev[k] = k - 1;
			}
			int head = m > 0 ? 0 : -1;
			int position = start;
			var free = new Stack<Rect>();
			const double eps = 0.01;

			while (head >= 0)
			{
				// Band height: the row a plain flow would build from here.
				double lineW = 0, bandH = 0;
				for (int k = head, c = 0; k >= 0; k = next[k], c++)
				{
					if (c > 0 && lineW + w[k] > width) break;
					lineW += w[k];
					bandH = Math.Max(bandH, h[k]);
				}

				int bandFirst = position;
				double bandRight = 0, y0 = _totalHeight;
				free.Clear();
				if (w[head] > width) bandRight = width;   // a lone oversized image keeps its row
				free.Push(new Rect(0, 0, Math.Max(width, w[head]), bandH));
				while (free.Count > 0)
				{
					var r = free.Pop();
					if (r.Width + eps < minW || r.Height + eps < minH) continue;
					int pick = -1, seen = 0;
					for (int k = head; k >= 0 && seen < PackLookahead; k = next[k], seen++)
						if (w[k] <= r.Width + eps && h[k] <= r.Height + eps) { pick = k; break; }
					if (pick < 0) continue;

					_slots[start + pick] = new Rect(r.X, y0 + r.Y, w[pick], h[pick]);
					bandRight = Math.Max(bandRight, r.X + w[pick]);
					if (prev[pick] >= 0) next[prev[pick]] = next[pick]; else head = next[pick];
					if (next[pick] >= 0) prev[next[pick]] = prev[pick];
					_order[position++] = start + pick;
					// LIFO: the column under the image fills before the strip to its right.
					free.Push(new Rect(r.X + w[pick], r.Y, Math.Max(0, r.Width - w[pick]), r.Height));
					free.Push(new Rect(r.X, r.Y + h[pick], w[pick], Math.Max(0, r.Height - h[pick])));
				}

				if (RightToLeft)
					for (int p = bandFirst; p < position; p++)
					{
						int idx = _order[p];
						var sl = _slots[idx];
						_slots[idx] = new Rect(Math.Max(0, width - sl.X - sl.Width), sl.Y, sl.Width, sl.Height);
					}
				_rows.Add(new Row { Y = y0, Height = bandH, Width = bandRight, First = bandFirst, Count = position - bandFirst });
				_totalHeight += bandH;
				_totalWidth = Math.Max(_totalWidth, bandRight);
			}
		}

		// Items of a row are centered vertically in it; right-to-left rows are
		// mirrored so they fill from the right edge. In book view rows are
		// centered horizontally, like pages in a reader.
		void CloseRow(int first, int end, double rowWidth, double rowHeight, double width)
		{
			double shift = _centerRows ? Math.Max(0, Math.Floor((width - rowWidth) / 2)) : 0;
			for (int k = first; k < end; k++)
			{
				var s = _slots[k];
				double x = RightToLeft ? Math.Max(0, width - s.X - s.Width - shift) : s.X + shift;
				double y = _totalHeight + Math.Round((rowHeight - s.Height) / 2);
				_slots[k] = new Rect(x, y, s.Width, s.Height);
			}
			_rows.Add(new Row { Y = _totalHeight, Height = rowHeight, Width = rowWidth, First = first, Count = end - first });
			_totalHeight += rowHeight;
			_totalWidth = Math.Max(_totalWidth, rowWidth);
		}

		// Realized = in the visual tree. Half a viewport above and below.
		const double RealizeMargin = 0.5;
		readonly HashSet<FrameworkElement> _realized = new HashSet<FrameworkElement>();
		readonly HashSet<FrameworkElement> _wantedChildren = new HashSet<FrameworkElement>();

		void Realize()
		{
			double top, bottom;
			ViewportBand(RealizeMargin, out top, out bottom);

			_wantedChildren.Clear();
			for (int r = FirstRowEndingAtOrBelow(top); r < _rows.Count && _rows[r].Y <= bottom; r++)
			{
				var row = _rows[r];
				for (int k = row.First; k < row.First + row.Count; k++) _wantedChildren.Add(_items[_order[k]]);
			}

			var children = InternalChildren;
			for (int i = children.Count - 1; i >= 0; i--)
			{
				var c = (FrameworkElement)children[i];
				if (_wantedChildren.Contains(c)) continue;
				children.RemoveAt(i);
				_realized.Remove(c);
			}
			foreach (var c in _wantedChildren)
				if (_realized.Add(c)) children.Add(c);

			foreach (FrameworkElement c in children)
			{
				var slot = _slots[_indexOf[c]];
				c.Measure(slot.Size);
			}
		}

		void ViewportBand(double margin, out double top, out double bottom)
		{
			var sv = ScrollOwner;
			if (sv != null && sv.ViewportHeight > 0)
			{
				top = sv.VerticalOffset;
				bottom = top + sv.ViewportHeight;
			}
			else
			{
				top = 0;
				bottom = 1500;
			}
			double m = (bottom - top) * margin;
			top -= m;
			bottom += m;
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			long perfStart = Perf.Start();
			foreach (FrameworkElement c in InternalChildren)
			{
				int i;
				if (!_indexOf.TryGetValue(c, out i)) continue;
				var slot = _slots[i];
				c.Arrange(slot);
				var img = c as LazyImage;
				if (img != null) img.LayoutRect = slot;
			}
			Perf.Stop(Perf.C.Arrange, perfStart);
			return finalSize;
		}

		bool FitToWidthActive()
		{
			var mw = MainWindow.Current;
			return mw != null && mw.IsFitToWidthEnabled && !LazyImage.TileMode;
		}


		// ------------------------------------------------------------------
		//  Lookups (row scrolling, hit testing, reading position)
		// ------------------------------------------------------------------

		// First row index with Y >= y.
		int LowerBoundY(double y)
		{
			int lo = 0, hi = _rows.Count;
			while (lo < hi) { int m = (lo + hi) >> 1; if (_rows[m].Y < y) lo = m + 1; else hi = m; }
			return lo;
		}

		// First row index with Y > y.
		int UpperBoundY(double y)
		{
			int lo = 0, hi = _rows.Count;
			while (lo < hi) { int m = (lo + hi) >> 1; if (_rows[m].Y <= y) lo = m + 1; else hi = m; }
			return lo;
		}

		// First row index with Bottom >= y (rows stack, so Bottom is sorted too).
		int FirstRowEndingAtOrBelow(double y)
		{
			int lo = 0, hi = _rows.Count;
			while (lo < hi) { int m = (lo + hi) >> 1; if (_rows[m].Bottom < y) lo = m + 1; else hi = m; }
			return lo;
		}

		Rect RowRect(int i) { var r = _rows[i]; return new Rect(0, r.Y, r.Width, r.Height); }

		/// <summary>The row with the SMALLEST Y that is >= y; default(Rect) when none.</summary>
		public Rect GetFirstRowBelow(double y)
		{
			int i = LowerBoundY(y);
			return i < _rows.Count ? RowRect(i) : default(Rect);
		}

		/// <summary>The bottom-most row whose TOP edge is strictly above y; default(Rect) when none.</summary>
		public Rect GetLastRowAbove(double y)
		{
			int i = LowerBoundY(y) - 1;
			return i >= 0 ? RowRect(i) : default(Rect);
		}

		/// <summary>
		/// The row occupying position y, chosen by START position (largest Y
		/// &lt;= y + slop); book-view pages share edges exactly, so the bottom
		/// edge is deliberately not tested. default(Rect) only without rows.
		/// </summary>
		public Rect GetRowAtOrAbove(double y)
		{
			const double eps = 2.0;
			int i = UpperBoundY(y + eps) - 1;
			if (i >= 0) return RowRect(i);
			return GetFirstRowBelow(y - eps);
		}

		public bool HasRows { get { return _rows.Count > 0; } }


		// Row holding display position i (rows are sorted by First).
		int RowOfIndex(int i)
		{
			int lo = 0, hi = _rows.Count;
			while (lo < hi) { int m = (lo + hi) >> 1; if (_rows[m].First <= i) lo = m + 1; else hi = m; }
			return lo - 1;
		}

		/// <summary>
		/// Arrow up/down: the image directly under / over this one inside its
		/// own (packed) row, else the image in the nearest row below (dir &gt; 0)
		/// or above (dir &lt; 0) that is closest horizontally, nearest vertically
		/// on a tie. Header rows are skipped. Null at the bottom / top.
		/// </summary>
		public LazyImage FindImageVertical(LazyImage from, int dir)
		{
			int i;
			if (from == null || !_indexOf.TryGetValue(from, out i) || i >= _pos.Length) return null;
			var f = _slots[i];
			double cx = f.X + f.Width / 2;
			int sgn = Math.Sign(dir), own = RowOfIndex(_pos[i]);
			for (int r = own; r >= 0 && r < _rows.Count; r += sgn)
			{
				var row = _rows[r];
				LazyImage best = null;
				double bestD = double.MaxValue, bestGap = double.MaxValue;
				for (int k = row.First; k < row.First + row.Count; k++)
				{
					int idx = _order[k];
					var img = _items[idx] as LazyImage;
					if (img == null || idx == i) continue;
					var s = _slots[idx];
					if (sgn > 0 ? s.Top < f.Bottom - 0.5 : s.Bottom > f.Top + 0.5) continue;   // not below / above
					double d = cx >= s.Left && cx <= s.Right ? 0 : Math.Min(Math.Abs(cx - s.Left), Math.Abs(cx - s.Right));
					double gap = sgn > 0 ? s.Top - f.Bottom : f.Top - s.Bottom;
					if (d < bestD - 0.5 || (d <= bestD + 0.5 && gap < bestGap)) { bestD = d; bestGap = gap; best = img; }
				}
				if (best != null && (r != own || bestD == 0)) return best;
			}
			return null;
		}

		/// <summary>
		/// Arrow left/right: the previous (step &lt; 0) / next image in reading
		/// order, headers skipped. In a packed row that is the nearest image
		/// beside this one (next = to the right, or left for right-to-left),
		/// moving on to the neighboring row at its end.
		/// </summary>
		public LazyImage FindImageHorizontal(LazyImage from, int step)
		{
			int i;
			if (from == null || !_indexOf.TryGetValue(from, out i) || i >= _pos.Length) return null;
			int sgn = Math.Sign(step);
			if (_packed)
			{
				var f = _slots[i];
				double cy = f.Y + f.Height / 2;
				int vis = RightToLeft ? -sgn : sgn;   // +1 = towards the right edge
				int own = RowOfIndex(_pos[i]);
				var row = _rows[own];
				LazyImage best = null;
				double bestGap = double.MaxValue, bestDy = double.MaxValue;
				for (int k = row.First; k < row.First + row.Count; k++)
				{
					int idx = _order[k];
					var img = _items[idx] as LazyImage;
					if (img == null || idx == i) continue;
					var s = _slots[idx];
					if (vis > 0 ? s.Left < f.Right - 0.5 : s.Right > f.Left + 0.5) continue;   // not beside
					double gap = vis > 0 ? s.Left - f.Right : f.Left - s.Right;
					double dy = cy >= s.Top && cy <= s.Bottom ? 0 : Math.Min(Math.Abs(cy - s.Top), Math.Abs(cy - s.Bottom));
					if (dy < bestDy - 0.5 || (dy <= bestDy + 0.5 && gap < bestGap)) { bestDy = dy; bestGap = gap; best = img; }
				}
				if (best != null) return best;
				// Row end: the first image of the next row / the last of the previous one.
				int p0 = sgn > 0 ? row.First + row.Count : row.First - 1;
				for (int p = p0; p >= 0 && p < _order.Length; p += sgn)
				{
					var img = _items[_order[p]] as LazyImage;
					if (img == null) continue;
					if (sgn > 0) return img;
					// Previous row: its image farthest along in reading order (rightmost, or leftmost for RTL).
					var prevRow = _rows[RowOfIndex(p)];
					LazyImage last = img;
					double edge = double.MinValue;
					for (int k = prevRow.First; k < prevRow.First + prevRow.Count; k++)
					{
						var c = _items[_order[k]] as LazyImage;
						if (c == null) continue;
						var s = _slots[_order[k]];
						double e = RightToLeft ? -s.Left : s.Right;
						if (e > edge + 0.5) { edge = e; last = c; }
					}
					return last;
				}
				return null;
			}
			for (int p = _pos[i] + sgn; p >= 0 && p < _order.Length; p += sgn)
			{
				var img = _items[_order[p]] as LazyImage;
				if (img != null) return img;
			}
			return null;
		}

		/// <summary>The images from a to b (either order), in display order (Shift-selection).</summary>
		public List<LazyImage> ImagesBetween(LazyImage a, LazyImage b)
		{
			var list = new List<LazyImage>();
			int ia, ib;
			if (a == null || b == null || !_indexOf.TryGetValue(a, out ia) || !_indexOf.TryGetValue(b, out ib)
				|| ia >= _pos.Length || ib >= _pos.Length) return list;
			int p0 = Math.Min(_pos[ia], _pos[ib]), p1 = Math.Max(_pos[ia], _pos[ib]);
			for (int p = p0; p <= p1; p++)
			{
				var img = _items[_order[p]] as LazyImage;
				if (img != null) list.Add(img);
			}
			return list;
		}

		public bool TryGetSlot(FrameworkElement item, out Rect slot)
		{
			int i;
			if (item != null && _indexOf.TryGetValue(item, out i) && i < _slots.Count) { slot = _slots[i]; return true; }
			slot = Rect.Empty;
			return false;
		}

		/// <summary>Scrolls so the item is fully visible (or its top at the viewport top).</summary>
		public void ScrollIntoView(FrameworkElement item, bool alignTop = false)
		{
			var sv = ScrollOwner;
			Rect slot;
			if (sv == null || !TryGetSlot(item, out slot)) return;
			double top = sv.VerticalOffset, vp = sv.ViewportHeight;
			if (alignTop || slot.Height >= vp || slot.Y < top) sv.ScrollToVerticalOffset(Math.Max(0, slot.Y - (alignTop ? 0 : 8)));
			else if (slot.Bottom > top + vp) sv.ScrollToVerticalOffset(slot.Bottom - vp + 8);
		}

		/// <summary>
		/// The first image at the viewport top and how far (0..1) its row has
		/// been scrolled past - the reading position.
		/// </summary>
		public LazyImage FirstVisibleImage(out double fraction)
		{
			fraction = 0;
			var sv = ScrollOwner;
			double top = sv != null ? sv.VerticalOffset : 0;
			for (int r = FirstRowEndingAtOrBelow(top + 0.5); r < _rows.Count; r++)
			{
				var row = _rows[r];
				for (int k = row.First; k < row.First + row.Count; k++)
				{
					var img = _items[_order[k]] as LazyImage;
					if (img == null) continue;
					fraction = row.Height > 0 ? Math.Max(0, Math.Min(1, (top - row.Y) / row.Height)) : 0;
					return img;
				}
			}
			return null;
		}

		/// <summary>Scroll offset that puts the item's row at the top, plus a fraction of the row.</summary>
		public bool TryGetOffsetOf(FrameworkElement item, double fraction, out double offset)
		{
			offset = 0;
			int i;
			if (item == null || !_indexOf.TryGetValue(item, out i) || i >= _pos.Length) return false;
			int r = RowOfIndex(_pos[i]);
			if (r < 0) return false;
			offset = _rows[r].Y + fraction * _rows[r].Height;
			return true;
		}


		// ------------------------------------------------------------------
		//  Scrolling and lazy loading
		// ------------------------------------------------------------------

		ScrollViewer ScrollOwner { get { return _scrollOwner; } set { scrollOwnerChanged(value); _scrollOwner = value; } } ScrollViewer _scrollOwner;

		void scrollOwnerChanged(ScrollViewer newScrollOwner)
		{
			if (ScrollOwner != null) ScrollOwner.ScrollChanged -= ScrollOwner_ScrollChanged;
			if (newScrollOwner != null) newScrollOwner.ScrollChanged += ScrollOwner_ScrollChanged;
		}

		void ScrollOwner_ScrollChanged(object sender, ScrollChangedEventArgs e)
		{
			// New rows came near the viewport: realize them (next layout pass,
			// still before this frame renders), then load/unload bitmaps.
			if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
				InvalidateMeasure();
			UpdateVisibleImages();
		}

		// Images inside the load band (viewport + LoadMargin) are loaded;
		// loaded images are released only once they leave the wider keep band,
		// so scrolling back and forth across the edge doesn't thrash. Images
		// staying in view are never touched (no flicker). Bitmaps load whether
		// or not the element is realized.
		const double LoadMargin = 0.5;
		const double KeepMargin = 1.0;

		readonly List<LazyImage> _loaded = new List<LazyImage>(128);
		readonly HashSet<LazyImage> _loadedSet = new HashSet<LazyImage>();
		readonly HashSet<LazyImage> _keepSet = new HashSet<LazyImage>();

		public void UpdateVisibleImages()
		{
			var sv = ScrollOwner;
			if (sv == null) return;
			long perfStart = Perf.Start();

			double vp = Math.Max(sv.ViewportHeight, 0);
			double top = sv.VerticalOffset;
			double bottom = top + vp;

			// On-screen first, then the prefetch margin: the decoder queue
			// serves what the user is looking at first.
			LoadBand(top, bottom);
			LoadBand(top - vp * LoadMargin, bottom + vp * LoadMargin);

			_keepSet.Clear();
			for (int r = FirstRowEndingAtOrBelow(top - vp * KeepMargin); r < _rows.Count && _rows[r].Y <= bottom + vp * KeepMargin; r++)
			{
				var row = _rows[r];
				for (int k = row.First; k < row.First + row.Count; k++)
				{
					var img = _items[_order[k]] as LazyImage;
					if (img != null) _keepSet.Add(img);
				}
			}

			for (int i = _loaded.Count - 1; i >= 0; i--)
			{
				var img = _loaded[i];
				if (_keepSet.Contains(img)) continue;
				img.UnLoadImage();
				_loadedSet.Remove(img);
				_loaded[i] = _loaded[_loaded.Count - 1];   // swap-remove, order irrelevant
				_loaded.RemoveAt(_loaded.Count - 1);
			}
			Perf.Stop(Perf.C.Visibility, perfStart);
		}

		void LoadBand(double top, double bottom)
		{
			for (int r = FirstRowEndingAtOrBelow(top); r < _rows.Count && _rows[r].Y <= bottom; r++)
			{
				var row = _rows[r];
				for (int k = row.First; k < row.First + row.Count; k++)
				{
					var img = _items[_order[k]] as LazyImage;
					if (img == null) continue;
					img.LoadImage();   // idempotent
					if (_loadedSet.Add(img)) _loaded.Add(img);
				}
			}
		}

		/// <summary>Benchmark probe: images intersecting the viewport, and how many have pixels.</summary>
		internal void CountVisible(out int total, out int withPixels)
		{
			total = withPixels = 0;
			var sv = ScrollOwner;
			if (sv == null) return;
			double top = sv.VerticalOffset, bottom = top + sv.ViewportHeight;
			for (int r = FirstRowEndingAtOrBelow(top); r < _rows.Count && _rows[r].Y <= bottom; r++)
			{
				var row = _rows[r];
				for (int k = row.First; k < row.First + row.Count; k++)
				{
					var img = _items[_order[k]] as LazyImage;
					if (img == null) continue;
					total++;
					if (img.Source != null) withPixels++;
				}
			}
		}

		// Positions changed without necessarily changing the scroll extent, so
		// ScrollChanged may not fire: re-evaluate once the layout pass settles.
		bool _visibilityUpdateQueued;
		void ScheduleVisibilityUpdate()
		{
			if (_visibilityUpdateQueued) return;
			_visibilityUpdateQueued = true;
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
			{
				_visibilityUpdateQueued = false;
				UpdateVisibleImages();
			}));
		}
	}
}
