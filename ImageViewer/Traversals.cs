using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics.Contracts;

namespace TreeCollection
{

	public enum TraversalEnumeratorMethod { Pre = 0, In, Post, Breadth }
	public enum TraversalEnumeratorType { Recursive = 0, Iterative }
	public enum TraversalEnumeratorDir { Normal = 0, Reverse }
	public enum TraversalEnumeratorInclusion { Inclusive = 0, Exclusive, Left, Right, LeftNeighboor, RightNeighboor, Neighboors }
 
	public class Traversals<TValue, TKey, TValueCollection> : IEnumerator<TValue>
		where TKey : IComparable<TKey>
		where TValueCollection : class, ICollection<TValue>, new()
	{

		ITreeCollection<TValue, TKey, TValueCollection> Tree;
		INode<TValue> _root = null;
		public INode<TValue> Root { get { if (_root == null) return Tree.Root; return _root; } set { _root = value; } }
		public TraversalEnumeratorMethod TraversalMethod { get; set; }
		public TraversalEnumeratorType TraversalType { get; set; }
		public TraversalEnumeratorDir TraversalDir { get; set; }
		public bool CompleteValueIteration { get; set; }


		private Func<INode<TValue>, Func<INode<TValue>, bool>, bool> TraversalSelector()
		{
			switch (TraversalMethod)
			{
				default:
					switch (TraversalType)
					{
						default:
							return PreOrder;
							break;
						case TraversalEnumeratorType.Iterative:
							return PreOrder_I;
							break;
					}
					break;
				case TraversalEnumeratorMethod.In:
					switch (TraversalType)
					{
						default:
							return InOrder;
							break;
					}
					break;
				case TraversalEnumeratorMethod.Post:
					switch (TraversalType)
					{
						default:
							return PostOrder;
							break;
					}
					break;
				case TraversalEnumeratorMethod.Breadth:
					switch (TraversalType)
					{
						default:
							return BreadthFirst;
							break;
					}
					break;

			}

		}

		public void TraverseNode(Func<INode<TValue>, bool> nodeCallback) { TraverseNode(Root, nodeCallback); } 
		public void TraverseNode(INode<TValue> root, Func<INode<TValue>, bool> nodeCallback) { TraversalSelector()(root, nodeCallback); }

		public void Traverse(Func<TValue, bool> valueCallback) { Traverse(Root, valueCallback); } 
		public void Traverse(INode<TValue> root, Func<TValue, bool> valueCallback)
		{
			Func<INode<TValue>, bool> callback = node =>
			{
				if (node.Values == null) return valueCallback(node.Value);

				if (!valueCallback(node.Value)) return false;

				bool flag = true;
				foreach (var v in node.Values)
				{
					flag &= valueCallback(v);
					if (CompleteValueIteration == false && flag == false) return false;
				}

				return flag;
			};

			TraversalSelector()(root, callback);

		} 

		public void Traverse(Func<TValue, INode<TValue>, bool> valueCallback) { Traverse(Root, valueCallback); }

		public void Traverse(INode<TValue> root, Func<TValue, INode<TValue>, bool> valueCallback)
		{
			Func<INode<TValue>, bool> callback = node =>
			{
				if (node.Values == null) return valueCallback(node.Value, node);

				if (!valueCallback(node.Value, node)) return false;

				bool flag = true;
				foreach (var v in node.Values)
				{
					flag &= valueCallback(v, node);
					if (CompleteValueIteration == false && flag == false) return false;
				}

				return flag;
			};

			TraversalSelector()(root, callback);

		}

		public bool BreadthFirst(INode<TValue> node, Func<INode<TValue>, bool> nodeCallback)
		{
			if (node == null) return false;
			var queue = new Queue<INode<TValue>>();
			//int height = 0;

			queue.Clear();
			if (Tree.EnumeratorRange == null || Tree.ItemKey == null)
			{
				
				queue.Enqueue(node);
				queue.Enqueue(null);
				
				while (queue.Count != 0)
				{
					node = queue.Dequeue();
					if (node == null)	// End of row signifier
					{
						//height++;
						queue.Enqueue(null);
						node = queue.Dequeue();
						if (node == null || queue.Count == 0) break;
					}

					if (nodeCallback(node) == false) break;
					
					if (node.MaxNumNodes > 2)
					{
						foreach(INode<TValue> n in node)
							if (n != null) queue.Enqueue(n);					
					} else
					{
						if (node.Left != null) queue.Enqueue(node.Left);
						if (node.Right != null) queue.Enqueue(node.Right);
					}
				}
			} else
			{
				while (!((Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) && (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)))
				{
					if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) < 0)
						node = node.Right;
					else if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) > 0)
						node = node.Left;
					if (node == null) return false;
					//height++;
				}

					queue.Enqueue(node);
					queue.Enqueue(null);
				
					while (queue.Count != 0)
					{
					node = queue.Dequeue();
					if (node == null)	// End of row signifier
					{
						//height++;
						queue.Enqueue(null);
						node = queue.Dequeue();
						if (node == null || queue.Count == 0) break;
					}

					if ((Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) && (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) && nodeCallback(node) == false) break;

					if (node.MaxNumNodes > 1)
					{
						foreach (INode<TValue> n in node)
						{
							if (n != null && ((Tree.ItemKey(n.Value).CompareTo(Tree.ItemKey(node.Value)) <= 0 && Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) || (Tree.ItemKey(n.Value).CompareTo(Tree.ItemKey(node.Value)) >= 0 && Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)))
								queue.Enqueue(n);
						}
					}
					else
					{
						if (node.Left != null && (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0)) queue.Enqueue(node.Left);
						if (node.Right != null && (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)) queue.Enqueue(node.Right);
					}
					
				
				}

			}

			return false;
		}

		public bool PreOrder(INode<TValue> node, Func<INode<TValue>, bool> nodeCallback)
		{
			if (node == null) return true;
			if (Tree.EnumeratorRange == null)
			{
				if (!nodeCallback(node)) return false;
				if (!PreOrder(node.Left, nodeCallback)) return false;
				if (!PreOrder(node.Right, nodeCallback)) return false;
			} else
			{
				if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0 && !nodeCallback(node)) return false;
				if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) if (!PreOrder(node.Left, nodeCallback)) return false;
				if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) if (!PreOrder(node.Right, nodeCallback)) return false;
			}

			return true;
		}

		public bool InOrder(INode<TValue> node, Func<INode<TValue>, bool> nodeCallback)
		{
			if (node == null) return true;
			if (Tree.EnumeratorRange == null)
			{
				var nl = (TraversalDir == TraversalEnumeratorDir.Reverse) ? node.Right : node.Left;
				var nr = (TraversalDir == TraversalEnumeratorDir.Reverse) ? node.Left : node.Right;
				if (!InOrder(nl, nodeCallback)) return false;
				if (!nodeCallback(node)) return false;
				if (!InOrder(nr, nodeCallback)) return false;
			} else
			{
				if (TraversalDir == TraversalEnumeratorDir.Normal)
				{ 
					if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) if (!InOrder(node.Left, nodeCallback)) return false;
					if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0 && !nodeCallback(node)) return false;
					if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) if (!InOrder(node.Right, nodeCallback)) return false;
				} else
				{
					if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) if (!InOrder(node.Right, nodeCallback)) return false;
					if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0 && Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && !nodeCallback(node)) return false;
					if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) if (!InOrder(node.Left, nodeCallback)) return false;
				} 
			}

			return true;
		}

		public bool PostOrder(INode<TValue> node, Func<INode<TValue>, bool> nodeCallback)
		{
			if (node == null) return true;
			if (Tree.EnumeratorRange == null)
			{
				if (!PostOrder(node.Left, nodeCallback)) return false;
				if (!nodeCallback(node)) return false;
				if (!PostOrder(node.Right, nodeCallback)) return false;
			} else
			{
				if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) if (!PostOrder(node.Left, nodeCallback)) return false;
				if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) if (!PostOrder(node.Right, nodeCallback)) return false;
				if (Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(node.Value).CompareTo(Tree.EnumeratorRange.High) <= 0 && !nodeCallback(node)) return false;
			}

			return true;
		}

		public bool PreOrder_I(INode<TValue> node, Func<INode<TValue>, bool> nodeCallback)
		{

			int count = 0;
			INode<TValue> current = null;
			if ((count++) >= Tree.NodeCount) return false;

			if (Tree.EnumeratorRange == null)
			{
				if (current == null) { current = node; nodeCallback(current); }
				Top:
				if (current.Left != null) { current = current.Left; nodeCallback(current); goto Top; }
				if (current.Right != null) { current = current.Right; nodeCallback(current); goto Top; }
				do
				{
					if (current == node) return false;
					if ((current == current.Parent.Left) && (current.Parent.Right != null)) { current = current.Parent.Right; nodeCallback(current); goto Top; }
					current = current.Parent;
				} while (current != null);
				return false;
			} else
			{
				if (current == null)
				{
					current = node;

					while (current != null)
					{
						if (Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) > 0)
							current = current.Left;
						if (current == null) return false;
						else
						{
							if (Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.Low) < 0)
								current = current.Right;
							if (current == null) return false;
						}
						if (Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
							break;
					}
					nodeCallback(current);
				}

			Top:
				if (current.Left != null && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0)
				{
					current = current.Left;
					if (Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
					{
						nodeCallback(current);
						goto Top;
					}
					while (current.Left != null && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) > 0)
						current = current.Left;
					if (Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
					{
						nodeCallback(current);
						goto Top;
					}
				}
				if (current.Right != null && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
				{
					current = current.Right;
					if (Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
					{
						nodeCallback(current);
						goto Top;
					}
					while (current.Right != null && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.Low) < 0)
						current = current.Right;
					if (Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
					{
						nodeCallback(current);
						goto Top;
					}

				}

				do
				{
					if (current == node) return false;
					if ((current == current.Parent.Left) && (current.Parent.Right != null))
					{
						current = current.Parent.Right;
						if (Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
						{
							nodeCallback(current);
							goto Top;
						}
						goto Top;
					}
					if (Tree.ItemKey(current.Value).CompareTo(Tree.EnumeratorRange.High) >= 0 && Tree.ItemKey(current.Parent.Value).CompareTo(Tree.EnumeratorRange.High) >= 0)
						return false;
					current = current.Parent;
				} while (current != null);
				return false;

			}

		}



		public Traversals(ITreeCollection<TValue, TKey, TValueCollection> Tree)
		{
			this.Tree = Tree;
			this.Root = Tree.Root;
			CompleteValueIteration = true;
			TraversalMethod = TraversalEnumeratorMethod.In;
		}




			


			int _count = 0;
			TKey MKey = default(TKey);
			INode<TValue> _current;
			INode<TValue> _previous;

			TValue IEnumerator<TValue>.Current { get { return _current.Value; } }
			public object Current { get { if (_current == null) return null; return _current.Value; } }
			public void Reset() { _count = 0; _current = default(INode<TValue>); }
			Stack<INode<TValue>> _stack = new Stack<INode<TValue>>();

			public bool MoveNext()
			{
				if (Root == null || Tree.NodeCount == 0) return false;

				switch (TraversalMethod)
				{
					case TraversalEnumeratorMethod.Pre: return PreOrderMove();
					//case TraversalEnumerators.InOrder: return InOrderMove();
					//case TraversalEnumerators.PostOrder: return PostOrderMove();
					default: return PreOrderMove();
				}
			}

			#region PreOrder

			/// <summary>
			/// Stack based PreOrder tree traversal
			/// </summary>
			/// <returns></returns>
			public bool PreOrderMove()
			{
				if (Tree.EnumeratorRange == null)
				{
					if (_current == null) { _stack.Clear(); _stack.Push(Root); }
					if (_stack.Count <= 0 || _count++ >= Tree.NodeCount) return false;
					_current = _stack.Pop();

					if (_current == null) return false;
					if (_current.Right != null) _stack.Push(_current.Right);
					if (_current.Left != null) _stack.Push(_current.Left);
					return true;
				} else
				{
					if (_current == null) { _count = 0; _stack.Clear(); _stack.Push(Root); }
					
					while(_count++ < Tree.NodeCount)
					{
						if (_stack.Count <= 0 || _count++ >= Tree.NodeCount) return false;
						_current = _stack.Pop();
						if (_current == null) return false;

						if (_current.Right != null && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) _stack.Push(_current.Right);					
						if (_current.Left != null && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) _stack.Push(_current.Left);
						if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
							return true;					
					}
				}
				return false;
			}

			/// <summary>
			/// Iterative PreOrder tree traversal
			/// </summary>
			/// <returns></returns>
			public bool PreOrderMove_I()
			{

				if ((_count++) >= Tree.NodeCount) return false;

				if (Tree.EnumeratorRange == null)
				{
					if (_current == null) { _current = Root; return true; }

					if (_current.Left != null) { _current = _current.Left; return true; }
					if (_current.Right != null) { _current = _current.Right; return true; }
					do
					{
						if (_current == Root) return false;
						if ((_current == _current.Parent.Left) && (_current.Parent.Right != null)) { _current = _current.Parent.Right; return true; }
						_current = _current.Parent;
					} while (_current != null);
					return true;
				} else
				{

					if (_current == null) 
					{ 
						_current = Root;

						while (_current != null)
						{
							if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) > 0)
								_current = _current.Left;
							if (_current == null) return false;
							else
							{
								if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) < 0)
									_current = _current.Right;
								if (_current == null) return false;
							}
							if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
								break;
						}
						return true; 
					}

					Top:
					if (_current.Left != null && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) 
					{
						_current = _current.Left; 
						if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) 
							return true; 
						while (_current.Left != null && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) > 0)
							_current = _current.Left;
						if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) 
							return true;
							
					}
					if (_current.Right != null && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) 
					{
						_current = _current.Right; 
						if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) 
							return true;
						while (_current.Right != null && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) < 0)
							_current = _current.Right;
						if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
							return true;
					}

					do
					{
						if (_current == Root) return false;
						if ((_current == _current.Parent.Left) && (_current.Parent.Right != null)) 
						{ 
							_current = _current.Parent.Right;
							if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)
								return true;
							goto Top;						
						}
						if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) >= 0 && Tree.ItemKey(_current.Parent.Value).CompareTo(Tree.EnumeratorRange.High) >= 0)
							return false;
						_current = _current.Parent;
					} while (_current != null);
					return false;
		
				}
			}

			#endregion PreOrder
			/*
			#region InOrder
			


			public bool InOrderMove()
			{
				if (Tree.EnumeratorRange == null)
				{

					if (_current == null) 
					{ 
						_count = 0;
						_stack.Clear(); 
						_stack.Push(Root);
					}
					if (_stack.Count <= 0 || ++_count > Tree.NodeCount) return false;

					_previous = _current;
					_current = _stack.Pop();

					while (_current.Left != null && (_previous == null || (_previous.Left != null || _previous.Right != null)) && _current.Left != _previous)
					{
						_stack.Push(_current);
						_current = _current.Left;
					}

					if (_current.Right != null)
						_stack.Push(_current.Right);

				
					return true;
					if (_current == null) 
					{ 
						_stack.Clear(); 
						if (Root == null) return false;
						if (Root.Right != null) _stack.Push(Root.Right); 
						_current = Root;
						while (_current.Left != null)
						{
							_stack.Push(_current);
							_current = _current.Left;
						}
						return true;
					} 
					if (_stack.Count <= 0 || ++_count >= Tree.NodeCount) return false;


					_previous = _current;
					_current = _stack.Pop();				
					if (_previous.Right != null) 
					{ 
						_stack.Push(_current); 
						_current = _previous.Right; 
						while (_current.Left != null)
						{
							_stack.Push(_current);
							_current = _current.Left;
						}
					}
					
					if (_current == null) return false;
					return true;

				} else
				{
					// Same code as above except must setup the stack up to the lowest valid node and ALSO break when passed high. This is because of the *in order* traversal.
					if (_current == null)
					{
						_stack.Clear();
						if (Root == null) return false;
						_stack.Push(Root.Right);
						_current = Root;
						while (_current.Left != null && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0)
						{
							if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) < 0) continue;
							if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0) 
								_stack.Push(_current);
							_current = _current.Left;
						}
						if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0)
							return true;
						else
							if (_current.Right != null && (Tree.ItemKey(_current.Right.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0) && (Tree.ItemKey(_current.Right.Value).CompareTo(Tree.EnumeratorRange.High) <= 0)) 
							{
								_current = _current.Right;
								return true;
							}
							else _current = _stack.Pop();
					}
					if (_stack.Count <= 0 || ++_count >= Tree.NodeCount) return false;


					_previous = _current;
					_current = _stack.Pop();
					if (_previous.Right != null)
					{
						_stack.Push(_current);
						_current = _previous.Right;
						while (_current.Left != null && Tree.ItemKey(_current.Left.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0)
						{
							_stack.Push(_current);
							_current = _current.Left;
						}
					}

					if (_current == null || Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) > 0) return false;
					return true;

				}
			}


			public bool InOrderMove_I()
			{
				if ((++_count) >= Tree.NodeCount) return false;

				if (Tree.EnumeratorRange == null)
				{
					if (_current == null) { _current = Tree.MinNode(); return true; }
					MKey = Tree.ItemKey(_current.Value);
					if ((_current.Right != null) && (MKey.CompareTo(Tree.ItemKey(_current.Right.Value)) < 0)) { _current = Tree.MinSubNode(_current.Right); return true; }
					while (MKey.CompareTo(Tree.ItemKey(_current.Parent.Value)) > 0) _current = _current.Parent;
					_current = _current.Parent;
					return true;
				} else
				{
					if (_current == null) 
					{ 
						_current = Root;
						
						_current = Tree.MinSubNode(Root);
						MKey = Tree.ItemKey(_current.Value);
					}

					
					while (_current.Left != null && Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0 && MKey.CompareTo(Tree.ItemKey(_current.Value)) > 0)
						_current = _current.Left;
					while (Tree.ItemKey(_current.Parent.Value).CompareTo(Tree.EnumeratorRange.Low) <= 0) 
					{
						_current = _current.Parent;
						MKey = Tree.ItemKey(_current.Value);
					}
					if (Tree.ItemKey(_current.Value).CompareTo(Tree.EnumeratorRange.High) <= 0 &&  MKey.CompareTo(Tree.ItemKey(_current.Value)) >= 0)
					{
						MKey = Tree.ItemKey(_current.Value);
						return true;
					}
					if (_current.Right != null) _current = _current.Right;


					return true;

					MKey = Tree.ItemKey(_current.Value);
					if ((_current.Right != null) && (MKey.CompareTo(Tree.ItemKey(_current.Right.Value)) < 0)) 
					{ 
						_current = _current.Right;
						while (_current.Left != null)						
							if (Tree.ItemKey(_current.Left.Value).CompareTo(Tree.EnumeratorRange.Low) >= 0)
								_current = _current.Left;
						return true; 
					}
					while (MKey.CompareTo(Tree.ItemKey(_current.Parent.Value)) > 0) _current = _current.Parent;
					_current = _current.Parent;

				}
				return true;
			}

			#endregion InOrder

			
			#region PostOrder

			public bool PostOrderMove()
			{

				return true;
			}



			public bool PostOrderMove_I()
			{
				if (_current == null) { _current = Root; do { _current = Tree.MinSubNode(_current); while (_current.Right != null) _current = Tree.MinSubNode(_current.Right); } while (_current.Right != null); return true; }
				if ((++_count) >= Tree.NodeCount) return false;

				if ((_current != Root) && (_current == _current.Parent.Right)) { _current = _current.Parent; return true; }
				_current = _current.Parent;
				if (_current == null) return false;
				while (_current.Right != null) _current = Tree.MinSubNode(_current.Right);

				return true;
			}

			#endregion PostOrder
*/

			public void Dispose() { }
	

	}
	
}
