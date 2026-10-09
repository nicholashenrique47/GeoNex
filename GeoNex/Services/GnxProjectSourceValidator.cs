using GeoNex.Models;
using OSGeo.GDAL;
using OSGeo.OGR;

namespace GeoNex.Services;

/// <summary>Validates durable layer sources before a project replaces the active map.</summary>
public static class GnxProjectSourceValidator
{
    public static async Task ValidarAsync(IEnumerable<Camada> camadas, object gdalRasterLock)
    {
        ArgumentNullException.ThrowIfNull(camadas);
        ArgumentNullException.ThrowIfNull(gdalRasterLock);

        Camada[] fontes = camadas.ToArray();
        lock (gdalRasterLock)
        {
            Gdal.AllRegister();
            Ogr.RegisterAll();
        }

        foreach (Camada camada in fontes)
        {
            string caminho = camada.CaminhoFonteOriginal ?? string.Empty;
            if (string.IsNullOrWhiteSpace(caminho)) continue;

            bool remota = GnxProjectStore.FonteEhRemota(caminho);
            if (!remota && !File.Exists(caminho))
                throw new FileNotFoundException(
                    $"A fonte da camada '{camada.Nome}' não foi encontrada: {caminho}", caminho);
            if (caminho.StartsWith("/vsimem/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"A fonte da camada '{camada.Nome}' usa um caminho GDAL temporário que não sobrevive ao fechamento do GeoNex.");

            bool aberta = await Task.Run(() =>
            {
                try
                {
                    lock (gdalRasterLock)
                    {
                        if (string.Equals(camada.Tipo, "Raster", StringComparison.OrdinalIgnoreCase))
                        {
                            using Dataset? dataset = Gdal.Open(caminho, Access.GA_ReadOnly);
                            return dataset is not null && dataset.RasterXSize > 0 && dataset.RasterYSize > 0;
                        }

                        if (!string.Equals(camada.Tipo, "Vetor", StringComparison.OrdinalIgnoreCase))
                            return false;

                        using DataSource? dataSource = Ogr.Open(caminho, 0);
                        if (dataSource is null || dataSource.GetLayerCount() == 0) return false;
                        using Layer? layer = dataSource.GetLayerByIndex(0);
                        if (layer is null) return false;
                        using FeatureDefn? definition = layer.GetLayerDefn();
                        return definition is not null;
                    }
                }
                catch (ApplicationException)
                {
                    // GDAL's C# bindings may throw ApplicationException for a
                    // CPL open error instead of returning a null dataset.
                    return false;
                }
            });

            if (!aberta)
                throw new InvalidDataException(
                    $"A fonte da camada '{camada.Nome}' existe, mas GDAL/OGR não conseguiu abri-la como {camada.Tipo}: {caminho}");
        }
    }
}
