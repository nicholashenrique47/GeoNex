using System.Buffers.Binary;
using System.Text;

namespace GeoNex.Services;

public readonly record struct ShapeRecordLayout(int ShapeType, int PartCount, int PointCount,
    long PointsOffset, long EndOffset);
public sealed record DbfLayout(int HeaderBytes, int RecordBytes, long RecordCount, IReadOnlyList<DbfField> Fields);

/// <summary>Structural validation before dereferencing mapped SHP/DBF data.
/// Lengths and offsets are bytes (64-bit), including files larger than 2 GiB.
/// Does not validate polygon topology or repair malformed input.</summary>
public static unsafe class ShapefileValidation
{
    public const int FileHeaderBytes = 100;

    public static bool Contains(long length, long offset, long count) =>
        length >= 0 && offset >= 0 && count >= 0 && offset <= length && count <= length - offset;

    public static int ValidateHeader(ReadOnlySpan<byte> header, long fileLength, string source)
    {
        if (header.Length < FileHeaderBytes || fileLength < FileHeaderBytes)
            throw Invalid(source, 0, "cabeçalho truncado");
        if (BinaryPrimitives.ReadInt32BigEndian(header) != 9994 ||
            BinaryPrimitives.ReadInt32LittleEndian(header[28..]) != 1000)
            throw Invalid(source, 0, "assinatura/versão inválida");
        long declared = BinaryPrimitives.ReadUInt32BigEndian(header[24..]) * 2L;
        if (declared != fileLength)
            throw Invalid(source, 24, "tamanho declarado difere do arquivo");
        int type = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
        if (!KnownType(type)) throw Invalid(source, 32, "tipo geométrico desconhecido");
        ValidateBounds(header[36..68], source, 36);
        return type;
    }

    public static long ValidateRecordHeader(byte* data, long length, long offset, long expectedNumber = 0)
    {
        Require(data, length, offset, 12, "SHP");
        var header = new ReadOnlySpan<byte>(data + offset, 8);
        int number = BinaryPrimitives.ReadInt32BigEndian(header);
        if (number <= 0 || (expectedNumber > 0 && number != expectedNumber))
            throw Invalid("SHP", offset, "número do registro inconsistente");
        long bytes = BinaryPrimitives.ReadUInt32BigEndian(header[4..]) * 2L;
        if (bytes < 4 || !Contains(length, offset + 8, bytes))
            throw Invalid("SHP", offset, "conteúdo do registro truncado");
        return offset + 8 + bytes;
    }

    public static ShapeRecordLayout ValidateRecord(byte* data, long length, long offset, int expectedType = -1)
    {
        long end = ValidateRecordHeader(data, length, offset);
        long content = offset + 8;
        int type = ReadInt(data, end, content);
        if (!KnownType(type) || (type != 0 && expectedType >= 0 && type != expectedType))
            throw Invalid("SHP", offset, "tipo do registro difere do cabeçalho");
        if (type == 0)
        {
            if (end - content != 4) throw Invalid("SHP", offset, "NullShape com tamanho inválido");
            return new(type, 0, 0, 0, end);
        }
        if (type is 1 or 11 or 21)
        {
            long minimum = type == 1 ? 20 : 28;
            Require(data, end, content, minimum, "SHP");
            long extra = end - content - minimum;
            if (extra != 0 && !(type == 11 && extra == 8))
                throw Invalid("SHP", offset, "Point/Z/M com tamanho inválido");
            double x = ReadDouble(data, content + 4), y = ReadDouble(data, content + 12);
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw Invalid("SHP", offset, "coordenada XY não finita");
            return new(type, 0, 1, content + 4, end);
        }

        bool multipart = type is not (8 or 18 or 28);
        Require(data, end, content, multipart ? 44 : 40, "SHP");
        ValidateBounds(new ReadOnlySpan<byte>(data + content + 4, 32), "SHP", offset);
        int parts = multipart ? ReadInt(data, end, content + 36) : 0;
        int points = ReadInt(data, end, content + (multipart ? 40 : 36));
        if (points < 0 || parts < 0 || parts > points || (multipart && ((parts == 0) != (points == 0))))
            throw Invalid("SHP", offset, "counts de partes/pontos inválidos");
        long pointsOffset = content + (multipart ? 44L + parts * (type == 31 ? 8L : 4L) : 40L);
        // Prove the entire part table and XY payload fit BEFORE reading any part index.
        Require(data, end, content, pointsOffset - content + points * 16L, "SHP");
        int previous = -1;
        for (int part = 0; part < parts; part++)
        {
            int start = ReadInt(data, end, content + 44 + part * 4L);
            if (start < 0 || start >= points || start <= previous || (part == 0 && start != 0))
                throw Invalid("SHP", offset, "índices de partes fora de ordem/limites");
            previous = start;
            if (type == 31)
            {
                int partType = ReadInt(data, end, content + 44 + parts * 4L + part * 4L);
                if (partType is < 0 or > 5) throw Invalid("SHP", offset, "tipo de MultiPatch inválido");
            }
        }

        long requiredEnd = pointsOffset + points * 16L;
        if (type is 13 or 15 or 18 or 31)
        {
            Require(data, end, requiredEnd, 16 + points * 8L, "SHP Z");
            requiredEnd += 16 + points * 8L;
        }
        long remaining = end - requiredEnd;
        bool mayHaveMeasures = type is 13 or 15 or 18 or 23 or 25 or 28 or 31;
        if (remaining != 0 && (!mayHaveMeasures || remaining != 16 + points * 8L))
            throw Invalid("SHP", offset, "bloco M parcial ou tamanho residual inválido");
        return new(type, parts, points, pointsOffset, end);
    }

