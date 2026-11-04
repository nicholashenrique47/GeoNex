using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        private readonly float _invCellSize;
        private readonly Dictionary<long, List<SKRect>> _occupiedCells;
        private readonly List<RotuloAlocado> _rotulosAlocados;
        private SKRect _screenBounds;
        private bool _hasScreenBounds;
        private float _breathingPadding;

        public IReadOnlyList<RotuloAlocado> RotulosAlocados => _rotulosAlocados;

        public LabelConflictGrid(float cellSize = 56f, int capacidadeInicial = 2048)
        {
            _cellSize = cellSize > 0 ? cellSize : 56f;
            _invCellSize = 1f / _cellSize;
            _occupiedCells = new Dictionary<long, List<SKRect>>(capacidadeInicial);
            _rotulosAlocados = new List<RotuloAlocado>(capacidadeInicial);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ObterChaveCelula(int x, int y) => ((long)x << 32) | (uint)y;

        /// <summary>
        /// Define os limites úteis da tela para impedir que rótulos fiquem cortados nas bordas do viewport.
        /// </summary>
        public void ConfigurarViewport(float larguraFisica, float alturaFisica, float margemBordaPx = 6f, float espacamentoMinimoPx = 2f)
        {
            if (larguraFisica > margemBordaPx * 2f && alturaFisica > margemBordaPx * 2f)
            {
                _screenBounds = new SKRect(margemBordaPx, margemBordaPx, larguraFisica - margemBordaPx, alturaFisica - margemBordaPx);
                _hasScreenBounds = true;
                _breathingPadding = Math.Max(0f, espacamentoMinimoPx);
            }
            else
            {
                _hasScreenBounds = false;
                _breathingPadding = 0f;
            }
        }

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
            _hasScreenBounds = false;
            _breathingPadding = 0f;
        }

        /// <summary>
        /// Verifica se a caixa delimitadora do rótulo colide com algum rótulo já posicionado na grade.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TestarColisao(in SKRect bounds)
        {
            if (_hasScreenBounds)
            {
                if (bounds.Left < _screenBounds.Left ||
                    bounds.Top < _screenBounds.Top ||
                    bounds.Right > _screenBounds.Right ||
                    bounds.Bottom > _screenBounds.Bottom)
                {
                    return true;
                }
            }

            int minX = (int)MathF.Floor(bounds.Left * _invCellSize);
            int maxX = (int)MathF.Floor(bounds.Right * _invCellSize);
            int minY = (int)MathF.Floor(bounds.Top * _invCellSize);
            int maxY = (int)MathF.Floor(bounds.Bottom * _invCellSize);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    long key = ObterChaveCelula(cx, cy);
                    if (_occupiedCells.TryGetValue(key, out var lista))
                    {
                        int count = lista.Count;
                        for (int i = 0; i < count; i++)
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Inserir(in SKRect bounds)
        {
            SKRect padded = _breathingPadding > 0f
                ? new SKRect(
                    bounds.Left - _breathingPadding,
                    bounds.Top - _breathingPadding,
                    bounds.Right + _breathingPadding,
                    bounds.Bottom + _breathingPadding)
                : bounds;

            int minX = (int)MathF.Floor(padded.Left * _invCellSize);
            int maxX = (int)MathF.Floor(padded.Right * _invCellSize);
            int minY = (int)MathF.Floor(padded.Top * _invCellSize);
            int maxY = (int)MathF.Floor(padded.Bottom * _invCellSize);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    long key = ObterChaveCelula(cx, cy);
                    if (!_occupiedCells.TryGetValue(key, out var lista))
                    {
                        lista = new List<SKRect>(4);
                        _occupiedCells[key] = lista;
                    }
                    lista.Add(padded);
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
                    primeiro.RequerLinhaChamada,
                    primeiro.RotacaoGraus);
                _rotulosAlocados.Add(resultado);
                return true;
            }

            // Avalia cada candidato por prioridade
            int count = candidatos.Count;
            for (int i = 0; i < count; i++)
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
                        cand.RequerLinhaChamada,
                        cand.RotacaoGraus);
                    _rotulosAlocados.Add(resultado);
                    return true;
                }
            }

            resultado = default;
            return false;
        }
    }
}
