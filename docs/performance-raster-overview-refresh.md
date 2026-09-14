# Renovação automática das sessões após gerar pirâmides

## Entrega

Segundo incremento de P1-R, em 13/09/2026. Após publicar uma pirâmide externa GeoTIFF, o GeoNex reabre a fonte e renova sua visão reprojetada quando necessária. As novas resoluções passam a ser reconhecidas sem remover e adicionar a camada. Estilo, posição, câmera e dados originais não são modificados.

Complementa a [publicação isolada](performance-raster-overview-publication.md) do [plano mestre](plano-mestre-motor-gis-qgis.md). Não encerra o baseline P0 nem demonstra equivalência de velocidade com QGIS.

## Coordenação e propriedade dos recursos

1. `CaptureRasterOverviewRefresh` captura nome da camada, identidade da instância carregada e caminho/tamanho/data de modificação da fonte antes de agendar a preparação.
2. `RasterOverviewBuilder.Schedule` aceita `onPublishedAsync` opcional e aguarda seu término antes de liberar o slot. A publicação bem-sucedida não é marcada como falha se o consumidor já não existir ou seu callback falhar.
3. `Home.razor` encaminha a conclusão para o contexto de alterações da UI. Captura o servidor e o pedido, em vez de consultar posteriormente o caminho mutável do diálogo. Rejeita callbacks após descarte da página ou troca do servidor.
4. `PauseForRasterMaintenanceAsync` aguarda o gate de renderização. Diferentemente da pausa usada por operações de substituição anteriores, não cancela a requisição ativa nem avança sua geração. Uma impressão já em execução pode concluir antes da troca; isso não altera a política geral de cancelamento entre outras requisições do servidor.
5. `TryRefreshRasterOverviews` verifica se a instância ainda é a mesma e se caminho, tamanho e data continuam compatíveis. Um nome de camada reutilizado não autoriza substituir a nova instância.
6. A nova fonte é aberta somente para leitura e validada quanto a dimensões, bandas, tipo, geotransformação, CRS e existência de overviews em todas as bandas. Uma guarda de arquivo bloqueia escrita/substituição durante a validação/publicação no Windows.
7. Quando o CRS atual do projeto difere do CRS da fonte, é preparado um VRT virtual sem nome, com a política de reamostragem existente. Não há reprojeção integral para arquivo nem novo temporário persistente em `/vsimem`.
8. Só após preparar os recursos são substituídos fonte/VRT e invalidados cache raster e cache global. As leases anteriores conservam recursos enquanto houver leitores; novos frames adquirem a nova geração depois da barreira.

O método de troca é síncrono e exige contexto de mutação da UI e barreira de manutenção. A preparação envolve abertura/metadados e VRT, não leitura integral de pixels. Ainda pode levar tempo perceptível em armazenamento lento; não é uma promessa de latência constante.

## Falhas e observabilidade

Camada removida/substituída, mapa descartado, arquivo alterado, ausência de overviews ou falha de preparação impedem a renovação. A sessão anterior é conservada quando a preparação falha. O `.ovr` já publicado permanece válido e pode ser usado em uma abertura futura.

Log de renovação: `Raster overview refresh published=... elapsed_ms=...`. Erros incluem tipo/mensagem, sem registrar pixels ou atributos. A comparação tamanho/data é uma guarda de mudança comum, não um checksum criptográfico capaz de detectar toda adulteração externa.

A sincronização é local ao aplicativo. Não foram adicionados suporte a máscaras/sidecars múltiplos, reserva de espaço em disco, reconstrução de overviews existentes ou monitoramento contínuo de alterações externas.

## Validação executada

`ProductionRasterRefreshContracts` carrega a DLL Release real do aplicativo, em vez de testar uma cópia simplificada do serviço. Fixture: GeoTIFF Float32 1024×1024, EPSG:4326, com duas resoluções reduzidas.

- Pipeline real: builder → callback assíncrono → barreira do servidor → renovação.
- Um frame mantido no gate impede a troca até ser liberado; a manutenção não cancela o token ativo nem altera a geração de requisição.
- A sessão antiga começa sem overviews; a nova reconhece dois níveis sem recarregar a camada.
- CRS idêntico não cria VRT. Mudança do projeto durante a preparação usa o CRS atual, incluindo teste 4326→3395.
- Comparação pixel a pixel com fonte e warp GDAL independentes; amostra de saída 128² e leitura nativa 1024². Hash do TIFF original preservado.
- Leases antigas de fonte, VRT e imagem de cache continuam legíveis após substituição; cache raster antigo deixa de ser oferecido a novos consumidores.
- Revisão global avança e é solicitada uma atualização do mapa.
- Repetição do callback, remoção, substituição pelo mesmo nome, mudança do arquivo, CRS inválido e descarte do mapa são tratados sem ressuscitar a camada ou publicar recursos incorretos.
- Regressões aprovadas: overviews, políticas raster, leases, sessões/worker online e contratos JavaScript do mapa.

Build Release: zero erros, 191 avisos preexistentes. PerfTest: zero erros, três avisos do código gerado pelo pacote GDAL. As mensagens de CRS inválido e HTTP 403 nos testes são falhas deliberadas para validar recuperação. Alertas preexistentes de dependências permanecem fora deste incremento.

Não houve teste visual interativo na WebView, benchmark com ortofoto grande ou exportação real de layout nesta etapa. A proteção da impressão foi validada pelo contrato do gate/token, não por um trabalho de impressão end-to-end.

## Reprodução

PowerShell na raiz do repositório, com binários nativos Release existentes:

```powershell
dotnet build GeoNex/GeoNex.csproj -c Release -p:GeoNexBuildNative=false --no-restore
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false --no-restore
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-raster-refresh-contracts GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --raster-overview-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --raster-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --resource-lease-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-session-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-worker-contracts
node PerfTest/MapEngineContracts.js
```

Os testes usam somente fixtures temporárias próprias; nenhum LOTES/ortofoto do usuário é alterado. Para desativar o fluxo automático, `GEONEX_BUILD_OVERVIEWS=0` antes de iniciar o aplicativo mantém a leitura normal e os overviews existentes. Não é necessário apagar dados ou caches.

## Referências e continuidade

GeoAI Skills SWE/DevOps e engenharia de dados orientaram a identidade dos recursos, leases, rejeição de mudanças obsoletas, preservação da fonte e comparação de pixels. Foram verificadas a [concorrência GDAL](https://gdal.org/en/stable/user/multithreading.html) e a [representação VRT](https://gdal.org/en/stable/drivers/raster/vrt.html), em 13/09/2026; o código foi testado com GDAL 3.12.1.

Próximas frentes: baseline interativo com raster grande, guardas de disco/máscaras ainda pendentes de P1-R e cache de imagens por camada (P1-C), para evitar repintar vetores inalterados quando o mapa base atualiza.
