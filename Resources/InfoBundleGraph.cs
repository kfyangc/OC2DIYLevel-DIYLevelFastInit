using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DIYLevelFastInit
{

    internal static class InfoBundleGraph
    {
        private sealed class Block
        {
            internal long Offset;
            internal int Size, Packed, Compression;
        }

        private sealed class Node
        {
            internal long Offset, Size;
            internal string Name;
            internal int Flags;
        }

        private sealed class Source
        {
            internal string Path;
            internal long Length, Modified;
            internal readonly List<Block> Blocks = new List<Block>();
            internal readonly List<Node> Nodes = new List<Node>();
            internal string[] References;
        }

        private static readonly Dictionary<string, Source> Sources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        private const int MaxMetadata = 8 * 1024 * 1024;
        private const int MaxPrefix = 64 * 1024 * 1024;

        internal static HashSet<string> Required(string info, string scene)
        {
            info = Path.GetFullPath(info);
            if (scene != null) scene = Path.GetFullPath(scene);
            Dictionary<string, string> owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string levels = Directory.GetParent(Directory.GetParent(info).FullName).FullName;
            foreach (DirectoryInfo dir in FastInitHost.SafeGetDirs(levels))
            {
                List<FileInfo> files = FastInitHost.SafeGetFiles(dir, "info*");
                if (files.Count == 0) continue;
                Source source;
                try { source = Open(files[0].FullName); }
                catch (IOException e)
                {
                    if (string.Equals(files[0].FullName, info, StringComparison.OrdinalIgnoreCase)) throw;
                    FastInitPlugin.Log.LogWarning("info dependency index skipped [" + dir.Name + "]: " + e.Message);
                    continue;
                }
                foreach (Node node in source.Nodes)
                    if (node.Name.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase))
                        if (!owners.ContainsKey(node.Name)) owners.Add(node.Name, source.Path);
            }
            HashSet<string> required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<string> pending = new Queue<string>();
            required.Add(info);
            pending.Enqueue(info);
            if (scene != null) pending.Enqueue(scene);
            while (pending.Count != 0)
                foreach (string reference in References(Open(pending.Dequeue())))
                {
                    string dependency;
                    if (owners.TryGetValue(reference, out dependency) && required.Add(dependency))
                        pending.Enqueue(dependency);
                }
            if (scene != null)
                FastInitPlugin.Log.LogInfo("info dependency graph [" + Path.GetFileName(scene) + "]: "
                    + string.Join(", ", new List<string>(required).ConvertAll(Path.GetFileName).ToArray()));
            return required;
        }

        private static Source Open(string path)
        {
            FileInfo file = new FileInfo(path);
            Source source;
            if (Sources.TryGetValue(path, out source) && source.Length == file.Length
                && source.Modified == file.LastWriteTimeUtc.Ticks) return source;
            source = new Source { Path = path, Length = file.Length, Modified = file.LastWriteTimeUtc.Ticks };
            using (FileStream stream = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (CString(reader) != "UnityFS" || U32(reader) != 6) throw new IOException("Unsupported UnityFS header: " + path);
                CString(reader);
                CString(reader);
                if (I64(reader) != file.Length) throw new IOException("Invalid UnityFS size: " + path);
                int packed = Size(U32(reader), MaxMetadata), size = Size(U32(reader), MaxMetadata);
                uint flags = U32(reader);
                long dataStart = stream.Position + ((flags & 128) == 0 ? packed : 0);
                if ((flags & ~255U) != 0 || (flags & 64) == 0) throw new IOException("Unsupported UnityFS flags: " + path);
                if ((flags & 128) != 0) stream.Position = file.Length - packed;
                byte[] bytes = Decode(reader, packed, size, (int)(flags & 63), size);
                using (BinaryReader table = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    table.ReadBytes(16);
                    int count = Size(U32(table), 100000);
                    long offset = dataStart;
                    for (int i = 0; i < count; i++)
                    {
                        int plain = Size(U32(table), int.MaxValue), compressed = Size(U32(table), int.MaxValue);
                        int compression = (table.ReadByte() << 8 | table.ReadByte()) & 63;
                        if (offset > file.Length - compressed) throw new IOException("Invalid UnityFS block");
                        source.Blocks.Add(new Block { Offset = offset, Size = plain, Packed = compressed, Compression = compression });
                        offset += compressed;
                    }
                    count = Size(U32(table), 20000);
                    for (int i = 0; i < count; i++)
                    {
                        long start = I64(table), length = I64(table);
                        int nodeFlags = (int)U32(table);
                        string name = CString(table);
                        if (start < 0 || length < 0) throw new IOException("Invalid UnityFS node");
                        source.Nodes.Add(new Node { Offset = start, Size = length, Flags = nodeFlags, Name = name });
                    }
                }
            }
            Sources[path] = source;
            return source;
        }

        private static string[] References(Source source)
        {
            if (source.References != null) return source.References;
            HashSet<string> references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Node node in source.Nodes)
            {
                if ((node.Flags & 4) == 0) continue;
                byte[] header = Range(source, node.Offset, 20);
                using (BinaryReader reader = new BinaryReader(new MemoryStream(header, false)))
                {
                    U32(reader);
                    U32(reader);
                    uint version = U32(reader);
                    int end = Size(U32(reader), MaxMetadata);
                    if (version < 9 || version > 21 || end < 20 || end > node.Size)
                        throw new IOException("Unsupported serialized metadata: " + source.Path);
                    string metadata = Encoding.ASCII.GetString(Range(source, node.Offset, end));
                    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(metadata,
                        @"archive:/(CAB-[0-9a-f]{32})/\1\x00", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        references.Add(match.Groups[1].Value);
                }
            }
            source.References = new List<string>(references).ToArray();
            return source.References;
        }

        private static byte[] Range(Source source, long start, int count)
        {
            byte[] result = new byte[count];
            long position = 0;
            int written = 0;
            using (FileStream stream = File.OpenRead(source.Path))
            using (BinaryReader reader = new BinaryReader(stream))
                foreach (Block block in source.Blocks)
                {
                    long end = position + block.Size;
                    if (start < end && start + count > position)
                    {
                        int from = (int)Math.Max(0, start - position);
                        int to = (int)Math.Min(block.Size, start + count - position);
                        if (to > MaxPrefix) throw new IOException("UnityFS metadata prefix too large");
                        stream.Position = block.Offset;
                        byte[] bytes = Decode(reader, block.Packed, block.Size, block.Compression, to);
                        Buffer.BlockCopy(bytes, from, result, written, to - from);
                        written += to - from;
                        if (written == count) return result;
                    }
                    position = end;
                }
            throw new IOException("Truncated UnityFS metadata");
        }

        private static byte[] Decode(BinaryReader reader, int packed, int size, int compression, int prefix)
        {
            if (prefix < 0 || prefix > size || prefix > MaxPrefix) throw new IOException("Invalid UnityFS prefix");
            if (compression == 0) return Exact(reader, prefix);
            if (compression == 1)
            {
                if (packed < 5) throw new IOException("Invalid LZMA block");
                byte[] properties = Exact(reader, 5);
                uint dictionary = BitConverter.ToUInt32(properties, 1);
                byte[] bounded = BitConverter.GetBytes(Math.Min(dictionary, (uint)Math.Max(4096, prefix + 273)));
                Buffer.BlockCopy(bounded, 0, properties, 1, 4);
                BundleCodecs.Compression.LZMA.Decoder decoder = new BundleCodecs.Compression.LZMA.Decoder();
                decoder.SetDecoderProperties(properties);
                using (MemoryStream output = new MemoryStream(prefix + 273))
                using (Stream input = new LimitedInput(reader.BaseStream, packed - 5))
                {
                    decoder.Code(input, output, packed - 5, prefix, null);
                    if (output.Length < prefix) throw new IOException("Truncated LZMA metadata");
                    byte[] result = new byte[prefix];
                    Buffer.BlockCopy(output.GetBuffer(), 0, result, 0, prefix);
                    return result;
                }
            }
            if (compression != 2 && compression != 3) throw new IOException("Unsupported UnityFS compression");
            if (packed > MaxPrefix) throw new IOException("LZ4 block too large");
            byte[] inputBytes = Exact(reader, packed), outputBytes = new byte[prefix];
            int source = 0, target = 0;
            while (target < prefix)
            {
                if (source >= inputBytes.Length) throw new IOException("Truncated LZ4 block");
                int token = inputBytes[source++], literal = token >> 4;
                if (literal == 15) literal += ExtendedLength(inputBytes, ref source);
                if (literal > inputBytes.Length - source) throw new IOException("Invalid LZ4 literal");
                int length = Math.Min(literal, prefix - target);
                Buffer.BlockCopy(inputBytes, source, outputBytes, target, length);
                target += length;
                source += literal;
                if (target == prefix) break;
                if (source > inputBytes.Length - 2) throw new IOException("Invalid LZ4 offset");
                int distance = inputBytes[source] | inputBytes[source + 1] << 8;
                source += 2;
                if (distance == 0 || distance > target) throw new IOException("Invalid LZ4 distance");
                int match = (token & 15) + 4;
                if ((token & 15) == 15) match += ExtendedLength(inputBytes, ref source);
                int until = target + Math.Min(match, prefix - target);
                while (target < until) { outputBytes[target] = outputBytes[target - distance]; target++; }
            }
            return outputBytes;
        }

        private static int ExtendedLength(byte[] bytes, ref int index)
        {
            int count = 0, value;
            do
            {
                if (index == bytes.Length || count > MaxPrefix) throw new IOException("Invalid LZ4 length");
                value = bytes[index++];
                count += value;
            } while (value == 255);
            return count;
        }

        private static byte[] Exact(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new IOException("Truncated UnityFS data");
            return bytes;
        }

        private static uint U32(BinaryReader reader)
        {
            return (uint)(reader.ReadByte() << 24 | reader.ReadByte() << 16 | reader.ReadByte() << 8 | reader.ReadByte());
        }

        private static long I64(BinaryReader reader)
        {
            return ((long)U32(reader) << 32) | U32(reader);
        }

        private static int Size(uint value, int limit)
        {
            if (value > limit) throw new IOException("UnityFS size exceeds limit");
            return (int)value;
        }

        private static string CString(BinaryReader reader)
        {
            List<byte> bytes = new List<byte>();
            byte value;
            while ((value = reader.ReadByte()) != 0)
            {
                if (bytes.Count == 4096) throw new IOException("UnityFS name too long");
                bytes.Add(value);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private sealed class LimitedInput : Stream
        {
            private readonly Stream _source;
            private long _remaining;
            internal LimitedInput(Stream source, long remaining) { _source = source; _remaining = remaining; }
            public override int ReadByte()
            {
                if (_remaining-- <= 0) throw new EndOfStreamException();
                int value = _source.ReadByte();
                if (value < 0) throw new EndOfStreamException();
                return value;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                count = (int)Math.Min(count, _remaining);
                int read = _source.Read(buffer, offset, count);
                _remaining -= read;
                return read;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
