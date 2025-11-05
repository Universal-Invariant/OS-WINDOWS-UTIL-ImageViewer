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



		public TreeCollection.ITreeCollection<Tuple<Rect, UIElement>, double, List<Tuple<Rect, UIElement>>> ElementLocations = new TreeCollection.AVLTree<Tuple<Rect, UIElement>, double>();

		public static SmartFlowPanel Current = null;
		public SmartFlowPanel() { Type = FlowPanelType.HorizontalWrapPanel; ElementLocations.ItemKey = (t) => { return t.Item1.Y; }; Current = this; }

		public void InvalidateLocations() { _invalidatedMeasure = true; _invalidatedArrange = true; InvalidateMeasure(); InvalidateVisual(); } bool _invalidatedMeasure = true; bool _invalidatedArrange = true;


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

			// Scan over all visible images
			ElementLocations.EnumeratorRange = new TreeCollection.KeyInterval<double>(e.VerticalOffset, e.VerticalOffset + height);
			ElementLocations.Traversal.TraversalMethod = TreeCollection.TraversalEnumeratorMethod.In;
			ElementLocations.Traversal.TraversalDir = TreeCollection.TraversalEnumeratorDir.Normal;
			ElementLocations.Traversal.Traverse((t, n) =>
			{
				var image = ((t.Item2 as ListViewItem).Content as LazyImage); if (image == null) return true;

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
				var image = ((t.Item2 as ListViewItem).Content as LazyImage); if (image == null) return true;

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

				var image = (t.Item2 as ListViewItem).Content as LazyImage;
				if (image == null) { VisibleUIElements.RemoveAt(i); continue; }
				if (t.Item1.Y + t.Item1.Height < e.VerticalOffset || e.VerticalOffset + height < t.Item1.Y)
				{
					image.UnLoadImage();
					VisibleUIElements.RemoveAt(i);
					continue;
				}

			}


			InvalidateLocations();


			return;
		}

		Size MeasureSize;
		Size infiniteSize = new Size(double.PositiveInfinity, double.PositiveInfinity);
		List<Tuple<Rect, UIElement>> TempElements = new List<Tuple<Rect,UIElement>>(50);
		protected Size HorizontalWrapPanelMeasureOverrideProc(Size availableSize)
		{
			if (!_invalidatedMeasure) return MeasureSize;
			if (ScrollOwner == null) ScrollOwner = VisualHelper.TryFindParent<ScrollViewer>(this);
			
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
 
					if (currentLineWidth + child.DesiredSize.Width >= availableSize.Width)
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
 


		protected Size HorizontalWrapPanelArrangeOverrideProc(Size finalSize)
		{
			if (!_invalidatedArrange) return finalSize;

			foreach(var t in VisibleUIElements) t.Item2.Arrange(new Rect(t.Item1.X, t.Item1.Y, t.Item2.DesiredSize.Width, t.Item2.DesiredSize.Height)); 

			_invalidatedArrange = false;
			return finalSize; 
		}

 

	}
 
 



}

