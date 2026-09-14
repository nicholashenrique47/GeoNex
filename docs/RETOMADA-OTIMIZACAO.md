# Retomada — 14/09/2026

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
