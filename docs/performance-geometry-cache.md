# Geometria final e cancelamento — 11/09/2026

Continuação sobre `0663bc9`, incorporado por fast-forward sem conflitos.

## Implementação

- O cache único por SHP foi substituído por um cache compartilhado, com entradas independentes para prévia e qualidade final. Alternar a qualidade não substitui mais a geometria final pela simplificada.
- Identidade: camada (identificador de instância), qualidade, compactação, escala exata e cobertura do viewport. Cor e desenho continuam no renderer; nenhuma geometria é deduplicada nesta etapa. Invalidar ou remover uma camada libera ambas as entradas.
- Orçamento de retenção compartilhado: 128 MiB estimados e no máximo 64 entradas. Contabilidade conservadora: 512 bytes por path + 16 por ponto + 8 por verbo. Não é limite de RSS: snapshots em uso e memória temporária do Skia ficam fora dele.
- Evicção prioriza prévias e depois a entrada menos recentemente usada. Uma prévia não expulsa geometria final para ocupar o cache. Paths maiores que o orçamento continuam sendo renderizados, sem retenção; isso pode aumentar recomputações em casos extremos comparado ao cache anterior, ilimitado.
- Cópias copy-on-write permitem concluir leitores durante invalidação/evicção. Locks protegem contabilidade e vida útil dos paths.
- Cancelamento verificado entre camadas, antes de publicar geometria, antes de desenhar, entre preenchimento/contorno, em fragmentos categorizados e antes da codificação. Não interrompe uma chamada Skia/GDAL que já começou.

## Validação e reprodução

```powershell
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false --no-restore
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --render-path-cache-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --large-shp-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --render-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --coordinate-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --resource-lease-contracts
node PerfTest/MapEngineContracts.js
dotnet run --project tests/PrintScale/PrintScale.csproj -c Release --no-restore
```

O parâmetro `GeoNexBuildNative=false` usa os binários nativos existentes; esta etapa não altera C++. Remova-o para recompilar o nativo com o toolchain instalado.

Contratos cobrem qualidade/compactação, escala, cobertura, igualdade de pixels com alpha/contorno/winding, copy-on-write, invalidação, evicção, orçamento e acesso concorrente. Na sequência sintética de 100 consultas alternadas, há apenas duas construções de geometria. Isso é um teste de reutilização, não um benchmark de FPS.

Executado nesta máquina: todos os comandos de contratos acima passaram, incluindo 60 verificações de impressão. Build MAUI Windows Debug passou com 0 erros e 191 avisos; PerfTest Release com 0 erros e 3 avisos do código gerado GDAL. O build continua reportando avisos de vulnerabilidades nas dependências Newtonsoft.Json 9.0.1 e SQLitePCLRaw.lib.e_sqlite3 2.1.11; não houve atualização de dependências nesta etapa. Essa pendência deve ser tratada antes de uma publicação de produção.

Não foram medidos LOTES nem a interface WebView nesta máquina. O custo do primeiro frame final, documentado na etapa anterior, não está resolvido por este cache. Próximo passo: medir separadamente geometria, preenchimento e contorno em dados reais antes de alterar o desenho final. Dividir paths ou deduplicar o frame final sem comparação de pixels não é seguro.

Rollback: reverter apenas as alterações desta etapa em `MapRenderingService.cs`, `LocalMapServer.cs`, os vínculos/testes de PerfTest e os novos arquivos de cache/documentação. Não reverter o trabalho incorporado do outro PC.
