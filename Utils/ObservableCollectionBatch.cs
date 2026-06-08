using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TRPServerPanel.Utils
{
    /// <summary>
    /// ObservableCollection with support for Batch updates (AddRange) to prevent UI flooding.
    /// </summary>
    public class ObservableCollectionBatch<T> : ObservableCollection<T>
    {
        private bool _suppressNotification = false;

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!_suppressNotification)
                base.OnCollectionChanged(e);
        }

        public void AddRange(IEnumerable<T> list)
        {
            if (list == null) return;

            _suppressNotification = true;
            foreach (var item in list)
            {
                Add(item);
            }
            _suppressNotification = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void RemoveRange(int index, int count)
        {
            _suppressNotification = true;
            for (int i = 0; i < count; i++)
            {
                if (Count > index)
                    RemoveAt(index);
            }
            _suppressNotification = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
