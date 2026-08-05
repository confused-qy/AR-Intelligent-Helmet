using System;
using System.Collections.Generic;

namespace MotorcycleNavigation
{
    internal sealed class MinHeap<T>
    {
        private readonly List<T> items = new List<T>();
        private readonly Comparison<T> comparison;

        public MinHeap(Comparison<T> comparison)
        {
            this.comparison = comparison;
        }

        public int Count
        {
            get { return items.Count; }
        }

        public void Clear()
        {
            items.Clear();
        }

        public void Push(T item)
        {
            items.Add(item);
            SiftUp(items.Count - 1);
        }

        public T Pop()
        {
            T root = items[0];
            int last = items.Count - 1;
            items[0] = items[last];
            items.RemoveAt(last);
            if (items.Count > 0)
                SiftDown(0);
            return root;
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (comparison(items[index], items[parent]) >= 0)
                    break;
                Swap(index, parent);
                index = parent;
            }
        }

        private void SiftDown(int index)
        {
            while (true)
            {
                int left = index * 2 + 1;
                int right = left + 1;
                int smallest = index;

                if (left < items.Count && comparison(items[left], items[smallest]) < 0)
                    smallest = left;
                if (right < items.Count && comparison(items[right], items[smallest]) < 0)
                    smallest = right;

                if (smallest == index)
                    break;
                Swap(index, smallest);
                index = smallest;
            }
        }

        private void Swap(int a, int b)
        {
            T temp = items[a];
            items[a] = items[b];
            items[b] = temp;
        }
    }
}
