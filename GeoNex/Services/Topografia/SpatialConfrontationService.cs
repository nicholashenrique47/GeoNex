namespace GeoNex.Services.Topografia;

using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using GeoNex.Topografia.Domain;

public sealed class SpatialConfrontationService
{
    private readonly GeometryFactory _geometryFactory = new();

    /// <summary>
    /// Detecta automaticamente os confrontantes de cada alinhamento perimétrico cruzando espacialmente
    /// com os polígonos das outras camadas ativas no mapa.
    /// </summary>
    public void DetectarConfrontantesEspaciais(
        ImovelCertificacaoSigef imovel,
        IEnumerable<(Geometry Geometria, Dictionary<string, object> Atributos)> poligonosVizinhos)
    {
        if (imovel is null || imovel.Segmentos.Count == 0 || poligonosVizinhos is null)
            return;

        foreach (var seg in imovel.Segmentos)
        {
            var p1 = new Coordinate(seg.De.EsteUtm, seg.De.NorteUtm);
            var p2 = new Coordinate(seg.Para.EsteUtm, seg.Para.NorteUtm);

            if (p1.Distance(p2) < 0.001) continue;

            var linha = _geometryFactory.CreateLineString(new[] { p1, p2 });
            var envelope = linha.EnvelopeInternal;
            envelope.ExpandBy(0.50); // Buffer de 50cm

            foreach (var (geomVizinho, atributos) in poligonosVizinhos)
            {
                if (geomVizinho is null || geomVizinho.IsEmpty) continue;

                if (!envelope.Intersects(geomVizinho.EnvelopeInternal))
                    continue;

                // Teste de proximidade linear
                if (linha.Distance(geomVizinho) <= 0.50)
                {
                    ExtrairAtributosConfrontante(seg, atributos);
                    break;
                }
            }
        }
    }

    private static void ExtrairAtributosConfrontante(SegmentoPerimetro seg, Dictionary<string, object> atributos)
    {
        if (atributos is null) return;

        string[] chavesNome = { "IMOVEL", "NOME", "DENOMINA", "NOME_IMOVE", "GLEBA", "PROPRIEDAD" };
        string[] chavesMatricula = { "MATRICULA", "MATR", "REGISTRO", "TRANSCRICA", "LIVRO" };
        string[] chavesProprietario = { "PROPRIETAR", "TITULAR", "DONO", "POSSEIRO", "NOME_PROP" };

        foreach (var k in chavesNome)
        {
            if (ObterValorAtributo(atributos, k, out string val) && !string.IsNullOrWhiteSpace(val))
            {
                seg.NomeConfrontante = val.Trim();
                break;
            }
        }

        foreach (var k in chavesMatricula)
        {
            if (ObterValorAtributo(atributos, k, out string val) && !string.IsNullOrWhiteSpace(val))
            {
                seg.MatriculaConfrontante = val.Trim();
                break;
            }
        }

        foreach (var k in chavesProprietario)
        {
            if (ObterValorAtributo(atributos, k, out string val) && !string.IsNullOrWhiteSpace(val))
            {
                seg.ProprietarioConfrontante = val.Trim();
                break;
            }
        }
    }

    private static bool ObterValorAtributo(Dictionary<string, object> atributos, string prefixo, out string valor)
    {
        valor = string.Empty;
        foreach (var kvp in atributos)
        {
            if (kvp.Key.Contains(prefixo, StringComparison.OrdinalIgnoreCase))
            {
                valor = kvp.Value?.ToString() ?? string.Empty;
                return true;
            }
        }
        return false;
    }
}
