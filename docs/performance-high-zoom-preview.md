# Navegação dos lotes em zoom alto — 21/09/2026

O caminho rápido de `LocalMapServer` recusava imagens em cache quando
`RenderPrecisionPolicy.NeedsLocalOrigin` exigia uma origem próxima à câmera.
Isso era conservador com a antiga composição de matrizes em float. Desde a
correção de alinhamento, `NavigationFramePolicy` compõe os deslocamentos da
câmera em double: a imagem já contém os polígonos calculados com origem precisa.

Removidas as duas exclusões por precisão, no atalho anterior ao gate e no
reaproveitamento dentro do render. As verificações de câmera, dimensão, DPI,
cobertura, impressão e invalidação continuam valendo. A construção de geometria
com origem precisa permanece ativa; a alteração reaproveita seus pixels.

## Medição no arquivo do usuário

`C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp`,
1.637.692.896 bytes, 4.937.409 feições importadas. Arquivo somente lido.
DLL real Release, GDAL 3.12.1, índice nativo, reprojeção para EPSG:3857,
1600 × 900 CSS, DPI 1, câmera sobre a feição intermediária, escala 4 px/m.
Sem mapa online, para isolar o custo vetorial. Seis movimentos de 0/4/8 px
após uma imagem final, com a mesma sequência antes/depois:

| Medida HTTP local | Antes | Depois |
|---|---:|---:|
| Mediana dos movimentos | 364 ms | 26 ms |
| Intervalo dos movimentos | 353–392 ms | 24–30 ms |
| Geometria por movimento | 46–65 ms | 0 ms |
| Desenho por movimento | 294–312 ms | 6–8 ms |
| Primeira imagem completa | 420 ms | 427 ms |

O ganho se aplica ao reaproveitamento enquanto a área visível cabe no cache.
A primeira imagem, alterações da cena, zoom final novo e movimentos para fora
da cobertura continuam exigindo trabalho completo. Não é medição de FPS no
WebView nem comparação com QGIS. O gargalo foi reproduzido sem downloads;
isso não atribui toda a percepção de regressão às alterações dos tiles.

## Validação

`HighZoomPreviewContracts` bloqueia o renderer completo e exige que a prévia
chegue mesmo assim. Confere pixels idênticos na mesma câmera, preservação de
espaços entre polígonos, pan, DPI 1/2, escalas 4/64 e invalidação após edição.
Falha por timeout na DLL anterior e passa na corrigida. Contratos de precisão,
alinhamento, coordenadas, SHP grande, PNG e endpoint online progressivo passaram.

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --high-zoom-preview-contracts GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp' --high-zoom
```
