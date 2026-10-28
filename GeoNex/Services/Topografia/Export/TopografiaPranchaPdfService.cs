#pragma warning disable CS0618 // SkiaSharp obsolete text API warnings

namespace GeoNex.Services.Topografia.Export;

using System;
using System.IO;
using SkiaSharp;
using GeoNex.Topografia.Domain;

public sealed class TopografiaPranchaPdfService
{
    private const float MmToPt = 2.83465f;
    private const float LarguraA3Pt = 420.0f * MmToPt; // 1190.55 pt
    private const float AlturaA3Pt = 297.0f * MmToPt;  // 841.89 pt

    public byte[] GerarPlantaTopograficaPdf(ImovelCertificacaoSigef imovel)
    {
        using var ms = new MemoryStream();
        using var document = SKDocument.CreatePdf(ms);
        using var canvas = document.BeginPage(LarguraA3Pt, AlturaA3Pt);

        // 1. Moldura ABNT
        float margemEsq = 25.0f * MmToPt;
        float margemSup = 10.0f * MmToPt;
        float margemDir = 10.0f * MmToPt;
        float margemInf = 10.0f * MmToPt;

        DesenharMolduraAbnt(canvas, margemEsq, margemSup, margemDir, margemInf);

        // 2. Região do Selo / Carimbo Cartorário (Canto Inferior Direito: 175mm x 90mm)
        float seloLargura = 175.0f * MmToPt;
        float seloAltura = 90.0f * MmToPt;
        float seloX = LarguraA3Pt - margemDir - seloLargura;
        float seloY = AlturaA3Pt - margemInf - seloAltura;

        DesenharCarimboCartorario(canvas, imovel, seloX, seloY, seloLargura, seloAltura);

        // 3. Região da Tabela de Vértices (Acima do Selo)
        float tabY = margemSup + 10.0f * MmToPt;
        float tabLargura = seloLargura;
        float tabAltura = seloY - tabY - 10.0f * MmToPt;
        DesenharTabelaVertices(canvas, imovel, seloX, tabY, tabLargura, tabAltura);

        // 4. Viewport da Planta da Gleba (Área Livre à Esquerda)
        float vpX = margemEsq + 5.0f * MmToPt;
        float vpY = margemSup + 5.0f * MmToPt;
        float vpLargura = seloX - vpX - 10.0f * MmToPt;
        float vpAltura = AlturaA3Pt - margemInf - vpY - 25.0f * MmToPt;

        DesenharPlantaGleba(canvas, imovel, vpX, vpY, vpLargura, vpAltura, out double escalaUtilizada);

        // 5. Escala Gráfica e Rosa dos Ventos
        DesenharRosaDosVentos(canvas, imovel, vpX + 15.0f * MmToPt, vpY + 25.0f * MmToPt);
        DesenharEscalaGrafica(canvas, escalaUtilizada, vpX + 10.0f * MmToPt, AlturaA3Pt - margemInf - 15.0f * MmToPt);

        document.EndPage();
        document.Close();

        return ms.ToArray();
    }

