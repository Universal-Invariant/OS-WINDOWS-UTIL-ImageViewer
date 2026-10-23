using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Utility;

namespace ImageViewer
{
	public enum FlowPanelType
	{
		HorizontalWrapPanel
	}


 


	public class SmartFlowPanel : Panel
	{

		Point _Offset;
		public event EventHandler ItemsVisible;
 
		public FlowPanelType Type { get { return _flowPanelType; } set { 
			_flowPanelType = value; 
			switch(value)
			{
				case FlowPanelType.HorizontalWrapPanel: MeasureOverrideProc = HorizontalWrapPanelMeasureOverrideProc; ArrangeOverrideProc = HorizontalWrapPanelArrangeOverrideProc; break;
			}
		 } } FlowPanelType _flowPanelType = FlowPanelType.HorizontalWrapPanel;
 
		protected Func<Size, Size> MeasureOverrideProc { get; set; }
		protected Func<Size, Size> ArrangeOverrideProc { get; set; }

		protected override Size MeasureOverride(Size availableSize) { return MeasureOverrideProc(availableSize); }
		protected override Size ArrangeOverride(Size finalSize) { return ArrangeOverrideProc(finalSize); }



		public TreeCollection.AVLTree<Tuple<Rect, UIElement>, double, List<Tuple<Rect, UIElement>>> ElementLocations = new TreeCollection.AVLTree<Tuple<Rect, UIElement>, double, List<Tuple<Rect, UIElement>>>();

		// ScrollViewer content extent reported by the previous measure pass.
		// When it differs from the current one the viewport changed and every
		// image must recompute its size (used by fit-to-width mode).
		Size _lastAvailableSize;

		public static SmartFlowPanel Current = null;
		public SmartFlowPanel() { Type = FlowPanelType.HorizontalWrapPanel; ElementLocations.ItemKey = (t) => { return t.Item1.Y; }; Current = this; }

		public void InvalidateLocations() { _invalidatedMeasure = true; _invalidatedArrange = true; InvalidateRowCache(); InvalidateMeasure(); InvalidateVisual(); } bool _invalidatedMeasure = true; bool _invalidatedArrange = true;


	// --- Row lookup for "scroll by image" ------------------------
	// ElementLocations holds one entry per laid-out element keyed by its row's Y.
	// These lookups walk the AVL tree directly (Root/Left/Right). We deliberately
	// do NOT use the bounded enumerator (Traversal.Traverse + EnumeratorRange):
	// its range filter silently skips nodes that hold qualifying children - with
	// an open-ended half-line interval the root itself fails the range test and
	// whole subtrees never get visited - which made both lookups return empty
	// results, so every wheel tick computed target == current offset and
	// scrolling appeared completely dead. The walks below visit every entry per
	// Y-key (node.Value AND node.Values, since duplicate-Y rows are collected
	// into the node's value list) and touch nothing related to lazy loading.

	/// <summary>
	/// The row with the SMALLEST Y that is >= y. Returns default(Rect) (IsEmpty)
	/// when none exists; callers guard with HasRows.
	/// </summary>
	public Rect GetFirstRowBelow(double y)
	{
		var rows = GetDistinctRows();
		Rect? best = null;
		foreach (var r in rows)
			if (r.Y >= y && (best == null || r.Y < best.Value.Y)) best = r;
		return best ?? default(Rect);
	}

	/// <summary>
	/// The bottom-most row whose TOP edge is strictly above y. Returns default(Rect)
	/// when none exists; callers guard with HasRows.
	/// </summary>
	public Rect GetLastRowAbove(double y)
	{
		var rows = GetDistinctRows();
		Rect? best = null;
		foreach (var r in rows)
			if (r.Y < y && (best == null || r.Y > best.Value.Y)) best = r;
		return best ?? default(Rect);
	}

