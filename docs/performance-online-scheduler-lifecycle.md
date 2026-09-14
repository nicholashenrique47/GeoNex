# P2-W — ciclo de vida e tentativas do agendador online

Data: 14/09/2026. Primeiro recorte de P2-W do [plano mestre](plano-mestre-motor-gis-qgis.md). Não implementa o scheduler HTTP completo nem declara paridade com QGIS/ArcGIS.

## Prioridade e diagnóstico

Após reduzir a repintura vetorial em P1-C, foi priorizado trabalho online desnecessário que disputa a única fila de leitura:

- Uma camada oculta/removida podia permanecer ativa, pendente ou aguardando retry.
- O timer compartilhado era reagendado para três segundos a cada nova falha, adiando fontes que já aguardavam recuperação.
- Ao atingir 16 falhas, o código apagava todos os cooldowns. Quando a fila estava cheia, uma tentativa vencida podia ser removida sem execução.

## Alterações

- `LatestRenderWorker.Request` aceita um predicado opcional de relevância. Ele é reavaliado na admissão, retirada da fila, publicação e retry; os consumidores existentes sem predicado mantêm a semântica anterior.
- `DiscardIrrelevant` cancela cooperativamente o ativo e remove pendências/retries que deixaram de ser relevantes. Uma requisição atrasada de um frame antigo também é rejeitada se sua fonte estiver oculta.
- `LocalMapServer` verifica presença na hierarquia visível, XML da fonte, CRS e offsets. O publisher existente continua verificando identidade do dataset antes de transferir a imagem.
- `Home.SincronizarHierarquia` publica uma lista completa por atribuição, em vez de limpar/preencher a mesma lista observada pelo worker. Depois solicita o descarte de trabalho obsoleto. O loop do renderer conserva a referência da lista de seu frame, evitando misturar contagem de uma hierarquia com índices de outra.
- Timer respeita o primeiro prazo pendente e não adia um alarme já armado quando outra fonte falha. Tentativa vencida sem vaga permanece registrada; rechecagem com piso de 100 ms evita loop ocupado.
- Overflow descarta somente a falha mais antiga, preservando os outros cooldowns. Continuam: até 16 pendências, 16 falhas, um worker e uma tentativa automática após o erro inicial de cada trabalho.
- Falhas de trabalhos já cancelados/irrelevantes não geram novos retries nem incrementam o relatório de erros online. Resultados obsoletos são descartados uma vez.
- Diagnóstico: `FailureCount`, `HasWork` no worker e `HasOnlineWork` no servidor; métricas existentes de falha/tempo permanecem.

Os predicados são leituras rápidas, thread-safe e não lançadoras; não devem fazer I/O nem mutações. Não há novas threads de download, novos formatos, dependências ou alocações de imagens adicionais.

## Limites e qualidade

Cancelar não interrompe instantaneamente um `RasterIO` nativo já bloqueado. O token impede continuação/publicação nos pontos cooperativos; a chamada nativa atual ainda pode aguardar seu timeout. A fila continua com um leitor, portanto uma chamada não interrompível ainda pode atrasar outra fonte.

Imagem válida anteriormente publicada permanece disponível. Níveis de zoom, pixels, CRS, geometria, impressão e algoritmo de reamostragem não foram alterados. Voltar a exibir a camada permite novas solicitações normalmente.

Não foi implementado suporte a `Retry-After`, classificação HTTP 401/403/429, ETag/304, regras `no-store`/`Vary`, novo cache em disco ou prioridades por tile. O GDAL continua responsável pelo HTTP; não se inferem headers/status confiáveis a partir do texto de seus erros. Esses itens de P2-W permanecem pendentes.

## Testes executados

- Novo `--online-scheduler-contracts`: ocultar ativo/pendente/retry; rejeitar pedido atrasado; descarte único; trabalho visível continua; primeira recuperação não adiada por segunda falha; overflow preserva 16 registros.
- DLL real com LOTES e `--delayed-basemap`: tiles retidos no servidor HTTP local; ocultação durante I/O; nenhum bitmap/ready obsoleto publicado; reexibição e refinamento funcionam. Falhas simuladas 403/204/500 preservam o cache válido.
- Regressões: online-worker, online-session, polygon-image-cache, resource-lease, render-precision e `MapEngineContracts.js` passaram.
- Contrato visual de P1-C mantido no ensaio online: diferença RGB máxima 5/255 e RMS 0,2843/255 contra composição antiga; alpha idêntico. Nenhuma nova tolerância visual foi introduzida nesta fase.
- Release do aplicativo: **0 erros, 191 avisos preexistentes**. PerfTest: 0 erros, 3 avisos GDAL gerados. Dependências com avisos NU1903/NU1603 continuam pendentes.

Não houve benchmark contra provedores públicos nem teste manual de navegação no WebView. O ganho comprovado é eliminar trabalho obsoleto e corrigir a recuperação; não foi medido aumento geral de FPS. A GeoAI Skill `swe-devops-standards` orientou o recorte, ownership/cancelamento e validação no ambiente real.

## Reprodução e reversão

```powershell
dotnet build GeoNex/GeoNex.csproj -c Release -p:GeoNexBuildNative=false --no-restore -v:q
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false --no-restore -v:q
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-scheduler-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll C:\Users\Nicho\Downloads\LOTES.geojson --delayed-basemap
```

O teste de produção pode criar SHP derivado no cache temporário pelo importador existente; não modifica o GeoJSON original. Reversão por restauração do binário anterior, ou reversão **somente destes hunks** do worker, servidor e hierarquia, seguida de build. Não usar reset de arquivos inteiros: existem alterações anteriores no mesmo worktree. Não há migração de dados/cache para desfazer.
