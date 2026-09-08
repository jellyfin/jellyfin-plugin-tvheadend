using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Helper;

namespace TVHeadEnd.HTSP
{
    public class HTSMessage
    {
        public const long HtspVersion = 20;
        private const byte HmfMap = 1;
        private const byte HmfS64 = 2;
        private const byte HmfStr = 3;
        private const byte HmfBin = 4;
        private const byte HmfList = 5;

        private readonly Dictionary<string, object> _dict;
        private ILogger<HTSMessage>? _logger;
        private byte[]? _data;

        public HTSMessage()
        {
            _dict = new Dictionary<string, object>();
        }

        public string Method
        {
            get
            {
                return GetString("method", string.Empty) ?? string.Empty;
            }

            set
            {
                _dict["method"] = value;
                _data = null;
            }
        }

        public void PutField(string name, object value)
        {
            if (value != null)
            {
                _dict[name] = value;
                _data = null;
            }
        }

        public void RemoveField(string name)
        {
            _dict.Remove(name);
            _data = null;
        }

        public Dictionary<string, object>.Enumerator GetEnumerator()
        {
            return _dict.GetEnumerator();
        }

        public bool ContainsField(string name)
        {
            return _dict.ContainsKey(name);
        }

        public System.Numerics.BigInteger GetBigInteger(string name)
        {
            try
            {
                return (System.Numerics.BigInteger)_dict[name];
            }
            catch (InvalidCastException)
            {
                _logger?.LogCritical(
                    "[TVHclient] Caught InvalidCastException for field name '{Name}'. Expected 'System.Numerics.BigInteger' but got '{Type}'",
                    name,
                    _dict[name].GetType());
                throw;
            }
        }

        public long GetLong(string name)
        {
            return (long)GetBigInteger(name);
        }

        public long GetLong(string name, long std)
        {
            if (!ContainsField(name))
            {
                return std;
            }

            return GetLong(name);
        }

        public int GetInt(string name)
        {
            return (int)GetBigInteger(name);
        }

        public int GetInt(string name, int std)
        {
            if (!ContainsField(name))
            {
                return std;
            }

            return GetInt(name);
        }

        public string? GetString(string name, string? std)
        {
            if (!ContainsField(name))
            {
                return std;
            }

            return GetString(name);
        }

        public string? GetString(string name)
        {
            object obj = _dict[name];
            if (obj == null)
            {
                return null;
            }

            return obj.ToString();
        }

        public IList<long?> GetLongList(string name)
        {
            List<long?> list = new List<long?>();

            if (!ContainsField(name))
            {
                return list;
            }

            foreach (object obj in (IList)_dict[name])
            {
                if (obj is System.Numerics.BigInteger)
                {
                    list.Add((long)((System.Numerics.BigInteger)obj));
                }
            }

            return list;
        }

        internal IList<long?> GetLongList(string name, IList<long?> std)
        {
            if (!ContainsField(name))
            {
                return std;
            }

            return GetLongList(name);
        }

        public IList<int?> GetIntList(string name)
        {
            List<int?> list = new List<int?>();

            if (!ContainsField(name))
            {
                return list;
            }

            foreach (object obj in (IList)_dict[name])
            {
                if (obj is System.Numerics.BigInteger)
                {
                    list.Add((int)((System.Numerics.BigInteger)obj));
                }
            }

            return list;
        }

        internal IList<int?> GetIntList(string name, IList<int?> std)
        {
            if (!ContainsField(name))
            {
                return std;
            }

            return GetIntList(name);
        }

        public IList GetList(string name)
        {
            return (IList)_dict[name];
        }

        public byte[] GetByteArray(string name)
        {
            return (byte[])_dict[name];
        }

        public byte[] BuildBytes()
        {
            if (_data != null)
            {
                return _data;
            }

            byte[] buf = Array.Empty<byte>();

            // calc data
            byte[] data = SerializeBinary(_dict);

            // calc length
            int len = data.Length;
            byte[] tmpByte = new byte[1];
            tmpByte[0] = unchecked((byte)((len >> 24) & 0xFF));
            buf = buf.Concat(tmpByte).ToArray();
            tmpByte[0] = unchecked((byte)((len >> 16) & 0xFF));
            buf = buf.Concat(tmpByte).ToArray();
            tmpByte[0] = unchecked((byte)((len >> 8) & 0xFF));
            buf = buf.Concat(tmpByte).ToArray();
            tmpByte[0] = unchecked((byte)(len & 0xFF));
            buf = buf.Concat(tmpByte).ToArray();

            // append data
            buf = buf.Concat(data).ToArray();

            return buf;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\nHTSMessage:\n");
            sb.Append("  <dump>\n");
            sb.Append(GetValueString(_dict, "    "));
            sb.Append("  </dump>\n\n");
            return sb.ToString();
        }