	/// <summary>
	/// The row that occupies viewport position y, chosen by START position:
	/// the row with the LARGEST Y that is <= y (+eps slop). Falls back to the
	/// first row below y when none starts at or above it.
	/// This deliberately does NOT require the row's bottom edge to extend
	/// strictly past y. With fit-to-width ("book view") active, consecutive
	/// oversized pages share edges exactly (row N+1's Y equals row N's Bottom),
	/// so the old strict "span covers yTop" test (Bottom > yTop + eps) failed
	/// right after a wheel notch landed exactly one page step into a tall row:
	/// the previous row was rejected (its Bottom sits AT yTop, not past it) and
	/// the covering lookup returned empty, making cur degenerate to the row
	/// ABOVE. Every subsequent notch then computed target == current offset,
	/// the wheel appeared dead, and the LineDown() failsafe scrolled by lines
	/// instead of snapping each next image's top-left corner to the viewport
	/// top (offset by the scroll overlap setting). That was the book-view
	/// scroll regression.
	/// Returns default(Rect) only when there are no laid-out rows at all.
	/// </summary>
	public Rect GetRowAtOrAbove(double y)
	{
		var rows = GetDistinctRows();
		const double eps = 2.0;
		Rect? best = null;
		foreach (var r in rows)
			if (r.Y <= y + eps && (best == null || r.Y > best.Value.Y)) best = r;
		if (best != null) return best.Value;
		return GetFirstRowBelow(y - eps);
	}

	// Distinct row rectangles (one per layout row). ElementLocations stores one
	// entry PER ELEMENT keyed by the row's Y, so a row of five images appears
	// five times; the HashSet dedupes them cheaply. Rebuilt only when the
	// layout changed (any relayout flips _rowsDirty), so repeated wheel ticks
	// over an unchanged layout are nearly free and never touch lazy loading.
	readonly HashSet<Rect> _rowSet = new HashSet<Rect>();
	List<Rect> _distinctRows;
	bool _rowsDirty = true;

	void InvalidateRowCache() { _rowsDirty = true; }

	List<Rect> GetDistinctRows()
	{
		if (!_rowsDirty && _distinctRows != null) return _distinctRows;
		_rowSet.Clear();
		VisitAll(ElementLocations.Root, delegate(Tuple<Rect, UIElement> t) { _rowSet.Add(t.Item1); });
		_distinctRows = new List<Rect>(_rowSet);
		_rowsDirty = false;
		return _distinctRows;
	}


	// Iterative (stack-based) pre-order walk of the whole AVL tree invoking act
	// for EVERY laid-out element - both node.Value and any duplicates in
	// node.Values. No recursion (trees can be deep), no allocations in the
	// steady state (the stack field is reused). O(n) worst case but n is the
	// number of laid-out elements and this runs only once per wheel tick.
	readonly Stack<TreeCollection.INode<Tuple<Rect, UIElement>>> _rowWalkStack
		= new Stack<TreeCollection.INode<Tuple<Rect, UIElement>>>();

	void VisitAll(TreeCollection.INode<Tuple<Rect, UIElement>> root, Action<Tuple<Rect, UIElement>> act)
	{
		_rowWalkStack.Clear();
		if (root != null) _rowWalkStack.Push(root);
		while (_rowWalkStack.Count > 0)
		{
			var n = _rowWalkStack.Pop();
			act(n.Value);
			if (n.Values != null)
				foreach (var v in n.Values) act(v);
			if (n.Left != null) _rowWalkStack.Push(n.Left);
			if (n.Right != null) _rowWalkStack.Push(n.Right);
		}
	}

	/// <summary>True when at least one laid-out row exists.</summary>
	public bool HasRows { get { return ElementLocations.Count > 0; } }

	/// <summary>
	/// Hit-test: the file path of the image whose laid-out rectangle contains
	/// the given point (in this panel's coordinate space), or null when the
	/// point misses every image. Walks the cached element locations directly -
	/// no visual-tree traversal - so it is cheap enough for a double-click.
	/// Duplicate-Y rows are covered because node.Value AND node.Values are both
	/// visited, same as the row-cache rebuild.
	/// </summary>
	public string GetImagePathAtPoint(Point p)
	{
		string found = null;
		VisitAll(ElementLocations.Root, delegate(Tuple<Rect, UIElement> t)
		{
			if (found != null) return;
			var r = t.Item1;
			if (r.Contains(p))
			{
				var img = GetImage(t.Item2);
				if (img != null && !string.IsNullOrEmpty(img.Path)) found = img.Path;
			}
		});
		return found;
	}

