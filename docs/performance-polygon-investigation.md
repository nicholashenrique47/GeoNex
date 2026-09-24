# Investigação de polígonos — 22/09/2026

Primeira rodada da meta contínua de desempenho, qualidade e fluidez. Não
conclui o plano mestre nem declara equivalência/perfeição. Nesta rodada, o
renderer de produção não foi modificado: os experimentos ficam em `PerfTest`.

## Referência reproduzível

LOTES.shp original, 4.937.409 feições, somente leitura. Mesma máquina das
medições de 21/09, GeoNex Release, GDAL 3.12.1. Área centrada na feição
intermediária em EPSG:3857 (-5412144, -2984383), 2050 × 1350 pixels com
overscan, 4 px/m. Sem raster/rede. O diagnóstico passou a registrar quantidade
de vértices e limites do path efetivamente retido pelo motor.

Uma rodada: primeira imagem HTTP 423 ms, geometria 58 ms, desenho 271 ms;
movimentos com cache 25–31 ms. Path final: 197.632 pontos, 65.152 fora do
retângulo do bitmap. Comparações de pintura usam o mesmo path real e a mesma
câmera, três amostras após aquecimento, ordem alternada e igualdade de pixels.

## Experimentos rejeitados

1. Descartar somente contornos fechados inteiramente externos, com margem
   para stroke/antialias: 197.632 → 165.888 pontos, pixels idênticos.
   Preparação ~13 ms; pintura mediana 261 → 261 ms. Não compensa o custo.
2. Remover contornos repetidos apenas na borda, preservando o preenchimento
   original e sua multiplicidade de winding: alterou pixels. Rejeitado por
   qualidade; não instalado no motor. Igualdade de coordenadas não prova
   equivalência do resultado do stroke/antialias com transparência.

`PerfTest/Experiments/PolygonContourCulling.cs` é uma implementação experimental
isolada do aplicativo. Os experimentos exigem `--polygon-paint-experiments`;
o diagnóstico normal de zoom alto não os executa.

## Referência QGIS instalada

QGIS 4.2.0, renderer sequencial de camada, OGR, mesma fonte, extensão,
EPSG:3857, tamanho físico e cores nominais: preenchimento cyan alpha 25,
borda cyan alpha 200 com 1 pixel, sem rótulos. Tempos de três renders:
9.799 / 6.318 / 3.380 ms. PNG gravado depois do cronômetro; nenhum cache de
imagem de render entre jobs. Aquecimento e acesso à fonte influenciam bastante.

Esses números NÃO constituem comparação controlada de superioridade: QGIS
pinta símbolos por feição e GeoNex agrega geometrias; transparências e
sobreposições podem produzir imagens diferentes. Providers, índices e
simplificação também precisam ser alinhados antes de comparar. Não medem
pan/zoom do canvas QGIS ou FPS no WebView. O script registra a versão e grava
referências em uma pasta temporária, sem alterar o SHP.

O código [QGIS 4.2.0 QgsSymbol](https://github.com/qgis/QGIS/blob/final-4_2_0/src/core/symbology/qgssymbol.cpp)
confirma recorte perto da extensão e opções antes/depois da reprojeção. Essa
referência orienta hipóteses, não garante ganho no backend Skia do GeoNex.
GDAL/OGR fornece acesso e transformação dos dados; o caminho de pintura
interativa do QGIS não se reduz ao `gdal_rasterize`.

## Reprodução

```powershell
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp' --high-zoom --polygon-paint-experiments
$env:QT_QPA_PLATFORM = 'offscreen'
& 'C:/Program Files/QGIS 4.2.0/bin/python-qgis.bat' 'PerfTest/QgisRenderMetrics.py' 'C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp'
```

## Próxima investigação

Separar preenchimento e stroke no perfil, avaliar pintura em blocos com
orçamento global e igualdade de pixels, e medir cenas mistas durante os
refinamentos dos tiles. Comparação visual/funcional com o projeto real e
alinhamento metodológico do benchmark QGIS continuam pendentes. Manter a
precisão da geometria, transparências, qualidade final e caches existentes.
