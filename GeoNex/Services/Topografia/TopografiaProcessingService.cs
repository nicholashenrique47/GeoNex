namespace GeoNex.Services.Topografia;

using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using GeoNex.Topografia.Domain;

public sealed class TopografiaProcessingService
{
    private const double RadToDeg = 180.0 / Math.PI;
    private const double DegToRad = Math.PI / 180.0;

    // Parâmetros do Elipsoide GRS80 (SIRGAS 2000)
    private const double SemiMajorAxisA = 6378137.0;
    private const double FlatteningF = 1.0 / 298.257222101;
    private const double EccentricitySquaredE2 = 2.0 * FlatteningF - FlatteningF * FlatteningF; // 0.00669438002290
    private const double SecondEccentricitySquaredEPrime2 = EccentricitySquaredE2 / (1.0 - EccentricitySquaredE2);
    private const double ScaleFactorK0 = 0.9996;

    public ImovelCertificacaoSigef ProcessarPoligonoParaSigef(
        Geometry geometria,
        string codigoCredenciado,
        int fusoUtm,
        string denominacao,
        string titular)
    {
        if (geometria is null || geometria.IsEmpty)
            throw new ArgumentException("A geometria não pode ser nula ou vazia.", nameof(geometria));

        Polygon poligono;
        if (geometria is Polygon p)
        {
            poligono = p;
        }
        else if (geometria is MultiPolygon mp && mp.NumGeometries > 0)
        {
            poligono = (Polygon)mp.GetGeometryN(0);
        }
        else
        {
            throw new ArgumentException("A feição selecionada deve ser um Polígono fechado.");
        }

        LineString exterior = poligono.ExteriorRing;
        Coordinate[] coordenadas = (Coordinate[])exterior.Coordinates.Clone();

        // Garantir Sentido Horário Rigoroso (Regra NTGIR/INCRA)
        if (NetTopologySuite.Algorithm.Orientation.IsCCW(coordenadas))
        {
            Array.Reverse(coordenadas);
        }

        string prefixo = string.IsNullOrWhiteSpace(codigoCredenciado) ? "ABC" : codigoCredenciado.Trim().ToUpperInvariant();
        if (prefixo.Length > 3) prefixo = prefixo[..3];

        var imovel = new ImovelCertificacaoSigef
        {
            Denominacao = string.IsNullOrWhiteSpace(denominacao) ? "Gleba Sem Denominação" : denominacao.Trim(),
            ProprietarioNome = string.IsNullOrWhiteSpace(titular) ? "Proprietário a Identificar" : titular.Trim(),
            CredenciadoCodigoIncra = prefixo,
            FusoUtm = fusoUtm,
            AreaMetrosQuadrados = Math.Abs(poligono.Area),
            AreaHectares = Math.Abs(poligono.Area) / 10000.0,
            PerimetroMetros = exterior.Length
        };

        int totalVertices = coordenadas.Length - 1;
        if (totalVertices < 3)
            throw new InvalidOperationException("O polígono possui menos de 3 vértices distintos.");

        // 1. Extração e conversão geodésica de cada vértice
        for (int i = 0; i < totalVertices; i++)
        {
            Coordinate c = coordenadas[i];
            var vertice = new VerticeTopografico
            {
                Indice = i + 1,
                Codigo = $"{prefixo}-M-{(i + 1):D4}",
                Tipo = TipoVertice.M,
                EsteUtm = c.X,
                NorteUtm = c.Y,
                AltitudeOrtometrica = double.IsNaN(c.Z) ? 0.0 : c.Z,
                SigmaX = 0.05,
                SigmaY = 0.05,
                SigmaZ = 0.08,
                MetodoPosicionamento = "PG1"
            };

            ConverterUtmParaGeodesica(c.X, c.Y, fusoUtm, true, out double latDec, out double lonDec);
            vertice.LatitudeDecimal = latDec;
            vertice.LongitudeDecimal = lonDec;
            vertice.LatitudeGms = FormatarParaPadraoIncra(latDec, isLatitude: true);
            vertice.LongitudeGms = FormatarParaPadraoIncra(lonDec, isLatitude: false);

            imovel.Vertices.Add(vertice);
        }

        // 2. Extração dos alinhamentos perimétricos (Segmentos)
        for (int i = 0; i < totalVertices; i++)
        {
            VerticeTopografico vOrigem = imovel.Vertices[i];
            VerticeTopografico vDestino = imovel.Vertices[(i + 1) % totalVertices];

            double dX = vDestino.EsteUtm - vOrigem.EsteUtm;
            double dY = vDestino.NorteUtm - vOrigem.NorteUtm;

            double distPlana = Math.Sqrt(dX * dX + dY * dY);

            double azimuteRad = Math.Atan2(dX, dY);
            if (azimuteRad < 0) azimuteRad += 2.0 * Math.PI;
            double azimutePlanoGraus = azimuteRad * RadToDeg;

            // Convergência meridiana média do segmento
            double latMedia = (vOrigem.LatitudeDecimal + vDestino.LatitudeDecimal) / 2.0;
            double lonMedia = (vOrigem.LongitudeDecimal + vDestino.LongitudeDecimal) / 2.0;
            double convMeridiana = CalcularConvergenciaMeridiana(latMedia, lonMedia, fusoUtm);
            double azimuteGeodesico = (azimutePlanoGraus + convMeridiana) % 360.0;
            if (azimuteGeodesico < 0) azimuteGeodesico += 360.0;

            // Redução ao elipsoide (fator de escala k médio)
            double xMedio = (vOrigem.EsteUtm + vDestino.EsteUtm) / 2.0;
            double k = CalcularFatorEscalaK(xMedio);
            double distGeodesica = distPlana / k;

            var segmento = new SegmentoPerimetro
            {
                Ordem = i + 1,
                De = vOrigem,
                Para = vDestino,
                DistanciaPlanaMetros = distPlana,
                DistanciaGeodesicaMetros = distGeodesica,
                AzimutePlanoGraus = azimutePlanoGraus,
                AzimuteGeodesicoGraus = azimuteGeodesico,
                AzimuteFormatadoGms = FormatarDmsMemorial(azimutePlanoGraus),
                TipoLimite = TipoLimite.LA,
                DescricaoLimite = "Cerca",
                NomeConfrontante = "A DEFINIR",
                MatriculaConfrontante = ""
            };

            imovel.Segmentos.Add(segmento);
        }

        return imovel;
    }

