namespace GeoNex.Topografia.Domain;

using System;
using System.Collections.Generic;
using System.Linq;

public sealed class MetodoIncraInfo
{
    public string Codigo { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public double SigmaMaximoHorizontal { get; set; } = 0.50;
    public double SigmaMaximoVertical { get; set; } = 1.00;
    public bool PermiteMarcoFisico { get; set; } = true;
    public bool PermitePontoVirtual { get; set; } = false;
}

public sealed class LimiteIncraInfo
{
    public TipoLimite Tipo { get; set; } = TipoLimite.LA;
    public string Codigo { get; set; } = "LA";
    public string Nome { get; set; } = "Limite Artificial";
    public List<string> Subtipos { get; set; } = new();
}

public static class TopografiaNormativeCatalog
{
    public static readonly IReadOnlyList<MetodoIncraInfo> MetodosPosicionamento = new List<MetodoIncraInfo>
    {
        new()
        {
            Codigo = "PG1",
            Descricao = "Posicionamento Relativo Estático GNSS",
            SigmaMaximoHorizontal = 0.50,
            SigmaMaximoVertical = 1.00,
            PermiteMarcoFisico = true,
            PermitePontoVirtual = true
        },
        new()
        {
            Codigo = "PG2",
            Descricao = "Posicionamento Relativo Rápido GNSS",
            SigmaMaximoHorizontal = 0.50,
            SigmaMaximoVertical = 1.00,
            PermiteMarcoFisico = true,
            PermitePontoVirtual = false
        },
        new()
        {
            Codigo = "PG3",
            Descricao = "Posicionamento Cinemático em Tempo Real (RTK / NTRIP)",
            SigmaMaximoHorizontal = 0.50,
            SigmaMaximoVertical = 1.00,
            PermiteMarcoFisico = true,
            PermitePontoVirtual = false
        },
        new()
        {
            Codigo = "PG4",
            Descricao = "Posicionamento Diferencial GNSS em Código e Fase (DGPS)",
            SigmaMaximoHorizontal = 0.50,
            SigmaMaximoVertical = 1.00,
            PermiteMarcoFisico = true,
            PermitePontoVirtual = true
        },
        new()
        {
            Codigo = "PE1",
            Descricao = "Poligonação Topográfica Clássica",
            SigmaMaximoHorizontal = 0.50,
            SigmaMaximoVertical = 1.00,
            PermiteMarcoFisico = true,
            PermitePontoVirtual = false
        },
        new()
        {
            Codigo = "PE2",
            Descricao = "Irradiação Topográfica Clássica",
            SigmaMaximoHorizontal = 0.50,
            SigmaMaximoVertical = 1.00,
            PermiteMarcoFisico = true,
            PermitePontoVirtual = true
        },
        new()
        {
            Codigo = "PA1",
            Descricao = "Aerofotogrametria ou Drone / VANT",
            SigmaMaximoHorizontal = 0.50,
            SigmaMaximoVertical = 1.00,
            PermiteMarcoFisico = false,
            PermitePontoVirtual = true
        },
        new()
        {
            Codigo = "PV1",
            Descricao = "Ponto Virtual Projetado em Feição Fisiográfica",
            SigmaMaximoHorizontal = 3.00,
            SigmaMaximoVertical = 6.00,
            PermiteMarcoFisico = false,
            PermitePontoVirtual = true
        },
        new()
        {
            Codigo = "PV2",
            Descricao = "Ponto Virtual Geométrico / Cruzamento",
            SigmaMaximoHorizontal = 3.00,
            SigmaMaximoVertical = 6.00,
            PermiteMarcoFisico = false,
            PermitePontoVirtual = true
        }
    };

    public static readonly IReadOnlyList<LimiteIncraInfo> LimitesLegais = new List<LimiteIncraInfo>
    {
        new()
        {
            Tipo = TipoLimite.LA,
            Codigo = "LA",
            Nome = "Limite Artificial",
            Subtipos = new()
            {
                "Cerca de arame farpado",
                "Cerca de arame liso",
                "Muro de alvenaria ou concreto",
                "Vala ou canal de drenagem",
                "Faixa de domínio de rodovia",
                "Faixa de servidão de linha de transmissão",
                "Eixo de ferrovia"
            }
        },
        new()
        {
            Tipo = TipoLimite.LN,
            Codigo = "LN",
            Nome = "Limite Natural",
            Subtipos = new()
            {
                "Margem direita de curso d'água",
                "Margem esquerda de curso d'água",
                "Talvegue de rio ou córrego",
                "Espigão divisor de águas",
                "Linha de cumeada",
                "Crista de encosta",
                "Espelho d'água de represa / lagoa"
            }
        },
        new()
        {
            Tipo = TipoLimite.LV,
            Codigo = "LV",
            Nome = "Limite Virtual",
            Subtipos = new()
            {
                "Linha geodésica ideal entre marcos",
                "Paralelo astronômico",
                "Meridiano astronômico",
                "Linha de divisa intermunicipal ou interestadual"
            }
        }
    };

    public static MetodoIncraInfo ObterMetodo(string codigo)
    {
        return MetodosPosicionamento.FirstOrDefault(m => string.Equals(m.Codigo, codigo, StringComparison.OrdinalIgnoreCase))
               ?? MetodosPosicionamento[0];
    }

    public static bool ValidarSigmaMetodo(string codigoMetodo, TipoVertice tipo, double sigmaX, double sigmaY, out string motivoRejeicao)
    {
        var metodo = ObterMetodo(codigoMetodo);
        motivoRejeicao = string.Empty;

        if (tipo == TipoVertice.M && !metodo.PermiteMarcoFisico)
        {
            motivoRejeicao = $"O método '{metodo.Codigo} - {metodo.Descricao}' não é permitido pelo INCRA para marco físico implantado (M).";
            return false;
        }

        if (tipo == TipoVertice.V && !metodo.PermitePontoVirtual)
        {
            motivoRejeicao = $"O método '{metodo.Codigo}' exige ocupação e não é permitido para vértice virtual (V).";
            return false;
        }

        double maxSigma = Math.Max(sigmaX, sigmaY);
        if (maxSigma > metodo.SigmaMaximoHorizontal)
        {
            motivoRejeicao = $"Desvio padrão de {maxSigma:F2}m ultrapassa a tolerância máxima horizontal de {metodo.SigmaMaximoHorizontal:F2}m para o método {metodo.Codigo}.";
            return false;
        }

        return true;
    }
}
