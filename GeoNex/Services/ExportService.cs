using OSGeo.OGR;
using OSGeo.OSR;
using System;
using System.IO;

namespace GeoNex.Services
{
    public class ExportService
    {
        public bool ExportarVetor(string nomeCamadaOrigem, string caminhoShapefileOrigem, string caminhoDestino, string formatoDriver, int epsgDestino)
        {
            try
            {
                Ogr.RegisterAll();

                // 1. Abre a camada original
                using var dsOrigem = Ogr.Open(caminhoShapefileOrigem, 0);
                if (dsOrigem == null) throw new Exception("Falha ao abrir a fonte original.");

                var layerOrigem = dsOrigem.GetLayerByIndex(0);
                if (layerOrigem == null) throw new Exception("Camada vetorial nula no arquivo.");

                // 2. Prepara o SRS (Spatial Reference System) de Destino
                using var srsDestino = new SpatialReference("");
                srsDestino.ImportFromEPSG(epsgDestino);

                // 3. Prepara o Driver de Destino
                var driver = Ogr.GetDriverByName(formatoDriver); // "ESRI Shapefile" ou "GeoJSON" ou "KML"
                if (driver == null) throw new Exception($"Driver {formatoDriver} não encontrado no GDAL.");

                if (File.Exists(caminhoDestino)) File.Delete(caminhoDestino);

                using var dsDestino = driver.CreateDataSource(caminhoDestino, new string[] { });
                if (dsDestino == null) throw new Exception("Não foi possível criar o arquivo de destino.");

                // 4. Cria a nova Layer (com o novo SRC)
                var layerDestino = dsDestino.CreateLayer(nomeCamadaOrigem, srsDestino, layerOrigem.GetGeomType(), new string[] { });

                // 5. Copia os campos da tabela de atributos
                var defnOrigem = layerOrigem.GetLayerDefn();
                for (int i = 0; i < defnOrigem.GetFieldCount(); i++)
                {
                    using var fieldDefn = defnOrigem.GetFieldDefn(i);
                    layerDestino.CreateField(fieldDefn, 1);
                }

                // 6. Prepara o motor matemático de reprojeção
                // A maioria das origens no GeoNex são carregadas, mas algumas podem não ter SRC definido explicitamente. Assumimos WGS84 ou o original.
                var srsOrigem = layerOrigem.GetSpatialRef();
                if (srsOrigem == null)
                {
                    srsOrigem = new SpatialReference("");
                    srsOrigem.ImportFromEPSG(4326); // Fallback standard
                }

                using var coordTrans = new CoordinateTransformation(srsOrigem, srsDestino);

                // 7. Copia e Reprojeta Geometrias (Feature por Feature)
                layerOrigem.ResetReading();
                Feature feat;
                while ((feat = layerOrigem.GetNextFeature()) != null)
                {
                    using var geom = feat.GetGeometryRef();
                    if (geom != null)
                    {
                        geom.Transform(coordTrans); // Reprojeta fisicamente
                    }

                    using var outFeat = new Feature(layerDestino.GetLayerDefn());
                    outFeat.SetGeometry(geom);

                    for (int i = 0; i < defnOrigem.GetFieldCount(); i++)
                    {
                        outFeat.SetField(i, feat.GetFieldAsString(i));
                    }

                    layerDestino.CreateFeature(outFeat);
                }

                layerDestino.SyncToDisk();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EXPORT EXCEPTION]: {ex.Message}");
                return false;
            }
        }
    }
}