    /// <summary>
    /// Conversão analítica reversa rigorosa de Redfearn e Coticchia-Surace para projeção UTM / GRS80 (SIRGAS 2000).
    /// </summary>
    public static void ConverterUtmParaGeodesica(
        double este,
        double norte,
        int fuso,
        bool hemisferioSul,
        out double latDecimal,
        out double lonDecimal)
    {
        double x = este - 500000.0;
        double y = hemisferioSul ? norte - 10000000.0 : norte;

        double m = y / ScaleFactorK0;
        double e1 = (1.0 - Math.Sqrt(1.0 - EccentricitySquaredE2)) / (1.0 + Math.Sqrt(1.0 - EccentricitySquaredE2));

        double mu = m / (SemiMajorAxisA * (1.0 - EccentricitySquaredE2 / 4.0 - 3.0 * EccentricitySquaredE2 * EccentricitySquaredE2 / 64.0 - 5.0 * Math.Pow(EccentricitySquaredE2, 3) / 256.0));

        double j1 = 3.0 * e1 / 2.0 - 27.0 * Math.Pow(e1, 3) / 32.0;
        double j2 = 21.0 * e1 * e1 / 16.0 - 55.0 * Math.Pow(e1, 4) / 32.0;
        double j3 = 151.0 * Math.Pow(e1, 3) / 96.0;

        double fp = mu + j1 * Math.Sin(2.0 * mu) + j2 * Math.Sin(4.0 * mu) + j3 * Math.Sin(6.0 * mu);

        double sinFp = Math.Sin(fp);
        double cosFp = Math.Cos(fp);
        double tanFp = Math.Tan(fp);

        double c1 = SecondEccentricitySquaredEPrime2 * cosFp * cosFp;
        double t1 = tanFp * tanFp;
        double n1 = SemiMajorAxisA / Math.Sqrt(1.0 - EccentricitySquaredE2 * sinFp * sinFp);
        double r1 = SemiMajorAxisA * (1.0 - EccentricitySquaredE2) / Math.Pow(1.0 - EccentricitySquaredE2 * sinFp * sinFp, 1.5);
        double d = x / (n1 * ScaleFactorK0);

        double latRad = fp - (n1 * tanFp / r1) * (
            d * d / 2.0 -
            (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 * c1 - 9.0 * SecondEccentricitySquaredEPrime2) * Math.Pow(d, 4) / 24.0 +
            (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 * t1 - 252.0 * SecondEccentricitySquaredEPrime2 - 3.0 * c1 * c1) * Math.Pow(d, 6) / 720.0);

        latDecimal = latRad * RadToDeg;

        double lonRad = (d - (1.0 + 2.0 * t1 + c1) * Math.Pow(d, 3) / 6.0 +
            (5.0 - 2.0 * c1 + 28.0 * t1 - 3.0 * c1 * c1 + 8.0 * SecondEccentricitySquaredEPrime2 + 24.0 * t1 * t1) * Math.Pow(d, 5) / 120.0) / cosFp;

        double meridianoCentral = (fuso * 6 - 183) * DegToRad;
        lonDecimal = (meridianoCentral + lonRad) * RadToDeg;
    }

