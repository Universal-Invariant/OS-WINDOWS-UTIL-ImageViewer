using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics.Contracts;

namespace TreeCollection
{

	#region Node Definition




	public class AVLNode<TValue> : INode<TValue>
	{

		public override string ToString() { string s = ""; if (Parent != null && Parent.Value != null) s += Parent.Value.ToString() + " - " ; else s += "rt - "; if (Value != null) { s += Value.ToString() +	" {"; if (Left != null && Left.Value != null) s += Left.Value.ToString(); s += ", "; if (Right != null && Right.Value != null) s += Right.Value.ToString() + "}"; } return s; }

		#region INode<TValue> Members

		public TValue Value { get; set; }
		public ICollection<TValue> Values { get; set; }
		public bool IsValueCollection { get { return true; } }
		public int MaxNumValues	{ get { return 1; } set { } }
		public int MinNumValues { get { return 1; } set { } }
		public TValue MinValue() { return Value; }
		public TValue MaxValue() { return Value; }

		public ICollection<INode<TValue>> Nodes { get; set; }
		public bool IsNodeCollection { get { return true; } }
		public int MaxNumNodes { get { return 2; } set { } }
		public int MinNumNodes { get { return 0; } set { } }
		public INode<TValue> MaxNode() { return Right; }
		public INode<TValue> MinNode() { return Left; }
		public INode<TValue> Left { get; set; }
		public INode<TValue> Right { get; set; }
		public INode<TValue> Parent { get; set; }

		public bool IsColored { get { return false; } set { } }
		public int Balance { get; set; }

		public bool ForEach(Func<TValue, bool> Iterator)
		{ 
			if (!Iterator(Value)) return false;
			if (Values == null) return true;
			foreach(var v in Values) if (!Iterator(v)) return false;
			
			return true; 
		}

		#endregion

		#region IEnumerable<INode<TValue>> Members

		public IEnumerator<INode<TValue>> GetEnumerator()
		{
			return new BinaryNodeEnumerator<TValue>(this);
		}

		#endregion

		#region IEnumerable Members

		IEnumerator IEnumerable.GetEnumerator()
		{
			return new BinaryNodeEnumerator<TValue>(this);
		}
	
		#endregion
	}

	#endregion Node Definition