    private static void DesenharMolduraAbnt(SKCanvas canvas, float e, float s, float d, float i)
    {
        using var paintQuadro = new SKPaint
        {
            Color = SKColors.Black,
            StrokeWidth = 1.0f * MmToPt,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        canvas.DrawRect(e, s, LarguraA3Pt - e - d, AlturaA3Pt - s - i, paintQuadro);
    }

    private static void DesenharCarimboCartorario(SKCanvas canvas, ImovelCertificacaoSigef imovel, float x, float y, float w, float h)
    {
        using var paintBorda = new SKPaint { Color = SKColors.Black, StrokeWidth = 0.5f * MmToPt, Style = SKPaintStyle.Stroke };
        using var paintFundo = new SKPaint { Color = new SKColor(248, 249, 250), Style = SKPaintStyle.Fill };
        using var paintTextoBold = new SKPaint { Color = SKColors.Black, TextSize = 10.0f, IsAntialias = true, FakeBoldText = true };
        using var paintTextoRegular = new SKPaint { Color = SKColors.Black, TextSize = 8.5f, IsAntialias = true };

        canvas.DrawRect(x, y, w, h, paintFundo);
        canvas.DrawRect(x, y, w, h, paintBorda);

        canvas.DrawText("PLANTA TOPOGRÁFICA CADASTRAL - GEORREFERENCIAMENTO", x + 8, y + 16, paintTextoBold);
        canvas.DrawText("SISTEMA DE GESTÃO FUNDIÁRIA (SIGEF / INCRA)", x + 8, y + 28, paintTextoRegular);
        canvas.DrawLine(x, y + 34, x + w, y + 34, paintBorda);

        canvas.DrawText($"IMÓVEL: {imovel.Denominacao}", x + 8, y + 48, paintTextoBold);
        canvas.DrawText($"PROPRIETÁRIO: {imovel.ProprietarioNome}", x + 8, y + 60, paintTextoRegular);
        canvas.DrawText($"MUNICÍPIO / UF: {imovel.Municipio} - {imovel.Uf}", x + 8, y + 72, paintTextoRegular);
        canvas.DrawText($"ÁREA: {imovel.AreaHectares:N4} ha   |   PERÍMETRO: {imovel.PerimetroMetros:N2} m", x + 8, y + 84, paintTextoBold);

        canvas.DrawLine(x, y + 92, x + w, y + 92, paintBorda);

        canvas.DrawText($"RESP. TÉCNICO: {imovel.CredenciadoNome}", x + 8, y + 106, paintTextoRegular);
        canvas.DrawText($"CREA/CAU: {imovel.CredenciadoCreaCau}   |   ART/TRT: {imovel.NumeroArtTrt}", x + 8, y + 118, paintTextoRegular);
        canvas.DrawText($"CÓDIGO CREDENCIADO INCRA: {imovel.CredenciadoCodigoIncra}", x + 8, y + 130, paintTextoRegular);
        canvas.DrawText($"DATUM: {imovel.NomeDatumExibicao}  |  FUSO UTM: {imovel.FusoUtm}S", x + 8, y + 142, paintTextoRegular);
    }

    private static void DesenharTabelaVertices(SKCanvas canvas, ImovelCertificacaoSigef imovel, float x, float y, float w, float h)
    {
        using var paintBorda = new SKPaint { Color = SKColors.Black, StrokeWidth = 0.35f * MmToPt, Style = SKPaintStyle.Stroke };
        using var paintHeaderFundo = new SKPaint { Color = new SKColor(230, 235, 245), Style = SKPaintStyle.Fill };
        using var paintTextoBold = new SKPaint { Color = SKColors.Black, TextSize = 8.0f, IsAntialias = true, FakeBoldText = true };
        using var paintTexto = new SKPaint { Color = SKColors.Black, TextSize = 7.5f, IsAntialias = true };

        canvas.DrawRect(x, y, w, h, paintBorda);
        canvas.DrawRect(x, y, w, 18, paintHeaderFundo);
        canvas.DrawLine(x, y + 18, x + w, y + 18, paintBorda);

        canvas.DrawText("QUADRO DE COORDENADAS DOS VÉRTICES (UTM / SIRGAS 2000)", x + 6, y + 12, paintTextoBold);

        float colV = x + 6;
        float colE = x + 50;
        float colN = x + 105;
        float colZ = x + 160;

        float linhaY = y + 30;
        int maxLinhas = (int)((h - 35) / 12);
        int contador = 0;

        foreach (var v in imovel.Vertices)
        {
            if (contador++ >= maxLinhas)
            {
                canvas.DrawText($"... e mais {imovel.Vertices.Count - maxLinhas} vértices no memorial", x + 6, linhaY, paintTexto);
                break;
            }

            canvas.DrawText(v.Codigo, colV, linhaY, paintTextoBold);
            canvas.DrawText($"{v.EsteUtm:F2}", colE, linhaY, paintTexto);
            canvas.DrawText($"{v.NorteUtm:F2}", colN, linhaY, paintTexto);
            canvas.DrawText($"{v.AltitudeOrtometrica:F2}m", colZ, linhaY, paintTexto);
            linhaY += 12;
        }
    }

    private static void DesenharPlantaGleba(SKCanvas canvas, ImovelCertificacaoSigef imovel, float x, float y, float w, float h, out double escala)
    {
        escala = 5000.0;
        if (imovel.Vertices.Count < 3) return;

        double minE = double.MaxValue, maxE = double.MinValue;
        double minN = double.MaxValue, maxN = double.MinValue;

        foreach (var v in imovel.Vertices)
        {
            if (v.EsteUtm < minE) minE = v.EsteUtm;
            if (v.EsteUtm > maxE) maxE = v.EsteUtm;
            if (v.NorteUtm < minN) minN = v.NorteUtm;
            if (v.NorteUtm > maxN) maxN = v.NorteUtm;
        }

        double deltaE = maxE - minE;
        double deltaN = maxN - minN;

        float escalaX = (float)(deltaE / (w / MmToPt * 0.001));
        float escalaY = (float)(deltaN / (h / MmToPt * 0.001));
        double escalaBruta = Math.Max(escalaX, escalaY) * 1.30;

        int[] escalasPadrao = { 250, 500, 750, 1000, 1250, 1500, 2000, 2500, 5000, 7500, 10000, 15000, 20000, 25000, 50000 };
        escala = escalasPadrao[^1];
        foreach (var s in escalasPadrao)
        {
            if (s >= escalaBruta) { escala = s; break; }
        }

        float fatorPtPorMetro = (float)(MmToPt / (escala * 0.001));

        double midE = (minE + maxE) / 2.0;
        double midN = (minN + maxN) / 2.0;
        float centroX = x + w / 2.0f;
        float centroY = y + h / 2.0f;

        using var paintPoligono = new SKPaint
        {
            Color = new SKColor(220, 53, 69),
            StrokeWidth = 1.8f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        using var paintPreenchimento = new SKPaint
        {
            Color = new SKColor(220, 53, 69, 25),
            Style = SKPaintStyle.Fill
        };

        using var path = new SKPath();
        for (int i = 0; i < imovel.Vertices.Count; i++)
        {
            var v = imovel.Vertices[i];
            float ptX = centroX + (float)((v.EsteUtm - midE) * fatorPtPorMetro);
            float ptY = centroY - (float)((v.NorteUtm - midN) * fatorPtPorMetro);

            if (i == 0) path.MoveTo(ptX, ptY);
            else path.LineTo(ptX, ptY);
        }
        path.Close();

        canvas.DrawPath(path, paintPreenchimento);
        canvas.DrawPath(path, paintPoligono);

        using var paintMarco = new SKPaint { Color = SKColors.Blue, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var paintTxt = new SKPaint { Color = SKColors.Black, TextSize = 7.0f, IsAntialias = true, FakeBoldText = true };

        foreach (var v in imovel.Vertices)
        {
            float ptX = centroX + (float)((v.EsteUtm - midE) * fatorPtPorMetro);
            float ptY = centroY - (float)((v.NorteUtm - midN) * fatorPtPorMetro);
            canvas.DrawCircle(ptX, ptY, 2.5f, paintMarco);
            canvas.DrawText(v.Codigo, ptX + 4, ptY - 3, paintTxt);
        }
    }

    private static void DesenharRosaDosVentos(SKCanvas canvas, ImovelCertificacaoSigef imovel, float x, float y)
    {
        using var paintLinha = new SKPaint { Color = SKColors.Black, StrokeWidth = 1.2f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var paintPreto = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var paintTexto = new SKPaint { Color = SKColors.Black, TextSize = 9.0f, IsAntialias = true, FakeBoldText = true };

        canvas.DrawLine(x, y - 20, x, y + 20, paintLinha);
        canvas.DrawLine(x - 20, y, x + 20, y, paintLinha);

        using var pathNorte = new SKPath();
        pathNorte.MoveTo(x, y - 20);
        pathNorte.LineTo(x - 4, y - 6);
        pathNorte.LineTo(x, y);
        pathNorte.Close();
        canvas.DrawPath(pathNorte, paintPreto);

        canvas.DrawText("NQ", x - 6, y - 24, paintTexto);
        canvas.DrawText("N", x - 3, y - 10, paintTexto);
    }

    private static void DesenharEscalaGrafica(SKCanvas canvas, double escala, float x, float y)
    {
        using var paintTexto = new SKPaint { Color = SKColors.Black, TextSize = 8.5f, IsAntialias = true, FakeBoldText = true };
        using var paintPreto = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
        using var paintBranco = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var paintBorda = new SKPaint { Color = SKColors.Black, StrokeWidth = 0.5f, Style = SKPaintStyle.Stroke };

        canvas.DrawText($"ESCALA 1 : {escala:N0}", x, y - 6, paintTexto);

        float blocoLarguraPt = (float)(100.0 * (MmToPt / (escala * 0.001)));
        float blocoAlturaPt = 4.0f;

        canvas.DrawRect(x, y, blocoLarguraPt, blocoAlturaPt, paintPreto);
        canvas.DrawRect(x, y, blocoLarguraPt, blocoAlturaPt, paintBorda);
        canvas.DrawText("0", x - 2, y + 12, paintTexto);

        canvas.DrawRect(x + blocoLarguraPt, y, blocoLarguraPt, blocoAlturaPt, paintBranco);
        canvas.DrawRect(x + blocoLarguraPt, y, blocoLarguraPt, blocoAlturaPt, paintBorda);
        canvas.DrawText("100m", x + blocoLarguraPt - 8, y + 12, paintTexto);

        canvas.DrawRect(x + blocoLarguraPt * 2, y, blocoLarguraPt * 2, blocoAlturaPt, paintPreto);
        canvas.DrawRect(x + blocoLarguraPt * 2, y, blocoLarguraPt * 2, blocoAlturaPt, paintBorda);
        canvas.DrawText("200m", x + blocoLarguraPt * 2 - 8, y + 12, paintTexto);
        canvas.DrawText("400m", x + blocoLarguraPt * 4 - 8, y + 12, paintTexto);
    }
}