	/// <summary>
	/// Height of the row occupying viewport position yTop (the row whose TOP edge
	/// is the last one at or just above yTop). Used by "scroll by image" to clamp
	/// each step to at most one viewport height, so a row taller than the window
	/// (fit-to-width book pages) still scrolls in visible chunks instead of
	/// jumping its full height off-screen. Returns 0 when no such row exists.
	/// </summary>
	public double GetRowHeightAt(double yTop)
	{
		var r = GetLastRowAbove(yTop + 1.0);   // row starting at/just above yTop
		if (r.IsEmpty && r.Height <= 0) return 0;
		return r.Height;
	}


        ScrollViewer ScrollOwner { get { return _scrollOwner; } set { scrollOwnerChanged(value); _scrollOwner = value; } } ScrollViewer _scrollOwner;

		void scrollOwnerChanged(ScrollViewer newScrollOwner)
		{
			if (ScrollOwner != null) ScrollOwner.ScrollChanged -= new ScrollChangedEventHandler(ScrollOwner_ScrollChanged); 
			if (newScrollOwner != null) newScrollOwner.ScrollChanged += new ScrollChangedEventHandler(ScrollOwner_ScrollChanged);
		}
		
		List<Tuple<Rect, UIElement>> VisibleUIElements = new List<Tuple<Rect, UIElement>>(100);
		void ScrollOwner_ScrollChanged(object sender, ScrollChangedEventArgs e)
		{
			
			var height = ScrollOwner.ViewportHeight;
			var width = ScrollOwner.ViewportWidth;
			_Offset = new Point(e.HorizontalOffset, e.VerticalOffset);

			// NOTE: we deliberately do NOT rebuild the whole layout on every
			// scroll tick. The rectangles in ElementLocations only change when
			// an image's size changes (scale / fit-to-width / resize), never
			// because of scrolling. Re-invalidating measure+arrange here was a
			// major cost while dragging the scrollbar over large folders and
			// also produced transient frames where sizes and positions were
			// out of sync (the "transient overlaps"). Scrolling now only does
			// the lazy load/unload bookkeeping below.

			// Scan over all visible images
			ElementLocations.EnumeratorRange = new TreeCollection.KeyInterval<double>(e.VerticalOffset, e.VerticalOffset + height);
			ElementLocations.Traversal.TraversalMethod = TreeCollection.TraversalEnumeratorMethod.In;
			ElementLocations.Traversal.TraversalDir = TreeCollection.TraversalEnumeratorDir.Normal;
			ElementLocations.Traversal.Traverse((t, n) =>
			{
				var image = GetImage(t.Item2); if (image == null) return true;

				image.LoadImage();
				if (!VisibleUIElements.Contains(t)) VisibleUIElements.Add(t);

				return true;
			}); 


			// Get previous images before first found(images not fully displayed)
			ElementLocations.EnumeratorRange = new TreeCollection.KeyInterval<double>(0, e.VerticalOffset);
			ElementLocations.Traversal.TraversalMethod = TreeCollection.TraversalEnumeratorMethod.In;
			ElementLocations.Traversal.TraversalDir = TreeCollection.TraversalEnumeratorDir.Reverse;
			ElementLocations.Traversal.Traverse((t, n) =>
			{
				var image = GetImage(t.Item2); if (image == null) return true;

				if (t.Item1.Y + t.Item1.Height > e.VerticalOffset)
				{
					image.LoadImage();
					if (!VisibleUIElements.Contains(t)) VisibleUIElements.Add(t);

					return true;
				} else
					return false;
			});



			// Scan visible images and remove non-visible images
			for (var i = 0; i < VisibleUIElements.Count; i++)
			{
				var t = VisibleUIElements[i];

				var image = GetImage(t.Item2);
				if (image == null) { VisibleUIElements.RemoveAt(i); continue; }
				if (t.Item1.Y + t.Item1.Height < e.VerticalOffset || e.VerticalOffset + height < t.Item1.Y)
				{
					image.UnLoadImage();
					VisibleUIElements.RemoveAt(i);
					continue;
				}

			}

			// Restore the "whole tree" state so any later traversal that
			// expects to see every element is not silently clipped to the
			// last scroll band used above.
			ElementLocations.EnumeratorRange = null;

			return;
		}

		// Extract the LazyImage from a child element. Children are either the
		// LazyImages themselves or ListViewItem containers wrapping them; both
		// forms are handled so nothing is silently skipped during layout.
		static LazyImage GetImage(UIElement child)
		{
			var img = child as LazyImage;
			if (img != null) return img;
			var lvi = child as ListViewItem;
			if (lvi != null) return lvi.Content as LazyImage;
			return null;
		}