	public class AVLTree<TValue, TKey, TValueCollection> : ITreeCollection<TValue, TKey, TValueCollection>
		where TKey : IComparable<TKey>
		where TValueCollection : class, ICollection<TValue>, new()
	{

		public Func<TValue, TKey> ItemKey { get; set; }
		public Func<TValue, TValue, TValue> ItemCollision { get; set; }

		public KeyInterval<TKey> EnumeratorRange { get; set; }
		public Traversals<TValue, TKey, TValueCollection> Traversal { get; set; }

		public INode<TValue> Root { get; set; }
		int _count;
		public int Count { get { return _count; } protected set { _count = value; } }
		public int ValueCount { get { return _count; } protected set { _count = value; } }
		public int NodeCount { get; protected set; }
		

		protected INode<TValue> NewNode(TValue Value) { var n = new AVLNode<TValue>(); n.Value = Value; return n; }
		protected INode<TValue> NewNode(TValue Value, INode<TValue> Parent) { var n = NewNode(Value); n.Parent = Parent; return n; }
		
		protected INode<TValue> Find(TValue Value)
		{
			var node = Root;

			while (node != null)
			{
				if (ItemKey(Value).CompareTo(ItemKey(node.Value)) < 0)
				{
					if (node.Left == null) break;
					node = node.Left;
				} else if (ItemKey(Value).CompareTo(ItemKey(node.Value)) > 0)
				{
					if (node.Right == null) break;
					node = node.Right;
				} else return node;
			}

			return node;
		}

		public INode<TValue> Predecessor(INode<TValue> node)
		{
			if (node.Left != null) return node.Left;
			return node.Parent;
		}

		public INode<TValue> Successor(INode<TValue> node)
		{
			if (node.Right != null) return node.Right;
			return node.Parent;
		}

		public TValue Predecessor(TValue Value)
		{
			return Predecessor(Find(Value)).Value;
		}
		
		public TValue Successor(TValue Value)
		{
			return Successor(Find(Value)).Value;
		}

		public TValueCollection Predecessors(TValue Value)
		{
			return (TValueCollection)Predecessor(Find(Value)).Values;
		}

		public TValueCollection Successors(TValue Value)
		{
			return (TValueCollection)Successor(Find(Value)).Values;
		}

		public void Add(TValue value)
		{
			if (ItemKey == null) throw new Exception("ITreeCollection: ItemKey cannot be null!");
			TKey key = ItemKey(value);
			if (key == null) throw new ArgumentNullException();

			INode<TValue> p = this.Root;

			if (p == null)
			{
				this.Root = NewNode(value);
				NodeCount++;
				this.ValueCount++;
			}
			else
			{
				while (true)
				{
					int c = key.CompareTo(ItemKey(p.Value));

					if (c < 0)
					{
						if (p.Left != null)
							p = p.Left;
						else
						{
							p.Left = NewNode(value, p);							
							p.Balance--;
							NodeCount++;
							ValueCount++;
							break;
						}
					} else if (c > 0)
					{
						if (p.Right != null)
							p = p.Right;
						else
						{
							p.Right = NewNode(value, p);
							p.Balance++;
							NodeCount++;
							ValueCount++;
							break;
						}
					} else 
					{
						// itemkey already exists so add to value collection or issue collision
						if (p.IsValueCollection)
						{
							if (p.Values == null) 
							{
								p.Values = new TValueCollection();
								ValueCount--; // Since if Values == null we have one value(stored in Value) and we don't want it counted twice
							}
														
							if (!p.Values.Contains(value))
							{
								p.Values.Add(value);
								ValueCount++;
							}
						} else
						{
							if (p.Value.Equals(value) && ItemCollision != null)
								p.Value = ItemCollision(p.Value, value);
						}
											

						return; // No need to rebalance since we are not changing the node(just the values)
					}
				}

				#region Balancing

				while ((p.Balance != 0) && (p.Parent != null))
				{
					if (p.Parent.Left == p)
						p.Parent.Balance--;
					else
						p.Parent.Balance++;

					p = p.Parent;

					if (p.Balance == -2)
					{
						INode<TValue> x = p.Left;

						if (x.Balance == -1)
						{
							x.Parent = p.Parent;

							if (p.Parent == null)
								this.Root = x;
							else
								if (p.Parent.Left == p)
									p.Parent.Left = x;
								else
									p.Parent.Right = x;

							p.Left = x.Right;

							if (p.Left != null) p.Left.Parent = p;

							x.Right = p;
							p.Parent = x;

							x.Balance = 0;
							p.Balance = 0;
						} else
						{
							INode<TValue> w = x.Right;

							w.Parent = p.Parent;

							if (p.Parent == null)
								this.Root = w;
							else
								if (p.Parent.Left == p)
									p.Parent.Left = w;
								else
									p.Parent.Right = w;

							x.Right = w.Left;

							if (x.Right != null) x.Right.Parent = x;

							p.Left = w.Right;

							if (p.Left != null) p.Left.Parent = p;

							w.Left = x;
							w.Right = p;

							x.Parent = w;
							p.Parent = w;

							if (w.Balance == -1)
							{
								x.Balance = 0;
								p.Balance = 1;
							} else if (w.Balance == 0)
							{
								x.Balance = 0;
								p.Balance = 0;
							} else // w.Balance == 1
							{
								x.Balance = -1;
								p.Balance = 0;
							}

							w.Balance = 0;
						}

						break;
					} else if (p.Balance == 2)
					{
						INode<TValue> x = p.Right;

						if (x.Balance == 1)
						{
							x.Parent = p.Parent;

							if (p.Parent == null) this.Root = x;
							else
								if (p.Parent.Left == p) p.Parent.Left = x;
								else
									p.Parent.Right = x;
							
							p.Right = x.Left;

							if (p.Right != null)
							{
								p.Right.Parent = p;
							}

							x.Left = p;
							p.Parent = x;

							x.Balance = 0;
							p.Balance = 0;
						} else
						{
							INode<TValue> w = x.Left;

							w.Parent = p.Parent;

							if (p.Parent == null)
							{
								this.Root = w;
							} else
							{
								if (p.Parent.Left == p)
								{
									p.Parent.Left = w;
								} else
								{
									p.Parent.Right = w;
								}
							}

							x.Left = w.Right;

							if (x.Left != null)
							{
								x.Left.Parent = x;
							}

							p.Right = w.Left;

							if (p.Right != null)
							{
								p.Right.Parent = p;
							}

							w.Right = x;
							w.Left = p;

							x.Parent = w;
							p.Parent = w;

							if (w.Balance == 1)
							{
								x.Balance = 0;
								p.Balance = -1;
							} else if (w.Balance == 0)
							{
								x.Balance = 0;
								p.Balance = 0;
							} else // w.Balance == -1
							{
								x.Balance = 1;
								p.Balance = 0;
							}

							w.Balance = 0;
						}

						break;
					}
				}
			}
			#endregion Balancing

			return;
		}

		public bool Remove(TValue value)
		{
			TKey key = ItemKey(value);
			if (key == null) throw new ArgumentNullException();

			INode<TValue> p = this.Root;

			while (p != null)
			{
				int c = key.CompareTo(ItemKey(p.Value));

				if (c < 0)
				{
					p = p.Left;
				} else if (c > 0)
				{
					p = p.Right;
				} else
				{
					INode<TValue> y; // node from which rebalancing begins
					if (p.Values != null)
					{
						if (!p.Values.Remove(value)) return false;
						ValueCount--;
						if (p.Value.Equals(value))
						{							
							IEnumerator<TValue> e = p.Values.GetEnumerator();
							e.Reset(); e.MoveNext();
							p.Value = e.Current;
						}
						
						if (p.Values.Count == 1) p.Values = null;
						goto Done;
					} else 
					{
						if (!p.Value.Equals(value)) return false;
						NodeCount--;
						ValueCount--;
					}

					#region Balancing

					if (p.Right == null)	// Case 1: p has no right child
					{
						if (p.Left != null)
						{
							p.Left.Parent = p.Parent;
						}

						if (p.Parent == null)
						{
							this.Root = p.Left;

							goto Done;
						}

						if (p == p.Parent.Left)
						{
							p.Parent.Left = p.Left;

							y = p.Parent;
							// goto LeftDelete;
						} else
						{
							p.Parent.Right = p.Left;

							y = p.Parent;
							goto RightDelete;
						}
					} else if (p.Right.Left == null)	// Case 2: p's right child has no left child
					{
						if (p.Left != null)
						{
							p.Left.Parent = p.Right;
							p.Right.Left = p.Left;
						}

						p.Right.Balance = p.Balance;
						p.Right.Parent = p.Parent;

						if (p.Parent == null)
						{
							this.Root = p.Right;
						} else
						{
							if (p == p.Parent.Left)
							{
								p.Parent.Left = p.Right;
							} else
							{
								p.Parent.Right = p.Right;
							}
						}

						y = p.Right;

						goto RightDelete;
					} else	// Case 3: p's right child has a left child
					{
						INode<TValue> s = p.Right.Left;

						while (s.Left != null)
						{
							s = s.Left;
						}

						if (p.Left != null)
						{
							p.Left.Parent = s;
							s.Left = p.Left;
						}

						s.Parent.Left = s.Right;

						if (s.Right != null)
						{
							s.Right.Parent = s.Parent;
						}

						p.Right.Parent = s;
						s.Right = p.Right;

						y = s.Parent; // for rebalacing, must be set before we change s.Parent

						s.Balance = p.Balance;
						s.Parent = p.Parent;

						if (p.Parent == null)
						{
							this.Root = s;
						} else
						{
							if (p == p.Parent.Left)
							{
								p.Parent.Left = s;
							} else
							{
								p.Parent.Right = s;
							}
						}

						// goto LeftDelete;
					}

					// rebalancing begins

					LeftDelete:

					y.Balance++;

					if (y.Balance == 1)
					{
						goto Done;
					} else if (y.Balance == 2)
					{
						INode<TValue> x = y.Right;

						if (x.Balance == -1)
						{
							INode<TValue> w = x.Left;

							w.Parent = y.Parent;

							if (y.Parent == null)
							{
								this.Root = w;
							} else
							{
								if (y.Parent.Left == y)
								{
									y.Parent.Left = w;
								} else
								{
									y.Parent.Right = w;
								}
							}

							x.Left = w.Right;

							if (x.Left != null)
							{
								x.Left.Parent = x;
							}

							y.Right = w.Left;

							if (y.Right != null)
							{
								y.Right.Parent = y;
							}

							w.Right = x;
							w.Left = y;

							x.Parent = w;
							y.Parent = w;

							if (w.Balance == 1)
							{
								x.Balance = 0;
								y.Balance = -1;
							} else if (w.Balance == 0)
							{
								x.Balance = 0;
								y.Balance = 0;
							} else // w.Balance == -1
							{
								x.Balance = 1;
								y.Balance = 0;
							}

							w.Balance = 0;

							y = w; // for next iteration
						} else
						{
							x.Parent = y.Parent;

							if (y.Parent != null)
							{
								if (y.Parent.Left == y)
								{
									y.Parent.Left = x;
								} else
								{
									y.Parent.Right = x;
								}
							} else
							{
								this.Root = x;
							}

							y.Right = x.Left;

							if (y.Right != null)
							{
								y.Right.Parent = y;
							}

							x.Left = y;
							y.Parent = x;

							if (x.Balance == 0)
							{
								x.Balance = -1;
								y.Balance = 1;

								goto Done;
							} else
							{
								x.Balance = 0;
								y.Balance = 0;

								y = x; // for next iteration
							}
						}
					}

					goto LoopTest;


				RightDelete:

					y.Balance--;

					if (y.Balance == -1)
					{
						goto Done;
					} else if (y.Balance == -2)
					{
						INode<TValue> x = y.Left;

						if (x.Balance == 1)
						{
							INode<TValue> w = x.Right;

							w.Parent = y.Parent;

							if (y.Parent == null)
							{
								this.Root = w;
							} else
							{
								if (y.Parent.Left == y)
								{
									y.Parent.Left = w;
								} else
								{
									y.Parent.Right = w;
								}
							}

							x.Right = w.Left;

							if (x.Right != null)
							{
								x.Right.Parent = x;
							}

							y.Left = w.Right;

							if (y.Left != null)
							{
								y.Left.Parent = y;
							}

							w.Left = x;
							w.Right = y;

							x.Parent = w;
							y.Parent = w;

							if (w.Balance == -1)
							{
								x.Balance = 0;
								y.Balance = 1;
							} else if (w.Balance == 0)
							{
								x.Balance = 0;
								y.Balance = 0;
							} else // w.Balance == 1
							{
								x.Balance = -1;
								y.Balance = 0;
							}

							w.Balance = 0;

							y = w; // for next iteration
						} else
						{
							x.Parent = y.Parent;

							if (y.Parent != null)
							{
								if (y.Parent.Left == y)
								{
									y.Parent.Left = x;
								} else
								{
									y.Parent.Right = x;
								}
							} else
							{
								this.Root = x;
							}

							y.Left = x.Right;

							if (y.Left != null)
							{
								y.Left.Parent = y;
							}

							x.Right = y;
							y.Parent = x;

							if (x.Balance == 0)
							{
								x.Balance = 1;
								y.Balance = -1;

								goto Done;
							} else
							{
								x.Balance = 0;
								y.Balance = 0;

								y = x; // for next iteration
							}
						}
					}

				LoopTest:

					if (y.Parent != null)
					{
						if (y == y.Parent.Left)
						{
							y = y.Parent;
							goto LeftDelete;
						}

						y = y.Parent;
						goto RightDelete;
					}

					#endregion Balancing

				Done:
					return true;
				}
			}

			return false;
		}

		public bool RemoveKey(TKey key)
		{
			if (key == null) return false;

			INode<TValue> p = this.Root;

			while (p != null)
			{
				int c = key.CompareTo(ItemKey(p.Value));

				if (c < 0) p = p.Left;
					else if (c > 0) p = p.Right;
						else
						{
							ValueCount -= p.Values.Count;
							ValueCount++; // Node Value is not empty and don't want to double subtract
							p.Values = null;
							
							Remove(p.Value);
							return true;
						}
			}

			return false;
		}

		public TValueCollection GetValues(TKey key)
		{
			var col = new TValueCollection();
			if (key == null) return col;

			INode<TValue> p = this.Root;

			while (p != null)
			{
				int c = key.CompareTo(ItemKey(p.Value));

				if (c < 0) p = p.Left;
				else if (c > 0) p = p.Right;
				else
				{
								
					if (p.Values == null)
					{
						col.Add(p.Value);
						return col;
					}

					foreach(var v in p.Values)
						col.Add(v);

					return col;
					
				}
			}

			return col;
		}

		public void Clear()
		{
			this.Root = null;
			this.Count	= 0;
		}

		public void CopyTo(TValue[] array, int index)
		{
			if (array == null) throw new ArgumentNullException();
			if ((index < 0) || (index >= array.Length))	throw new ArgumentOutOfRangeException();
			if ((array.Length - index) < this.Count) throw new ArgumentException();

			Traversal.TraverseNode(this.Root, n => 
			{ 
				if (n.Values == null)
				{
					array[index++] = n.Value;
				}
				else
				{
					n.Values.CopyTo(array, index);
					index += n.Values.Count;
				}
				return true; 
			} );

		
		}

		public bool Contains(TValue value)
		{
			var node = Root;

			while (node != null)
			{
				int c = ItemKey(node.Value).CompareTo(ItemKey(value));
				if (c > 0) 
				{ 
					if (node.Left == null) return false;
					node = node.Left; 
				} else 
				{
					if (c == 0) return (node.Values == null) ? node.Value.Equals(value) : node.Values.Contains(value);
					if (node.Right == null) return false;
					node = node.Right; 
				}
				
			}
			
			return false;
		}

		public bool IsReadOnly { get; set; }


		#region Helper Functions

		public INode<TValue> FindNode(TKey Key, INode<TValue> node)
		{
			while (node != null)
			{
				int c = ItemKey(node.Value).CompareTo(Key);
				if (c == 0) break;
				if (c > 0) { if (node.Left == null) break; node = node.Left; continue; } 
				else { if (node.Right == null) break; node = node.Right; continue; }
			}
			return node;
		}


		protected INode<TValue> MinSubNode(INode<TValue> node) { if (node == null) return null; while (node.Left != null) node = node.Left; return node; }
		protected INode<TValue> MaxSubNode(INode<TValue> node) { if (node == null) return null; while (node.Right != null) node = node.Right; return node; }
		protected INode<TValue> MinAboveSubNode(INode<TValue> node) { return MinSubNode(node.Right); }
		protected INode<TValue> MaxBelowSubNode(INode<TValue> node) { return MaxSubNode(node.Left); }
		protected INode<TValue> MinNode() { return MinSubNode(Root); }
		protected INode<TValue> MaxNode() { return MaxSubNode(Root); }

		protected INode<TValue> FindNode(TKey Key) { return FindNode(Key, Root); }
		public TValue Find(TKey Key) { if (FindNode(Key) == null) return default(TValue); return FindNode(Key).Value; }


		protected INode<TValue> MaxBelowNode(TKey Key) { return MaxSubNode(FindNode(Key).Right); }
		protected INode<TValue> MinAboveNode(TKey Key) { return MinSubNode(FindNode(Key).Left); }
		public TValue MaxBelow(TKey Key) { return MaxBelowNode(Key).Value; }
		public TValue MinAbove(TKey Key) { return MinAboveNode(Key).Value; }
		public TValue Min() { return MinSubNode(Root).Value; }
		public TValue Max() { return MaxSubNode(Root).Value; }

		#endregion Helper Functions


		#region IEnumerator/Tree Traversal



		#region GetEnumerators

		/// <summary>
		/// Enumerates the tree using the DefaultTreeEnumeratorType.
		/// </summary>
		/// <returns></returns>
		public IEnumerator GetEnumerator() { return Traversal; }
		
		/// <summary>
		/// Enumerates the tree using the TreeEnumerators type.
		/// </summary>
		/// <param name="type"></param> Specifies which type of TreeEnumerator to use.
		/// <returns></returns>
		public IEnumerator GetEnumerator(TraversalEnumeratorMethod type) { return Traversal; }

		/// <summary>
		/// Enumerators the tree using the DefaultTreeEnumeratorType.
		/// </summary>
		/// <returns></returns>
		IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()	{ return this.GetEnumerator() as IEnumerator<TValue>; }

		#endregion GetEnumerators

		#endregion IEnumerator/Tree Traversal


		public AVLTree()
		{
			this.Traversal = new Traversals<TValue,TKey, TValueCollection>(this);
		}
	}



	/// <summary>
	/// An int and AVLNode based key AVL Tree Collection.
	/// </summary>
	/// <typeparam name="TValue"></typeparam>Value's Type
	public class AVLTree<TValue> : AVLTree<TValue, int, List<TValue>> {  }

	/// <summary>
	/// An int and AVLNode based key AVL Tree Collection.
	/// </summary>
	/// <typeparam name="TValue"></typeparam>Value's Type
	public class AVLTree<TValue, TKey> : AVLTree<TValue, TKey, List<TValue>> where TKey : IComparable<TKey> { }


}