    public static DbfLayout ValidateDbf(byte* data, long length, long expectedRecords)
    {
        Require(data, length, 0, 33, "DBF");
        var header = new ReadOnlySpan<byte>(data, 32);
        long count = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        int headerBytes = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        int recordBytes = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        if (headerBytes < 33 || recordBytes < 1 || !Contains(length, 0, headerBytes))
            throw Invalid("DBF", 8, "tamanho de cabeçalho/registro inválido");
        if (count != expectedRecords)
            throw Invalid("DBF", 4, "contagem de registros difere do SHP (incluindo NullShape)");
        Require(data, length, headerBytes, count * recordBytes, "DBF");
        var fields = new List<DbfField>();
        int position = 32, fieldStart = 1;
        while (position < headerBytes && data[position] != 0x0D)
        {
            // A descriptor needs 32 bytes plus space for its terminator.
            if (!Contains(headerBytes - 1, position, 32))
                throw Invalid("DBF", position, "descritor de campo truncado");
            int fieldLength = data[position + 16];
            if (fieldLength == 0 || !Contains(recordBytes, fieldStart, fieldLength))
                throw Invalid("DBF", position, "campo fora do registro");
            string name = Encoding.ASCII.GetString(new ReadOnlySpan<byte>(data + position, 11)).TrimEnd('\0', ' ');
            fields.Add(new DbfField { Name = name, Offset = fieldStart, Length = fieldLength });
            fieldStart += fieldLength;
            position += 32;
        }
        if (position >= headerBytes || data[position] != 0x0D)
            throw Invalid("DBF", position, "terminador de campos ausente");
        return new(headerBytes, recordBytes, count, fields);
    }

    public static long DbfFieldOffset(long length, int headerBytes, int recordBytes, long recordCount,
        long fid, int fieldOffset, int fieldLength)
    {
        if (headerBytes < 33 || recordBytes < 1 || fid < 0 || fid >= recordCount ||
            fieldOffset < 1 || fieldLength < 1 || !Contains(recordBytes, fieldOffset, fieldLength) ||
            fid > (long.MaxValue - headerBytes) / recordBytes)
            throw Invalid("DBF", 0, "FID ou campo fora dos limites");
        long record = headerBytes + fid * recordBytes;
        if (!Contains(length, record, recordBytes)) throw Invalid("DBF", record, "registro truncado");
        return record + fieldOffset;
    }

    private static void Require(byte* data, long length, long offset, long count, string source)
    {
        if (data == null || !Contains(length, offset, count))
            throw Invalid(source, offset, "leitura fora dos limites");
    }
    private static int ReadInt(byte* data, long length, long offset)
    {
        Require(data, length, offset, 4, "SHP");
        return BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(data + offset, 4));
    }
    private static double ReadDouble(byte* data, long offset) =>
        BinaryPrimitives.ReadDoubleLittleEndian(new ReadOnlySpan<byte>(data + offset, 8));
    private static bool KnownType(int type) => type is 0 or 1 or 3 or 5 or 8 or 11 or 13 or 15 or 18 or 21 or 23 or 25 or 28 or 31;
    private static void ValidateBounds(ReadOnlySpan<byte> bounds, string source, long offset)
    {
        double minX = BinaryPrimitives.ReadDoubleLittleEndian(bounds);
        double minY = BinaryPrimitives.ReadDoubleLittleEndian(bounds[8..]);
        double maxX = BinaryPrimitives.ReadDoubleLittleEndian(bounds[16..]);
        double maxY = BinaryPrimitives.ReadDoubleLittleEndian(bounds[24..]);
        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY) ||
            minX > maxX || minY > maxY) throw Invalid(source, offset, "envelope inválido");
    }
    private static InvalidDataException Invalid(string source, long offset, string reason) =>
        new($"{source} inválido no byte {offset}: {reason}.");
}
