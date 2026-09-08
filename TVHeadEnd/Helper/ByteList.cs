using System;
using System.Collections.Generic;
using System.Threading;

namespace TVHeadEnd.Helper
{
    public class ByteList
    {
        private readonly List<byte> _data;

        public ByteList()
        {
            _data = new List<byte>();
        }

        public byte[] GetFromStart(int count)
        {
            lock (_data)
            {
                while (_data.Count < count)
                {
                    Monitor.Wait(_data);
                }

                return _data.GetRange(0, count).ToArray();
            }
        }

        public byte[] ExtractFromStart(int count)
        {
            lock (_data)
            {
                while (_data.Count < count)
                {
                    Monitor.Wait(_data);
                }

                byte[] result = _data.GetRange(0, count).ToArray();
                _data.RemoveRange(0, count);
                return result;
            }
        }

        public void AppendAll(byte[] data)
        {
            lock (_data)
            {
                _data.AddRange(data);
                if (_data.Count >= 1)
                {
                    // wake up any blocked dequeue
                    Monitor.PulseAll(_data);
                }
            }
        }

        public void AppendCount(byte[] data, long count)
        {
            lock (_data)
            {
                byte[] dataRange = new byte[count];
                Array.Copy(data, 0, dataRange, 0, dataRange.Length);
                AppendAll(dataRange);
            }
        }

        public int Count()
        {
            lock (_data)
            {
                return _data.Count;
            }
        }
    }
}
