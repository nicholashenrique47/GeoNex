using System;
using System.Collections.Generic;
using System.Linq;

namespace GeoNex.Services
{
    /// <summary>
    /// Catálogo central singleton de algoritmos de geoprocessamento registrados no GeoNex.
    /// </summary>
    public sealed class ProcessingAlgorithmRegistry
    {
        private readonly Dictionary<string, IGeoprocessingAlgorithm> _algoritmos = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<IGeoprocessingAlgorithm>> _categorias = new(StringComparer.OrdinalIgnoreCase);

        public void Registrar(IGeoprocessingAlgorithm algoritmo)
        {
            if (algoritmo == null) throw new ArgumentNullException(nameof(algoritmo));
            _algoritmos[algoritmo.Id] = algoritmo;

            if (!_categorias.TryGetValue(algoritmo.Categoria, out var lista))
            {
                lista = new List<IGeoprocessingAlgorithm>();
                _categorias[algoritmo.Categoria] = lista;
            }
            if (!lista.Any(a => string.Equals(a.Id, algoritmo.Id, StringComparison.OrdinalIgnoreCase)))
            {
                lista.Add(algoritmo);
            }
        }

        public IGeoprocessingAlgorithm? ObterPorId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return _algoritmos.GetValueOrDefault(id);
        }

        public IReadOnlyDictionary<string, List<IGeoprocessingAlgorithm>> ObterCategorias() => _categorias;

        public IReadOnlyCollection<IGeoprocessingAlgorithm> ObterTodos() => _algoritmos.Values;

        public IEnumerable<IGeoprocessingAlgorithm> Buscar(string? termo)
        {
            if (string.IsNullOrWhiteSpace(termo))
                return _algoritmos.Values;

            var termoNorm = termo.Trim().ToLowerInvariant();
            return _algoritmos.Values.Where(a =>
                a.Nome.ToLowerInvariant().Contains(termoNorm) ||
                a.Categoria.ToLowerInvariant().Contains(termoNorm) ||
                a.Descricao.ToLowerInvariant().Contains(termoNorm) ||
                a.Tags.Any(t => t.ToLowerInvariant().Contains(termoNorm)));
        }
    }
}
