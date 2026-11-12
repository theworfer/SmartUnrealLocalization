using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// An <see cref="ObservableCollection{T}"/> that can be refilled with ONE
    /// change notification instead of one per item.
    ///
    /// <para><b>Why.</b> The grid's row list is rebuilt from scratch on every
    /// filter, sort, search-scope toggle and file switch. Doing that as
    /// <c>Clear()</c> followed by N <c>Add()</c> calls raises N+1
    /// CollectionChanged events, and the DataGrid does real work on each one —
    /// on a 13 000-row file that is 13 001 layout passes for a single click, long
    /// enough to look like the app has hung.</para>
    ///
    /// <para><see cref="ReplaceAll"/> mutates the backing list directly and then
    /// raises a single Reset, which is exactly what a wholesale replacement is.
    /// Consumers that track individual items (there are none here — the grid
    /// re-reads the list) would need the granular events instead.</para>
    /// </summary>
    public sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);

            // Count and the indexer both changed; WPF/Avalonia bindings listen for
            // these two property names by convention, and omitting them leaves
            // stale "N items" readouts behind after a rebuild.
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
