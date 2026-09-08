using OSGeo.OSR;
using System.Threading;

namespace GeoNex.Services
{
    /// <summary>
    /// Factory centralizado — NUNCA criar SpatialReference diretamente.
    /// Garante que TODA SpatialReference usa TRADITIONAL_GIS_ORDER (X=Lng/Easting, Y=Lat/Northing).
    /// </summary>
    public static class SrsFactory
    {
        public static SpatialReference FromEPSG(int epsg)
        {
            var sr = new SpatialReference("");
            sr.ImportFromEPSG(epsg);
            sr.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            return sr;
        }

        public static SpatialReference FromWkt(string wkt)
        {
            var sr = new SpatialReference(wkt);
            sr.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            return sr;
        }

        public static SpatialReference FromUserInput(string input)
        {
            var sr = new SpatialReference("");
            sr.SetFromUserInput(input);
            sr.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            return sr;
        }

        /// <summary>
        /// Cria transformação thread-safe (ThreadLocal) para uso em Parallel.For / GetPath.
        /// Cada thread recebe a sua própria instância do CoordinateTransformation (zero contenção).
        /// </summary>
        public static ThreadLocal<CoordinateTransformation> CreateThreadLocalTransform(string srcWkt, string dstSrs)
        {
            return new ThreadLocal<CoordinateTransformation>(() =>
            {
                var srOrig = FromWkt(srcWkt);
                var srDest = FromUserInput(dstSrs);
                return new CoordinateTransformation(srOrig, srDest);
            });
        }

        /// <summary>
        /// Cria uma transformação simples (não thread-safe) para uso pontual.
        /// </summary>
        public static CoordinateTransformation CreateTransform(string srcWktOrEpsg, string dstWktOrEpsg)
        {
            var srOrig = FromUserInput(srcWktOrEpsg);
            var srDest = FromUserInput(dstWktOrEpsg);
            return new CoordinateTransformation(srOrig, srDest);
        }

        /// <summary>
        /// Verifica se dois SRS são iguais (mesma projeção).
        /// </summary>
        public static bool IsSame(string srsA, string srsB)
        {
            if (string.IsNullOrEmpty(srsA) || string.IsNullOrEmpty(srsB)) return false;
            if (srsA == srsB) return true; // Fast-path para WKT idêntico
            try
            {
                using var a = FromUserInput(srsA);
                using var b = FromUserInput(srsB);
                return a.IsSame(b, null) == 1;
            }
            catch { return false; }
        }
    }
}
