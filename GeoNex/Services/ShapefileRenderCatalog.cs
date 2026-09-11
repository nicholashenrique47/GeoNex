using System.Buffers.Binary;

namespace GeoNex.Services;

/// <summary>
/// A bounded, approximate-preview catalog. Final frames always use all records. Hash hits are always checked byte-for-byte;
/// the original records, FIDs, attributes and selection index remain untouched.
/// </summary>
public static class ShapefileRenderCatalog
{
    public const int MinimumFeatures = 100_000;
    public const int MaximumUniqueFeatures = 262_144;

    public static unsafe int[]? Build(byte* data, long length, ReadOnlySpan<long> offsets,
        CancellationToken cancellationToken = default, int minimumFeatures = MinimumFeatures,
        int maximumUniqueFeatures = MaximumUniqueFeatures)
    {
        if (data == null || offsets.Length < minimumFeatures) return null;
        var representatives = new List<int>();
        var hashes = new Dictionary<ulong, int>();
        for (int i = 0; i < offsets.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> record = ReadRecord(data, length, offsets[i]);
            if (record.Length < 44) return null;
            int type = BinaryPrimitives.ReadInt32LittleEndian(record);
            if (type is not (5 or 15 or 25)) return null;
            ulong hash = Hash(record);
            if (hashes.TryGetValue(hash, out int previous))
            {
                // A collision abandons the optimization; it can never drop a feature.
                if (!record.SequenceEqual(ReadRecord(data, length, offsets[previous]))) return null;
                continue;
            }
            if (representatives.Count >= maximumUniqueFeatures) return null;
            hashes.Add(hash, i);
            representatives.Add(i);
        }
        return representatives.Count <= offsets.Length * 0.75 ? representatives.ToArray() : null;
    }

    private static unsafe ReadOnlySpan<byte> ReadRecord(byte* data, long length, long offset)
    {
        if (offset < 100 || offset > length - 8) return default;
        long bytes = 2L * BinaryPrimitives.ReadInt32BigEndian(new ReadOnlySpan<byte>(data + offset + 4, 4));
        if (bytes < 4 || bytes > int.MaxValue || bytes > length - offset - 8) return default;
        return new ReadOnlySpan<byte>(data + offset + 8, (int)bytes);
    }

    private static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        // Word-at-a-time hash; equality, not this hash, establishes identity.
        unchecked
        {
            ulong hash = 14695981039346656037UL ^ (ulong)bytes.Length;
            while (bytes.Length >= 8)
            {
                hash = (hash ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes)) * 1099511628211UL;
                bytes = bytes[8..];
            }
            foreach (byte value in bytes) hash = (hash ^ value) * 1099511628211UL;
            return hash;
        }
    }
}
