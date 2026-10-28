namespace GeoNex.Components.Pages;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using NetTopologySuite.Geometries;
using GeoNex.Topografia.Domain;
using GeoNex.Services.Topografia;
using GeoNex.Services.Topografia.Export;
using GeoNex.Services.Topografia.Validation;
using GeoNex.Services.Licensing;

public partial class Home
{
    [Inject] private TopografiaProcessingService TopoProcessingService { get; set; } = null!;
    [Inject] private SigefOdsExportService SigefOdsService { get; set; } = null!;
    [Inject] private MemorialDescritivoExportService MemorialService { get; set; } = null!;
    [Inject] private SpatialConfrontationService ConfrontationService { get; set; } = null!;
    [Inject] private TopografiaForensicValidator ForensicValidator { get; set; } = null!;
    [Inject] private LicenseValidationService LicenseService { get; set; } = null!;

    private bool _mostrarModalCertificacaoSigef = false;
    private bool _mostrarModalLicenca = false;
    private int _abaAtivaModalSigef = 1; // 1: Imóvel, 2: Vértices, 3: Confrontantes, 4: Memorial, 5: Auditoria
    private ImovelCertificacaoSigef? _imovelSigefAtivo;
    private string _textoMemorialVisualizacao = string.Empty;
    private string _mensagemFeedbackTopo = string.Empty;
    private bool _feedbackTopoErro = false;
    private string _chaveLicencaInput = string.Empty;

    private async Task AbrirModalCertificacaoSigefAsync()
    {
        try
        {
            // 1. Obter geometria ativa: ou a feição selecionada, ou a primeira feição da camada ativa
            Geometry? geomAlvo = null;

            if (SelectedVectorFeatureState is { IsDeleted: false } && SelectedVectorFeatureState.Current is not null)
            {
                geomAlvo = SelectedVectorFeatureState.Current;
            }
            else if (CamadaAtivaVetorialDisponivel && CamadaAtivaFerramentas is not null &&
                     MapService.FeicoesOriginais.TryGetValue(CamadaAtivaFerramentas.Nome, out var featureCollection))
            {
                // Procurar primeira geometria válida na camada ativa
                geomAlvo = featureCollection
                    .Select(f => f.Geometry)
                    .FirstOrDefault(g => g is Polygon or MultiPolygon);
            }

            if (geomAlvo is null)
            {
                // Fallback de demonstração com gleba padrão no Fuso 23S (Ribeirão Preto / Campinas)
                geomAlvo = CriarGlebaDemonstracao();
            }

            // 2. Processar a geometria
            _imovelSigefAtivo = TopoProcessingService.ProcessarPoligonoParaSigef(
                geomAlvo,
                codigoCredenciado: "ABC",
                fusoUtm: 23,
                denominacao: "Fazenda Santa Maria",
                titular: "João da Silva Sauro"
            );

            // 3. Detectar confrontantes espaciais se houver outras camadas vetoriais abertas
            DetectarVizinhosNasCamadasAbertas();

            // 4. Executar auditoria preliminar
            ForensicValidator.ExecutarAuditoria(_imovelSigefAtivo);

            // 5. Atualizar o texto corrido do memorial
            AtualizarPreviewMemorial();

            _abaAtivaModalSigef = 1;
            _mostrarModalCertificacaoSigef = true;
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            ExibirAlertaTopografia($"Erro ao inicializar certificação SIGEF: {ex.Message}", true);
        }
    }

    private void FecharModalCertificacaoSigef()
    {
        _mostrarModalCertificacaoSigef = false;
    }

    private void AlternarAbaModalSigef(int aba)
    {
        _abaAtivaModalSigef = aba;
        if (aba == 4)
        {
            AtualizarPreviewMemorial();
        }
        else if (aba == 5 && _imovelSigefAtivo is not null)
        {
            ForensicValidator.ExecutarAuditoria(_imovelSigefAtivo);
        }
    }

    private void AtualizarPreviewMemorial()
    {
        if (_imovelSigefAtivo is not null)
        {
            _textoMemorialVisualizacao = MemorialService.GerarTextoMemorialCorrido(_imovelSigefAtivo);
        }
    }