		Size MeasureSize;
		Size infiniteSize = new Size(double.PositiveInfinity, double.PositiveInfinity);
		List<Tuple<Rect, UIElement>> TempElements = new List<Tuple<Rect,UIElement>>(50);
		List<Tuple<Rect, UIElement>> ArrangeList = new List<Tuple<Rect, UIElement>>(50);
		// Fit-to-width ("book format") mode: the fraction of the usable
		// viewport width each image is scaled to. Must stay below 1.0 so
		// rounding drift can never push an image past the wrap threshold.
		const double FitToWidthFraction = 0.985;

		protected Size HorizontalWrapPanelMeasureOverrideProc(Size availableSize)
		{
			if (ScrollOwner == null) ScrollOwner = VisualHelper.TryFindParent<ScrollViewer>(this);

			bool relayout = _invalidatedMeasure;

			// The ScrollViewer measures its content with infinite height and a
			// fixed width equal to the viewport. When that width changes (the
			// window was resized, or a vertical scrollbar appeared/disappeared)
			// while fit-to-width is active, every image must recompute its size
			// BEFORE we lay anything out - otherwise the stale sizes overlap the
			// new row boundaries. Detecting it here (from the actual extent) is
			// race-free, unlike debouncing on Window.SizeChanged.
			if (!availableSize.Width.Equals(_lastAvailableSize.Width))
			{
				_lastAvailableSize = availableSize;
				// ALWAYS force a full relayout when the viewport changes, even
				// when fit-to-width is OFF: previously relayout was only set inside
				// the FitToWidthActive() branch, so toggling book view off (or any
				// size change with book view disabled) returned the cached MeasureSize
				// and left rows laid out for window-width pages -> massive overlaps.
				relayout = true;
				if (FitToWidthActive())
				{
					var target = UsableFitWidth(availableSize);
					if (target > 0)
					{
						foreach (UIElement child in InternalChildren)
						{
							var img = GetImage(child);
							if (img != null) img.RefreshSize(target);
						}
					}
				}
			}

			if (!relayout) return MeasureSize;
			
			{
				TempElements.Clear();

				ElementLocations.Clear();
				InvalidateRowCache();

				var currentLineHeight = 0.0;
				var currentLineWidth = 0.0;
				var totalWidth = 0.0;
				var totalHeight = 0.0; 

				foreach(UIElement child in InternalChildren)
				{
					child.Measure(infiniteSize);

					// WPF measure/arrange passes are asynchronous: after UpdateSize()
					// changes a child's explicit Width/Height, some children may not
					// have been re-measured yet and still report a STALE DesiredSize.
					// Wrapping/positioning with those stale (larger) widths made big
					// images overlap after using the scale slider. Re-measure any
					// child whose DesiredSize no longer matches its explicit size so
					// the line math always uses the SCALED sizes.
					// NOTE: for ListViewItem containers the explicit size lives on
					// the LazyImage inside them - comparing against the container's
					// NaN Width/Height would trigger an endless second Measure() of
					// an already-valid subtree (a large per-scroll CPU cost).
					// CRITICAL: compare against the IMAGE's rendered DesiredSize, not its
					// raw explicit Width/Height. ListViewItem containers add item chrome
					// (default Padding 16px left/right + border), so container.DesiredSize =
					// image size + padding and the old comparison could NEVER match: every
					// child got measured twice per pass (CPU waste) and stale DesiredSizes
					// slipped through into the row math (~20px position errors).
					var img = GetImage(child);
					if (img != null)
					{
						// Re-measure the image ONLY when its explicit size and its
						// DesiredSize disagree. Measuring unconditionally was a
						// regression: Image.MeasureOverride re-decodes/refreshes the
						// bitmap source, so doing it for every child on every measure
						// pass froze large folders ("book view broke") and burned CPU
						// against our laziness goal.
						if (!img.Width.Equals(img.DesiredSize.Width) ||
						    !img.Height.Equals(img.DesiredSize.Height))
							img.Measure(infiniteSize);
						if (!child.DesiredSize.Width.Equals(img.DesiredSize.Width) ||
						    !child.DesiredSize.Height.Equals(img.DesiredSize.Height))
							child.Measure(infiniteSize);
					}

					// Wrap only when the child would actually extend past the row and
					// only if the row already contains something. The old ">=" test
					// wrapped elements that exactly filled the row (they were placed
					// at x=0 while their slot said otherwise -> overlaps), and wrapping
					// a lone oversized element desynchronized measure from arrange.
					if (TempElements.Count > 0 && currentLineWidth + child.DesiredSize.Width > availableSize.Width)
					{
						currentLineWidth = 0;
						foreach (var t in TempElements) ElementLocations.Add(new Tuple<Rect, UIElement>(new Rect(t.Item1.X, totalHeight, t.Item1.Width, currentLineHeight), t.Item2));
						totalHeight += currentLineHeight; 

						currentLineHeight = 0;
						TempElements.Clear();
						
					} 
 
					TempElements.Add(new Tuple<Rect, UIElement>(new Rect(currentLineWidth, 0, child.DesiredSize.Width, 0), child));

					currentLineWidth += child.DesiredSize.Width;
					currentLineHeight = Math.Max(currentLineHeight, child.DesiredSize.Height);
					totalWidth = Math.Max(currentLineWidth, totalWidth);

				}

				foreach (var t in TempElements) ElementLocations.Add(new Tuple<Rect, UIElement>(new Rect(t.Item1.X, totalHeight, t.Item1.Width, currentLineHeight), t.Item2));

				totalHeight += currentLineHeight;
				
				MeasureSize = new Size(double.IsPositiveInfinity(availableSize.Width) ? totalWidth : availableSize.Width, double.IsPositiveInfinity(availableSize.Height) ? totalHeight : availableSize.Height);
				_invalidatedMeasure = false;
				
				
			}

			return MeasureSize;

		}
 


