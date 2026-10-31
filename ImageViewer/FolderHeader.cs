using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageViewer
{
	/// <summary>
	/// A full-width row naming the subfolder the following images come from
	/// (shown when subfolders are included and "Group by folder" is on).
	/// Clicking it opens that folder.
	/// </summary>
	public class FolderHeader : FrameworkElement, IGridItem
	{
		public const double RowHeight = 34;

		public readonly string FolderPath;
		readonly string _title;
		int _count;
		FormattedText _text, _countText;

		static Brush _textBrush = Brushes.Gainsboro, _dimBrush = Brushes.Gray, _lineBrush = Brushes.DimGray;
		static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
		static readonly Typeface CountFace = new Typeface("Segoe UI");

		public FolderHeader(string folderPath, string title)
		{
			FolderPath = folderPath;
			_title = title;
			Cursor = Cursors.Hand;
			ToolTip = "Open " + folderPath;
		}

		public int Count
		{
			get { return _count; }
			set { if (_count == value) return; _count = value; _countText = null; InvalidateVisual(); }
		}

		public double LayoutWidth { get { return 0; } }   // full row: the panel gives it the whole width
		public double LayoutHeight { get { return RowHeight; } }
		public bool IsFullRow { get { return true; } }

		/// <summary>Colors follow the image-area background.</summary>
		public static void SetBackdrop(Color background)
		{
			double luma = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B;
			bool dark = luma < 140;
			_textBrush = Frozen(new SolidColorBrush(dark ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x20, 0x20, 0x20)));
			_dimBrush = Frozen(new SolidColorBrush(dark ? Color.FromRgb(0x9A, 0x9A, 0x9A) : Color.FromRgb(0x60, 0x60, 0x60)));
			_lineBrush = Frozen(new SolidColorBrush(dark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x40, 0, 0, 0)));
		}

		static Brush Frozen(Brush b) { b.Freeze(); return b; }

		public void RefreshVisual() { _text = null; _countText = null; InvalidateVisual(); }

		protected override Size MeasureOverride(Size availableSize)
		{
			return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, RowHeight);
		}

		protected override void OnRender(DrawingContext dc)
		{
			var size = RenderSize;
			dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
#pragma warning disable CS0618
			if (_text == null)
				_text = new FormattedText(_title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 13, _textBrush, LazyImage.DpiScale)
				{ MaxTextWidth = Math.Max(1, size.Width - 120), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
			if (_countText == null)
				_countText = new FormattedText(_count == 1 ? "1 image" : _count.ToString("N0") + " images", CultureInfo.CurrentUICulture,
					FlowDirection.LeftToRight, CountFace, 12, _dimBrush, LazyImage.DpiScale);
#pragma warning restore CS0618
			double y = size.Height - 10 - _text.Height;
			dc.DrawText(_text, new Point(10, y));
			dc.DrawText(_countText, new Point(10 + _text.WidthIncludingTrailingWhitespace + 10, y + 1));
			dc.DrawRectangle(_lineBrush, null, new Rect(10, size.Height - 5, Math.Max(0, size.Width - 20), 1));
		}

		protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
		{
			base.OnMouseLeftButtonUp(e);
			if (MainWindow.Current != null) MainWindow.Current.OpenFolderFromHeader(FolderPath);
			e.Handled = true;
		}
	}
}
