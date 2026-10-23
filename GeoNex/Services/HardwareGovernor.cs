using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GeoNex.Services;

/// <summary>
/// Pilar 1 &amp; 2: Hardware Governor e Operações de Memória/SIMD de Baixo Nível.
/// Detecta topologia de CPU, cache e capacidades SIMD (AVX2 / AVX-512), gerencia
/// DMA Prefetching de páginas mapeadas (PrefetchVirtualMemory) e operações SIMD de pixels.
/// </summary>
public static unsafe class HardwareGovernor
{
    private static readonly Lazy<HardwareTopology> _topology = new(ProbeTopology);
    public static HardwareTopology Topology => _topology.Value;

    public readonly record struct HardwareTopology(
        int LogicalCores,
        int PhysicalCores,
        int PerformanceCores,
        int EfficiencyCores,
        int L2CacheBytesPerCore,
        long L3CacheBytesTotal,
        bool HasAvx2,
        bool HasAvx512);

    private static HardwareTopology ProbeTopology()
    {
        int logical = Math.Max(1, Environment.ProcessorCount);
        bool avx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        bool avx512 = System.Runtime.Intrinsics.X86.Avx512F.IsSupported;

        int physical = Math.Max(1, logical / 2);
        int pCores = physical;
        int eCores = 0;
        int l2PerCore = 512 * 1024;
        long l3Total = 8L * 1024 * 1024;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                ProbeWindowsProcessorTopology(ref physical, ref pCores, ref eCores, ref l2PerCore, ref l3Total);
            }
            catch
            {
                // Fallback seguro caso a chamada Win32 falhe em ambientes restritos.
            }
        }

        return new HardwareTopology(
            logical,
            Math.Max(1, physical),
            Math.Max(1, pCores),
            Math.Max(0, eCores),
            Math.Clamp(l2PerCore, 128 * 1024, 16 * 1024 * 1024),
            Math.Max(1024 * 1024, l3Total),
            avx2,
            avx512);
    }

    /// <summary>
    /// Retorna o tamanho ideal de lote de feições para caber em ~75% do Cache L2 por núcleo.
    /// </summary>
    public static int OptimalVectorBatchSize
    {
        get
        {
            int l2 = Topology.L2CacheBytesPerCore;
            // ~64 bytes de trabalho médio por feição no lote nativo
            return Math.Clamp((l2 * 3 / 4) / 64, 4096, 32768);
        }
    }

    /// <summary>
    /// Preenche o canal Alpha (0xFF000000) em um buffer RGBA8888 usando instruções SIMD (Vector256/Vector)
    /// eliminando o loop escalar lento pixel a pixel após ReadRaster de 3 bandas.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void FillOpaqueAlphaSimd(IntPtr bufferPtr, int width, int height, int rowBytes = 0)
    {
        if (bufferPtr == IntPtr.Zero || width <= 0 || height <= 0) return;
        int stride = rowBytes > 0 ? rowBytes : width * 4;
        byte* basePtr = (byte*)bufferPtr.ToPointer();

        if (stride == width * 4)
        {
            long totalPixels = (long)width * height;
            if (totalPixels <= int.MaxValue)
            {
                FillAlphaSpan(new Span<uint>(basePtr, (int)totalPixels));
                return;
            }
        }

        for (int y = 0; y < height; y++)
        {
            FillAlphaSpan(new Span<uint>(basePtr + (long)y * stride, width));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void FillAlphaSpan(Span<uint> pixels)
    {
        const uint alphaMask = 0xFF000000u;
        int i = 0;
        int vecCount = Vector<uint>.Count;
        if (Vector.IsHardwareAccelerated && pixels.Length >= vecCount)
        {
            var maskVec = new Vector<uint>(alphaMask);
            int limit = pixels.Length - vecCount;
            ref uint start = ref MemoryMarshal.GetReference(pixels);
            for (; i <= limit; i += vecCount)
            {
                ref uint current = ref Unsafe.Add(ref start, i);
                var v = Vector.LoadUnsafe(ref current);
                (v | maskVec).StoreUnsafe(ref current);
            }
        }

        for (; i < pixels.Length; i++)
        {
            pixels[i] |= alphaMask;
        }
    }

    /// <summary>
    /// Aciona o DMA/read-ahead do Windows (PrefetchVirtualMemory) em uma região contígua mapeada
    /// (como arquivos .shp e .dbf inteiros em disco local ou rede SMB), evitando milhares de Page Faults de 4 KB.
    /// </summary>
    public static void PrefetchMappedMemory(byte* basePtr, long byteLength, long startOffset = 0)
    {
        if (!OperatingSystem.IsWindows() || basePtr == null || byteLength <= startOffset || startOffset < 0)
            return;

        try
        {
            const long maxPrefetchBytes = 512L * 1024 * 1024;
            const long chunkBytes = 32L * 1024 * 1024;
            long remaining = Math.Min(byteLength - startOffset, maxPrefetchBytes);
            if (remaining <= 0) return;

            Span<Win32MemoryRangeEntry> ranges = stackalloc Win32MemoryRangeEntry[16];
            int rangeCount = 0;
            long offset = startOffset;
            while (remaining > 0 && rangeCount < ranges.Length)
            {
                long chunk = Math.Min(chunkBytes, remaining);
                ranges[rangeCount++] = new Win32MemoryRangeEntry
                {
                    VirtualAddress = (nint)(basePtr + offset),
                    NumberOfBytes = (nuint)chunk
                };
                offset += chunk;
                remaining -= chunk;
            }

            if (rangeCount > 0)
            {
                fixed (Win32MemoryRangeEntry* pRanges = ranges)
                {
                    PrefetchVirtualMemory(GetCurrentProcess(), (nuint)rangeCount, pRanges, 0);
                }
            }
        }
        catch
        {
            // Prefetch é consultivo; falhas são silenciosamente ignoradas.
        }
    }

    /// <summary>
    /// Aciona o DMA do Windows (PrefetchVirtualMemory) nas páginas do arquivo .shp mapeado
    /// para evitar Page Faults síncronos durante a leitura nativa de lotes vetoriais.
    /// </summary>
    public static void PrefetchShapefileOffsets(byte* basePtr, long fileLength, ReadOnlySpan<long> offsets)
    {
        if (!OperatingSystem.IsWindows() || basePtr == null || fileLength <= 100 || offsets.Length < 64)
            return;

        if (fileLength <= 256L * 1024 * 1024 && offsets.Length >= 2048)
        {
            PrefetchMappedMemory(basePtr, fileLength);
            return;
        }

        try
        {
            // Agrupa janelas contíguas ou próximas (dentro de 128 KB) para minimizar entradas de syscall
            const int maxEntries = 128;
            const long mergeWindowBytes = 128 * 1024;
            const long defaultRecordReadWindow = 16384;

            Span<Win32MemoryRangeEntry> ranges = stackalloc Win32MemoryRangeEntry[maxEntries];
            int rangeCount = 0;

            int step = Math.Max(1, offsets.Length / maxEntries);
            long currentStart = -1;
            long currentEnd = -1;

            for (int i = 0; i < offsets.Length && rangeCount < maxEntries; i += step)
            {
                long off = offsets[i];
                if (off < 100 || off >= fileLength) continue;
                long end = Math.Min(fileLength, off + defaultRecordReadWindow);

                if (currentStart < 0)
                {
                    currentStart = off;
                    currentEnd = end;
                }
                else if (off >= currentStart && off <= currentEnd + mergeWindowBytes)
                {
                    if (end > currentEnd) currentEnd = end;
                }
                else
                {
                    ranges[rangeCount++] = new Win32MemoryRangeEntry
                    {
                        VirtualAddress = (nint)(basePtr + currentStart),
                        NumberOfBytes = (nuint)(currentEnd - currentStart)
                    };
                    currentStart = off;
                    currentEnd = end;
                }
            }

            if (currentStart >= 0 && rangeCount < maxEntries)
            {
                ranges[rangeCount++] = new Win32MemoryRangeEntry
                {
                    VirtualAddress = (nint)(basePtr + currentStart),
                    NumberOfBytes = (nuint)(currentEnd - currentStart)
                };
            }

            if (rangeCount > 0)
            {
                fixed (Win32MemoryRangeEntry* pRanges = ranges)
                {
                    PrefetchVirtualMemory(GetCurrentProcess(), (nuint)rangeCount, pRanges, 0);
                }
            }
        }
        catch
        {
            // Prefetch é puramente consultivo; falhas são silenciosamente ignoradas.
        }
    }

    private static void ProbeWindowsProcessorTopology(
        ref int physicalCores,
        ref int pCores,
        ref int eCores,
        ref int l2PerCore,
        ref long l3Total)
    {
        uint returnLength = 0;
        GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationAll, IntPtr.Zero, ref returnLength);
        if (returnLength == 0 || returnLength > 1024 * 1024) return;

        byte[] buffer = new byte[returnLength];
        fixed (byte* ptr = buffer)
        {
            if (!GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationAll, (IntPtr)ptr, ref returnLength))
                return;

            int coreCount = 0;
            int maxEfficiencyClass = 0;
            Span<byte> coreEfficiency = stackalloc byte[256];
            int maxL2 = 0;
            long sumL3 = 0;

            uint offset = 0;
            while (offset + 8 <= returnLength)
            {
                var info = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)(ptr + offset);
                if (info->Size == 0) break;

                if (info->Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                {
                    byte eff = info->Processor.EfficiencyClass;
                    if (eff > maxEfficiencyClass) maxEfficiencyClass = eff;
                    if (coreCount < coreEfficiency.Length)
                        coreEfficiency[coreCount] = eff;
                    coreCount++;
                }
                else if (info->Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache)
                {
                    byte level = info->Cache.Level;
                    uint cacheSize = info->Cache.CacheSize;
                    if (level == 2 && cacheSize > maxL2)
                        maxL2 = (int)cacheSize;
                    else if (level == 3)
                        sumL3 += cacheSize;
                }

                offset += info->Size;
            }

            if (coreCount > 0)
            {
                physicalCores = coreCount;
                if (maxEfficiencyClass > 0)
                {
                    int perf = 0, eff = 0;
                    int tracked = Math.Min(coreCount, coreEfficiency.Length);
                    for (int i = 0; i < tracked; i++)
                    {
                        if (coreEfficiency[i] == maxEfficiencyClass) perf++;
                        else eff++;
                    }
                    pCores = Math.Max(1, perf);
                    eCores = eff;
                }
                else
                {
                    pCores = coreCount;
                    eCores = 0;
                }
            }

            if (maxL2 >= 64 * 1024) l2PerCore = maxL2;
            if (sumL3 >= 512 * 1024) l3Total = sumL3;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32MemoryRangeEntry
    {
        public nint VirtualAddress;
        public nuint NumberOfBytes;
    }

    private enum LOGICAL_PROCESSOR_RELATIONSHIP : int
    {
        RelationProcessorCore = 0,
        RelationNumaNode = 1,
        RelationCache = 2,
        RelationProcessorPackage = 3,
        RelationGroup = 4,
        RelationAll = 0xffff
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_RELATIONSHIP
    {
        public byte Flags;
        public byte EfficiencyClass;
        public fixed byte Reserved[20];
        public ushort GroupCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CACHE_RELATIONSHIP
    {
        public byte Level;
        public byte Associativity;
        public ushort LineSize;
        public uint CacheSize;
        public int Type;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
    {
        [FieldOffset(0)]
        public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
        [FieldOffset(4)]
        public uint Size;
        [FieldOffset(8)]
        public PROCESSOR_RELATIONSHIP Processor;
        [FieldOffset(8)]
        public CACHE_RELATIONSHIP Cache;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrefetchVirtualMemory(
        nint hProcess,
        nuint NumberOfEntries,
        Win32MemoryRangeEntry* VirtualAddresses,
        uint Flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType,
        IntPtr Buffer,
        ref uint ReturnedLength);
}
