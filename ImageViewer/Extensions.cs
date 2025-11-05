using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;


namespace ImageViewer
{
	public static class RectExtension
	{
		public static Rect Clip(this Rect rect1, Rect rect) { return default(Rect); }
		public static Rect XY(this Rect rect, double X2, double Y2)
		{
			rect.Width = X2 - rect.X;
			rect.Height = Y2 - rect.Y;
			return rect;
		}

		public static string Format(this Rect r)
		{
			return "(" + String.Format("{0,4:#0}", r.X) + ", " + String.Format("{0,4:#0}", r.Y) + ", " + String.Format("{0,4:#0}", r.Width) + ", " + String.Format("{0,4:#0}", r.Height) + ")";
		}

	}

	public static class PointExtension
	{
		public static Point Ofs(this Point point, double X, double Y)
		{
			return new Point(point.X + X, point.Y + Y);
		}

		public static string Format(this Point p)
		{
			return "(" + String.Format("{0:#0}", p.X) + ", " + String.Format("{0:#0}", p.Y) + ")";
		}

	
	}

	public static class DoubleExtension
	{
		public static string Format(this double p)
		{
			return String.Format("{0:#0.##0}", p);
		}
	}

	public static class ColorExtension
	{
		public static Color RGBA(this Color color, byte r, byte g, byte b, byte a)
		{
			color.A = a;
			color.R = r;
			color.G = g;
			color.B = b;
			return color;
		}

		public static Color RGB(this Color color, byte r, byte g, byte b)
		{
			color.A = 255;
			color.R = r;
			color.G = g;
			color.B = b;
			return color;
		}

		
	}

	public static class SolidColorBrushExtension
	{
		public static SolidColorBrush New(this SolidColorBrush brush, byte r, byte g, byte b, byte a)
		{
			Color c = new Color();
			c = c.RGBA(r, g, b, a);

			return new SolidColorBrush(c);
		}
	}

}