    /// <summary>
    /// Calcula a convergência meridiana γ no ponto.
    /// </summary>
    public static double CalcularConvergenciaMeridiana(double latDecimal, double lonDecimal, int fuso)
    {
        double centralMeridian = fuso * 6 - 183;
        double dLonRad = (lonDecimal - centralMeridian) * DegToRad;
        double latRad = latDecimal * DegToRad;

        return Math.Atan(Math.Tan(dLonRad) * Math.Sin(latRad)) * RadToDeg;
    }

    /// <summary>
    /// Calcula o fator de escala k da projeção UTM.
    /// </summary>
    public static double CalcularFatorEscalaK(double este)
    {
        double dX = este - 500000.0;
        const double rAprox = 6371000.0;
        return ScaleFactorK0 * (1.0 + (dX * dX) / (2.0 * rAprox * rAprox));
    }

    /// <summary>
    /// Formata coordenada no padrão oficial exigido pelo SIGEF/INCRA (ex: "-22 14 05,123").
    /// </summary>
    public static string FormatarParaPadraoIncra(double valorDecimal, bool isLatitude)
    {
        char sinal = valorDecimal < 0 ? '-' : '+';
        double abs = Math.Abs(valorDecimal);
        int graus = (int)Math.Floor(abs);
        double restoMinutos = (abs - graus) * 60.0;
        int minutos = (int)Math.Floor(restoMinutos);
        double segundos = (restoMinutos - minutos) * 60.0;

        return $"{sinal}{graus:00} {minutos:00} {segundos:00.000}".Replace('.', ',');
    }

    /// <summary>
    /// Formata ângulo para texto de memorial descritivo (ex: "125°34'12.4\"").
    /// </summary>
    public static string FormatarDmsMemorial(double anguloDecimal)
    {
        double normalizado = (anguloDecimal % 360.0 + 360.0) % 360.0;
        int graus = (int)Math.Floor(normalizado);
        double restoMinutos = (normalizado - graus) * 60.0;
        int minutos = (int)Math.Floor(restoMinutos);
        double segundos = (restoMinutos - minutos) * 60.0;

        return $"{graus:00}°{minutos:00}'{segundos:00.0}\"";
    }
}
