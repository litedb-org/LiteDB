using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Service to implement merge sort, in disk, to run ORDER BY command.
    /// [ThreadSafe]
    /// </summary>
    internal class SortService : IDisposable
    {
        private readonly SortDisk _disk;

        private readonly List<SortContainer> _containers = new List<SortContainer>();
        private readonly int _containerSize;
        private readonly Done _done = new Done { Running = true };

        private readonly int _order;
        private readonly EnginePragmas _pragmas;
        private readonly BufferSlice _buffer;
        private readonly Lazy<Stream> _reader;

        private static readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

        /// <summary>
        /// Get how many documents was inserted by Insert method
        /// </summary>
        public int Count => _done.Count;

        /// <summary>
        /// Expose used container in this sort operation
        /// </summary>
        public IReadOnlyCollection<SortContainer> Containers => _containers;

        public SortService(SortDisk disk, int order, EnginePragmas pragmas)
        {
            _disk = disk;
            _order = order;
            _pragmas = pragmas;
            _containerSize = disk.ContainerSize;

            _reader = new Lazy<Stream>(() => _disk.GetReader());

            var bytes = new byte [disk.ContainerSize];

            _buffer = new BufferSlice(bytes, 0, _containerSize);
        }

        public void Dispose()
        {
            // release all container positions
            foreach(var container in _containers)
            {
                container.Dispose();

                // return only was used
                if (container.Position >= 0)
                {
                    _disk.Return(container.Position);
                }
            }

            // return open strem into disk
            if (_reader.IsValueCreated)
            {
                _disk.Return(_reader.Value);
            }
        }

        /// <summary>
        /// Read all input items and store in temp disk ordered in each container
        /// </summary>
        public void Insert(IEnumerable<KeyValuePair<BsonValue, PageAddress>> items)
        {
            // slit all items in sorted containers
            foreach (var containerItems in this.SliptValues(items, _done))
            {
                var container = new SortContainer(_pragmas.Collation, _containerSize);

                // insert segmented items inside a container - reuse same buffer slice
                container.Insert(containerItems, _order, _buffer);

                _containers.Add(container);

                // initialize container readers: if single container, do not use Stream file... only buffer memory
                if (_done.Running == false && _containers.Count == 1)
                {
                    container.InitializeReader(null, _buffer, _pragmas.UtcDate);
                }
                else
                {
                    // store in disk
                    container.Position = _disk.GetContainerPosition();

                    _disk.Write(container.Position, _buffer);

                    container.InitializeReader(_reader.Value, null, _pragmas.UtcDate);
                }
            }
        }

        /// <summary>
        /// Slipt all items in big sorted containers - Do merge sort with all containers
        /// </summary>
        public IEnumerable<KeyValuePair<BsonValue, PageAddress>> Sort()
        {
            if (_containers.Count == 0) yield break;

            // starts with first container as current
            var current = _containers[0];

            // if single container, just return ordered data
            if (_containers.Count == 1)
            {
                do
                {
                    yield return current.Current;
                }
                while (current.MoveNext());

                current.Dispose();
            }
            else
            {
                var diffOrder = _order * -1;

                // merge sort with all containers
                while (_containers.Any(x => !x.IsEOF))
                {
                    foreach (var container in _containers.Where(x => !x.IsEOF))
                    {
                        var diff = container.Current.Key.CompareTo(current.Current.Key, _pragmas.Collation);

                        if (diff == diffOrder)
                        {
                            current = container;
                        }
                    }

                    yield return current.Current;

                    var lastKey = current.Current.Key;

                    if (current.MoveNext() == false)
                    {
                        // now, current container must any new container that still have values
                        current = _containers.FirstOrDefault(x => !x.IsEOF);
                    }

                    // after run MoveNext(), if container contains same lastKey, can return now
                    while (current?.Current.Key == lastKey)
                    {
                        yield return current.Current;

                        if (current.MoveNext() == false)
                        {
                            current = _containers.FirstOrDefault(x => !x.IsEOF);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Split values in many IEnumerable. Each enumerable contains values to be insert in a single container
        /// </summary>
        private IEnumerable<IEnumerable<KeyValuePair<BsonValue, PageAddress>>> SliptValues(IEnumerable<KeyValuePair<BsonValue, PageAddress>> source, Done done)
        {
            using (var enumerator = source.GetEnumerator())
            {
                if (enumerator.MoveNext())
                {
                    done.Count = 1;

                    while (done.Running)
                    {
                        yield return this.YieldValues(enumerator, done);
                    }
                }
            }
        }

        /// <summary>
        /// Loop in values enumerator to return N values for a single container
        /// </summary>
        private IEnumerable<KeyValuePair<BsonValue, PageAddress>> YieldValues(IEnumerator<KeyValuePair<BsonValue, PageAddress>> source, Done done)
        {
            var size = IndexNode.GetKeyLength(source.Current.Key, false) + PageAddress.SIZE;

            yield return source.Current;

            while (source.MoveNext())
            {
                var length = IndexNode.GetKeyLength(source.Current.Key, false) + PageAddress.SIZE;

                done.Count++;

                if (size + length > _containerSize) yield break;

                size += length;

                yield return source.Current;
            }

            done.Running = false;
        }
    }
}