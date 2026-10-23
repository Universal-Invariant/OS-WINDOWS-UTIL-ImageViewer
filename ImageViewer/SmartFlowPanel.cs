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

		public void InvalidateLocations() { _invalidatedMeasure = true; _invalidatedArrange = true; InvalidateMeasure(); InvalidateVisual(); } bool _invalidatedMeasure = true; bool _invalidatedArrange = true;


	// --- Row lookup for "scroll by image" ------------------------
	// ElementLocations holds one entry per laid-out element keyed by its
	// row's Y, so these are O(log n) tree descents - no traversal, no
	// visual-tree walking, nothing that disturbs lazy loading.

	/// <summary>
	/// The topmost row whose TOP edge is at or below y (the next row that
	/// can be brought to the viewport top). Null when none exists.
	/// </summary>
	public Rect? GetFirstRowBelow(double y)
	{
		var node = ElementLocations.FindNode(y, ElementLocations.Root);
		if (node == null) return null;   // empty tree

		int c = ElementLocations.ItemKey(node.Value).CompareTo(y);
		if (c >= 0)
		{
			// Node starts at/after y: the leftmost descendant is the smallest such key.
			while (node.Left != null) node = node.Left;
		} else
		{
			// Node starts above y; the answer is the smallest key in its right subtree.
			node = MinSubNodePublic(node.Right);
		}
		if (node == null) return null;
		return FirstRectOf(node);
	}

	/// <summary>
	/// The bottom-most row whose TOP edge is strictly above y (the row
	/// currently cut off at the viewport top, or just before it).
	/// Null when none exists.
	/// </summary>
	public Rect? GetLastRowAbove(double y)
	{
		var node = ElementLocations.FindNode(y, ElementLocations.Root);
		if (node == null) return null;   // empty tree

		int c = ElementLocations.ItemKey(node.Value).CompareTo(y);
		if (c < 0)
		{
			// This node qualifies; keep going right for a larger (lower) key.
			while (node.Right != null) node = node.Right;
		} else
		{
			// Node starts at/after y; the answer is the largest key in its left subtree.
			node = MaxSubNodePublic(node.Left);
		}
		if (node == null) return null;
		return FirstRectOf(node);
	}

	static TreeCollection.INode<Tuple<Rect, UIElement>> MinSubNodePublic(TreeCollection.INode<Tuple<Rect, UIElement>> node)
	{
		if (node == null) return null;
		while (node.Left != null) node = node.Left;
		return node;
	}

	static TreeCollection.INode<Tuple<Rect, UIElement>> MaxSubNodePublic(TreeCollection.INode<Tuple<Rect, UIElement>> node)
	{
		if (node == null) return null;
		while (node.Right != null) node = node.Right;
		return node;
	}

	// A tree node groups all elements of one row (same Y key); the shared
	// rect dimensions live on every entry, so any one suffices.
	static Rect? FirstRectOf(TreeCollection.INode<Tuple<Rect, UIElement>> node)
	{
		var v = node.Value;
		if (v != null) return v.Item1;
		var list = node.Values;
		if (list != null) { foreach (var t in list) return t.Item1; }
		return null;
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
				if (FitToWidthActive())
				{
					var target = UsableFitWidth(availableSize);
					if (target > 0)
					{
						relayout = true;
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
					var img = GetImage(child);
					if (img != null && (!child.DesiredSize.Width.Equals(img.Width) || !child.DesiredSize.Height.Equals(img.Height)))
						child.Measure(infiniteSize);

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