    private void DetectarVizinhosNasCamadasAbertas()
    {
        if (_imovelSigefAtivo is null) return;

        var vizinhos = new List<(Geometry Geometria, Dictionary<string, object> Atributos)>();

        foreach (var camada in CamadasAtivas.Where(c => c.Tipo == "Vetor" && c != CamadaAtivaFerramentas))
        {
            if (MapService.FeicoesOriginais.TryGetValue(camada.Nome, out var featureCollection))
            {
                foreach (var f in featureCollection)
                {
                    if (f.Geometry is not null && !f.Geometry.IsEmpty)
                    {
                        var dict = new Dictionary<string, object>();
                        if (f.Attributes is not null)
                        {
                            foreach (var nomeAtrib in f.Attributes.GetNames())
                            {
                                dict[nomeAtrib] = f.Attributes[nomeAtrib];
                            }
                        }
                        vizinhos.Add((f.Geometry, dict));
                    }
                }
            }
        }

        if (vizinhos.Count > 0)
        {
            ConfrontationService.DetectarConfrontantesEspaciais(_imovelSigefAtivo, vizinhos);
        }
    }

    private async Task ExecutarAuditoriaForenseAsync()
    {
        if (_imovelSigefAtivo is null)
        {
            await AbrirModalCertificacaoSigefAsync();
        }

        if (_imovelSigefAtivo is not null)
        {
            var erros = ForensicValidator.ExecutarAuditoria(_imovelSigefAtivo);
            _abaAtivaModalSigef = 5;
            _mostrarModalCertificacaoSigef = true;

            int criticos = erros.Count(e => e.Gravidade == "ERRO_CRITICO");
            int avisos = erros.Count(e => e.Gravidade == "AVISO");

            if (criticos == 0 && avisos == 0)
            {
                ExibirAlertaTopografia("Auditoria Concluída: 100% em conformidade com o padrão INCRA 3ª Edição! Planilha pronta para o SIGEF.", false);
            }
            else
            {
                ExibirAlertaTopografia($"Auditoria Concluída: {criticos} erro(s) crítico(s) e {avisos} aviso(s) detectado(s).", criticos > 0);
            }

            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task NumerarVerticesHorarioAsync()
    {
        if (_imovelSigefAtivo is null)
        {
            await AbrirModalCertificacaoSigefAsync();
        }
        else
        {
            string prefixo = _imovelSigefAtivo.CredenciadoCodigoIncra;
            for (int i = 0; i < _imovelSigefAtivo.Vertices.Count; i++)
            {
                _imovelSigefAtivo.Vertices[i].Codigo = $"{prefixo}-M-{(i + 1):D4}";
            }
            AtualizarPreviewMemorial();
            ExibirAlertaTopografia($"Vértices renumerados com sucesso no sentido horário ({_imovelSigefAtivo.Vertices.Count} vértices).", false);
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task AbrirQuadroCoordenadasAsync()
    {
        await AbrirModalCertificacaoSigefAsync();
        _abaAtivaModalSigef = 2;
        await InvokeAsync(StateHasChanged);
    }

    private async Task DetectarVizinhosEspaciaisAsync()
    {
        if (_imovelSigefAtivo is null)
        {
            await AbrirModalCertificacaoSigefAsync();
        }

        DetectarVizinhosNasCamadasAbertas();
        _abaAtivaModalSigef = 3;
        AtualizarPreviewMemorial();
        ExibirAlertaTopografia("Varredura espacial R-Tree concluída contra as camadas vizinhas.", false);
        await InvokeAsync(StateHasChanged);
    }

    private async Task AbrirTabelaCartorioAsync()
    {
        await AbrirModalCertificacaoSigefAsync();
        _abaAtivaModalSigef = 3;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ExportarPlanilhaOdsAsync()
    {
        if (_imovelSigefAtivo is null) return;

        try
        {
            if (!LicenseService.ConsumirCreditoOuValidarAssinatura())
            {
                _mostrarModalLicenca = true;
                return;
            }

            byte[] bytesOds = SigefOdsService.ExportarPlanilhaOds(_imovelSigefAtivo);
            string nomeArquivo = $"{SanitizarNomeArquivo(_imovelSigefAtivo.Denominacao)}_SIGEF.ods";
            string pastaDestino = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string caminhoCompleto = Path.Combine(pastaDestino, nomeArquivo);

            await File.WriteAllBytesAsync(caminhoCompleto, bytesOds);
            ExibirAlertaTopografia($"Planilha ODS oficial gravada com sucesso em:\n{caminhoCompleto}", false);
        }
        catch (Exception ex)
        {
            ExibirAlertaTopografia($"Falha ao exportar ODS: {ex.Message}", true);
        }
    }

    private async Task ExportarMemorialDocxAsync()
    {
        if (_imovelSigefAtivo is null) return;

        try
        {
            if (!LicenseService.ConsumirCreditoOuValidarAssinatura())
            {
                _mostrarModalLicenca = true;
                return;
            }

            byte[] bytesDocx = MemorialService.GerarArquivoDocx(_imovelSigefAtivo);
            string nomeArquivo = $"Memorial_Descritivo_{SanitizarNomeArquivo(_imovelSigefAtivo.Denominacao)}.docx";
            string pastaDestino = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string caminhoCompleto = Path.Combine(pastaDestino, nomeArquivo);

            await File.WriteAllBytesAsync(caminhoCompleto, bytesDocx);
            ExibirAlertaTopografia($"Memorial Descritivo DOCX gerado com sucesso em:\n{caminhoCompleto}", false);
        }
        catch (Exception ex)
        {
            ExibirAlertaTopografia($"Falha ao exportar DOCX: {ex.Message}", true);
        }
    }

    private async Task BaixarPacoteZipAsync()
    {
        if (_imovelSigefAtivo is null) return;

        try
        {
            if (!LicenseService.ConsumirCreditoOuValidarAssinatura())
            {
                _mostrarModalLicenca = true;
                return;
            }

            byte[] bytesOds = SigefOdsService.ExportarPlanilhaOds(_imovelSigefAtivo);
            byte[] bytesDocx = MemorialService.GerarArquivoDocx(_imovelSigefAtivo);
            string memorialTexto = MemorialService.GerarTextoMemorialCorrido(_imovelSigefAtivo);

            using var zipMs = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                var odsEntry = archive.CreateEntry($"{SanitizarNomeArquivo(_imovelSigefAtivo.Denominacao)}_SIGEF.ods");
                using (var s = odsEntry.Open()) s.Write(bytesOds, 0, bytesOds.Length);

                var docxEntry = archive.CreateEntry($"Memorial_Descritivo_{SanitizarNomeArquivo(_imovelSigefAtivo.Denominacao)}.docx");
                using (var s = docxEntry.Open()) s.Write(bytesDocx, 0, bytesDocx.Length);

                var txtEntry = archive.CreateEntry("Memorial_Descritivo_Texto.txt");
                using (var sw = new StreamWriter(txtEntry.Open(), System.Text.Encoding.UTF8)) sw.Write(memorialTexto);
            }

            string nomeZip = $"Pacote_Certificacao_{SanitizarNomeArquivo(_imovelSigefAtivo.Denominacao)}.zip";
            string caminhoZip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), nomeZip);

            await File.WriteAllBytesAsync(caminhoZip, zipMs.ToArray());
            ExibirAlertaTopografia($"Pacote de Certificação Completo (.ZIP) gravado em:\n{caminhoZip}", false);
        }
        catch (Exception ex)
        {
            ExibirAlertaTopografia($"Falha ao gerar pacote ZIP: {ex.Message}", true);
        }
    }

    private void AbrirModalLicenca()
    {
        _mostrarModalLicenca = true;
    }

    private void FecharModalLicenca()
    {
        _mostrarModalLicenca = false;
    }

    private void AtivarChaveLicenca()
    {
        if (!string.IsNullOrWhiteSpace(_chaveLicencaInput))
        {
            LicenseService.AtivarChaveLicenca(_chaveLicencaInput);
            _mostrarModalLicenca = false;
            ExibirAlertaTopografia("Licença Pro ativada com sucesso! Emissões ilimitadas habilitadas.", false);
        }
    }

    private void ExibirAlertaTopografia(string mensagem, bool isErro)
    {
        _mensagemFeedbackTopo = mensagem;
        _feedbackTopoErro = isErro;
        ExibirNotificacaoSalvamento(isErro ? "TOPOGRAFIA & SIGEF" : "SUCESSO SIGEF", mensagem, erro: isErro);
    }

    private static string SanitizarNomeArquivo(string nome)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            nome = nome.Replace(c, '_');
        }
        return nome.Trim().Replace(' ', '_');
    }

    private static Geometry CriarGlebaDemonstracao()
    {
        // Polígono retangular regular no Fuso 23S (~Ribeirão Preto / SP)
        var coords = new Coordinate[]
        {
            new CoordinateZ(245000.0, 7650000.0, 620.5),
            new CoordinateZ(245800.0, 7650000.0, 625.0),
            new CoordinateZ(245800.0, 7649200.0, 618.2),
            new CoordinateZ(245000.0, 7649200.0, 615.0),
            new CoordinateZ(245000.0, 7650000.0, 620.5) // Fechamento
        };
        return new GeometryFactory().CreatePolygon(coords);
    }
}