        private string GetValueString(object? value, string pad)
        {
            if (value is byte[])
            {
                StringBuilder sb = new StringBuilder();
                byte[] bVal = (byte[])value;
                for (int ii = 0; ii < bVal.Length; ii++)
                {
                    sb.Append(bVal[ii]);
                    // sb.Append(" (" + Convert.ToString(bVal[ii], 2).PadLeft(8, '0') + ")");
                    sb.Append(", ");
                }

                return sb.ToString();
            }
            else if (value is IDictionary)
            {
                StringBuilder sb = new StringBuilder();
                IDictionary dictVal = (IDictionary)value;
                foreach (object key in dictVal.Keys)
                {
                    object? currValue = dictVal[key];
                    sb.Append(pad + key + " : " + GetValueString(currValue, pad + "  ") + "\n");
                }

                return sb.ToString();
            }
            else if (value is ICollection)
            {
                StringBuilder sb = new StringBuilder();
                ICollection colVal = (ICollection)value;
                foreach (object tmpObj in colVal)
                {
                    sb.Append(GetValueString(tmpObj, pad) + ", ");
                }

                return sb.ToString();
            }

            return string.Empty + value;
        }

        private byte[] SerializeBinary(IDictionary map)
        {
            byte[] buf = Array.Empty<byte>();
            foreach (object key in map.Keys)
            {
                object? value = map[key];
                byte[] sub = SerializeBinary(key.ToString() ?? string.Empty, value);
                buf = buf.Concat(sub).ToArray();
            }

            return buf;
        }

        private byte[] SerializeBinary(ICollection list)
        {
            byte[] buf = Array.Empty<byte>();
            foreach (object value in list)
            {
                byte[] sub = SerializeBinary(string.Empty, value);
                buf = buf.Concat(sub).ToArray();
            }

            return buf;
        }

        private byte[] SerializeBinary(string name, object? value)
        {
            byte[] bName = GetBytes(name);
            byte[] bData = Array.Empty<byte>();
            byte type;

            if (value is string)
            {
                type = HTSMessage.HmfStr;
                bData = GetBytes((string)value);
            }
            else if (value is System.Numerics.BigInteger)
            {
                type = HTSMessage.HmfS64;
                bData = ToByteArray((System.Numerics.BigInteger)value);
            }
            else if (value is int?)
            {
                type = HTSMessage.HmfS64;
                bData = ToByteArray((int)value);
            }
            else if (value is long?)
            {
                type = HTSMessage.HmfS64;
                bData = ToByteArray((long)value);
            }
            else if (value is byte[])
            {
                type = HTSMessage.HmfBin;
                bData = (byte[])value;
            }
            else if (value is IDictionary)
            {
                type = HTSMessage.HmfMap;
                bData = SerializeBinary((IDictionary)value);
            }
            else if (value is ICollection)
            {
                type = HTSMessage.HmfList;
                bData = SerializeBinary((ICollection)value);
            }
            else if (value == null)
            {
                throw new IOException("[TVHclient] HTSPMessage.getValueString: HTSP doesn't support null values");
            }
            else
            {
                throw new IOException("[TVHclient] HTSPMessage.getValueString: unhandled class for " + name + ": " + value + " (" + value.GetType().Name + ")");
            }

            byte[] buf = new byte[1 + 1 + 4 + bName.Length + bData.Length];
            buf[0] = type;
            buf[1] = unchecked((byte)(bName.Length & 0xFF));
            buf[2] = unchecked((byte)((bData.Length >> 24) & 0xFF));
            buf[3] = unchecked((byte)((bData.Length >> 16) & 0xFF));
            buf[4] = unchecked((byte)((bData.Length >> 8) & 0xFF));
            buf[5] = unchecked((byte)(bData.Length & 0xFF));

            Array.Copy(bName, 0, buf, 6, bName.Length);
            Array.Copy(bData, 0, buf, 6 + bName.Length, bData.Length);

            return buf;
        }

