using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections;

namespace TreeCollection
{

	public class KeyInterval<TKey> { public TKey Low; public TKey High; public KeyInterval(TKey Low, TKey High) { this.Low = Low; this.High = High; } public override string ToString() { return Low.ToString() + ", " + High.ToString(); } }

	public interface INode<TValue> : IEnumerable<INode<TValue>>
	{
		TValue Value { get; set; }
		ICollection<TValue> Values { get; set; }		
		bool IsValueCollection { get; }
		int MaxNumValues { get; set; }
		int MinNumValues { get; set; }
		TValue MinValue();
		TValue MaxValue();
		bool ForEach(Func<TValue, bool> Iterator);



		ICollection<INode<TValue>> Nodes { get; set; }		
		bool IsNodeCollection { get; }
		int MaxNumNodes { get; set; }
		int MinNumNodes { get; set; }
		INode<TValue> MaxNode();
		INode<TValue> MinNode();
		INode<TValue> Left { get; set; }
		INode<TValue> Right { get; set; }
		INode<TValue> Parent { get; set; }
		
		bool IsColored { get; set; }
		int Balance { get; set; }

	}

	

		



	/// <summary>
	/// A Tree Like Collection
	/// </summary>
	/// <typeparam name="TValue">Value Type</typeparam>
	/// <typeparam name="TKey">Key Type</typeparam>
	/// <typeparam name="TNode">Node Type</typeparam>
	public interface ITreeCollection<TValue, TKey, TValueCollection> : ICollection<TValue>
		where TKey : IComparable<TKey>
		where TValueCollection : class, ICollection<TValue>, new()
	{
		
		INode<TValue> Root { get; }
		Func<TValue, TKey> ItemKey { get; set; }
		KeyInterval<TKey> EnumeratorRange { get; set; }
		Traversals<TValue, TKey, TValueCollection> Traversal { get; set; }

		INode<TValue> Predecessor(INode<TValue> Value);
		INode<TValue> Successor(INode<TValue> Value);		
		TValue Predecessor(TValue Value);
		TValue Successor(TValue Value);
		TValueCollection Predecessors(TValue Value);
		TValueCollection Successors(TValue Value);

		int NodeCount { get; }
		int ValueCount { get; }
	}

	#region Abstract Classes

	/// <summary>
	/// An Abstract Implementation of IEnumerableNode for simple direct enumeration.
	/// </summary>
	/// <typeparam name="TNode">Node Type</typeparam>
	public abstract class ANodeEnumerator<TValue> : IEnumerator<INode<TValue>>
	{
		protected int _count = 0;
		protected INode<TValue> _start = null;
		public INode<TValue> Current { get; protected set; }
		public void Dispose() { }
		public void Reset() { Current = null; _count = 0; }
		object IEnumerator.Current { get { return Current; } }
		public abstract bool MoveNext();
	}

	/// <summary>
	/// A Default Binary Node Enumerator(Wraps Left and Right with an Enumerator).
	/// </summary>
	/// <typeparam name="TNode">Node Type</typeparam>
	public class BinaryNodeEnumerator<TValue> : ANodeEnumerator<TValue>
	{ 

		

		public override bool MoveNext()
		{ 
			if (_count++ > 2) return false;
			if (_count == 1)
				Current = _start.Left;
			else
				Current = _start.Right;
			_count++;
			return true;
		}

		public BinaryNodeEnumerator(INode<TValue> start)
		{
			_start = start;
		}
	}

	#endregion Abstract Classes

}
