using System.Collections.Concurrent;

namespace GeoNex.Services.Attributes;

/// <summary>
/// Serviço de sessão que sincroniza em tempo real as operações espaciais e a seleção
/// entre a Janela Principal (Mapa) e a Janela Separada da Tabela de Atributos (Estilo Compositor de Impressão / QGIS).
/// </summary>
public sealed class AttributeTableSessionService
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, string> _caminhosPorCamada = new(StringComparer.OrdinalIgnoreCase);

    public string? CamadaAtivaNome { get; private set; }
    public string? CamadaAtivaCaminho { get; private set; }

    public event Action? OnCamadaAtivaAlterada;
    public event Action<string, IReadOnlySet<long>>? OnSelecaoAlteradaPeloMapa;
    public event Action<string, IReadOnlySet<long>>? OnSelecaoAlteradaPelaTabela;
    public event Action<string, long>? OnRequisicaoZoomFeicao;
    public event Action<string, long>? OnRequisicaoPanFeicao;
    public event Action<string, long>? OnRequisicaoBlinkFeicao;

    public void RegistrarCaminhoCamada(string nomeCamada, string caminhoArquivo)
    {
        if (!string.IsNullOrWhiteSpace(nomeCamada) && !string.IsNullOrWhiteSpace(caminhoArquivo))
        {
            _caminhosPorCamada[nomeCamada] = caminhoArquivo;
            string semExt = Path.GetFileNameWithoutExtension(nomeCamada);
            if (!string.IsNullOrWhiteSpace(semExt)) _caminhosPorCamada[semExt] = caminhoArquivo;
        }
    }

    public string? ObterCaminhoCamada(string nomeCamada)
    {
        if (string.IsNullOrWhiteSpace(nomeCamada)) return null;
        if (_caminhosPorCamada.TryGetValue(nomeCamada, out string? path) && File.Exists(path)) return path;
        string semExt = Path.GetFileNameWithoutExtension(nomeCamada);
        if (!string.IsNullOrWhiteSpace(semExt) && _caminhosPorCamada.TryGetValue(semExt, out string? pathSemExt) && File.Exists(pathSemExt)) return pathSemExt;
        return null;
    }

    public void DefinirCamadaAtiva(string nomeCamada, string? caminhoArquivo = null)
    {
        lock (_lock)
        {
            CamadaAtivaNome = nomeCamada;
            CamadaAtivaCaminho = caminhoArquivo;
            if (!string.IsNullOrWhiteSpace(caminhoArquivo))
            {
                RegistrarCaminhoCamada(nomeCamada, caminhoArquivo);
            }
        }
        OnCamadaAtivaAlterada?.Invoke();
    }

    public void NotificarSelecaoTabela(string nomeCamada, IReadOnlySet<long> fids)
    {
        OnSelecaoAlteradaPelaTabela?.Invoke(nomeCamada, fids);
    }

    public void NotificarSelecaoMapa(string nomeCamada, IReadOnlySet<long> fids)
    {
        OnSelecaoAlteradaPeloMapa?.Invoke(nomeCamada, fids);
    }

    public void SolicitarZoomFeicao(string nomeCamada, long fid)
    {
        OnRequisicaoZoomFeicao?.Invoke(nomeCamada, fid);
    }

    public void SolicitarPanFeicao(string nomeCamada, long fid)
    {
        OnRequisicaoPanFeicao?.Invoke(nomeCamada, fid);
    }

    public void SolicitarBlinkFeicao(string nomeCamada, long fid)
    {
        OnRequisicaoBlinkFeicao?.Invoke(nomeCamada, fid);
    }
}
