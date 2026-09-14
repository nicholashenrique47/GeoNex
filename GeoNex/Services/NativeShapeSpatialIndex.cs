using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using NetTopologySuite.Geometries;

namespace GeoNex.Services
{
    /// <summary>
    /// Índice espacial compacto construído e consultado em C++. Substitui milhões
    /// de ItemBoundable/Envelope da STRtree por arrays contíguos e uma grade CSR.
    /// </summary>
    public sealed unsafe class NativeShapeSpatialIndex : IDisposable
    {
        private readonly List<CompiledFeature> _features;
        private NativeIndexHandle? _handle;
        private const int MinimumQueryCapacity = 8_192; // 32 KiB of feature IDs.
        private int _queryCapacityHint = MinimumQueryCapacity;

        private sealed class NativeIndexHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public NativeIndexHandle() : base(ownsHandle: true) { }
            public void Initialize(nint value) => SetHandle(value);

            protected override bool ReleaseHandle()
            {
                NativeMethods.DestroyShapeSpatialIndex(handle);
                return true;
            }
        }

        public long NativeBytes { get; }
        public int GridCells { get; }
        public int GridEntries { get; }
        public int OversizedFeatures { get; }
        public NativeShapeSpatialIndex? UniformRenderIndex { get; private set; }
        public IList<CompiledFeature> AllFeatures => _features.AsReadOnly();

        public NativeShapeSpatialIndex(MemoryMappedShapefile shapefile, List<CompiledFeature> features)
            : this(shapefile, features, buildCatalog: true) { }

        private NativeShapeSpatialIndex(MemoryMappedShapefile shapefile, List<CompiledFeature> features, bool buildCatalog)
        {
            ArgumentNullException.ThrowIfNull(shapefile);
            ArgumentNullException.ThrowIfNull(features);
            _features = features;

            if (features.Count == 0) return;

            var nativeHandle = new NativeIndexHandle();
            long[] offsets = ArrayPool<long>.Shared.Rent(features.Count);
            // Sem reprojeção, o C++ lê os envelopes diretamente do SHP mapeado.
            // Com reprojeção on-the-fly, o índice deve usar os envelopes no CRS do
            // projeto; misturar viewport projetado com bounds crus omite feições.
            bool useProjectedBounds = shapefile.TransformLocal != null;
            try
            {
                double[]? projectedBounds = useProjectedBounds
                    ? GC.AllocateUninitializedArray<double>(checked(features.Count * 4))
                    : null;
                byte[]? projectedKinds = useProjectedBounds
                    ? GC.AllocateUninitializedArray<byte>(features.Count)
                    : null;
                for (int i = 0; i < features.Count; ++i)
                {
                    var feature = features[i];
                    offsets[i] = feature.DataOffset;
                    if (projectedBounds != null && projectedKinds != null)
                    {
                        int cursor = i * 4;
                        projectedBounds[cursor] = feature.EnvelopeWorld.MinX;
                        projectedBounds[cursor + 1] = feature.EnvelopeWorld.MinY;
                        projectedBounds[cursor + 2] = feature.EnvelopeWorld.MaxX;
                        projectedBounds[cursor + 3] = feature.EnvelopeWorld.MaxY;
                        projectedKinds[i] = (byte)feature.Kind;
                    }
                }
                fixed (long* offsetPointer = offsets)
                fixed (double* boundsPointer = projectedBounds)
                fixed (byte* kindsPointer = projectedKinds)
                {
                    nativeHandle.Initialize(NativeMethods.CreateShapeSpatialIndex(
                        shapefile.ShpPointer,
                        shapefile.FileLength,
                        offsetPointer,
                        boundsPointer,
                        kindsPointer,
                        features.Count,
                        GeoNexHardware.WorkersFor(features.Count)));
                }

                if (nativeHandle.IsInvalid)
                    throw new InvalidOperationException("O índice espacial nativo não pôde ser construído.");

                NativeBytes = NativeMethods.GetShapeSpatialIndexBytes(
                    nativeHandle.DangerousGetHandle(), out int cells, out int entries, out int oversized);
                GridCells = cells;
                GridEntries = entries;
                OversizedFeatures = oversized;
                _handle = nativeHandle;
                if (buildCatalog && !useProjectedBounds && features.Count >= ShapefileRenderCatalog.MinimumFeatures)
                {
                    int[]? representatives = ShapefileRenderCatalog.Build(
                        shapefile.ShpPointer, shapefile.FileLength, offsets.AsSpan(0, features.Count));
                    if (representatives != null)
                    {
                        var renderFeatures = new List<CompiledFeature>(representatives.Length);
                        foreach (int index in representatives) renderFeatures.Add(features[index]);
                        UniformRenderIndex = new NativeShapeSpatialIndex(shapefile, renderFeatures, buildCatalog: false);
                        Console.WriteLine($"[GEONEX PERF] Geometria compartilhada: {features.Count:N0} registros / {renderFeatures.Count:N0} geometrias para prévia (quadro final preserva todos os registros).");
                    }
                }
            }
            catch
            {
                nativeHandle.Dispose();
                throw;
            }
            finally
            {
                ArrayPool<long>.Shared.Return(offsets);
            }
        }

