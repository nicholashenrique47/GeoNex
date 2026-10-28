namespace GeoNex.Topografia.Domain;

using System;
using System.Collections.Generic;

public enum TipoVertice
{
    M, // Marco físico artificial implantado (concreto, ferro ou marco de apoio)
    P, // Ponto ocupado no terreno sem implantação de marco físico
    V  // Vértice virtual (acidente natural, curva de rio, eixo de via ou linha ideal)
}

public enum TipoLimite
{
    LA, // Limite Artificial (cerca, muro, vala, rodovia)
    LN, // Limite Natural (margem de rio, córrego, espigão)
    LV  // Limite Virtual (projeção astronômica, linha imaginária)
}

public enum TipoDatum
{
    Sirgas2000,    // SIRGAS 2000 (GRS80) - Padrão Obrigatório INCRA / SIGEF
    Sad69,         // SAD69 (IAG-GRS67) - Histórico Brasileiro
    CorregoAlegre, // Córrego Alegre (Hayford / Internacional 1924)
    Wgs84          // WGS 84 (GPS Nativo)
}

public enum TipoSistemaCoordenadas
{
    ProjetadaUtm,
    GeograficaDecimal
}

public sealed class VerticeTopografico
{
    public int Indice { get; set; }
    public string Codigo { get; set; } = string.Empty;
    public TipoVertice Tipo { get; set; } = TipoVertice.M;
    public double EsteUtm { get; set; }
    public double NorteUtm { get; set; }
    public double AltitudeOrtometrica { get; set; }
    public double LatitudeDecimal { get; set; }
    public double LongitudeDecimal { get; set; }
    public string LatitudeGms { get; set; } = string.Empty;
    public string LongitudeGms { get; set; } = string.Empty;
    public double SigmaX { get; set; } = 0.05;
    public double SigmaY { get; set; } = 0.05;
    public double SigmaZ { get; set; } = 0.08;
    public string MetodoPosicionamento { get; set; } = "PG1";
}

public sealed class SegmentoPerimetro
{
    public int Ordem { get; set; }
    public VerticeTopografico De { get; set; } = null!;
    public VerticeTopografico Para { get; set; } = null!;
    public double AzimutePlanoGraus { get; set; }
    public double AzimuteGeodesicoGraus { get; set; }
    public string AzimuteFormatadoGms { get; set; } = string.Empty;
    public double DistanciaPlanaMetros { get; set; }
    public double DistanciaGeodesicaMetros { get; set; }
    public TipoLimite TipoLimite { get; set; } = TipoLimite.LA;
    public string DescricaoLimite { get; set; } = "Cerca";
    public string NomeConfrontante { get; set; } = string.Empty;
    public string ProprietarioConfrontante { get; set; } = string.Empty;
    public string MatriculaConfrontante { get; set; } = string.Empty;
    public string CnsCartorioConfrontante { get; set; } = string.Empty;
}

public sealed class AuditoriaTopograficaItem
{
    public string Codigo { get; set; } = string.Empty;
    public string Gravidade { get; set; } = "INFO"; // INFO, AVISO, ERRO_CRITICO
    public string Mensagem { get; set; } = string.Empty;
    public double? CoordX { get; set; }
    public double? CoordY { get; set; }
}

public sealed class ImovelCertificacaoSigef
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Denominacao { get; set; } = "Gleba Sem Denominação";
    public string CodigoIncra { get; set; } = string.Empty;
    public string Matricula { get; set; } = string.Empty;
    public string LivroCartorio { get; set; } = "2";
    public string FolhaCartorio { get; set; } = string.Empty;
    public string Comarca { get; set; } = string.Empty;
    public string Municipio { get; set; } = string.Empty;
    public string Uf { get; set; } = "SP";
    public string ProprietarioNome { get; set; } = string.Empty;
    public string ProprietarioCpfCnpj { get; set; } = string.Empty;

    public string CredenciadoNome { get; set; } = string.Empty;
    public string CredenciadoCodigoIncra { get; set; } = "ABC";
    public string CredenciadoCreaCau { get; set; } = string.Empty;
    public string NumeroArtTrt { get; set; } = string.Empty;

    public TipoDatum DatumOrigem { get; set; } = TipoDatum.Sirgas2000;
    public TipoSistemaCoordenadas SistemaOrigem { get; set; } = TipoSistemaCoordenadas.ProjetadaUtm;
    public int FusoUtm { get; set; } = 23;
    public bool HemisferioSul { get; set; } = true;
    public double AreaHectares { get; set; }
    public double AreaMetrosQuadrados { get; set; }
    public double PerimetroMetros { get; set; }

    public List<VerticeTopografico> Vertices { get; set; } = new();
    public List<SegmentoPerimetro> Segmentos { get; set; } = new();
    public List<AuditoriaTopograficaItem> Diagnosticos { get; set; } = new();

    public string NomeDatumExibicao => DatumOrigem switch
    {
        TipoDatum.Sirgas2000 => "SIRGAS 2000 (GRS80) - Padrão Oficial INCRA",
        TipoDatum.Sad69 => "SAD69 (Transformado p/ SIRGAS 2000 via IBGE)",
        TipoDatum.CorregoAlegre => "Córrego Alegre (Transformado p/ SIRGAS 2000)",
        TipoDatum.Wgs84 => "WGS 84 (Compatível com SIRGAS 2000)",
        _ => "SIRGAS 2000"
    };
}
