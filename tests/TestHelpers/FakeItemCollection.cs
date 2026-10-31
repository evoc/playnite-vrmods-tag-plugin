using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteVRMods.Tests.TestHelpers
{
    public class FakeBufferedUpdate : IDisposable
    {
        public void Dispose() { }
    }

    public class FakeItemCollection<T> : IItemCollection<T> where T : DatabaseObject
    {
        private readonly List<T> _items = new();
        private readonly Dictionary<string, T> _byName = new(StringComparer.OrdinalIgnoreCase);

        public T this[Guid id]
        {
            get => _items.FirstOrDefault(i => i.Id == id);
            set
            {
                var existing = _items.FirstOrDefault(i => i.Id == id);
                if (existing != null)
                {
                    var idx = _items.IndexOf(existing);
                    _items[idx] = value;
                    if (!string.IsNullOrEmpty(value.Name))
                        _byName[value.Name] = value;
                }
                else
                {
                    _items.Add(value);
                    if (!string.IsNullOrEmpty(value.Name))
                        _byName[value.Name] = value;
                }
            }
        }

        public List<T> Items => _items;

        public T Get(Guid id) => _items.FirstOrDefault(i => i.Id == id);

        public List<T> Get(IList<Guid> ids) => _items.Where(i => ids.Contains(i.Id)).ToList();

        public T Add(string itemName)
        {
            _byName.TryGetValue(itemName, out var existing);
            if (existing != null)
                return existing;

            var item = (T)Activator.CreateInstance(typeof(T), itemName)!;
            _items.Add(item);
            _byName[itemName] = item;
            return item;
        }

        public T Add(string itemName, Func<T, string, bool> existingComparer)
        {
            var existing = _items.FirstOrDefault(i => existingComparer(i, itemName));
            if (existing != null)
                return existing;

            var item = (T)Activator.CreateInstance(typeof(T), itemName)!;
            _items.Add(item);
            return item;
        }

        public IEnumerable<T> Add(List<string> items) => items.Select(Add);

        public T Add(MetadataProperty property) => throw new NotImplementedException();
        public IEnumerable<T> Add(IEnumerable<MetadataProperty> properties) => throw new NotImplementedException();
        public IEnumerable<T> Add(List<string> items, Func<T, string, bool> existingComparer) => throw new NotImplementedException();
        public void Add(IEnumerable<T> items) { foreach (var item in items) _items.Add(item); }
        public void Add(T item) => _items.Add(item);
        public bool Remove(Guid id) { var item = Get(id); if (item != null) { _items.Remove(item); return true; } return false; }
        public bool Remove(IEnumerable<T> items) { var removed = false; foreach (var item in items) if (Remove(item.Id)) removed = true; return removed; }
        public bool Remove(T item) => _items.Remove(item);
        public void Update(T item) { var idx = _items.FindIndex(i => i.Id == item.Id); if (idx >= 0) _items[idx] = item; }
        public void Update(IEnumerable<T> items) { foreach (var item in items) Update(item); }
        public void BeginBufferUpdate() { }
        public void EndBufferUpdate() { }
        public IDisposable BufferedUpdate() => new FakeBufferedUpdate();
        public IEnumerable<T> GetClone() => _items.ToList();

        public event EventHandler<ItemCollectionChangedEventArgs<T>>? ItemCollectionChanged;
        public event EventHandler<ItemUpdatedEventArgs<T>>? ItemUpdated;

        /// <summary>
        /// Raises ItemCollectionChanged the way Playnite does when a game is added to or removed
        /// from the library. Test-only: FakeItemCollection's own Add/Remove stay silent so tests
        /// decide when a change is reported.
        /// </summary>
        public void RaiseItemCollectionChanged(IEnumerable<T>? addedItems = null, IEnumerable<T>? removedItems = null)
        {
            ItemCollectionChanged?.Invoke(this, new ItemCollectionChangedEventArgs<T>(
                addedItems?.ToList() ?? new List<T>(),
                removedItems?.ToList() ?? new List<T>()));
        }

        /// <summary>
        /// Raises ItemUpdated the way Playnite does on a database write: OldData is the row read back
        /// from storage before the save, NewData the row that was just saved. That pairing is what
        /// lets a listener tell "the user renamed this game" from "the user changed its playtime".
        /// Test-only, for the same reason as above.
        /// </summary>
        public void RaiseItemUpdated(IEnumerable<(T OldData, T NewData)> updates)
        {
            ItemUpdated?.Invoke(this, new ItemUpdatedEventArgs<T>(
                updates.Select(u => new ItemUpdateEvent<T>(u.OldData, u.NewData)).ToList()));
        }

        public bool ContainsItem(Guid id) => _items.Any(i => i.Id == id);
        public GameDatabaseCollection CollectionType => GameDatabaseCollection.Uknown;
        public void Dispose() { }
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public new bool Equals(object obj) => _items.Equals(obj);
        public new int GetHashCode() => _items.GetHashCode();
        /// <summary>
        /// Optional per-row pause for the enumerator, used by the threading tests. Playnite's real
        /// collection walks a LiteDB result set, so enumeration has a duration this fake otherwise
        /// does not have - and duration is the whole point of a "do not hold a lock across this walk"
        /// test. When set, the walk snapshots the rows first: a DB cursor hands out rows it already
        /// read, it does not fault because somebody added a row mid-cursor.
        /// </summary>
        public TimeSpan? EnumerationDelay { get; set; }

        public IEnumerator<T> GetEnumerator()
            => EnumerationDelay == null
                ? _items.GetEnumerator()
                : SlowWalk(_items.ToList(), EnumerationDelay.Value);

        private static IEnumerator<T> SlowWalk(List<T> snapshot, TimeSpan delay)
        {
            foreach (var item in snapshot)
            {
                Thread.Sleep(delay);
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int IndexOf(T item) => _items.IndexOf(item);
        public void Insert(int index, T item) => _items.Insert(index, item);
        public void RemoveAt(int index) => _items.RemoveAt(index);
        public bool Contains(T item) => _items.Contains(item);
        public void Clear() => _items.Clear();
    }

    public class NullLogger : ILogger
    {
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(System.Exception ex) { }
        public void Info(System.Exception ex, string message) { }
        public void Debug(System.Exception ex, string message) { }
        public void Warn(System.Exception ex, string message) { }
        public void Error(System.Exception ex, string message) { }
        public void Trace(string message) { }
        public void Trace(System.Exception ex, string message) { }
    }
}
