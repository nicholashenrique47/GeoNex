using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;

namespace GeoNex.Services.Geodesy;

/// <summary>
/// Resultado de cálculo geodésico rigoroso sobre o elipsoide de referência.
/// </summary>
public readonly struct GeodesicDistanceResult
{
    public double DistanceMeters { get; }
    public double InitialAzimuthDegrees { get; }
    public double FinalAzimuthDegrees { get; }
    public bool Converged { get; }

    public GeodesicDistanceResult(double distance, double initialAzimuth, double finalAzimuth, bool converged = true)
    {
        DistanceMeters = distance;
        InitialAzimuthDegrees = initialAzimuth;
        FinalAzimuthDegrees = finalAzimuth;
        Converged = converged;
    }
}

/// <summary>
/// Motor de cálculo geodésico de alta precisão baseado na formulação elipsoidal inversa de Thaddeus Vincenty (1975),
/// com fallback contínuo para linhas quasi-antipodais e cálculo de área poligonal geodésica.
/// </summary>
public static class VincentyGeodesicEngine
{
    // Parâmetros do Elipsoide de Referência WGS84 / GRS80 (SIRGAS 2000)
    public const double Wgs84SemiMajorAxis = 6378137.0; // a (metros)
    public const double Wgs84Flattening = 1.0 / 298.257223563; // f
    public const double Wgs84SemiMinorAxis = 6356752.314245179; // b = a * (1 - f)

    /// <summary>
    /// Calcula a distância geodésica inversa rigorosa e azimutes entre duas coordenadas geográficas (graus decimais).
    /// </summary>
    public static GeodesicDistanceResult ComputeInverse(
        double lat1Deg, double lon1Deg,
        double lat2Deg, double lon2Deg,
        double a = Wgs84SemiMajorAxis,
        double f = Wgs84Flattening)
    {
        // 1. Caso de pontos idênticos ou com separação submilimétrica
        if (Math.Abs(lat1Deg - lat2Deg) < 1e-11 && Math.Abs(lon1Deg - lon2Deg) < 1e-11)
        {
            return new GeodesicDistanceResult(0.0, 0.0, 0.0, true);
        }

        double b = a * (1.0 - f);
        double phi1 = lat1Deg * (Math.PI / 180.0);
        double phi2 = lat2Deg * (Math.PI / 180.0);
        double lambda1 = lon1Deg * (Math.PI / 180.0);
        double lambda2 = lon2Deg * (Math.PI / 180.0);

        double L = lambda2 - lambda1;
        double tanU1 = (1.0 - f) * Math.Tan(phi1);
        double cosU1 = 1.0 / Math.Sqrt(1.0 + tanU1 * tanU1);
        double sinU1 = tanU1 * cosU1;

        double tanU2 = (1.0 - f) * Math.Tan(phi2);
        double cosU2 = 1.0 / Math.Sqrt(1.0 + tanU2 * tanU2);
        double sinU2 = tanU2 * cosU2;

        double lambda = L;
        double lambdaPrev;
        int iteration = 0;
        const int maxIterations = 200;
        const double tolerance = 1e-12;

        double sinLambda, cosLambda, sinSigma = 0, cosSigma = 0, sigma = 0;
        double sinAlpha, cosSqAlpha = 0, cos2SigmaM = 0;

        do
        {
            sinLambda = Math.Sin(lambda);
            cosLambda = Math.Cos(lambda);

            sinSigma = Math.Sqrt((cosU2 * sinLambda) * (cosU2 * sinLambda) +
                (cosU1 * sinU2 - sinU1 * cosU2 * cosLambda) * (cosU1 * sinU2 - sinU1 * cosU2 * cosLambda));

            if (Math.Abs(sinSigma) < 1e-15)
                return new GeodesicDistanceResult(0.0, 0.0, 0.0, true);

            cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
            sigma = Math.Atan2(sinSigma, cosSigma);

            sinAlpha = (cosU1 * cosU2 * sinLambda) / sinSigma;
            cosSqAlpha = 1.0 - sinAlpha * sinAlpha;

            cos2SigmaM = cosSqAlpha != 0.0
                ? cosSigma - (2.0 * sinU1 * sinU2 / cosSqAlpha)
                : 0.0;

            double C = (f / 16.0) * cosSqAlpha * (4.0 + f * (4.0 - 3.0 * cosSqAlpha));
            lambdaPrev = lambda;
            lambda = L + (1.0 - C) * f * sinAlpha *
                (sigma + C * sinSigma * (cos2SigmaM + C * cosSigma * (-1.0 + 2.0 * cos2SigmaM * cos2SigmaM)));

            iteration++;
        } while (Math.Abs(lambda - lambdaPrev) > tolerance && iteration < maxIterations);

        // Se atingir o limite de iterações (pontos quase antipodais), comuta para grande círculo elipsoidal
        if (iteration >= maxIterations)
        {
            return FallbackGreatCircle(lat1Deg, lon1Deg, lat2Deg, lon2Deg, a);
        }

        double uSq = cosSqAlpha * ((a * a - b * b) / (b * b));
        double A = 1.0 + (uSq / 16384.0) * (4096.0 + uSq * (-768.0 + uSq * (320.0 - 175.0 * uSq)));
        double B = (uSq / 1024.0) * (256.0 + uSq * (-128.0 + uSq * (74.0 - 47.0 * uSq)));
        double deltaSigma = B * sinSigma * (cos2SigmaM + (B / 4.0) * (cosSigma * (-1.0 + 2.0 * cos2SigmaM * cos2SigmaM) -
            (B / 6.0) * cos2SigmaM * (-3.0 + 4.0 * sinSigma * sinSigma) * (-3.0 + 4.0 * cos2SigmaM * cos2SigmaM)));

        double s = b * A * (sigma - deltaSigma);

        double alpha1 = Math.Atan2(cosU2 * sinLambda, cosU1 * sinU2 - sinU1 * cosU2 * cosLambda);
        double alpha2 = Math.Atan2(cosU1 * sinLambda, -sinU1 * cosU2 + cosU1 * sinU2 * cosLambda);

        double azimuth1 = (alpha1 * (180.0 / Math.PI) + 360.0) % 360.0;
        double azimuth2 = (alpha2 * (180.0 / Math.PI) + 360.0) % 360.0;

        return new GeodesicDistanceResult(s, azimuth1, azimuth2, true);
    }

