namespace GeoNex.Services.Topografia.Validation;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GeoNex.Topografia.Domain;

public sealed class TopografiaForensicValidator
{
    private static readonly Regex CredenciadoRegex = new(@"^[A-Z0-9]{3}$", RegexOptions.Compiled);
    private static readonly Regex CodigoVerticeRegex = new(@"^[A-Z0-9]{3}-[MPV]-[0-9]{4}$", RegexOptions.Compiled);

    public List<AuditoriaTopograficaItem> ExecutarAuditoria(ImovelCertificacaoSigef imovel)
    {
        var resultados = new List<AuditoriaTopograficaItem>();
        if (imovel is null) return resultados;

        // 1. Validar Código do Credenciado
        if (string.IsNullOrWhiteSpace(imovel.CredenciadoCodigoIncra) || !CredenciadoRegex.IsMatch(imovel.CredenciadoCodigoIncra))
        {
            resultados.Add(new AuditoriaTopograficaItem
            {
                Codigo = "SIGEF_ERR_005",
                Gravidade = "AVISO",
                Mensagem = "O código do credenciado INCRA deve conter exatamente 3 caracteres alfanuméricos (ex: ABC)."
            });
        }

        // 2. Validar Quantidade Mínima de Vértices
        if (imovel.Vertices.Count < 3)
        {
            resultados.Add(new AuditoriaTopograficaItem
            {
                Codigo = "SIGEF_ERR_007",
                Gravidade = "ERRO_CRITICO",
                Mensagem = "O perímetro possui menos de 3 vértices distintos. O polígono não fecha."
            });
            return resultados;
        }

        // 3. Validar Duplicidade de Vértices (Spikes < 0.05m)
        for (int i = 0; i < imovel.Vertices.Count; i++)
        {
            var v1 = imovel.Vertices[i];
            var v2 = imovel.Vertices[(i + 1) % imovel.Vertices.Count];

            double dX = v2.EsteUtm - v1.EsteUtm;
            double dY = v2.NorteUtm - v1.NorteUtm;
            double dist = Math.Sqrt(dX * dX + dY * dY);

            if (dist < 0.05)
            {
                resultados.Add(new AuditoriaTopograficaItem
                {
                    Codigo = "SIGEF_ERR_003",
                    Gravidade = "ERRO_CRITICO",
                    Mensagem = $"Vértices consecutivos com distância menor que 5cm ({v1.Codigo} e {v2.Codigo}). O robô do SIGEF rejeitará como duplicidade.",
                    CoordX = v1.EsteUtm,
                    CoordY = v1.NorteUtm
                });
            }
        }

        // 4. Validar Sigmas Posicionais Máximos (Tolerâncias NTGIR)
        foreach (var v in imovel.Vertices)
        {
            if (v.Tipo == TipoVertice.M && (v.SigmaX > 0.50 || v.SigmaY > 0.50))
            {
                resultados.Add(new AuditoriaTopograficaItem
                {
                    Codigo = "SIGEF_ERR_004",
                    Gravidade = "AVISO",
                    Mensagem = $"O marco {v.Codigo} possui desvio padrão ({Math.Max(v.SigmaX, v.SigmaY):F2}m) superior à tolerância máxima do INCRA para marco físico (0,50m).",
                    CoordX = v.EsteUtm,
                    CoordY = v.NorteUtm
                });
            }
            else if (v.Tipo == TipoVertice.V && (v.SigmaX > 3.00 || v.SigmaY > 3.00))
            {
                resultados.Add(new AuditoriaTopograficaItem
                {
                    Codigo = "SIGEF_ERR_004",
                    Gravidade = "AVISO",
                    Mensagem = $"O vértice virtual {v.Codigo} possui desvio padrão ({Math.Max(v.SigmaX, v.SigmaY):F2}m) superior a 3,00m.",
                    CoordX = v.EsteUtm,
                    CoordY = v.NorteUtm
                });
            }
        }

        // 5. Validar Preenchimento de Confrontantes
        int confrontantesIndefinidos = 0;
        foreach (var seg in imovel.Segmentos)
        {
            if (string.IsNullOrWhiteSpace(seg.NomeConfrontante) || seg.NomeConfrontante.Contains("A DEFINIR", StringComparison.OrdinalIgnoreCase))
            {
                confrontantesIndefinidos++;
            }
        }

        if (confrontantesIndefinidos > 0)
        {
            resultados.Add(new AuditoriaTopograficaItem
            {
                Codigo = "SIGEF_ERR_006",
                Gravidade = "AVISO",
                Mensagem = $"Existem {confrontantesIndefinidos} alinhamentos com confrontante 'A DEFINIR'. Recomenda-se preencher a denominação dos vizinhos antes de submeter ao SIGEF."
            });
        }

        // 6. Validar Consistência do Fuso UTM com Longitude Média
        if (imovel.Vertices.Count > 0)
        {
            double somaLon = 0;
            foreach (var v in imovel.Vertices) somaLon += v.LongitudeDecimal;
            double lonMedia = somaLon / imovel.Vertices.Count;
            int fusoEsperado = (int)Math.Floor((lonMedia + 180.0) / 6.0) + 1;

            if (fusoEsperado != imovel.FusoUtm)
            {
                resultados.Add(new AuditoriaTopograficaItem
                {
                    Codigo = "SIGEF_ERR_008",
                    Gravidade = "AVISO",
                    Mensagem = $"O Fuso UTM selecionado ({imovel.FusoUtm}) difere do fuso geográfico natural da gleba ({fusoEsperado}). Verifique se o projeto foi planejado em fuso adjacente estendido."
                });
            }
        }

        imovel.Diagnosticos = resultados;
        return resultados;
    }
}
