using System;
using System.Windows.Media;

namespace ImageViewer
{
	/// <summary>
	/// What is drawn behind transparent images (icons, PNGs with alpha).
	/// Stored in Settings.TransparencyBackground as "None", "Checker",
	/// "Background" (follows the image-area background color, so transparent
	/// images blend into it) or a color ("#FF8B0000"); the older "Light" /
	/// "Dark" values still work. The toolbar swatch button (and B / Shift+B)
	/// steps through Choices().
	/// </summary>
	internal static class Backdrop
	{
		public struct Choice
		{
			public string Value, Name;
			public Choice(string value, string name) { Value = value; Name = name; }
		}

		public const string SameAsBackground = "Background";

		static readonly Choice[] Fixed =
		{
			new Choice("Checker", "Checkerboard"),
			new Choice("#FF000000", "Black"),
			new Choice("#FFFFFFFF", "White"),
			new Choice("#FF8B0000", "Dark red"),
			new Choice("#FF006400", "Dark green"),
			new Choice("#FF00008B", "Dark blue"),
		};

		/// <summary>The image-area background color (MainWindow.ApplyBackgroundColor keeps it current).</summary>
		public static Color BackgroundColor = Colors.Black;

		static readonly Brush Checker = CreateChecker(8);
		static readonly Brush SwatchChecker = CreateChecker(4);

		/// <summary>
		/// The cycle: checkerboard, the image-area background ("Same as
		/// background") unless one of the fixed colors already is that color -
		/// then that entry is labeled as such - and the fixed colors.
		/// </summary>
		public static Choice[] Choices()
		{
			var list = new System.Collections.Generic.List<Choice>(Fixed.Length + 1);
			int same = FixedIndexOf(BackgroundColor);
			for (int i = 0; i < Fixed.Length; i++)
			{
				var c = Fixed[i];
				list.Add(i == same ? new Choice(c.Value, c.Name + " (same as background)") : c);
				if (i == 0 && same < 0) list.Add(new Choice(SameAsBackground, "Same as background"));
			}
			return list.ToArray();
		}

		static int FixedIndexOf(Color color)
		{
			for (int i = 0; i < Fixed.Length; i++)
			{
				Color c;
				if (TryColor(Fixed[i].Value, out c) && c == color) return i;
			}
			return -1;
		}

		static bool TryColor(string value, out Color color)
		{
			color = default(Color);
			if (string.IsNullOrEmpty(value) || value[0] != '#') return false;
			try { color = (Color)ColorConverter.ConvertFromString(value); return true; }
			catch (FormatException) { return false; }
		}

		static string Normalize(string value)
		{
			if (string.IsNullOrEmpty(value)) return "Checker";
			if (value == "Light") return "#FFF2F2F2";
			if (value == "Dark") return "#FF2A2A2A";
			return value;
		}

		/// <summary>Brush for a setting value; null = draw nothing.</summary>
		public static Brush BrushFor(string value, bool swatch = false)
		{
			value = Normalize(value);
			if (value == "None") return null;
			if (value == "Checker") return swatch ? SwatchChecker : Checker;
			Color color;
			if (value == SameAsBackground) color = BackgroundColor;
			else if (!TryColor(value, out color)) return swatch ? SwatchChecker : Checker;
			var b = new SolidColorBrush(color);
			b.Freeze();
			return b;
		}

		public static string NameOf(string value)
		{
			value = Normalize(value);
			if (value == "None") return "Nothing";
			int i = IndexIn(Choices(), value);
			if (i >= 0) return Choices()[i].Name;
			if (value == "#FFF2F2F2") return "Light gray";
			if (value == "#FF2A2A2A") return "Dark gray";
			return value;
		}

		// Position of a value in the cycle. "Same as background" while the
		// background equals a fixed color is that color's entry, and vice versa.
		static int IndexIn(Choice[] choices, string value)
		{
			int i = Array.FindIndex(choices, c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
			if (i >= 0) return i;
			Color color;
			if (value == SameAsBackground) color = BackgroundColor;
			else if (!TryColor(value, out color)) return -1;
			if (color == BackgroundColor)
			{
				i = Array.FindIndex(choices, c => c.Value == SameAsBackground);
				if (i >= 0) return i;
			}
			int f = FixedIndexOf(color);
			return f < 0 ? -1 : Array.FindIndex(choices, c => c.Value == Fixed[f].Value);
		}

		/// <summary>The next (step = +1) or previous (-1) entry of the cycle.</summary>
		public static string Step(string value, int step)
		{
			var choices = Choices();
			int i = IndexIn(choices, Normalize(value));
			if (i < 0) return choices[0].Value;   // "None" or another color: start over at checkerboard
			int n = choices.Length;
			return choices[((i + step) % n + n) % n].Value;
		}

		static Brush CreateChecker(double cell)
		{
			var light = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
			var dark = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
			light.Freeze(); dark.Freeze();
			var g = new DrawingGroup();
			g.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new System.Windows.Rect(0, 0, cell * 2, cell * 2))));
			g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new System.Windows.Rect(0, 0, cell, cell))));
			g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new System.Windows.Rect(cell, cell, cell, cell))));
			var b = new DrawingBrush(g)
			{
				TileMode = TileMode.Tile,
				Viewport = new System.Windows.Rect(0, 0, cell * 2, cell * 2),
				ViewportUnits = BrushMappingMode.Absolute,
			};
			b.Freeze();
			return b;
		}
	}
}
