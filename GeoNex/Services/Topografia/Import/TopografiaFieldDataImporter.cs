namespace GeoNex.Services.Topografia.Import;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GeoNex.Topografia.Domain;

public sealed class TopografiaFieldDataImporter
{
    private sealed class MapeamentoColunas
    {
        public int ColunaNome { get; set; } = -1;
        public int ColunaX { get; set; } = -1;
        public int ColunaY { get; set; } = -1;
        public int ColunaZ { get; set; } = -1;
        public int ColunaTipo { get; set; } = -1;
        public int ColunaMetodo { get; set; } = -1;
        public int ColunaConfrontante { get; set; } = -1;
        public int ColunaSigma { get; set; } = -1;
    }

    public List<VerticeTopografico> ImportarArquivoCampo(Stream streamArquivo, out List<string> alertas)
    {
        alertas = new List<string>();
        var vertices = new List<VerticeTopografico>();

        using var reader = new StreamReader(streamArquivo);
        string? primeiraLinha = null;
        int linhaAtual = 0;

        // Pular comentários iniciais e linhas em branco para encontrar o cabeçalho
        while ((primeiraLinha = reader.ReadLine()) != null)
        {
            linhaAtual++;
            string trimmed = primeiraLinha.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//")) continue;
            break;
        }

        if (string.IsNullOrWhiteSpace(primeiraLinha))
            throw new InvalidOperationException("O arquivo de campo está vazio ou contém apenas comentários.");

        char delimitador = DetectarDelimitador(primeiraLinha);
        var cabecalhos = primeiraLinha.Split(delimitador);
        var mapa = IdentificarColunas(cabecalhos);

        string? linha;
        while ((linha = reader.ReadLine()) != null)
        {
            linhaAtual++;
            string trimmed = linha.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//")) continue;

            var partes = linha.Split(delimitador);
            if (partes.Length < 2) continue;

            try
            {
                string nome = (mapa.ColunaNome >= 0 && mapa.ColunaNome < partes.Length)
                    ? partes[mapa.ColunaNome].Trim()
                    : $"P{linhaAtual}";

                double x = ParseCoordenada(partes[mapa.ColunaX]);
                double y = ParseCoordenada(partes[mapa.ColunaY]);
                double z = (mapa.ColunaZ >= 0 && mapa.ColunaZ < partes.Length)
                    ? ParseCoordenada(partes[mapa.ColunaZ])
                    : 0.0;

                string metodo = (mapa.ColunaMetodo >= 0 && mapa.ColunaMetodo < partes.Length)
                    ? partes[mapa.ColunaMetodo].Trim().ToUpperInvariant()
                    : "PG1";

                double sigma = (mapa.ColunaSigma >= 0 && mapa.ColunaSigma < partes.Length)
                    ? ParseCoordenada(partes[mapa.ColunaSigma])
                    : 0.05;

                // Heurística de transposição de eixos (se Este > 1.000.000 e Norte < 1.000.000, inverter)
                if (x > 1000000.0 && y < 1000000.0)
                {
                    (x, y) = (y, x);
                    alertas.Add("Detectada inversão entre colunas Este e Norte; os eixos foram ajustados automaticamente.");
                }

                // Inferir Tipo de Vértice (M, P ou V) a partir do nome
                TipoVertice tipo = TipoVertice.M;
                if (nome.Contains("-P-", StringComparison.OrdinalIgnoreCase) || nome.StartsWith("P", StringComparison.OrdinalIgnoreCase))
                    tipo = TipoVertice.P;
                else if (nome.Contains("-V-", StringComparison.OrdinalIgnoreCase) || nome.StartsWith("V", StringComparison.OrdinalIgnoreCase))
                    tipo = TipoVertice.V;

                vertices.Add(new VerticeTopografico
                {
                    Indice = vertices.Count + 1,
                    Codigo = nome,
                    Tipo = tipo,
                    EsteUtm = x,
                    NorteUtm = y,
                    AltitudeOrtometrica = z,
                    SigmaX = sigma,
                    SigmaY = sigma,
                    MetodoPosicionamento = metodo
                });
            }
            catch (Exception ex)
            {
                alertas.Add($"Linha {linhaAtual} ignorada por erro de formatação: {ex.Message}");
            }
        }

        return vertices;
    }

    private static char DetectarDelimitador(string linha)
    {
        if (linha.Contains(';')) return ';';
        if (linha.Contains(',')) return ',';
        if (linha.Contains('\t')) return '\t';
        return ' ';
    }

    private static MapeamentoColunas IdentificarColunas(string[] cabecalhos)
    {
        var mapa = new MapeamentoColunas();
        for (int i = 0; i < cabecalhos.Length; i++)
        {
            string col = cabecalhos[i].Trim().ToUpperInvariant();
            if (col is "PONTO" or "NOME" or "NAME" or "ID" or "CODIGO" or "VERTICE" or "POINT")
                mapa.ColunaNome = i;
            else if (col is "ESTE" or "EAST" or "EASTING" or "X" or "LONGITUDE" or "LON")
                mapa.ColunaX = i;
            else if (col is "NORTE" or "NORTH" or "NORTHING" or "Y" or "LATITUDE" or "LAT")
                mapa.ColunaY = i;
            else if (col is "COTA" or "Z" or "ALTITUDE" or "ELEVATION" or "ALT" or "ELEV")
                mapa.ColunaZ = i;
            else if (col is "METODO" or "METHOD" or "TIPO_POS" or "POS")
                mapa.ColunaMetodo = i;
            else if (col is "SIGMA" or "PRECISAO" or "ACCURACY" or "STD" or "STDDEV")
                mapa.ColunaSigma = i;
            else if (col is "CONFRONTANTE" or "VIZINHO" or "CONFRONTA")
                mapa.ColunaConfrontante = i;
        }

        // Se não houver cabeçalhos explícitos com texto reconhecido, assumir ordem típica: Nome, X, Y, Z
        if (mapa.ColunaX == -1 || mapa.ColunaY == -1)
        {
            mapa.ColunaNome = 0;
            mapa.ColunaX = 1;
            mapa.ColunaY = 2;
            mapa.ColunaZ = cabecalhos.Length > 3 ? 3 : -1;
        }

        return mapa;
    }

    private static double ParseCoordenada(string valor)
    {
        valor = valor.Trim().Replace(',', '.');
        return double.Parse(valor, CultureInfo.InvariantCulture);
    }
}
