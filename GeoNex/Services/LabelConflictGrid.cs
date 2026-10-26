using System;
using System.Collections.Generic;
using SkiaSharp;

namespace GeoNex.Services
{
    /// <summary>
    /// Rótulo alocado com sucesso pelo motor de resolução de conflitos para renderização final.
    /// </summary>
    public readonly record struct RotuloAlocado(
        string Texto,
        SKPoint PontoOrigemTexto,
        SKRect Limites,
        SKPoint PontoAncoraFeicao,
        bool DesenharLinhaChamada,
        float RotacaoGraus = 0f
    );

    /// <summary>
    /// Grade espacial em coordenadas de tela de alta performance para resolução de conflitos e anti-colisão em O(1).
    /// Reutilizável entre frames sem alocações no heap (zero-allocation).
    /// </summary>
    public sealed class LabelConflictGrid
    {
        private readonly float _cellSize;
        private readonly Dictionary<long, List<SKRect>> _occupiedCells;
        private readonly List<RotuloAlocado> _rotulosAlocados;

        public IReadOnlyList<RotuloAlocado> RotulosAlocados => _rotulosAlocados;

        public LabelConflictGrid(float cellSize = 56f, int capacidadeInicial = 2048)
        {
            _cellSize = cellSize > 0 ? cellSize : 56f;
            _occupiedCells = new Dictionary<long, List<SKRect>>(capacidadeInicial);
            _rotulosAlocados = new List<RotuloAlocado>(capacidadeInicial);
        }

        private static long ObterChaveCelula(int x, int y) => ((long)x << 32) | (uint)y;

        /// <summary>
        /// Limpa a grade para reutilização no próximo frame sem desalocar memória.
        /// </summary>
        public void Limpar()
        {
            foreach (var list in _occupiedCells.Values)
            {
                list.Clear();
            }
            _rotulosAlocados.Clear();
        }

        /// <summary>
        /// Verifica se a caixa delimitadora do rótulo colide com algum rótulo já posicionado na grade.
        /// </summary>
        public bool TestarColisao(in SKRect bounds)
        {
            int minX = (int)MathF.Floor(bounds.Left / _cellSize);
            int maxX = (int)MathF.Floor(bounds.Right / _cellSize);
            int minY = (int)MathF.Floor(bounds.Top / _cellSize);
            int maxY = (int)MathF.Floor(bounds.Bottom / _cellSize);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    long key = ObterChaveCelula(cx, cy);
                    if (_occupiedCells.TryGetValue(key, out var lista) && lista.Count > 0)
                    {
                        for (int i = 0; i < lista.Count; i++)
                        {
                            if (lista[i].IntersectsWith(bounds))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Insere os limites do rótulo em todas as células de tela sobrepostas.
        /// </summary>
        public void Inserir(in SKRect bounds)
        {
            int minX = (int)MathF.Floor(bounds.Left / _cellSize);
            int maxX = (int)MathF.Floor(bounds.Right / _cellSize);
            int minY = (int)MathF.Floor(bounds.Top / _cellSize);
            int maxY = (int)MathF.Floor(bounds.Bottom / _cellSize);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    long key = ObterChaveCelula(cx, cy);
                    if (!_occupiedCells.TryGetValue(key, out var lista))
                    {
                        lista = new List<SKRect>(2);
                        _occupiedCells[key] = lista;
                    }
                    lista.Add(bounds);
                }
            }
        }

        /// <summary>
        /// Avalia ordenadamente a lista de candidatos por prioridade.
        /// O primeiro candidato sem colisão é imediatamente reservado e alocado.
        /// </summary>
        public bool TentarAlocarCandidato(
            string texto,
            List<CandidatoRotulo> candidatos,
            bool forcarSemColisao,
            out RotuloAlocado resultado)
        {
            if (candidatos == null || candidatos.Count == 0)
            {
                resultado = default;
                return false;
            }

            if (!forcarSemColisao)
            {
                // Anti-colisão desligada: aloca sempre o primeiro candidato preferencial
                var primeiro = candidatos[0];
                resultado = new RotuloAlocado(
                    texto,
                    primeiro.PontoOrigemTexto,
                    primeiro.LimitesTexto,
                    primeiro.PontoAncora,
                    primeiro.RequerLinhaChamada);
                _rotulosAlocados.Add(resultado);
                return true;
            }

            // Avalia cada candidato por prioridade
            for (int i = 0; i < candidatos.Count; i++)
            {
                var cand = candidatos[i];
                if (!TestarColisao(cand.LimitesTexto))
                {
                    Inserir(cand.LimitesTexto);
                    resultado = new RotuloAlocado(
                        texto,
                        cand.PontoOrigemTexto,
                        cand.LimitesTexto,
                        cand.PontoAncora,
                        cand.RequerLinhaChamada);
                    _rotulosAlocados.Add(resultado);
                    return true;
                }
            }

            resultado = default;
            return false;
        }
    }
}
