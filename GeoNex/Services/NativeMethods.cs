using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GeoNex.Services
{
    public static class NativeMethods
    {
        private const string NativeLibraryFileName = "GeoNexNative.dll";
        public const uint ExpectedAbiVersion = 4;
        private static readonly nint NativeLibraryHandle;

        public static string LoadedPath { get; private set; } = string.Empty;
        public static string LoadedSha256 { get; private set; } = string.Empty;
        public static uint AbiVersion { get; private set; }

        static NativeMethods()
        {
            LoadedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, NativeLibraryFileName));
            if (!File.Exists(LoadedPath))
                throw new DllNotFoundException($"A DLL nativa esperada nao existe em '{LoadedPath}'.");

            nint loadedHandle = NativeLibrary.Load(LoadedPath);
            NativeLibraryHandle = loadedHandle;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveNativeImport);
                AbiVersion = GetGeoNexNativeAbiVersion();
                if (AbiVersion != ExpectedAbiVersion)
                {
                    throw new BadImageFormatException(
                        $"ABI GeoNexNative incompatível: esperado={ExpectedAbiVersion}, carregado={AbiVersion}, caminho='{LoadedPath}'.");
                }

                using FileStream stream = File.OpenRead(LoadedPath);
                LoadedSha256 = Convert.ToHexString(SHA256.HashData(stream));
                DebugLogger.Log($"GeoNexNative ABI={AbiVersion} SHA256={LoadedSha256} Path={LoadedPath}");
            }
            catch
            {
                NativeLibrary.Free(loadedHandle);
                throw;
            }
        }

        private static nint ResolveNativeImport(
            string libraryName,
            System.Reflection.Assembly assembly,
            DllImportSearchPath? searchPath) =>
            string.Equals(Path.GetFileName(libraryName), NativeLibraryFileName, StringComparison.OrdinalIgnoreCase)
                ? NativeLibraryHandle
                : 0;

        [DllImport(NativeLibraryFileName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint GetGeoNexNativeAbiVersion();

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern nint CreateRenderCancellation();

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void CancelRender(nint handle);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void DestroyRenderCancellation(nint handle);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int ProcessShapeBatchV4(
            byte* basePtr, long fileLength, nint cancellation,
            long* offsets, int count, float* outBuffer, int maxFloats,
            double offsetX, double offsetY, float zoomReal, float tolerancePixels,
            float microLodPixels, float viewportLeft, float viewportTop,
            float viewportRight, float viewportBottom, byte* microGrid,
            int microGridWidth, int microGridHeight, out int processedFeatures,
            out int requiredFloats, out int emittedParts, out int emittedVertices,
            out int emittedMicroFeatures);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void ParseShapefilePartAVX2(
            byte* inBytes, 
            float* outPoints, 
            int numPoints, 
            double offsetX, 
            double offsetY);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int QuantizeGeometry(
            float* inputPoints, int numPoints,
            float* outputPoints,
            float resolution);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int ProcessPolygonBatchAVX2(
            byte* basePtr,
            int* offsets,
            int count,
            float* outBuffer, int maxFloats,
            double offsetX,
            double offsetY,
            float zoomReal,
            float resolution);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int ProcessShapeBatchV2(
            byte* basePtr,
            long* offsets,
            int count,
            float* outBuffer,
            int maxFloats,
            double offsetX,
            double offsetY,
            float zoomReal,
            float tolerancePixels,
            float microLodPixels,
            float viewportLeft,
            float viewportTop,
            float viewportRight,
            float viewportBottom,
            out int processedFeatures,
            out int requiredFloats,
            out int emittedParts,
            out int emittedVertices);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int ProcessShapeBatchV3(
            byte* basePtr,
            long* offsets,
            int count,
            float* outBuffer,
            int maxFloats,
            double offsetX,
            double offsetY,
            float zoomReal,
            float tolerancePixels,
            float microLodPixels,
            float viewportLeft,
            float viewportTop,
            float viewportRight,
            float viewportBottom,
            byte* microGrid,
            int microGridWidth,
            int microGridHeight,
            out int processedFeatures,
            out int requiredFloats,
            out int emittedParts,
            out int emittedVertices,
            out int emittedMicroFeatures);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe nint CreateShapeSpatialIndex(
            byte* basePtr,
            long fileLength,
            long* offsets,
            double* bounds,
            byte* kinds,
            int count,
            int workerCount);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void DestroyShapeSpatialIndex(nint handle);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int QueryShapeSpatialIndex(
            nint handle,
            double queryMinX,
            double queryMinY,
            double queryMaxX,
            double queryMaxY,
            int* results,
            int resultCapacity,
            out int requiredCount);

        [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern long GetShapeSpatialIndexBytes(
            nint handle,
            out int gridCells,
            out int gridEntries,
            out int oversizedFeatures);
    }
}
