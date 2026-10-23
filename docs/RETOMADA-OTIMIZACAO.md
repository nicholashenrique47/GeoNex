# Retomada — 14/09/2026

## Incremento atual — índice espacial para simbologia categorizada

- Compilação de categorias agora mantém um índice `STRtree` por camada para polígonos e linhas, indexando os chunks já agrupados por categoria e preservando a ordem original de desenho.
- O servidor consulta somente os chunks que intersectam o viewport antes de rasterizar; o caminho de estilo sem categorias não foi alterado.
- Preenchimentos categorizados não pequenos agora são agrupados por categoria em um único `SKPath` por trecho visível do frame, reduzindo chamadas `DrawPath`; bordas continuam sendo desenhadas por chunk para preservar o LOD durante interação.
- Adicionado teste isolado em `tests/VectorRenderIndex`, cobrindo reutilização do buffer, ordem de desenho e equivalência com filtragem linear em uma amostra de 10.000 chunks.
- Validação concluída: teste do índice passou com 26 asserções; `GeoNex.csproj` Release compilou com 0 erros (avisos RZ preexistentes); análise do `LocalMapServer` não encontrou erros; `git diff --check` passou (somente avisos de normalização LF/CRLF do Git).

## Pesquisa GDAL após compilação

- Pedido atual: pesquisar repositório GDAL, sem implementar. Registrado em `docs/pesquisa-gdal-3.12.1.md`, com links para tag compatível com o projeto.
- Próxima investigação: comparar RasterIO em faixas 128/256/512 e janela completa; batching WMS pode ser fragmentado pelas faixas atuais. É hipótese, não causa comprovada. Medir cache frio/quente, nitidez, pixels, memória e cancelamento antes de mudar padrão.
- Restava 1% semanal na consulta (99% usado), 12% da janela curta. Nenhum build/teste em execução; motor não modificado nesta pesquisa.

## Correção atual — latência até tiles nítidos

- Usuário relatou refinamento mais rápido antes do novo transporte. Fetch/Blob introduz espera pelo corpo completo antes de iniciar decode; causalidade real ainda não medida.
- Restaurado Image direto por padrão, preservando resolução física e caches. Reuso cliente agora opt-in (`window.GEONEX_FRAME_REUSE=true`); false/reload restaura padrão. Não altera downloads/zoom dos provedores nem geometrias.
- CONCLUÍDO: contratos JS passaram (padrão/false sem fetch ou Blob, quadro refinado, câmera/dimensões e regressões do modo experimental); build Release 0 erros/3 avisos preexistentes; diff --check passou. Nenhum processo pendente. Próximo passo: validar no WebView tempo até nitidez, antes de reativar o experimento.
- Última consulta: 76% usado na janela curta, 97% semanal (3% restante). Concluir apenas esta correção e registrar resultados.

## Incremento atual — reuso cliente/servidor

- Implementados ID opaco de payload (ligado ao SHA-256 integral existente), resposta 204 explícita sem PNG quando `reuse` coincide, e fallback 200 completo quando não coincide. IDs não servem para buscar outros quadros.
- WebView JS retém uma imagem apresentada (até 16 MiB de pixels), reutiliza sem decode, preserva commit de câmera e A/B; aborta fetch obsoleto, limpa ao reset e recupera quadro completo se perder a base. Impressão/preview mantêm caminho Image anterior.
- CONCLUÍDO: contratos de cache/IDs e JS (incluindo fallback CORS e teto de imagem); DLL real LOTES com 204 vazio/200 fallback, print/rotação/preview e regressão online com falhas passaram. Build final 0 erros (avisos preexistentes; rebuild anterior 191, incremental final 3). Nenhum build/teste pendente. Não foi testado manualmente no WebView.
- Documentado em `docs/performance-frame-reuse-protocol.md`; plano mestre atualizado. Próximo passo: medir uso real no WebView antes de implementar deltas espaciais. Reversão: `window.GEONEX_FRAME_REUSE=false` ou `GEONEX_ENCODED_FRAME_CACHE=0` no processo.
- Uso consultado: 58% janela curta, 94% semanal (6% restante). Encerrar após validar/documentar; não iniciar outra fase.

