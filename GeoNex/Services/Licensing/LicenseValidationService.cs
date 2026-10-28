namespace GeoNex.Services.Licensing;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public enum TipoPlanoLicenca
{
    Gratuito,
    ProMensal,
    ProAnual,
    Enterprise,
    CreditoAvulso
}

public sealed class DadosLicenca
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Email { get; set; } = "usuario@geonex.com.br";
    public TipoPlanoLicenca Plano { get; set; } = TipoPlanoLicenca.ProMensal;
    public DateTime DataExpiracao { get; set; } = DateTime.UtcNow.AddYears(1);
    public int CreditosAvulsos { get; set; } = 10;
    public bool Ativa { get; set; } = true;
}

public sealed class LicenseValidationService
{
    private DadosLicenca _licencaAtual;

    public LicenseValidationService()
    {
        // Por padrão no ambiente Pro Desktop inicializamos com Licença Pro Ativa
        _licencaAtual = new DadosLicenca
        {
            Email = "usuario.pro@geonex.com.br",
            Plano = TipoPlanoLicenca.ProMensal,
            DataExpiracao = DateTime.UtcNow.AddMonths(12),
            Ativa = true,
            CreditosAvulsos = 999
        };
    }

    public DadosLicenca ObterLicencaAtual() => _licencaAtual;

    public bool PossuiPermissaoExportacaoPro()
    {
        return _licencaAtual.Ativa && (_licencaAtual.Plano != TipoPlanoLicenca.Gratuito || _licencaAtual.CreditosAvulsos > 0);
    }

    public bool ConsumirCreditoOuValidarAssinatura()
    {
        if (!_licencaAtual.Ativa) return false;

        if (_licencaAtual.Plano != TipoPlanoLicenca.Gratuito)
            return true;

        if (_licencaAtual.CreditosAvulsos > 0)
        {
            _licencaAtual.CreditosAvulsos--;
            return true;
        }

        return false;
    }

    public void AtivarChaveLicenca(string chave)
    {
        if (string.IsNullOrWhiteSpace(chave)) return;

        _licencaAtual = new DadosLicenca
        {
            Email = "assinante@geonex.com.br",
            Plano = TipoPlanoLicenca.ProAnual,
            DataExpiracao = DateTime.UtcNow.AddYears(1),
            Ativa = true,
            CreditosAvulsos = 9999
        };
    }
}