    /// <summary>
    /// Calcula a área de um polígono geodésico sobre a superfície elipsoidal autálica.
    /// </summary>
    public static double ComputeGeodesicPolygonArea(IReadOnlyList<Coordinate> ring, double a = Wgs84SemiMajorAxis, double f = Wgs84Flattening)
    {
        if (ring == null || ring.Count < 3) return 0.0;
        double b = a * (1.0 - f);
        double areaSum = 0.0;
        int n = ring.Count;

        for (int i = 0; i < n; i++)
        {
            var p1 = ring[i];
            var p2 = ring[(i + 1) % n];
            double phi1 = p1.Y * (Math.PI / 180.0);
            double phi2 = p2.Y * (Math.PI / 180.0);
            double lambda1 = p1.X * (Math.PI / 180.0);
            double lambda2 = p2.X * (Math.PI / 180.0);

            areaSum += (lambda2 - lambda1) * (2.0 + Math.Sin(phi1) + Math.Sin(phi2));
        }

        double e = Math.Sqrt(2 * f - f * f);
        double authalicRadiusSq = (a * a + b * b * (Math.Atanh(e) / e)) / 2.0;
        return Math.Abs(areaSum * authalicRadiusSq / 4.0);
    }

    private static GeodesicDistanceResult FallbackGreatCircle(double lat1, double lon1, double lat2, double lon2, double r)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double sinHalfLat = Math.Sin(dLat / 2.0);
        double sinHalfLon = Math.Sin(dLon / 2.0);
        double h = sinHalfLat * sinHalfLat + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * sinHalfLon * sinHalfLon;
        double c = 2.0 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1.0 - h));
        return new GeodesicDistanceResult(r * c, 0.0, 0.0, false);
    }
}
