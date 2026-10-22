using System;

namespace DIYLevelFastInit.BundleCodecs
{
    /// <summary>Encodes independent LZ4 blocks with the required final literal margin.</summary>
    internal sealed class Lz4BlockEncoder
    {
        private readonly int[] _positions = new int[65536];

        internal int Encode(byte[] source, int length, byte[] destination)
        {
            if (source == null || destination == null || length < 0 || length > source.Length
                || destination.Length < length + length / 255 + 16) throw new ArgumentException("Invalid LZ4 buffer");
            for (int i = 0; i < _positions.Length; i++) _positions[i] = -1;
            int position = 0, anchor = 0, written = 0;
            while (position <= length - 12)
            {
                uint value = Read32(source, position);
                int hash = (int)(unchecked(value * 2654435761U) >> 16);
                int previous = _positions[hash];
                _positions[hash] = position;
                if (previous < 0 || position - previous > 65535 || Read32(source, previous) != value)
                {
                    position++;
                    continue;
                }
                int match = 4;
                while (position + match < length - 5 && source[previous + match] == source[position + match]) match++;
                int literals = position - anchor;
                destination[written++] = (byte)((Math.Min(literals, 15) << 4) | Math.Min(match - 4, 15));
                if (literals >= 15) WriteLength(destination, ref written, literals - 15);
                Buffer.BlockCopy(source, anchor, destination, written, literals);
                written += literals;
                int distance = position - previous;
                destination[written++] = (byte)distance;
                destination[written++] = (byte)(distance >> 8);
                if (match >= 19) WriteLength(destination, ref written, match - 19);
                position += match;
                anchor = position;
            }
            int remaining = length - anchor;
            destination[written++] = (byte)(Math.Min(remaining, 15) << 4);
            if (remaining >= 15) WriteLength(destination, ref written, remaining - 15);
            Buffer.BlockCopy(source, anchor, destination, written, remaining);
            return written + remaining;
        }

        private static uint Read32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        }

        private static void WriteLength(byte[] bytes, ref int position, int length)
        {
            while (length >= 255) { bytes[position++] = 255; length -= 255; }
            bytes[position++] = (byte)length;
        }
    }
}
