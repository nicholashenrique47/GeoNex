using System.Buffers;
using System.Buffers.Binary;

namespace GeoNex.Services;

/// <summary>Streams SHX entries without retaining a second file-sized buffer.</summary>
public static class ShapefileIndexReader
{
    public const int BufferBytes = 64 * 1024;

    // The stream is positioned after the 100-byte header. Offsets remain in source/FID order.
    public static void Read(Stream stream, Span<long> offsets, long shapeFileLength)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            long previousEnd = 100;
            for (int start = 0; start < offsets.Length;)
            {
                int count = Math.Min(BufferBytes / 8, offsets.Length - start);
                stream.ReadExactly(buffer.AsSpan(0, count * 8));
                for (int i = 0; i < count; i++)
                {
                    long offset = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i * 8, 4)) * 2L;
                    long length = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i * 8 + 4, 4)) * 2L;
                    // Never silently publish only the valid prefix of a damaged index.
                    if (offset < previousEnd || length < 4 || offset + 8 + length > shapeFileLength)
                        throw new InvalidDataException($"Índice SHX inválido no registro {start + i + 1}.");
                    offsets[start + i] = offset;
                    previousEnd = offset + 8 + length;
                }
                start += count;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
