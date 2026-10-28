namespace GeoNex.Services.Topografia;

using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using GeoNex.Topografia.Domain;

public sealed class TopografiaProcessingService
{
    private const double RadToDeg = 180.0 / Math.PI;
    private const double DegToRad = Math.PI / 180.0;

    // Parâmetros do Elipsoide GRS80 (SIRGAS 2000 e WGS84)
    private const double SemiMajorAxisA = 6378137.0;
    private const double FlatteningF = 1.0 / 298.257222101;
    private const double EccentricitySquaredE2 = 2.0 * FlatteningF - FlatteningF * FlatteningF; // 0.00669438002290
    private const double SecondEccentricitySquaredEPrime2 = EccentricitySquaredE2 / (1.0 - EccentricitySquaredE2);
    private const double ScaleFactorK0 = 0.9996;

    // Parâmetros Elipsoide IAG-GRS67 (SAD69)
    private const double Sad69A = 6378160.0;
    private const double Sad69F = 1.0 / 298.25;
    private const double Sad69E2 = 2.0 * Sad69F - Sad69F * Sad69F;

    // Parâmetros Elipsoide Hayford / Internacional 1924 (Córrego Alegre)
    private const double CorregoA = 6378388.0;
    private const double CorregoF = 1.0 / 297.0;
    private const double CorregoE2 = 2.0 * CorregoF - CorregoF * CorregoF;

    // Parâmetros Oficiais de Translação IBGE (Resolução PR-01/2005) para SIRGAS 2000
    // SAD69 -> SIRGAS 2000: dX = -67.35, dY = +4.69, dZ = -60.55
    private const double Sad69DeltaX = -67.35;
    private const double Sad69DeltaY = 4.69;
    private const double Sad69DeltaZ = -60.55;

    // Córrego Alegre -> SIRGAS 2000: dX = -205.57, dY = +168.77, dZ = -3.74
    private const double CorregoDeltaX = -205.57;
    private const double CorregoDeltaY = 168.77;
    private const double CorregoDeltaZ = -3.74;

    public ImovelCertificacaoSigef ProcessarPoligonoParaSigef(
        Geometry geometria,
        string codigoCredenciado,
        int fusoUtm = 23,
        string denominacao = "Gleba Sem Denominação",
        string titular = "Proprietário a Identificar",
        TipoDatum datumOrigem = TipoDatum.Sirgas2000)
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

        int totalVertices = coordenadas.Length - 1;
        if (totalVertices < 3)
            throw new InvalidOperationException("O polígono possui menos de 3 vértices distintos.");

