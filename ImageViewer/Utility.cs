using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;

namespace Utility
{


	public static class Rand
	{
		private const int BufferSize = 1024;
		private static byte[] RandomBuffer;
		private static int BufferOffset;
		private static RNGCryptoServiceProvider rng;
		static Rand()
		{
			var c = new CspParameters();
			c.Flags = CspProviderFlags.CreateEphemeralKey;

			RandomBuffer = new byte[BufferSize];
			rng = new RNGCryptoServiceProvider(c);
			BufferOffset = RandomBuffer.Length;
		}

		private static void FillBuffer() { rng.GetBytes(RandomBuffer); BufferOffset = 0; }

		public static int Next()
		{
			if (BufferOffset >= RandomBuffer.Length) FillBuffer();
			int val = BitConverter.ToInt32(RandomBuffer, BufferOffset) & 0x7fffffff;
			BufferOffset += sizeof(int);
			return val;
		}

		public static int Next(int maxValue) { return Next() % maxValue; }
		public static int Next(int minValue, int maxValue) { return (maxValue < minValue) ? minValue : (minValue + Next(maxValue - minValue)); }
		public static double NextDouble() { int val = Next(); return (double)val / int.MaxValue; }
		public static double NextDouble(double minValue, double maxValue) { return minValue + (maxValue - minValue)*NextDouble(); }
		public static void GetBytes(byte[] buff) { rng.GetBytes(buff); }

		public static int[] Next(int minValue, int maxValue, int number)
		{
			var i = new int[number];
			for (int j = 0; j < number; j++) i[j] = Next(minValue, maxValue);
			return i;
		}

		public static double[] NextDouble(double minValue, double maxValue, int number)
		{
			var i = new double[number];
			for (int j = 0; j < number; j++) i[j] = NextDouble(minValue, maxValue);
			return i;
		}

		public static T RandEnum<T>(T e)
		{
			T[] vals = (T[])Enum.GetValues(typeof(T));
			return vals[Next(0, vals.Length)];
		}
	}

	public static class VisualHelper
	{
		/// <summary>
		/// Finds a parent of a given item on the visual tree.
		/// </summary>
		/// <typeparam name="T">The type of the queried item.</typeparam>
		/// <param name="child">A direct or indirect child of the
		/// queried item.</param>
		/// <returns>The first parent item that matches the submitted
		/// type parameter. If not matching item can be found, a null
		/// reference is being returned.</returns>
		public static T TryFindParent<T>(this DependencyObject child)
			where T : DependencyObject
		{
			//get parent item
			DependencyObject parentObject = GetParentObject(child);

			//we've reached the end of the tree
			if (parentObject == null) return null;

			//check if the parent matches the type we're looking for
			T parent = parentObject as T;
			if (parent != null)
			{
				return parent;
			} else
			{
				//use recursion to proceed with next level
				return TryFindParent<T>(parentObject);
			}
		}

		/// <summary>
		/// This method is an alternative to WPF's
		/// <see cref="VisualTreeHelper.GetParent"/> method, which also
		/// supports content elements. Keep in mind that for content element,
		/// this method falls back to the logical tree of the element!
		/// </summary>
		/// <param name="child">The item to be processed.</param>
		/// <returns>The submitted item's parent, if available. Otherwise
		/// null.</returns>
		public static DependencyObject GetParentObject(this DependencyObject child)
		{
			if (child == null) return null;

			//handle content elements separately
			ContentElement contentElement = child as ContentElement;
			if (contentElement != null)
			{
				DependencyObject parent = ContentOperations.GetParent(contentElement);
				if (parent != null) return parent;

				FrameworkContentElement fce = contentElement as FrameworkContentElement;
				return fce != null ? fce.Parent : null;
			}

			//also try searching for parent in framework elements (such as DockPanel, etc)
			FrameworkElement frameworkElement = child as FrameworkElement;
			if (frameworkElement != null)
			{
				DependencyObject parent = frameworkElement.Parent;
				if (parent != null) return parent;
			}

			//if it's not a ContentElement/FrameworkElement, rely on VisualTreeHelper
			return VisualTreeHelper.GetParent(child);
		}
	}
}
