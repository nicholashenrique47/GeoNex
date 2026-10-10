using GeoNex.Models;
using OSGeo.GDAL;
using OSGeo.OGR;

namespace GeoNex.Services;

/// <summary>Validates durable layer sources before a project replaces the active map.</summary>
public static class GnxProjectSourceValidator
{
    public static async Task<IReadOnlyList<string>> ValidarTodasAsync(
        IEnumerable<Camada> camadas,
        object gdalRasterLock)
    {
        ArgumentNullException.ThrowIfNull(camadas);
        ArgumentNullException.ThrowIfNull(gdalRasterLock);

        var problemas = new List<string>();
        foreach (Camada camada in camadas)
        {
            try
            {
                await ValidarAsync(new[] { camada }, gdalRasterLock);
            }
            catch (FileNotFoundException ex)
            {
                problemas.Add(ex.Message);
            }
            catch (InvalidDataException ex)
            {
                problemas.Add(ex.Message);
            }
        }

        return problemas;
    }

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
                    CriarDiagnostico(camada, caminho, "o arquivo não foi encontrado"), caminho);
            if (caminho.StartsWith("/vsimem/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    CriarDiagnostico(camada, caminho,
                        "o caminho GDAL é temporário e deixa de existir quando o GeoNex fecha"));

            (bool Aberta, string Detalhe) resultado = await Task.Run(() =>
            {
                try
                {
                    lock (gdalRasterLock)
                    {
                        if (string.Equals(camada.Tipo, "Raster", StringComparison.OrdinalIgnoreCase))
                        {
                            using Dataset? dataset = Gdal.Open(caminho, Access.GA_ReadOnly);
                            if (dataset is null)
                                return (false, ExplicarFalhaRaster(caminho));
                            if (dataset.RasterXSize <= 0 || dataset.RasterYSize <= 0)
                                return (false, "O raster abriu, mas não contém uma grade de pixels válida.");
                            return (true, string.Empty);
                        }

                        if (!string.Equals(camada.Tipo, "Vetor", StringComparison.OrdinalIgnoreCase))
                            return (false, $"O tipo de camada '{camada.Tipo}' não é compatível com uma fonte de arquivo.");

                        using DataSource? dataSource = Ogr.Open(caminho, 0);
                        if (dataSource is null)
                            return (false, ObterErroGdalOuPadrao("O GDAL/OGR não reconheceu o arquivo como uma fonte vetorial."));
                        if (dataSource.GetLayerCount() == 0)
                            return (false, "O arquivo abriu, mas não contém uma camada vetorial.");
                        using Layer? layer = dataSource.GetLayerByIndex(0);
                        if (layer is null)
                            return (false, "O arquivo abriu, mas a primeira camada vetorial não pôde ser lida.");
                        using FeatureDefn? definition = layer.GetLayerDefn();
                        return definition is not null
                            ? (true, string.Empty)
                            : (false, "O arquivo abriu, mas não foi possível ler a definição dos campos e da geometria.");
                    }
                }
                catch (ApplicationException ex)
                {
                    // GDAL's C# bindings may throw ApplicationException for a
                    // CPL open error instead of returning a null dataset.
                    lock (gdalRasterLock)
                        return (false, ObterErroGdalOuPadrao(ex.Message));
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            });

            if (!resultado.Aberta)
                throw new InvalidDataException(
                    CriarDiagnostico(camada, caminho, resultado.Detalhe));
        }
    }

    private static string CriarDiagnostico(Camada camada, string caminho, string motivo)
    {
        string acao = GnxProjectStore.FonteEhRemota(caminho)
            ? "Confira a URL, a conexão de rede, a disponibilidade do serviço e as credenciais necessárias."
            : string.Equals(camada.Tipo, "Vetor", StringComparison.OrdinalIgnoreCase) &&
              string.Equals(Path.GetExtension(caminho), ".shp", StringComparison.OrdinalIgnoreCase)
                ? "Selecione o .shp correto e mantenha os arquivos .shx e .dbf correspondentes na mesma pasta; o .prj também é recomendado."
                : string.Equals(camada.Tipo, "Raster", StringComparison.OrdinalIgnoreCase) &&
                  EhCaminhoEcw(caminho)
                    ? "Verifique se esta instalação tem suporte ECW e se o arquivo está íntegro."
                    : string.Equals(camada.Tipo, "Raster", StringComparison.OrdinalIgnoreCase)
                        ? "Confirme se o formato raster é compatível com o GDAL desta instalação e se o arquivo está íntegro."
                        : "Confirme o formato vetorial, a integridade do arquivo e se a fonte selecionada é a correta.";

        return $"A camada '{camada.Nome}' não pôde ser aberta. Fonte: {caminho}. Motivo: {motivo}. Ação sugerida: {acao}";
    }

    private static string ObterErroGdalOuPadrao(string padrao)
    {
        string erroGdal = Gdal.GetLastErrorMsg();
        return string.IsNullOrWhiteSpace(erroGdal) ? padrao : $"{padrao} Detalhe do GDAL: {erroGdal}";
    }

    private static string ExplicarFalhaRaster(string caminho)
    {
        if (EhCaminhoEcw(caminho) && Gdal.GetDriverByName("ECW") is null)
            return "O suporte nativo a ECW não foi carregado nesta instalação.";
        return ObterErroGdalOuPadrao("O GDAL não conseguiu abrir o raster; verifique o formato e a integridade do arquivo.");
    }

    private static bool EhCaminhoEcw(string caminho) =>
        string.Equals(Path.GetExtension(caminho), ".ecw", StringComparison.OrdinalIgnoreCase);
}