## Retomada atual (uso renovado)

- Janela curta reiniciada (0% usado); semanal 85% usado na consulta inicial. Continuar verificando antes de expansão.
- Testados filtros None/Up/Sub, níveis 0/1 no LOTES. Nenhum comprimido superou o caminho store em encode+decode; o experimento de compressão continua opt-in.
- Próxima implementação em andamento: cache limitado de payload PNG codificado para quadros pixel-idênticos. SHA-256 de todos os pixels + formato/dimensões/alpha/nível; leases para descarte seguro; fallback sem retenção sob pressão. Sem alterar protocolo WebView/qualidade.
- CONCLUÍDO: `EncodedFrameCache.cs`, integração na navegação final e contratos. LOTES no mesmo processo: encode direto ~26,5 ms; primeiro cache ~35,8–38,5 ms; hits ~9,7–9,8 ms; bytes/pixels idênticos. Custo de miss documentado. Desativação: `GEONEX_ENCODED_FRAME_CACHE=0`; impressão/preview/WebP não usam esse cache.
- Build final 0 erros/191 avisos preexistentes; PerfTest 0/3. Contratos de cache/PNG, scheduler, leases e JS passaram; DLL real passou LOTES, print/rotação/preview, tiles lentos, ocultação/reexibição e falhas. Nenhum build/teste em andamento. Detalhes: `docs/performance-encoded-frame-cache.md`.
- Próximo passo: medir taxa de hits/misses no WebView; projetar reuso/delta com identificação do frame base e fallback completo. O cache atual ainda envia PNG completo e não poupa decode do navegador. Não reativar compressão experimental por padrão.
- Última leitura de uso nesta retomada: 27% usado na janela curta, 89% semanal (11% restante). Pausa segura após concluir este incremento; nenhum resgate/compra de créditos.

## Histórico do ponto anterior

- Uso no início desta tarefa: 15% disponível na janela de 5h, 17% semanal. Sem resgate/compra de créditos.
- Concluído anteriormente: P1-C (imagem de polígonos) e primeiro P2-W (ciclo de vida/retries online); builds e contratos passaram. Ver plano mestre e documentos correspondentes.
- Tarefa atual: recorte de transporte P3-W — melhorar a seleção da compressão PNG sem perdas. O seletor atual trata variação horizontal como incompressibilidade e pode enviar ~11 MB mesmo para imagem compressível.
- Resultado: seletor PNG por amostra Sub/ZLib de 16 KiB implementado, **experimental e desligado por padrão** (`GEONEX_PNG_COMPRESSION_PROBE=1` para testar). LOTES reduziu 11.089.331 → 2.117.172 bytes (~81%), mas encode subiu para 81–94 ms e HTTP+decode para ~134–137 ms nos hits. Não ativar por padrão: piora de latência nesta medição, apesar de preservar pixels. Próximo passo é transporte incremental/reuso de payload, não aumentar compressão indiscriminadamente.
- Validação final CONCLUÍDA: guarda opt-in e contratos PNG passaram; DLL real com opção desligada manteve PNG de 11.089.331 bytes, encode ~26–38 ms e HTTP+decode ~95–97 ms nos hits. Produção LOTES, bypass print/rotação/preview passaram. Antes da guarda, também passaram tiles lentos/falhas/ocultação e JS. Build final: 0 erros/191 avisos preexistentes; PerfTest 0 erros/3 avisos. `git diff --check` passou. Nenhum processo de build/teste pendente.
- Última leitura de uso: 8% restante na janela curta e 16% semanal. Encerrar este recorte após validação final; não iniciar outro para evitar esgotar sem registro.
- Arquivos principais: `GeoNex/Services/MapFrameEncoding.cs`, `PerfTest/FrameEncodingContracts.cs`, `PerfTest/ProductionMapMetrics.cs`.
- Build: `dotnet build GeoNex/GeoNex.csproj -c Release -p:GeoNexBuildNative=false --no-restore -v:q`.
- Teste: `dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --frame-encoding-contracts`.
- Preservar worktree sujo e mudanças anteriores. Não usar reset/reverter arquivos inteiros. Atualizar este MD após testes ou antes de interromper.