        public NativeFeatureQuery Query(Envelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            NativeIndexHandle? nativeHandle = Volatile.Read(ref _handle);
            if (nativeHandle == null || envelope.IsNull || _features.Count == 0)
                return NativeFeatureQuery.Empty(_features);

            bool addedReference = false;
            int[]? indices = null;
            try
            {
                // Dispose may close the captured handle before AddRef. Once the
                // reference succeeds, destruction waits for both native calls.
                try { nativeHandle.DangerousAddRef(ref addedReference); }
                catch (ObjectDisposedException) { return NativeFeatureQuery.Empty(_features); }

                nint handle = nativeHandle.DangerousGetHandle();
                int capacity = Math.Min(_features.Count,
                    Math.Max(MinimumQueryCapacity, Volatile.Read(ref _queryCapacityHint)));
                indices = ArrayPool<int>.Shared.Rent(Math.Max(capacity, 16));
                int copied;
                int required;
                fixed (int* resultPointer = indices)
                {
                    copied = NativeMethods.QueryShapeSpatialIndex(
                        handle, envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY,
                        resultPointer, indices.Length, out required);
                }

                if (required > indices.Length)
                {
                    // Acquire before returning the old buffer: a failed Rent
                    // must leave exactly one buffer owned by this finally block.
                    int[] replacement = ArrayPool<int>.Shared.Rent(required);
                    int[] previous = indices;
                    indices = replacement;
                    ArrayPool<int>.Shared.Return(previous);
                    fixed (int* resultPointer = indices)
                    {
                        copied = NativeMethods.QueryShapeSpatialIndex(
                            handle, envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY,
                            resultPointer, indices.Length, out required);
                    }
                }
                Interlocked.Exchange(ref _queryCapacityHint,
                    Math.Min(_features.Count, Math.Max(MinimumQueryCapacity, required)));
                var result = new NativeFeatureQuery(_features, indices, copied);
                indices = null;
                return result;
            }
            finally
            {
                if (indices != null) ArrayPool<int>.Shared.Return(indices);
                if (addedReference) nativeHandle.DangerousRelease();
            }
        }

        public void Dispose()
        {
            UniformRenderIndex?.Dispose();
            Interlocked.Exchange(ref _handle, null)?.Dispose();
        }
    }

    public sealed class NativeFeatureQuery : IList<CompiledFeature>, IDisposable
    {
        private readonly List<CompiledFeature> _features;
        private int[]? _indices;

        internal NativeFeatureQuery(List<CompiledFeature> features, int[] indices, int count)
        {
            _features = features;
            _indices = indices;
            Count = count;
        }

        internal static NativeFeatureQuery Empty(List<CompiledFeature> features) =>
            new(features, Array.Empty<int>(), 0);

        public int Count { get; }
        public bool IsReadOnly => true;

        public CompiledFeature this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count || _indices == null)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _features[_indices[index]];
            }
            set => throw new NotSupportedException();
        }

        public IEnumerator<CompiledFeature> GetEnumerator()
        {
            for (int i = 0; i < Count; ++i) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int IndexOf(CompiledFeature item)
        {
            for (int i = 0; i < Count; ++i)
                if (ReferenceEquals(this[i], item)) return i;
            return -1;
        }

        public bool Contains(CompiledFeature item) => IndexOf(item) >= 0;
        public void CopyTo(CompiledFeature[] array, int arrayIndex)
        {
            for (int i = 0; i < Count; ++i) array[arrayIndex + i] = this[i];
        }

        public void Dispose()
        {
            int[]? indices = System.Threading.Interlocked.Exchange(ref _indices, null);
            if (indices is { Length: > 0 }) ArrayPool<int>.Shared.Return(indices);
        }

        public void Add(CompiledFeature item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, CompiledFeature item) => throw new NotSupportedException();
        public bool Remove(CompiledFeature item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }

    internal static class GeoNexHardware
    {
        public static int IndexWorkerCount => VectorRuntimeResources.Current.Workers;
        public static int WorkersFor(int featureCount) => VectorRuntimeResources.Current.WorkersFor(featureCount);
    }
}
