using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;
using NetTopologySuite.Geometries;
using System.Buffers;

namespace GeoNex.Services
{
    public unsafe class FastShapeReader
    {
        public static (double MinX, double MaxX, double MinY, double MaxY) GetExtent(string shpPath)
        {
            using var fs = new FileStream(shpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            fs.Seek(36, SeekOrigin.Begin);
            double minX = br.ReadDouble();
            double minY = br.ReadDouble();
            double maxX = br.ReadDouble();
            double maxY = br.ReadDouble();
            return (minX, maxX, minY, maxY);
        }

        public unsafe static (List<CompiledFeature> Features, MemoryMappedShapefile ShpData) ReadAllFeatures(string filePath, string layerName, double offsetX, double offsetY, string? catColumnName = null)
        {
            var features = new List<CompiledFeature>();
            
            // 1. Carrega o DBF via MemoryMappedFile
            string dbfPath = Path.ChangeExtension(filePath, ".dbf");
            System.IO.MemoryMappedFiles.MemoryMappedFile? dbfMmf = null;
            System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? dbfAccessor = null;
            byte* dbfPtr = null;

            if (File.Exists(dbfPath))
            {
                dbfMmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(dbfPath, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                dbfAccessor = dbfMmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                dbfAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref dbfPtr);
            }
            
            var dbfFields = new List<(string Name, int Offset, int Length)>();
            int catFieldIndex = -1;
            int dbfHeaderBytes = 0;
            int dbfRecordBytes = 0;
            var iso = System.Text.Encoding.GetEncoding("ISO-8859-1");

            if (dbfPtr != null)
            {
                dbfHeaderBytes = *(short*)(dbfPtr + 8);
                dbfRecordBytes = *(short*)(dbfPtr + 10);
                
                int currentOffset = 1; 
                int fieldOffset = 32;
                while (dbfPtr[fieldOffset] != 0x0D && fieldOffset < dbfHeaderBytes)
                {
                    string fieldName = new string((sbyte*)dbfPtr, fieldOffset, 11, System.Text.Encoding.ASCII).TrimEnd('\0', ' ');
                    int fieldLength = dbfPtr[fieldOffset + 16];
                    
                    dbfFields.Add((fieldName, currentOffset, fieldLength));
                    currentOffset += fieldLength;
                    fieldOffset += 32;
                }
                
                if (catColumnName != null) {
                    catFieldIndex = dbfFields.FindIndex(f => f.Name.Equals(catColumnName, StringComparison.OrdinalIgnoreCase));
                }
            }
            
            // 2. Carrega o SHP via MemoryMappedFile
            var shpMmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            var shpAccessor = shpMmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            byte* ptr = null;
            shpAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

            try
            {
                long fileLength = new FileInfo(filePath).Length;
                int offset = 100; // O Header do SHP sempre tem 100 bytes
                
                var recordOffsets = new List<int>(500000);
                while (offset < fileLength)
                {
                    recordOffsets.Add(offset);
                    byte* recPtr = ptr + offset;
                    int contentLength = (recPtr[4] << 24) | (recPtr[5] << 16) | (recPtr[6] << 8) | recPtr[7];
                    offset += 8 + (contentLength * 2);
                }

                for (int i = 0; i < recordOffsets.Count; i++)
                {
                    int recOffset = recordOffsets[i];
                    byte* recPtr = ptr + recOffset;
                    
                    byte* dataPtr = recPtr + 8;
                    int shapeType = *(int*)dataPtr;
                    
                    var feature = new CompiledFeature
                    {
                        FID = i,
                        EnvelopeWorld = new Envelope(),
                        Kind = GeometryKind.Unknown,
                        DataOffset = recOffset, // O SALTO DA MAGICA
                        LayerName = layerName
                    };
                    
                    if (dbfPtr != null && catFieldIndex >= 0)
                    {
                        var field = dbfFields[catFieldIndex];
                        int dbfRecOffset = dbfHeaderBytes + (i * dbfRecordBytes);
                        if (dbfRecOffset + field.Offset + field.Length <= dbfAccessor.Capacity)
                        {
                            feature.CategoryValue = new string((sbyte*)dbfPtr, dbfRecOffset + field.Offset, field.Length, iso).Trim();
                        }
                    }

                    if (shapeType == 1 || shapeType == 11 || shapeType == 21)
                    {
                        feature.Kind = GeometryKind.Point;
                        double x = *(double*)(dataPtr + 4);
                        double y = *(double*)(dataPtr + 12);
                        feature.EnvelopeWorld.ExpandToInclude(x, y);
                        feature.CentroidLocal = new SKPoint((float)(x - offsetX), -(float)(y - offsetY));
                    }
                    else if (shapeType == 3 || shapeType == 13 || shapeType == 23) // PolyLine
                    {
                        feature.Kind = GeometryKind.Line;
                        double minx = *(double*)(dataPtr + 4);
                        double miny = *(double*)(dataPtr + 12);
                        double maxx = *(double*)(dataPtr + 20);
                        double maxy = *(double*)(dataPtr + 28);
                        feature.EnvelopeWorld.Init(minx, maxx, miny, maxy);
                        feature.CentroidLocal = new SKPoint((float)((minx + maxx) / 2 - offsetX), -(float)((miny + maxy) / 2 - offsetY));
                    }
                    else if (shapeType == 5 || shapeType == 15 || shapeType == 25) // Polygon
                    {
                        feature.IsPolygon = true;
                        feature.Kind = GeometryKind.Polygon;
                        double minx = *(double*)(dataPtr + 4);
                        double miny = *(double*)(dataPtr + 12);
                        double maxx = *(double*)(dataPtr + 20);
                        double maxy = *(double*)(dataPtr + 28);
                        feature.EnvelopeWorld.Init(minx, maxx, miny, maxy);
                        feature.CentroidLocal = new SKPoint((float)((minx + maxx) / 2 - offsetX), -(float)((miny + maxy) / 2 - offsetY));
                    }

                    if (feature.Kind != GeometryKind.Unknown)
                        features.Add(feature);
                }
            }
            finally
            {
                if (ptr != null) shpAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                if (dbfPtr != null)
                {
                    dbfAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    dbfAccessor.Dispose();
                    dbfMmf.Dispose();
                }
            }

            var shpResult = new MemoryMappedShapefile { File = shpMmf, Accessor = shpAccessor };
            return (features, shpResult);
        }
    }
}