## Validação com dados reais — lotes e ortofotos

- `PerfTest/ProductionVectorSmoke.cs` foi ajustado para construir `MapLocalCoordinate` via reflexão; o harness não deve passar `SKPoint` para a API de cache preciso.
- `LOTES.shp`: 77.146 feições lidas em 41,55 ms; metadados reprojetados em 101,64 ms; precisão em alto zoom com erro máximo de `5,7556E-07 m` (contra `0,12425 m` no caminho anterior); cache preciso e equivalência de pixels em três zooms passaram.
- `GUARATUBA_ESTRADAS_ORTOFOTO_2022.tif`: abertura em 282,689 ms, GeoTIFF `238248x101815`, 4 bandas, blocos `256x256`, 10 overviews. No zoom 1, após aquecimento, I/O ficou em aproximadamente 33–36 ms e renderização em 41–44 ms.
- `Compress2.ecw`: abertura em 238,499 ms, driver ECW `100286x180795`, 3 bandas, blocos `256x256`, 9 overviews. No zoom 1, após aquecimento, I/O ficou em aproximadamente 3–4 ms e renderização em 9–10 ms.
- O teste do TIF em zoom 8 atingiu uma janela sem pixels no foco central e falhou pelo guard do próprio benchmark (`Empty raster focus`); não indica corrupção do raster. O próximo benchmark deve calibrar foco/extent por zoom antes de comparar p50/p95.
- Corrigido o batching categorizado para não adiar preenchimentos quando a camada possui borda: isso preserva a ordem visual de polígonos sobrepostos; o agrupamento continua ativo para camadas preenchidas sem borda. Build Release do GeoNex e teste do índice categorizado (26 asserções) passaram.
- No overlay de edição vetorial, buscas repetidas de vértices selecionados foram trocadas por `HashSet<VertexAddress>` durante a montagem dos índices e do payload cliente. O custo deixa de crescer quadraticamente quando uma feição possui muitos vértices selecionados; análise do arquivo não encontrou erros e o build Release passou com 0 erros.
- O hover de vértices também deixou de calcular `sqrt/pow` a cada movimento: a tolerância de arraste agora é comparada diretamente em distância ao quadrado. O lote real continuou aprovado: 77.146 feições, reprojeção em 99,87 ms, precisão/cache/pixels aprovados. Foi testada faixa RasterIO de 256 linhas, mas o TIF apresentou I/O de aproximadamente 38–47 ms contra 33–36 ms anteriormente; a mudança foi revertida e 128 linhas foi preservado.
- O snap de edição passou a reutilizar um `HashSet<long>` para os FIDs selecionados, em vez de criar `SelectedVectorFeatureStates`, LINQ e outro conjunto a cada movimento do mouse. O build Release passou sem erros, a análise do arquivo não encontrou problemas e o smoke test real de `LOTES.shp` manteve leitura, reprojeção, precisão, cache e pixels aprovados.
mas - A tolerância de arraste no hover de vértices agora usa distância ao quadrado, eliminando `Math.Sqrt/Math.Pow` por evento. O teste do índice categorizado continuou com 26 asserções aprovadas.
- A faixa RasterIO de 256 linhas foi reavaliada no ECW e apresentou aquecimento estável em aproximadamente 36–45 ms no HTTP completo, sem alteração de pixels; como o TIF ficou pior no teste anterior, a política conservadora de 128 linhas permanece. O ECW real abriu com 9 overviews e todas as sete amostras renderizaram pixels não vazios.

## Benchmark integrado final — vetor, pintura e frame

