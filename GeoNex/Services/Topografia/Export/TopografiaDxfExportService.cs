namespace GeoNex.Services.Topografia.Export;

using System;
using System.IO;
using System.Text;
using GeoNex.Topografia.Domain;

public sealed class TopografiaDxfExportService
{
    public byte[] GerarDxfCadastral(ImovelCertificacaoSigef imovel, double espacamentoGridMetros = 200.0)
    {
        using var ms = new MemoryStream();
        // Usar codificação Latin1 para garantir compatibilidade com versões antigas do AutoCAD
        using var writer = new StreamWriter(ms, Encoding.Latin1);

        EscreverHeader(writer);
        EscreverTables(writer);
        EscreverBlocks(writer);
        EscreverEntities(writer, imovel, espacamentoGridMetros);
        EscreverEof(writer);

        writer.Flush();
        return ms.ToArray();
    }

    private static void EscreverHeader(StreamWriter w)
    {
        w.WriteLine("0\nSECTION\n2\nHEADER");
        w.WriteLine("9\n$ACADVER\n1\nAC1009"); // AutoCAD R12 ASCII
        w.WriteLine("9\n$INSUNITS\n70\n6");     // 6 = Metros
        w.WriteLine("0\nENDSEC");
    }

    private static void EscreverTables(StreamWriter w)
    {
        w.WriteLine("0\nSECTION\n2\nTABLES");
        w.WriteLine("0\nTABLE\n2\nLAYER\n70\n7");

        CriarLayer(w, "01_PERIMETRO", 1);           // Vermelho ACI 1
        CriarLayer(w, "02_MARCOS_SIMBOLO", 4);       // Ciano ACI 4
        CriarLayer(w, "03_VERTICES_TEXTO", 7);       // Branco/Preto ACI 7
        CriarLayer(w, "04_AZIMUTES_DISTANCIAS", 3);  // Verde ACI 3
        CriarLayer(w, "05_CONFRONTANTES", 2);        // Amarelo ACI 2
        CriarLayer(w, "06_MALHA_UTM", 8);            // Cinza ACI 8
        CriarLayer(w, "07_TABELA_VERTICES", 7);      // Branco ACI 7

        w.WriteLine("0\nENDTAB");
        w.WriteLine("0\nENDSEC");
    }

    private static void CriarLayer(StreamWriter w, string nome, int corAci)
    {
        w.WriteLine("0\nLAYER");
        w.WriteLine($"2\n{nome}");
        w.WriteLine("70\n0");
        w.WriteLine($"62\n{corAci}");
        w.WriteLine("6\nCONTINUOUS");
    }

    private static void EscreverBlocks(StreamWriter w)
    {
        w.WriteLine("0\nSECTION\n2\nBLOCKS");
        // Bloco SIMBOLO_MARCO (Círculo de 1.5m de raio com cruzeta de 5m de envergadura)
        w.WriteLine("0\nBLOCK\n2\nSIMBOLO_MARCO\n70\n0\n10\n0.0\n20\n0.0\n30\n0.0");
        w.WriteLine("0\nCIRCLE\n8\n0\n10\n0.0\n20\n0.0\n30\n0.0\n40\n1.5");
        w.WriteLine("0\nLINE\n8\n0\n10\n-2.5\n20\n0.0\n30\n0.0\n11\n2.5\n21\n0.0\n31\n0.0");
        w.WriteLine("0\nLINE\n8\n0\n10\n0.0\n20\n-2.5\n30\n0.0\n11\n0.0\n21\n2.5\n31\n0.0");
        w.WriteLine("0\nENDBLK");
        w.WriteLine("0\nENDSEC");
    }

