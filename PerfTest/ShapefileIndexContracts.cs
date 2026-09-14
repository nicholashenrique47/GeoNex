using System.Buffers.Binary;
using GeoNex.Services;

internal static class ShapefileIndexContracts
{
    public static void Run()
    {
        const int count = 20_003;
        var index = new byte[count * 8];
        var offsets = new long[count];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(i * 8, 4), (uint)(50 + 6 * i));
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(i * 8 + 4, 4), 2);
        }
        using (var stream = new MemoryStream(index)) ShapefileIndexReader.Read(stream, offsets, 100 + count * 12);
        for (int i = 0; i < count; i++) Check(offsets[i] == 100 + i * 12, "order across buffer boundaries");
        using (var truncated = new MemoryStream(index[..^1]))
            Throws<EndOfStreamException>(() => ShapefileIndexReader.Read(truncated, offsets, 100 + count * 12));
        using (var stream = new MemoryStream(index))
            Throws<InvalidDataException>(() => ShapefileIndexReader.Read(stream, offsets, 101));
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(8, 4), 50);
        using (var stream = new MemoryStream(index))
            Throws<InvalidDataException>(() => ShapefileIndexReader.Read(stream, offsets, 100 + count * 12));
        // SHX uses unsigned 16-bit-word offsets, including files beyond 2 GiB.
        byte[] large = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(large, 1_500_000_000);
        BinaryPrimitives.WriteUInt32BigEndian(large.AsSpan(4), 2);
        using (var stream = new MemoryStream(large)) ShapefileIndexReader.Read(stream, offsets.AsSpan(0, 1), 3_000_000_012);
        Check(offsets[0] == 3_000_000_000L, "64-bit byte offsets");
        Console.WriteLine("SHX stream: PASS (20,003 records, boundaries, truncation, overlap, >2 GiB offsets, 64 KiB buffer)");
    }

    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
}