        private byte[] ToByteArray(System.Numerics.BigInteger big)
        {
            byte[] b = BitConverter.GetBytes((long)big);
            byte[] b1 = Array.Empty<byte>();
            bool tail = false;
            for (int ii = 0; ii < b.Length; ii++)
            {
                if (b[ii] != 0 || !tail)
                {
                    tail = true;
                    b1 = b1.Concat(new byte[] { b[ii] }).ToArray();
                }
            }

            if (b1.Length == 0)
            {
                b1 = new byte[1];
            }

            return b1;
        }

        public static HTSMessage? Parse(byte[] data, ILogger<HTSMessage> logger)
        {
            if (data.Length < 4)
            {
                logger.LogError("[TVHclient] HTSMessage.parse(byte[]): didn't receive enough data");
                return null;
            }

            long len = UIntToLong(data[0], data[1], data[2], data[3]);
            // Message not fully read
            if (data.Length < len + 4)
            {
                logger.LogError("[TVHclient] HTSMessage.parse(byte[]): didn't receive enough data for len: {Len}", len);
                return null;
            }

            // drops 4 bytes (length information)
            byte[] messageData = new byte[len];
            Array.Copy(data, 4, messageData, 0, len);

            HTSMessage msg = DeserializeBinary(messageData);

            msg._logger = logger;
            msg._data = data;

            return msg;
        }

        public static long UIntToLong(byte b1, byte b2, byte b3, byte b4)
        {
            long i = 0;
            i <<= 8;
            i ^= b1 & 0xFF;
            i <<= 8;
            i ^= b2 & 0xFF;
            i <<= 8;
            i ^= b3 & 0xFF;
            i <<= 8;
            i ^= b4 & 0xFF;
            return i;
        }

        private static System.Numerics.BigInteger ToBigInteger(byte[] b)
        {
            byte[] b1 = new byte[8];
            for (int ii = 0; ii < b.Length; ii++)
            {
                b1[ii] = b[ii];
            }

            long lValue = BitConverter.ToInt64(b1, 0);
            return new System.Numerics.BigInteger(lValue);
        }

        private static HTSMessage DeserializeBinary(byte[] messageData)
        {
            byte type, namelen;
            long datalen;

            HTSMessage msg = new HTSMessage();
            int cnt = 0;

            ByteBuffer buf = new ByteBuffer(messageData);
            while (buf.HasRemaining())
            {
                type = buf.Get();
                namelen = buf.Get();
                datalen = UIntToLong(buf.Get(), buf.Get(), buf.Get(), buf.Get());

                if (buf.Length() < namelen + datalen)
                {
                    throw new IOException("[TVHclient] HTSMessage.deserializeBinary: buffer limit exceeded");
                }

                // Get the key for the map (the name)
                string name;
                if (namelen == 0)
                {
                    name = Convert.ToString(cnt++, CultureInfo.InvariantCulture);
                }
                else
                {
                    byte[] bName = new byte[namelen];
                    buf.Get(bName);
                    name = NewString(bName);
                }

                // Get the actual content
                object? obj;
                byte[] bData = new byte[datalen];
                buf.Get(bData);

                switch (type)
                {
                    case HTSMessage.HmfStr:
                        {
                            obj = NewString(bData);
                            break;
                        }

                    case HmfBin:
                        {
                            obj = bData;
                            break;
                        }

                    case HmfS64:
                        {
                            obj = ToBigInteger(bData);
                            break;
                        }

                    case HmfMap:
                        {
                            obj = DeserializeBinary(bData);
                            break;
                        }

                    case HmfList:
                        {
                            obj = new List<object>(DeserializeBinary(bData)._dict.Values);
                            break;
                        }

                    default:
                        throw new IOException("[TVHclient] HTSMessage.deserializeBinary: unknown data type");
                }

                msg.PutField(name, obj);
            }

            return msg;
        }

        private static string NewString(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        private byte[] GetBytes(string s)
        {
            System.Text.Encoding encoding = System.Text.Encoding.UTF8;
            byte[] bytes = new byte[encoding.GetByteCount(s)];
            encoding.GetBytes(s, 0, s.Length, bytes, 0);
            return bytes;
        }
    }
}