    private static void EscreverEntities(StreamWriter w, ImovelCertificacaoSigef imovel, double gridStep)
    {
        w.WriteLine("0\nSECTION\n2\nENTITIES");

        if (imovel.Vertices.Count >= 3)
        {
            // 1. Camada 01_PERIMETRO (Polilinha fechada)
            w.WriteLine("0\nPOLYLINE\n8\n01_PERIMETRO\n66\n1\n70\n1"); // 70=1 flag fechada
            foreach (var v in imovel.Vertices)
            {
                w.WriteLine($"0\nVERTEX\n8\n01_PERIMETRO\n10\n{v.EsteUtm:F3}\n20\n{v.NorteUtm:F3}\n30\n{v.AltitudeOrtometrica:F3}");
            }
            w.WriteLine("0\nSEQEND");

            // 2. Camadas 02_MARCOS_SIMBOLO e 03_VERTICES_TEXTO
            foreach (var v in imovel.Vertices)
            {
                // Inserir bloco
                w.WriteLine($"0\nINSERT\n2\nSIMBOLO_MARCO\n8\n02_MARCOS_SIMBOLO\n10\n{v.EsteUtm:F3}\n20\n{v.NorteUtm:F3}\n30\n0.0\n41\n1.0\n42\n1.0\n43\n1.0");

                // Texto do vértice deslocado +2.5m
                w.WriteLine($"0\nTEXT\n8\n03_VERTICES_TEXTO\n10\n{(v.EsteUtm + 2.5):F3}\n20\n{(v.NorteUtm + 2.5):F3}\n30\n0.0\n40\n2.5\n1\n{v.Codigo}\n50\n0.0");
            }

            // 3. Camadas 04_AZIMUTES_DISTANCIAS e 05_CONFRONTANTES
            foreach (var seg in imovel.Segmentos)
            {
                double dE = seg.Para.EsteUtm - seg.De.EsteUtm;
                double dN = seg.Para.NorteUtm - seg.De.NorteUtm;
                double angRad = Math.Atan2(dN, dE);

                // Normalização ABNT de legibilidade do texto: entre -90° e +90°
                double angGraus = angRad * 180.0 / Math.PI;
                if (angGraus > 90.0 || angGraus <= -90.0) angGraus += 180.0;
                if (angGraus >= 360.0) angGraus -= 360.0;

                double midE = (seg.De.EsteUtm + seg.Para.EsteUtm) / 2.0;
                double midN = (seg.De.NorteUtm + seg.Para.NorteUtm) / 2.0;

                double normE = -Math.Sin(angRad);
                double normN = Math.Cos(angRad);

                // Offset de 3m para métrica
                double txtE = midE + normE * 3.0;
                double txtN = midN + normN * 3.0;
                string rotuloMetrica = $"Az: {seg.AzimuteFormatadoGms}  Dist: {seg.DistanciaPlanaMetros:F2}m";
                w.WriteLine($"0\nTEXT\n8\n04_AZIMUTES_DISTANCIAS\n10\n{txtE:F3}\n20\n{txtN:F3}\n30\n0.0\n40\n2.0\n1\n{rotuloMetrica}\n50\n{angGraus:F1}");

                // Offset de 8m para confrontante
                if (!string.IsNullOrWhiteSpace(seg.NomeConfrontante))
                {
                    double confE = midE + normE * 8.0;
                    double confN = midN + normN * 8.0;
                    string rotuloConf = $"Conf: {seg.NomeConfrontante}";
                    w.WriteLine($"0\nTEXT\n8\n05_CONFRONTANTES\n10\n{confE:F3}\n20\n{confN:F3}\n30\n0.0\n40\n2.8\n1\n{rotuloConf}\n50\n{angGraus:F1}");
                }
            }

            // 4. Camada 06_MALHA_UTM (Cruzetas e Anotações de Coordenadas)
            GerarMalhaUtm(w, imovel, gridStep);
        }

        w.WriteLine("0\nENDSEC");
    }

    private static void GerarMalhaUtm(StreamWriter w, ImovelCertificacaoSigef imovel, double gridStep)
    {
        if (gridStep <= 10.0) gridStep = 200.0;

        double minE = double.MaxValue, maxE = double.MinValue;
        double minN = double.MaxValue, maxN = double.MinValue;

        foreach (var v in imovel.Vertices)
        {
            if (v.EsteUtm < minE) minE = v.EsteUtm;
            if (v.EsteUtm > maxE) maxE = v.EsteUtm;
            if (v.NorteUtm < minN) minN = v.NorteUtm;
            if (v.NorteUtm > maxN) maxN = v.NorteUtm;
        }

        double margem = Math.Max(maxE - minE, maxN - minN) * 0.15;
        minE -= margem; maxE += margem;
        minN -= margem; maxN += margem;

        double startE = Math.Floor(minE / gridStep) * gridStep;
        double endE = Math.Ceiling(maxE / gridStep) * gridStep;
        double startN = Math.Floor(minN / gridStep) * gridStep;
        double endN = Math.Ceiling(maxN / gridStep) * gridStep;

        double tamCruzeta = gridStep * 0.04;

        for (double x = startE; x <= endE; x += gridStep)
        {
            for (double y = startN; y <= endN; y += gridStep)
            {
                // Barra horizontal
                w.WriteLine($"0\nLINE\n8\n06_MALHA_UTM\n10\n{(x - tamCruzeta):F3}\n20\n{y:F3}\n30\n0.0\n11\n{(x + tamCruzeta):F3}\n21\n{y:F3}\n31\n0.0");
                // Barra vertical
                w.WriteLine($"0\nLINE\n8\n06_MALHA_UTM\n10\n{x:F3}\n20\n{(y - tamCruzeta):F3}\n30\n0.0\n11\n{x:F3}\n21\n{(y + tamCruzeta):F3}\n31\n0.0");

                // Texto indicador
                w.WriteLine($"0\nTEXT\n8\n06_MALHA_UTM\n10\n{(x + 1.2):F3}\n20\n{(y + 1.2):F3}\n30\n0.0\n40\n1.8\n1\nE:{x:F0} N:{y:F0}\n50\n0.0");
            }
        }
    }

    private static void EscreverEof(StreamWriter w)
    {
        w.WriteLine("0\nEOF");
    }
}
