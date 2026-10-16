using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;

namespace TVHeadEnd.Helper
{
    public class BlockingBuffer<T>
    {
        private readonly Queue<T> _queue = new Queue<T>();
        private readonly int _maxSize;
        private Exception? _error;

        public BlockingBuffer(int maxSize)
        {
            _maxSize = maxSize;
        }

        public void Enqueue(T item)
        {
            lock (_queue)
            {
                while (_queue.Count >= _maxSize && _error == null)
                {
                    Monitor.Wait(_queue);
                }

                ThrowIfClosed();
                _queue.Enqueue(item);
                if (_queue.Count == 1)
                {
                    // wake up any blocked dequeue
                    Monitor.PulseAll(_queue);
                }
            }
        }

        public T Dequeue()
        {
            lock (_queue)
            {
                while (_queue.Count == 0)
                {
                    ThrowIfClosed();
                    Monitor.Wait(_queue);
                }

                T item = _queue.Dequeue();
                if (_queue.Count == _maxSize - 1)
                {
                    // wake up any blocked enqueue
                    Monitor.PulseAll(_queue);
                }

                return item;
            }
        }

        public bool TryDequeue([MaybeNullWhen(false)] out T item, TimeSpan timeout)
        {
            long started = Stopwatch.GetTimestamp();
            lock (_queue)
            {
                while (_queue.Count == 0)
                {
                    ThrowIfClosed();
                    TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero)
                    {
                        item = default;
                        return false;
                    }

                    Monitor.Wait(_queue, remaining);
                }

                item = _queue.Dequeue();
                if (_queue.Count == _maxSize - 1)
                {
                    // wake up any blocked enqueue
                    Monitor.PulseAll(_queue);
                }

                return true;
            }
        }

        public void Close(Exception error)
        {
            lock (_queue)
            {
                _error ??= error;
                Monitor.PulseAll(_queue);
            }
        }

        private void ThrowIfClosed()
        {
            if (_error != null)
            {
                throw new IOException("The connection buffer is closed", _error);
            }
        }
    }
}
