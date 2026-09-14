# Reutilização de payload PNG — recorte de P3-W

Data: 14/09/2026. Continuação do [plano mestre](plano-mestre-motor-gis-qgis.md). Não conclui transporte incremental nem muda o protocolo WebView.

## Decisão e implementação

O ensaio anterior de compressão reduzia LOTES de 11,09 MB para 2,12 MB, mas aumentava latência. Testar filtros None/Up/Sub nos níveis 0/1 também não superou o caminho store em encode+decode. `GEONEX_PNG_COMPRESSION_PROBE` continua desligado por padrão.

`EncodedFrameCache` guarda **um PNG codificado**. Um novo quadro só reutiliza o payload quando o SHA-256 de todos os pixels, dimensões, ordem dos canais, tipo de alpha e nível de compressão coincidem. Não depende apenas de revisão da cena, câmera ou amostra de pixels. Headers de frame/telemetria são sempre os atuais; o navegador continua recebendo PNG completo.

Integração no `LocalMapServer`: somente PNG de navegação final. Preview/interação, impressão e WebP ficam no encoder anterior. Formatos diferentes de RGBA/BGRA, imagens com perfil de cor explícito ou acima de 128 MiB de pixels não ficam em cache; são codificados normalmente. Não modifica CRS, geometrias, rasterização, pixels, alpha ou arquivos-fonte.

SHA-256 aqui é uma identidade criptográfica do conteúdo, não uma comparação literal sem possibilidade matemática de colisão. Todo pixel é incluído, ignorando apenas padding de linhas; não há hash amostral ou truncado. A alternativa de comparar buffers exigiria reter também a imagem não comprimida.

## Recursos, cancelamento e reversão

- Teto retido: menor entre 32 MiB e RAM disponível/32; desativa retenção abaixo de 256 MiB disponíveis. Não há novo piso de alocação. Na medição de LOTES, reteve 11.089.331 bytes.
- Substituição retira a entrada anterior antes de codificar a próxima. Resposta maior que o orçamento continua sendo servida, mas não permanece em cache.
- `ResourceLease<SKData>` mantém o buffer nativo vivo até o fim da escrita HTTP, mesmo após substituição ou encerramento. Leitores em andamento podem prolongar buffers retirados: esse custo transitório e o scratch do encoder não entram no contador retido; P5 continua pendente.
- Cancelamento verificado antes e durante o hash e após o encoder nativo, antes da publicação. Encoder em execução não se torna cooperativamente cancelável.
- Contadores: `EncodedFrameCacheHits`, `EncodedFrameCacheBytes`; `encode` inclui a verificação dos pixels.
- Desativação: `GEONEX_ENCODED_FRAME_CACHE=0`. Sem migração de dados. Preservar alterações anteriores caso reverta hunks; não resetar arquivos inteiros.

## Evidências

DLL real .NET 10 Release/SkiaSharp 3.119.2, LOTES com 57.454 feições, quadro físico 2050×1350, mesmo PNG store de 11.089.331 bytes. A/B no mesmo processo e mesmos pixels:

| Operação | Encode/verificação | Encode + decode |
|---|---:|---:|
| Cache desligado | 26,45–26,55 ms | 40,52–42,61 ms |
| Primeiro quadro, cache vazio | 35,80–38,48 ms | 49,84–54,13 ms |
| Quadro repetido, hit | 9,71–9,80 ms | 23,11–29,65 ms |

**O miss é mais caro**: é necessário calcular o hash além de codificar. O ganho ocorre em repetições, não em qualquer pan/zoom. O volume transmitido e o custo de decode do navegador não foram reduzidos. São amostras locais, não percentis, FPS garantido, benchmark interativo no WebView ou comparação com QGIS.

O benchmark de payload usa pixels da resposta real sem o perfil sRGB acrescentado pelo decoder, correspondendo às superfícies não etiquetadas do renderer. Perfis explícitos foram testados separadamente como bypass, sem reinterpretá-los em produção.

Passaram: `--encoded-frame-cache-contracts` (último pixel alterado, canais/alpha, dimensões, nível, bytes, imagens independentes, cor gerenciada/bypass, RAM, cancelamento, concorrência, leases e shutdown); frame-encoding, online-scheduler, resource-lease e JS. Endpoint real LOTES confirmou hits e pixels idênticos; os contratos preexistentes de impressão/rotação/preview passaram. A rotação pode usar o cache de payload se seus pixels coincidirem, embora fique fora do cache de pintura P1-C. Endpoint com tiles locais lentos verificou ocultação/reexibição, refinamento e preservação da imagem válida em falhas.

Build final: **0 erros / 191 avisos preexistentes** no app; PerfTest 0 erros / 3 avisos gerados pelo GDAL. NU1903/NU1603 preexistentes não foram tratados. A GeoAI Skill `swe-devops-standards` orientou ownership, fallback, teste na DLL real e registro do custo de misses.

## Reprodução e próximos passos

```powershell
dotnet build GeoNex/GeoNex.csproj -c Release -p:GeoNexBuildNative=false --no-restore -v:q
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false --no-restore -v:q
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --encoded-frame-cache-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll C:\Users\Nicho\Downloads\LOTES.geojson
```

Linhas `PAYLOAD` registram a comparação e exigem bytes/pixels idênticos. O importador real pode criar derivados no cache temporário, sem editar o GeoJSON.

Próximo recorte recomendado: protocolo de reuso/delta validado pelo frame apresentado, fallback completo em divergência/retry e testes de câmera/DPI/alpha/stale decode. Isso poderá evitar também transferência e decode; não foi implementado aqui. Medir taxa real de hits/misses antes de ampliar a política de admissão.
