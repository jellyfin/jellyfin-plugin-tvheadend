using System;
using System.IO;

namespace TVHeadEnd.Helper
{
    public sealed class ByteBuffer : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;
        private readonly BinaryWriter _writer;

        public ByteBuffer(byte[] data)
        {
            _stream = new MemoryStream();
            _reader = new BinaryReader(_stream);
            _writer = new BinaryWriter(_stream);
            _writer.Write(data);
            _stream.Position = 0;
        }

        public long Length()
        {
            return _stream.Length;
        }

        public bool HasRemaining()
        {
            return (_stream.Length - _stream.Position) > 0;
        }

        public byte Get()
        {
            return (byte)_stream.ReadByte();
        }

        public void Get(byte[] dst)
        {
            _stream.Read(dst, 0, dst.Length);
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