		bool FitToWidthActive()
		{
			var mw = MainWindow.Current;
			return mw != null && mw.IsFitToWidthEnabled;
		}

		static double UsableFitWidth(Size availableSize)
		{
			if (double.IsPositiveInfinity(availableSize.Width) || availableSize.Width < 10) return 0;
			return availableSize.Width * FitToWidthFraction;
		}

		protected Size HorizontalWrapPanelArrangeOverrideProc(Size finalSize)
		{
			if (!_invalidatedArrange) return finalSize;

			// Arrange EVERY child at the rectangle computed during measure.
			// Previously only VisibleUIElements were arranged, which left
			// off-screen children sitting at their OLD rectangles; when they
			// later scrolled into view they rendered on top of the newly
			// laid-out rows ("scaling randomly sorts it out a little").
			// Arranging all measured locations guarantees non-overlapping
			// slots for the entire list.
			// PERF: collect the locations ONCE (a single tree traversal) and
			// arrange directly from them - no per-child dictionary scans. The
			// location rects already carry the exact sizes captured during
			// measure, so we arrange with those dimensions instead of
			// re-reading DesiredSize (which can be stale mid-propagation and
			// caused the transient overlaps).
			// NOTE: EnumeratorRange must stay null here so the traversal visits
			// ALL elements (the scroll handler leaves it set to the visible band).
			// NOTE: multiple elements can share a row and therefore an identical
			// Y key - the tree stores those in one node's duplicate list, so
			// CompleteValueIteration must be on or only one image per row would
			// be arranged and the rest would keep their old rectangles (overlap!).
			ElementLocations.EnumeratorRange = null;
			ElementLocations.Traversal.TraversalMethod = TreeCollection.TraversalEnumeratorMethod.In;
			ElementLocations.Traversal.TraversalDir = TreeCollection.TraversalEnumeratorDir.Normal;
			ElementLocations.Traversal.CompleteValueIteration = true;
			ElementLocations.Traversal.Root = ElementLocations.Root;

			ArrangeList.Clear();
			ElementLocations.Traversal.Traverse((Tuple<Rect, UIElement> loc) =>
			{
				ArrangeList.Add(loc);
				return true;
			});

			foreach (var loc in ArrangeList)
			{
				var child = loc.Item2;
				if (child != null)
					child.Arrange(new Rect(loc.Item1.X, loc.Item1.Y, loc.Item1.Width, loc.Item1.Height));
			}

			_invalidatedArrange = false;
			return finalSize; 
		}

 

	}
 
 



}

