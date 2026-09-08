using System;
using System.IO;

namespace PerfTest
{
    class Program
    {
        static unsafe void Main(string[] args)
        {
            string path = @"C:\Users\Windows 10\Desktop\SHP S\LOTEAMENTOS_LOTES.shp";
            byte[] shpData = File.ReadAllBytes(path);
            fixed (byte* ptr = shpData)
            {
                int offset = 100;
                byte* recPtr = ptr + offset;
                byte* dataPtr = recPtr + 8;
                int shapeType = *(int*)dataPtr;
                int numParts = *(int*)(dataPtr + 36);
                int numPoints = *(int*)(dataPtr + 40);
                Console.WriteLine($"ShapeType: {shapeType}, NumParts: {numParts}, NumPoints: {numPoints}");
            }
        }
    }
}