        // 1. Detectar se as coordenadas da feição são Geográficas (graus) ou Projetadas (UTM metros)
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < totalVertices; i++)
        {
            Coordinate c = coordenadas[i];
            if (c.X < minX) minX = c.X;
            if (c.X > maxX) maxX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.Y > maxY) maxY = c.Y;
        }

        bool ehGeografica = Math.Abs(minX) <= 180.0 && Math.Abs(maxX) <= 180.0 &&
                            Math.Abs(minY) <= 90.0 && Math.Abs(maxY) <= 90.0;

        int fusoFinal = fusoUtm;
        bool hemisferioSulFinal = true;

        if (ehGeografica)
        {
            double lonCentro = (minX + maxX) / 2.0;
            double latCentro = (minY + maxY) / 2.0;

            // Auto-detecção inteligente do fuso UTM natural
            fusoFinal = (int)Math.Floor((lonCentro + 180.0) / 6.0) + 1;
            hemisferioSulFinal = latCentro < 0.0;
        }

        string prefixo = string.IsNullOrWhiteSpace(codigoCredenciado) ? "ABC" : codigoCredenciado.Trim().ToUpperInvariant();
        if (prefixo.Length > 3) prefixo = prefixo[..3];

        var imovel = new ImovelCertificacaoSigef
        {
            Denominacao = string.IsNullOrWhiteSpace(denominacao) ? "Gleba Sem Denominação" : denominacao.Trim(),
            ProprietarioNome = string.IsNullOrWhiteSpace(titular) ? "Proprietário a Identificar" : titular.Trim(),
            CredenciadoCodigoIncra = prefixo,
            FusoUtm = fusoFinal,
            HemisferioSul = hemisferioSulFinal,
            DatumOrigem = datumOrigem,
            SistemaOrigem = ehGeografica ? TipoSistemaCoordenadas.GeograficaDecimal : TipoSistemaCoordenadas.ProjetadaUtm
        };

        // 2. Converter todos os vértices para UTM SIRGAS 2000 e Geodésica SIRGAS 2000
        var listaCoordsUtm = new List<Coordinate>();

        for (int i = 0; i < totalVertices; i++)
        {
            Coordinate c = coordenadas[i];
            double altZ = double.IsNaN(c.Z) ? 0.0 : c.Z;

            double esteUtmSirgas;
            double norteUtmSirgas;
            double latSirgas;
            double lonSirgas;

            if (ehGeografica)
            {
                // Entrada em Lat/Lon
                double latOrigem = c.Y;
                double lonOrigem = c.X;

                // Transformação de Datum se a fonte não for SIRGAS 2000
                TransformarParaSirgas2000(latOrigem, lonOrigem, altZ, datumOrigem, out latSirgas, out lonSirgas, out _);

                // Projeção Direta Geodésica -> UTM
                ConverterGeodesicaParaUtm(latSirgas, lonSirgas, fusoFinal, hemisferioSulFinal, out esteUtmSirgas, out norteUtmSirgas);
            }
            else
            {
                // Entrada em UTM Metros
                if (datumOrigem == TipoDatum.Sirgas2000 || datumOrigem == TipoDatum.Wgs84)
                {
                    esteUtmSirgas = c.X;
                    norteUtmSirgas = c.Y;
                    ConverterUtmParaGeodesica(esteUtmSirgas, norteUtmSirgas, fusoFinal, hemisferioSulFinal, out latSirgas, out lonSirgas);
                }
                else
                {
                    // Converte UTM no Datum antigo -> Lat/Lon Datum antigo -> Transforma para SIRGAS 2000 -> Projeta em UTM SIRGAS 2000
                    ConverterUtmParaGeodesicaNoDatum(c.X, c.Y, fusoFinal, hemisferioSulFinal, datumOrigem, out double latAntiga, out double lonAntiga);
                    TransformarParaSirgas2000(latAntiga, lonAntiga, altZ, datumOrigem, out latSirgas, out lonSirgas, out _);
                    ConverterGeodesicaParaUtm(latSirgas, lonSirgas, fusoFinal, hemisferioSulFinal, out esteUtmSirgas, out norteUtmSirgas);
                }
            }

            listaCoordsUtm.Add(new CoordinateZ(esteUtmSirgas, norteUtmSirgas, altZ));
        }

        // 3. Garantir Ordenação Horária Estrita no plano UTM SIRGAS 2000
        var coordsFechamento = new List<Coordinate>(listaCoordsUtm);
        coordsFechamento.Add(listaCoordsUtm[0]);
        if (NetTopologySuite.Algorithm.Orientation.IsCCW(coordsFechamento.ToArray()))
        {
            listaCoordsUtm.Reverse();
        }

        // 4. Montar a lista de Vértices Topográficos
        for (int i = 0; i < listaCoordsUtm.Count; i++)
        {
            Coordinate c = listaCoordsUtm[i];
            ConverterUtmParaGeodesica(c.X, c.Y, fusoFinal, hemisferioSulFinal, out double latDec, out double lonDec);

            var vertice = new VerticeTopografico
            {
                Indice = i + 1,
                Codigo = $"{prefixo}-M-{(i + 1):D4}",
                Tipo = TipoVertice.M,
                EsteUtm = c.X,
                NorteUtm = c.Y,
                AltitudeOrtometrica = c.Z,
                LatitudeDecimal = latDec,
                LongitudeDecimal = lonDec,
                LatitudeGms = FormatarParaPadraoIncra(latDec, isLatitude: true),
                LongitudeGms = FormatarParaPadraoIncra(lonDec, isLatitude: false),
                SigmaX = 0.05,
                SigmaY = 0.05,
                SigmaZ = 0.08,
                MetodoPosicionamento = "PG1"
            };

            imovel.Vertices.Add(vertice);
        }

        // 5. Cálculo rigoroso de área e perímetro no plano UTM SIRGAS 2000
        double areaCalculada = 0.0;
        double perimetroCalculado = 0.0;
        int nVerts = imovel.Vertices.Count;

        for (int i = 0; i < nVerts; i++)
        {
            var v1 = imovel.Vertices[i];
            var v2 = imovel.Vertices[(i + 1) % nVerts];

            areaCalculada += (v1.EsteUtm * v2.NorteUtm) - (v2.EsteUtm * v1.NorteUtm);

            double dX = v2.EsteUtm - v1.EsteUtm;
            double dY = v2.NorteUtm - v1.NorteUtm;
            double distPlana = Math.Sqrt(dX * dX + dY * dY);
            perimetroCalculado += distPlana;

            double azimuteRad = Math.Atan2(dX, dY);
            if (azimuteRad < 0) azimuteRad += 2.0 * Math.PI;
            double azimutePlanoGraus = azimuteRad * RadToDeg;

            // Convergência meridiana média do segmento
            double latMedia = (v1.LatitudeDecimal + v2.LatitudeDecimal) / 2.0;
            double lonMedia = (v1.LongitudeDecimal + v2.LongitudeDecimal) / 2.0;
            double convMeridiana = CalcularConvergenciaMeridiana(latMedia, lonMedia, fusoFinal);
            double azimuteGeodesico = (azimutePlanoGraus + convMeridiana) % 360.0;
            if (azimuteGeodesico < 0) azimuteGeodesico += 360.0;

            // Redução ao elipsoide (fator de escala k médio)
            double xMedio = (v1.EsteUtm + v2.EsteUtm) / 2.0;
            double k = CalcularFatorEscalaK(xMedio);
            double distGeodesica = distPlana / k;

            var segmento = new SegmentoPerimetro
            {
                Ordem = i + 1,
                De = v1,
                Para = v2,
                DistanciaPlanaMetros = distPlana,
                DistanciaGeodesicaMetros = distGeodesica,
                AzimutePlanoGraus = azimutePlanoGraus,
                AzimuteGeodesicoGraus = azimuteGeodesico,
                AzimuteFormatadoGms = FormatarDmsMemorial(azimutePlanoGraus),
                TipoLimite = TipoLimite.LA,
                DescricaoLimite = "Cerca",
                NomeConfrontante = "A DEFINIR",
                MatriculaConfrontante = string.Empty,
                CnsCartorioConfrontante = string.Empty
            };

            imovel.Segmentos.Add(segmento);
        }

        imovel.AreaMetrosQuadrados = Math.Abs(areaCalculada) / 2.0;
        imovel.AreaHectares = imovel.AreaMetrosQuadrados / 10000.0;
        imovel.PerimetroMetros = perimetroCalculado;

        return imovel;
    }

    /// <summary>
    /// Projeção direta rigorosa de Redfearn: Geodésicas SIRGAS 2000 (Lat, Lon) -> UTM Planas (Este, Norte).
    /// Suporta qualquer fuso UTM de 1 a 60 (comum no Brasil: 18 a 25) e hemisférios Norte/Sul.
    /// </summary>
    public static void ConverterGeodesicaParaUtm(
        double latDecimal,
        double lonDecimal,
        int fuso,
        bool hemisferioSul,
        out double este,
        out double norte)
    {
        double latRad = latDecimal * DegToRad;
        double lonRad = lonDecimal * DegToRad;

        double meridianoCentralGraus = fuso * 6 - 183;
        double lon0Rad = meridianoCentralGraus * DegToRad;

        double dLon = lonRad - lon0Rad;

        double sinLat = Math.Sin(latRad);
        double cosLat = Math.Cos(latRad);
        double tanLat = Math.Tan(latRad);

        double n = SemiMajorAxisA / Math.Sqrt(1.0 - EccentricitySquaredE2 * sinLat * sinLat);
        double t = tanLat * tanLat;
        double c = SecondEccentricitySquaredEPrime2 * cosLat * cosLat;
        double aCoeff = cosLat * dLon;

        // Comprimento do arco de meridiano
        double m = SemiMajorAxisA * (
            (1.0 - EccentricitySquaredE2 / 4.0 - 3.0 * EccentricitySquaredE2 * EccentricitySquaredE2 / 64.0 - 5.0 * Math.Pow(EccentricitySquaredE2, 3) / 256.0) * latRad -
            (3.0 * EccentricitySquaredE2 / 8.0 + 3.0 * EccentricitySquaredE2 * EccentricitySquaredE2 / 32.0 + 45.0 * Math.Pow(EccentricitySquaredE2, 3) / 1024.0) * Math.Sin(2.0 * latRad) +
            (15.0 * EccentricitySquaredE2 * EccentricitySquaredE2 / 256.0 + 45.0 * Math.Pow(EccentricitySquaredE2, 3) / 1024.0) * Math.Sin(4.0 * latRad) -
            (35.0 * Math.Pow(EccentricitySquaredE2, 3) / 3072.0) * Math.Sin(6.0 * latRad)
        );

        este = ScaleFactorK0 * n * (
            aCoeff +
            (1.0 - t + c) * Math.Pow(aCoeff, 3) / 6.0 +
            (5.0 - 18.0 * t + t * t + 72.0 * c - 58.0 * SecondEccentricitySquaredEPrime2) * Math.Pow(aCoeff, 5) / 120.0
        ) + 500000.0;

        double nCalculado = ScaleFactorK0 * (
            m +
            n * tanLat * (
                aCoeff * aCoeff / 2.0 +
                (5.0 - t + 9.0 * c + 4.0 * c * c) * Math.Pow(aCoeff, 4) / 24.0 +
                (61.0 - 58.0 * t + t * t + 600.0 * c - 330.0 * SecondEccentricitySquaredEPrime2) * Math.Pow(aCoeff, 6) / 720.0
            )
        );

        norte = hemisferioSul ? nCalculado + 10000000.0 : nCalculado;
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
    /// Converte UTM para coordenadas geodésicas levando em consideração os parâmetros específicos do elipsoide de origem (SAD69 ou Córrego Alegre).
    /// </summary>
    public static void ConverterUtmParaGeodesicaNoDatum(
        double este,
        double norte,
        int fuso,
        bool hemisferioSul,
        TipoDatum datum,
        out double latDecimal,
        out double lonDecimal)
    {
        double a = datum == TipoDatum.Sad69 ? Sad69A : datum == TipoDatum.CorregoAlegre ? CorregoA : SemiMajorAxisA;
        double e2 = datum == TipoDatum.Sad69 ? Sad69E2 : datum == TipoDatum.CorregoAlegre ? CorregoE2 : EccentricitySquaredE2;
        double ep2 = e2 / (1.0 - e2);

        double x = este - 500000.0;
        double y = hemisferioSul ? norte - 10000000.0 : norte;

        double m = y / ScaleFactorK0;
        double e1 = (1.0 - Math.Sqrt(1.0 - e2)) / (1.0 + Math.Sqrt(1.0 - e2));

        double mu = m / (a * (1.0 - e2 / 4.0 - 3.0 * e2 * e2 / 64.0 - 5.0 * Math.Pow(e2, 3) / 256.0));

        double j1 = 3.0 * e1 / 2.0 - 27.0 * Math.Pow(e1, 3) / 32.0;
        double j2 = 21.0 * e1 * e1 / 16.0 - 55.0 * Math.Pow(e1, 4) / 32.0;
        double j3 = 151.0 * Math.Pow(e1, 3) / 96.0;

        double fp = mu + j1 * Math.Sin(2.0 * mu) + j2 * Math.Sin(4.0 * mu) + j3 * Math.Sin(6.0 * mu);

        double sinFp = Math.Sin(fp);
        double cosFp = Math.Cos(fp);
        double tanFp = Math.Tan(fp);

        double c1 = ep2 * cosFp * cosFp;
        double t1 = tanFp * tanFp;
        double n1 = a / Math.Sqrt(1.0 - e2 * sinFp * sinFp);
        double r1 = a * (1.0 - e2) / Math.Pow(1.0 - e2 * sinFp * sinFp, 1.5);
        double d = x / (n1 * ScaleFactorK0);

        double latRad = fp - (n1 * tanFp / r1) * (
            d * d / 2.0 -
            (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 * c1 - 9.0 * ep2) * Math.Pow(d, 4) / 24.0 +
            (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 * t1 - 252.0 * ep2 - 3.0 * c1 * c1) * Math.Pow(d, 6) / 720.0);

        latDecimal = latRad * RadToDeg;

        double lonRad = (d - (1.0 + 2.0 * t1 + c1) * Math.Pow(d, 3) / 6.0 +
            (5.0 - 2.0 * c1 + 28.0 * t1 - 3.0 * c1 * c1 + 8.0 * ep2 + 24.0 * t1 * t1) * Math.Pow(d, 5) / 120.0) / cosFp;

        double meridianoCentral = (fuso * 6 - 183) * DegToRad;
        lonDecimal = (meridianoCentral + lonRad) * RadToDeg;
    }

    /// <summary>
    /// Realiza a transformação tridimensional de Datum (SAD69, Córrego Alegre, WGS84) para SIRGAS 2000
    /// utilizando os parâmetros oficiais geocêntricos do IBGE (Resolução PR-01/2005).
    /// </summary>
    public static void TransformarParaSirgas2000(
        double latOrigem,
        double lonOrigem,
        double hOrigem,
        TipoDatum datumOrigem,
        out double latSirgas,
        out double lonSirgas,
        out double hSirgas)
    {
        if (datumOrigem == TipoDatum.Sirgas2000 || datumOrigem == TipoDatum.Wgs84)
        {
            latSirgas = latOrigem;
            lonSirgas = lonOrigem;
            hSirgas = hOrigem;
            return;
        }

        double aOrigem = datumOrigem == TipoDatum.Sad69 ? Sad69A : CorregoA;
        double e2Origem = datumOrigem == TipoDatum.Sad69 ? Sad69E2 : CorregoE2;
        double dX = datumOrigem == TipoDatum.Sad69 ? Sad69DeltaX : CorregoDeltaX;
        double dY = datumOrigem == TipoDatum.Sad69 ? Sad69DeltaY : CorregoDeltaY;
        double dZ = datumOrigem == TipoDatum.Sad69 ? Sad69DeltaZ : CorregoDeltaZ;

        // 1. Geodésica Origem -> Cartesiana Geocêntrica (X, Y, Z)
        double phi = latOrigem * DegToRad;
        double lambda = lonOrigem * DegToRad;
        double sinPhi = Math.Sin(phi);
        double cosPhi = Math.Cos(phi);
        double sinLam = Math.Sin(lambda);
        double cosLam = Math.Cos(lambda);

        double n = aOrigem / Math.Sqrt(1.0 - e2Origem * sinPhi * sinPhi);
        double x = (n + hOrigem) * cosPhi * cosLam;
        double y = (n + hOrigem) * cosPhi * sinLam;
        double z = (n * (1.0 - e2Origem) + hOrigem) * sinPhi;

        // 2. Aplica translação oficial IBGE
        double xSirgas = x + dX;
        double ySirgas = y + dY;
        double zSirgas = z + dZ;

        // 3. Cartesiana Geocêntrica -> Geodésica SIRGAS 2000 (GRS80) via algoritmo de Bowring
        double p = Math.Sqrt(xSirgas * xSirgas + ySirgas * ySirgas);
        double theta = Math.Atan2(zSirgas * SemiMajorAxisA, p * (SemiMajorAxisA * (1.0 - FlatteningF)));

        double sinTheta = Math.Sin(theta);
        double cosTheta = Math.Cos(theta);

        double phiSirgas = Math.Atan2(
            zSirgas + SecondEccentricitySquaredEPrime2 * (SemiMajorAxisA * (1.0 - FlatteningF)) * Math.Pow(sinTheta, 3),
            p - EccentricitySquaredE2 * SemiMajorAxisA * Math.Pow(cosTheta, 3)
        );

        double lambdaSirgas = Math.Atan2(ySirgas, xSirgas);
        double nSirgas = SemiMajorAxisA / Math.Sqrt(1.0 - EccentricitySquaredE2 * Math.Sin(phiSirgas) * Math.Sin(phiSirgas));
        hSirgas = p / Math.Cos(phiSirgas) - nSirgas;

        latSirgas = phiSirgas * RadToDeg;
        lonSirgas = lambdaSirgas * RadToDeg;
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
