using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DIYLevelFastInit.BundleCodecs
{
    /// <summary>Streams a bounded UnityFS v6 LZMA payload into independent LZ4 blocks.</summary>
    internal static class UnityFsRepacker
    {
        internal sealed class ConversionResult
        {
            internal byte[] SourceHash, DataHash, OutputHash;
            internal long SourceLength, OutputLength, DataLength;
            internal int BlockCount;
        }

        private sealed class Node
        {
            internal long Offset, Size;
            internal uint Flags;
            internal byte[] Name;
        }

        private sealed class Layout
        {
            internal byte[] UnityVersion, Revision, Hash, Properties;
            internal long DataStart, SourceLength;
            internal uint Flags, Plain, Packed;
            internal ushort BlockFlags;
            internal readonly List<Node> Nodes = new List<Node>();
        }

        private sealed class Block
        {
            internal uint Plain, Packed;
            internal ushort Flags;
        }

        private const int ChunkSize = 128 * 1024, CheckSize = 64 * 1024, MaxMetadata = 8 * 1024 * 1024;
        private const long MaxBytes = 2L * 1024 * 1024 * 1024 - 1;
        private const uint MaxDictionary = 64 * 1024 * 1024;

        internal static bool IsEligible(string path)
        {
            try
            {
                using (FileStream stream = OpenSource(path)) ReadLayout(stream);
                return true;
            }
            catch (System.IO.IsolatedStorage.IsolatedStorageException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        internal static ConversionResult Convert(string source, string destination, Func<bool> cancelled)
        {
            return Convert(source, destination, cancelled, null);
        }

        internal static ConversionResult Convert(string source, string destination, Func<bool> cancelled, Action checkpoint)
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The source bundle cannot be a conversion destination");
            Check(cancelled, checkpoint);
            using (FileStream input = OpenSource(source))
            {
                Layout layout = ReadLayout(input);
                int count = checked((int)(((long)layout.Plain + ChunkSize - 1) / ChunkSize));
                int metadataSize = 16 + 4 + count * 10 + 4;
                foreach (Node node in layout.Nodes) metadataSize = checked(metadataSize + 21 + node.Name.Length);
                if (metadataSize > MaxMetadata) throw new IOException("Converted UnityFS metadata is too large");
                Check(cancelled, checkpoint);
                using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CheckSize))
                {
                    WriteHeader(output, layout, 0, metadataSize);
                    long metadataStart = output.Position;
                    long bodyStart = metadataStart + metadataSize;
                    if (bodyStart + layout.Plain > MaxBytes) throw new IOException("Converted bundle exceeds the output limit");
                    output.SetLength(bodyStart);
                    output.Position = bodyStart;
                    input.Position = layout.DataStart + 5;
                    byte[] dataHash;
                    List<Block> blocks;
                    using (SHA256 hash = SHA256.Create())
                    {
                        ChunkOutput sink = new ChunkOutput(output, layout.Plain, hash, cancelled, checkpoint);
                        Compression.LZMA.Decoder decoder = new Compression.LZMA.Decoder();
                        decoder.SetDecoderProperties(layout.Properties);
                        using (LimitedInput limited = new LimitedInput(input, layout.Packed - 5, cancelled, checkpoint))
                            decoder.Code(limited, sink, layout.Packed - 5, layout.Plain, null);
                        sink.Finish();
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        dataHash = hash.Hash;
                        blocks = sink.Blocks;
                    }
                    if (blocks.Count != count) throw new IOException("Converted UnityFS block count changed");
                    long length = output.Position;
                    output.Position = metadataStart;
                    WriteMetadata(output, layout, blocks);
                    if (output.Position != bodyStart) throw new IOException("Converted UnityFS metadata reservation changed");
                    output.Position = 0;
                    WriteHeader(output, layout, length, metadataSize);
                    output.Flush();
                    return new ConversionResult
                    {
                        SourceLength = layout.SourceLength, DataLength = layout.Plain, OutputLength = length, BlockCount = count,
                        SourceHash = HashStream(input, layout.SourceLength, cancelled, checkpoint), DataHash = dataHash,
                        OutputHash = HashStream(output, length, cancelled, checkpoint)
                    };
                }
            }
        }

        private static FileStream OpenSource(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CheckSize);
        }

        private static Layout ReadLayout(FileStream stream)
        {
            if (stream.Length > MaxBytes || stream.Length < 40) throw new IOException("Unsupported UnityFS file size");
            if (Encoding.ASCII.GetString(CString(stream, 8)) != "UnityFS" || U32(stream) != 6) throw new IOException("Unsupported UnityFS header");
            Layout result = new Layout();
            result.UnityVersion = CString(stream, 128);
            result.Revision = CString(stream, 128);
            result.SourceLength = stream.Length;
            if (U64(stream) != (ulong)stream.Length) throw new IOException("UnityFS size does not match the file");
            int packed = Bounded(U32(stream), MaxMetadata), plain = Bounded(U32(stream), MaxMetadata);
            result.Flags = U32(stream);
            if ((result.Flags & ~255U) != 0 || (result.Flags & 64) == 0) throw new IOException("Unsupported UnityFS flags");
            long headerEnd = stream.Position;
            bool atEnd = (result.Flags & 128) != 0;
            long metadataStart = atEnd ? stream.Length - packed : headerEnd;
            result.DataStart = atEnd ? headerEnd : headerEnd + packed;
            if (metadataStart < headerEnd || metadataStart > stream.Length - packed) throw new IOException("Invalid UnityFS metadata extent");
            stream.Position = metadataStart;
            byte[] metadata = DecodeMetadata(Exact(stream, packed), plain, (int)(result.Flags & 63));
            using (MemoryStream table = new MemoryStream(metadata, false))
            {
                result.Hash = Exact(table, 16);
                if (U32(table) != 1) throw new IOException("Only single-block source bundles are eligible");
                result.Plain = U32(table);
                result.Packed = U32(table);
                result.BlockFlags = U16(table);
                if ((result.BlockFlags & ~127) != 0 || (result.BlockFlags & 63) != 1 || result.Plain == 0
                    || result.Plain > MaxBytes || result.Packed < 10 || result.Packed > MaxBytes)
                    throw new IOException("Unsupported UnityFS source block");
                long expectedEnd = atEnd ? metadataStart : stream.Length;
                if (result.DataStart + result.Packed != expectedEnd) throw new IOException("Invalid UnityFS data extent");
                int nodes = Bounded(U32(table), 20000);
                if (nodes == 0) throw new IOException("UnityFS has no directory nodes");
                for (int i = 0; i < nodes; i++)
                {
                    ulong offset = U64(table), size = U64(table);
                    uint flags = U32(table);
                    byte[] name = CString(table, 4096);
                    if (offset > result.Plain || size > result.Plain - offset) throw new IOException("UnityFS node exceeds the data extent");
                    result.Nodes.Add(new Node { Offset = (long)offset, Size = (long)size, Flags = flags, Name = name });
                }
                if (table.Position != table.Length) throw new IOException("Trailing UnityFS metadata");
            }
            stream.Position = result.DataStart;
            result.Properties = Exact(stream, 5);
            ValidateProperties(result.Properties);
            return result;
        }

        private static byte[] DecodeMetadata(byte[] packed, int length, int compression)
        {
            if (compression == 0)
            {
                if (packed.Length != length) throw new IOException("UnityFS metadata length mismatch");
                return packed;
            }
            if (compression == 1)
            {
                if (packed.Length < 10) throw new IOException("Truncated LZMA metadata");
                byte[] properties = new byte[5];
                Buffer.BlockCopy(packed, 0, properties, 0, 5);
                ValidateProperties(properties);
                using (MemoryStream input = new MemoryStream(packed, 5, packed.Length - 5, false))
                using (MemoryStream output = new MemoryStream(length))
                using (LimitedInput limited = new LimitedInput(input, packed.Length - 5, null, null))
                {
                    Compression.LZMA.Decoder decoder = new Compression.LZMA.Decoder();
                    decoder.SetDecoderProperties(properties);
                    CappedOutput capped = new CappedOutput(output, length);
                    decoder.Code(limited, capped, packed.Length - 5, length, null);
                    if (capped.Written != length) throw new IOException("Truncated LZMA metadata");
                    return output.ToArray();
                }
            }
            if (compression != 2 && compression != 3) throw new IOException("Unsupported UnityFS metadata compression");
            byte[] bytes = new byte[length];
            int source = 0, target = 0;
            while (source < packed.Length)
            {
                int token = packed[source++];
                int literals = Lz4Length(packed, ref source, token >> 4);
                if (literals > packed.Length - source || literals > bytes.Length - target) throw new IOException("Invalid LZ4 literal");
                Buffer.BlockCopy(packed, source, bytes, target, literals);
                source += literals;
                target += literals;
                if (source == packed.Length) break;
                if (source > packed.Length - 2) throw new IOException("Truncated LZ4 distance");
                int distance = packed[source] | packed[source + 1] << 8;
                source += 2;
                if (distance == 0 || distance > target) throw new IOException("Invalid LZ4 distance");
                int match = Lz4Length(packed, ref source, token & 15) + 4;
                if (match > bytes.Length - target) throw new IOException("Invalid LZ4 match");
                int end = target + match;
                while (target < end) { bytes[target] = bytes[target - distance]; target++; }
            }
            if (target != length) throw new IOException("LZ4 metadata length mismatch");
            return bytes;
        }

        private static int Lz4Length(byte[] bytes, ref int position, int length)
        {
            if (length != 15) return length;
            int next;
            do
            {
                if (position == bytes.Length || length > MaxMetadata) throw new IOException("Invalid LZ4 length");
                next = bytes[position++];
                length += next;
            } while (next == 255);
            return length;
        }

        private static void ValidateProperties(byte[] properties)
        {
            uint dictionary = (uint)(properties[1] | properties[2] << 8 | properties[3] << 16 | properties[4] << 24);
            if (properties[0] >= 225 || dictionary > MaxDictionary) throw new IOException("Unsupported LZMA dictionary or properties");
        }

        private static byte[] HashStream(Stream stream, long length, Func<bool> cancelled, Action checkpoint)
        {
            stream.Position = 0;
            using (SHA256 hash = SHA256.Create())
            {
                byte[] buffer = new byte[CheckSize];
                long remaining = length;
                while (remaining != 0)
                {
                    Check(cancelled, checkpoint);
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new IOException("File truncated during hashing");
                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }
                if (stream.Length != length) throw new IOException("File changed during hashing");
                hash.TransformFinalBlock(new byte[0], 0, 0);
                Check(cancelled, checkpoint);
                return hash.Hash;
            }
        }

        private static void WriteHeader(Stream stream, Layout layout, long length, int metadataSize)
        {
            WriteCString(stream, Encoding.ASCII.GetBytes("UnityFS"));
            Write32(stream, 6);
            WriteCString(stream, layout.UnityVersion);
            WriteCString(stream, layout.Revision);
            Write64(stream, (ulong)length);
            Write32(stream, (uint)metadataSize);
            Write32(stream, (uint)metadataSize);
            Write32(stream, layout.Flags & 64U);
        }

        private static void WriteMetadata(Stream stream, Layout layout, List<Block> blocks)
        {
            stream.Write(layout.Hash, 0, layout.Hash.Length);
            Write32(stream, (uint)blocks.Count);
            foreach (Block block in blocks) { Write32(stream, block.Plain); Write32(stream, block.Packed); Write16(stream, block.Flags); }
            Write32(stream, (uint)layout.Nodes.Count);
            foreach (Node node in layout.Nodes)
            {
                Write64(stream, (ulong)node.Offset); Write64(stream, (ulong)node.Size); Write32(stream, node.Flags); WriteCString(stream, node.Name);
            }
        }

        private static void Check(Func<bool> cancelled, Action checkpoint)
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException("Bundle conversion cancelled");
            if (checkpoint != null) checkpoint();
            if (cancelled != null && cancelled()) throw new OperationCanceledException("Bundle conversion cancelled");
        }

        private abstract class ForwardStream : Stream
        {
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }

        private sealed class LimitedInput : ForwardStream
        {
            private readonly Stream _source;
            private readonly Func<bool> _cancelled;
            private readonly Action _checkpoint;
            private long _remaining;
            private int _untilCheck;
            internal LimitedInput(Stream source, long length, Func<bool> cancelled, Action checkpoint)
            { _source = source; _remaining = length; _cancelled = cancelled; _checkpoint = checkpoint; }
            public override bool CanRead { get { return true; } }
            public override int ReadByte()
            {
                if (_untilCheck-- <= 0) { Check(_cancelled, _checkpoint); _untilCheck = CheckSize; }
                if (_remaining == 0) throw new IOException("Truncated LZMA input");
                int value = _source.ReadByte();
                if (value < 0) throw new IOException("Truncated LZMA input");
                _remaining--;
                return value;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                Check(_cancelled, _checkpoint);
                int read = _source.Read(buffer, offset, (int)Math.Min(Math.Min(count, CheckSize), _remaining));
                if (read == 0 && count != 0) throw new IOException("Truncated LZMA input");
                _remaining -= read;
                return read;
            }
        }

        private sealed class CappedOutput : ForwardStream
        {
            private readonly Stream _output;
            private readonly long _limit;
            internal long Written;
            internal CappedOutput(Stream output, long limit) { _output = output; _limit = limit; }
            public override bool CanWrite { get { return true; } }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count > _limit - Written) throw new IOException("LZMA output exceeds the declared length");
                _output.Write(buffer, offset, count);
                Written += count;
            }
        }

        private sealed class ChunkOutput : ForwardStream
        {
            private readonly Stream _output;
            private readonly long _limit;
            private readonly SHA256 _hash;
            private readonly Func<bool> _cancelled;
            private readonly Action _checkpoint;
            private readonly byte[] _plain = new byte[ChunkSize], _packed = new byte[ChunkSize + ChunkSize / 255 + 16];
            private readonly Lz4BlockEncoder _encoder = new Lz4BlockEncoder();
            private long _written;
            private int _filled;
            internal readonly List<Block> Blocks = new List<Block>();
            internal ChunkOutput(Stream output, long limit, SHA256 hash, Func<bool> cancelled, Action checkpoint)
            { _output = output; _limit = limit; _hash = hash; _cancelled = cancelled; _checkpoint = checkpoint; }
            public override bool CanWrite { get { return true; } }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count > _limit - _written) throw new IOException("LZMA output exceeds the declared length");
                while (count != 0)
                {
                    Check(_cancelled, _checkpoint);
                    int take = Math.Min(Math.Min(count, CheckSize), ChunkSize - _filled);
                    _hash.TransformBlock(buffer, offset, take, buffer, offset);
                    Buffer.BlockCopy(buffer, offset, _plain, _filled, take);
                    _filled += take; _written += take; offset += take; count -= take;
                    if (_filled == ChunkSize) FlushBlock();
                }
            }
            internal void Finish()
            {
                if (_written != _limit) throw new IOException("Truncated LZMA output");
                if (_filled != 0) FlushBlock();
            }
            private void FlushBlock()
            {
                Check(_cancelled, _checkpoint);
                int packed = _encoder.Encode(_plain, _filled, _packed);
                bool compressed = packed < _filled;
                int length = compressed ? packed : _filled;
                if (_output.Position > MaxBytes - length) throw new IOException("Converted bundle exceeds the output limit");
                _output.Write(compressed ? _packed : _plain, 0, length);
                Blocks.Add(new Block { Plain = (uint)_filled, Packed = (uint)length, Flags = (ushort)(compressed ? 2 : 0) });
                _filled = 0;
                Check(_cancelled, _checkpoint);
            }
        }

        private static int Bounded(uint value, int max) { if (value > max) throw new IOException("UnityFS field exceeds its limit"); return (int)value; }
        private static byte[] Exact(Stream stream, int count)
        {
            byte[] bytes = new byte[count]; int position = 0;
            while (position < count) { int read = stream.Read(bytes, position, count - position); if (read == 0) throw new IOException("Truncated UnityFS file"); position += read; }
            return bytes;
        }
        private static byte[] CString(Stream stream, int limit)
        {
            using (MemoryStream bytes = new MemoryStream())
            {
                for (int i = 0; i < limit; i++) { int next = stream.ReadByte(); if (next < 0) throw new IOException("Truncated UnityFS string"); if (next == 0) return bytes.ToArray(); bytes.WriteByte((byte)next); }
            }
            throw new IOException("UnityFS string exceeds its limit");
        }
        private static ushort U16(Stream stream) { int a = stream.ReadByte(), b = stream.ReadByte(); if (a < 0 || b < 0) throw new IOException("Truncated UnityFS field"); return (ushort)(a << 8 | b); }
        private static uint U32(Stream stream) { return (uint)U16(stream) << 16 | U16(stream); }
        private static ulong U64(Stream stream) { return (ulong)U32(stream) << 32 | U32(stream); }
        private static void Write16(Stream stream, ushort value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void Write32(Stream stream, uint value) { Write16(stream, (ushort)(value >> 16)); Write16(stream, (ushort)value); }
        private static void Write64(Stream stream, ulong value) { Write32(stream, (uint)(value >> 32)); Write32(stream, (uint)value); }
        private static void WriteCString(Stream stream, byte[] bytes) { stream.Write(bytes, 0, bytes.Length); stream.WriteByte(0); }
    }
}
