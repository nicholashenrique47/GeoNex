# Retomada — 14/09/2026

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
