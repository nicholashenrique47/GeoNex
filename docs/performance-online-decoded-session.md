# Mapas web: retenção do cache decodificado entre câmeras

2026-09-13. Escopo: Google, Esri e demais mapas online publicados com XML TMS; não altera geometria vetorial, URLs, limites de zoom nem estilo.

## Referência QGIS e diferença encontrada

Fontes primárias consultadas nesta data:

- [QgsTileCache — QGIS 3.44.0](https://raw.githubusercontent.com/qgis/QGIS/final-3_44_0/src/core/qgstilecache.cpp): conserva imagens decodificadas em RAM e consulta cache de rede quando necessário.
- [QgsTileDownloadManager](https://api.qgis.org/api/classQgsTileDownloadManager.html): agrupa pedidos do mesmo tile e permite concluir downloads úteis mesmo quando a câmera muda, em uma thread separada.

O GeoNex já tinha cache GDAL em disco, fila de câmera mais recente e I/O fora do renderizador vetorial. Mas cada câmera abria e fechava seu dataset: os blocos decodificados daquele dataset deixavam de existir. A revisão identificou esse desperdício; não atribui toda a diferença de desempenho apenas a ele.

## Alteração implementada

`OnlineRasterSession` mantém até dois datasets TMS independentes da UI/impressão. Uma leitura por vez garante que nenhum dataset GDAL seja usado concorrentemente. Identidade é o XML completo (inclui URL/configuração); LRU remove fontes menos recentes. Após cinco minutos, uma nova leitura reabre a fonte, limitando a vida dos blocos decodificados. O cache em disco continua seguindo sua política existente.

Os blocos usam o orçamento global adaptativo do GDAL, sem criar um segundo cache de imagens sem limite. O dataset ativo tem uma lease: fechar o servidor não espera HTTP nem descarta o objeto durante RasterIO. Erros invalidam a sessão da fonte; o bitmap publicado anteriormente permanece válido. Cancelamento normal preserva blocos baixados com sucesso. Falha RasterIO é verificada antes do cancelamento, evitando conservar um bloco defeituoso quando os dois coincidem.

`LocalMapServer` agora usa a sessão e expõe `OnlineDatasetOpens`/`OnlineDatasetReuses` para diagnóstico. O leitor estático permanece disponível para leitura isolada e testes de referência. XML temporário é removido após o driver WMS interpretar sua configuração.

GeoAI Skills aplicadas: SWE/DevOps e engenharia de dados; orientaram isolamento dos recursos, limites, teste de falhas, validação pixel a pixel e preservação dos dados. Nenhum código QGIS foi copiado para a implementação.

## Verificação

- Provedor local, cache em disco desativado: **1 HTTP no primeiro carregamento; 0 HTTP adicionais no pan curto e retorno dentro do mesmo tile**. Um dataset aberto, duas reutilizações. Pixels de retorno idênticos e pan idêntico a uma leitura independente. Isso prova reutilização naquele cenário, não velocidade geral ou funcionamento offline de áreas nunca vistas.
- Testes de LRU (máximo dois), expiração, falha HTTP 403 e recuperação, encerramento enquanto servidor permanece bloqueado.
- Contratos anteriores aprovados: níveis reais de tiles/DPI/overzoom, pixels das faixas, caminho RGB direto, leitura reprojetada, orçamento, retry, cancelamento, propriedade de recursos e qualidade preview/final.
- LOTES, motor real + provedor local bloqueado: navegação vetorial independente da rede, refinamento da câmera atual e preservação do último bitmap válido sob HTTP 403/204/500. Nesta execução: três aberturas de datasets e duas reutilizações (falhas forçam reabertura).
- Build Release: zero erros, 191 avisos preexistentes. PerfTest: zero erros, três avisos preexistentes.

Reproduzir: `dotnet build PerfTest/PerfTest.csproj -c Release --no-restore -p:GeoNexBuildNative=false`; `dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-session-contracts`; também `--online-worker-contracts`, `--resource-lease-contracts`, `--online-basemap-contracts` e `--production-map-metrics <GeoNex.dll> <LOTES.shp> --delayed-basemap`.

## O que ainda falta para equivalência

Não foi feito benchmark QGIS versus GeoNex na mesma máquina/provedor/câmera/rede. A implementação ainda publica o mosaico ao concluir a leitura, não cada tile conforme chega. Faltam apresentação progressiva, priorização espacial por tile e deduplicação de pedidos entre câmeras independentes no nível HTTP. A sessão atual é serial: uma operação nativa em rede ainda pode atrasar a próxima câmera online até retornar, embora vetores não esperem por ela. A velocidade da rede e o detalhe nativo do provedor continuam sendo limites externos.

Próxima etapa de arquitetura: gerenciador de tiles visíveis com identidade provedor/z/x/y, fila central com concorrência limitada, tiles prontos publicados progressivamente e reaproveitamento de níveis vizinhos apenas como prévia. Exige testes de cobertura, alpha, seams, câmera obsoleta, falhas parciais e carga do provedor antes de substituir o caminho atual.

Rollback localizado: voltar a chamada de leitura online do servidor a `OnlineRasterFrameReader.Read(...)` e remover o campo/dispose da sessão, preservando as otimizações anteriores. Não reverter todo o worktree.