- O harness `ProductionMapMetrics` foi corrigido para as APIs atuais: seleção explícita da sobrecarga de compilação, passagem dos seis argumentos de carregamento e construção reflexiva de `MapLocalCoordinate`. O benchmark completo agora termina sem exceção.
- Em `LOTES.shp` (77.146 feições, escala 4, viewport real), a conversão levou `7,57 ms`, o carregamento total `176,31 ms`, a reprojeção `98,84 ms`, a leitura `148 ms`, a publicação do índice `25 ms` e o índice nativo ocupou `3,0 MB` (`1.218` células, `83.370` entradas, `0` oversized).
- Primeiro frame frio: geometria `13,32 ms`, pintura/draw `21,60 ms`, render `56,62 ms`, encode PNG `28,81 ms`, escrita `2,61 ms`, resposta HTTP `110,04 ms`, PNG de `470.580 bytes`. Isso separa o custo inicial de preparação do custo de frames repetidos.
- Frames aquecidos: geometria foi reutilizada (`0 ms`), draw ficou entre `0,01–4,46 ms`, encode entre `1,91–13,11 ms` e respostas entre `2,98–21,60 ms`; não houve alteração de pixels e as alocações caíram para `0 bytes` nos frames reutilizados.
- A repetição com pintura experimental/bandas não demonstrou ganho adicional mensurável no cenário real, portanto nenhum modo experimental foi ativado por padrão. O gargalo restante observado é principalmente encode PNG e transporte no primeiro frame, não o índice ou o culling vetorial.
- Build Release do `PerfTest` passou com `0` erros. Os números são baseline de uma execução; superioridade global sobre QGIS/ArcGIS ainda exige comparação controlada no mesmo viewport, resolução, hardware e percentis.

## Otimização raster — compressão adaptativa validada

- O benchmark real mostrou que o modo PNG sem DEFLATE enviava aproximadamente `34,4 MB` por frame de ortofoto. Foi adicionada uma sondagem limitada para raster que escolhe compressão/paleta exata quando o conteúdo se beneficia; nenhum pixel é alterado.
- No TIF real, o frame caiu para aproximadamente `307 KB`, com encode aquecido de `28–31 ms` e resposta de `143–161 ms`, contra aproximadamente `34,4 MB`, `52–67 ms` e `205–269 ms` no modo anterior.
- No ECW real, o frame caiu para aproximadamente `1,96–2,08 MB`, com respostas aquecidas de `86–96 ms`, contra `34,4 MB` e `122–142 ms`; a leitura GDAL aquecida permaneceu em aproximadamente `11–13 ms`.
- A política adaptativa foi medida, mas não ficou ativa por padrão: em máquinas reais ela pode aumentar o trabalho de CPU e atrasar a sensação de carregamento apesar de reduzir bytes. O padrão voltou para `png-store` em raster grande; `GEONEX_RASTER_PNG_COMPRESSION_PROBE=1` habilita a alternativa apenas para teste controlado.
- A captura visual de uso real mostrou refinamento lento/pixelado durante a chegada do frame. A decisão foi priorizar latência percebida e resposta imediata, sem alterar pixels, resolução ou qualidade da fonte.
- Após recompilar o harness, o padrão restaurado foi confirmado no ECW: `34.385.414 bytes` por frame e encode de aproximadamente `49–54 ms`, sem a sondagem adaptativa. Para aplicar no aplicativo já aberto, é necessário reiniciar o processo do GeoNex/WebView.
- O cache de identidade foi experimentado também durante frames raster interativos. Como as amostras de pan/zoom produziram pixels diferentes e não houve hits, a ativação foi removida para evitar o custo de hash sem benefício mensurável.
- Regressão final do vetor real após a mudança raster: `LOTES.shp` continuou com `0 ms` de geometria nos frames aquecidos, draw de `0,01–6,02 ms`, encode de `2,03–13,94 ms` e respostas de `3,56–24,95 ms`; as variações de carregamento ocorreram sob menor memória disponível e não alteraram o caminho de renderização.
