# Primeiro incremento do plano: manutenção raster isolada

## Escopo e resultado

Implementado em 13/09/2026, iniciando P0/P1-R do [plano mestre](plano-mestre-motor-gis-qgis.md). A preparação automática de pirâmides GeoTIFF deixa de segurar o bloqueio global durante `BuildOverviews`. O arquivo final só aparece depois de concluído e validado. Não houve mudança no estilo, nas geometrias vetoriais, nas URLs de mapas base ou no C++.

Esta entrega não encerra P0/P1-R nem demonstra equivalência de desempenho com QGIS. O principal resultado verificável é a eliminação da dependência entre o trabalho longo de construção e o lock de leitura, conservando a exclusão mútua na publicação curta.

## Implementação

- `RasterOverviewBuilder`: um construtor ativo por processo; máximo de 32 trabalhos admitidos, incluindo o ativo; duplicatas por caminho rejeitadas. Cancelamento e descarte de CTS compartilham sincronização.
- O builder abre o GeoTIFF com handle próprio read-only e guarda de arquivo que, no Windows, impede escrita/substituição da fonte durante o trabalho. Não copia os pixels originais nem altera o TIFF.
- Um VRT temporário de identidade é criado em diretório exclusivo ao lado da fonte. `BuildOverviews` escreve o `.vrt.ovr` isolado, sem adquirir `GdalRasterLock`. O VRT não representa uma reprojeção.
- Todos os escritores são fechados; número de bandas, níveis, dimensões e tipos são validados. O `.ovr` completo é publicado por rename no mesmo volume, sem sobrescrever destino existente. O lock protege somente essa publicação.
- Cancelamento antes da publicação elimina temporários conhecidos; não apaga nem substitui um `.ovr` preexistente. Limpeza não recorre sobre o diretório da fonte. Falhas deixam a leitura do original disponível.
- `RasterReadLock`: espera cancelável em intervalos de 25 ms, conservando afinidade de thread e exclusão mútua. Usado nos caminhos de leitura/VRT e warp local de `LocalMapServer`; não usar `await` dentro desse escopo.
- `RenderTelemetry`: `gdal_wait` separado de `io`, também no log e em `Server-Timing`. O I/O anterior incluía espera no lock; comparar séries antigas exige considerar essa mudança de definição.
- `RasterOverviewBuilder.Metrics`: último trabalho ativo/concluído, sem histórico ilimitado ou caminhos retidos; etapa, fila, construção, espera/publicação e bytes. A duração `PublicationWaitMilliseconds` inclui o rename curto; tempos de fases interrompidas podem não estar completos.

## Guardas e limitações deliberadas

A preparação automática desta primeira versão aceita somente driver `GTiff`, sem overviews existentes, sem máscara por dataset e sem `.msk`/`.aux.xml` adjacente. Paleta e NoData sem essas dependências são cobertos pelos testes. Demais formatos continuam sendo lidos normalmente; somente sua preparação automática foi suspensa até existir contrato de publicação validado. Não há remoção de overviews existentes.

O diretório da fonte precisa permitir escrita e dispor de espaço para o derivado. A admissão já limita quantidade de trabalhos, mas ainda não reserva espaço em disco nem implementa o governador global de memória/I/O. Falta também tratar máscaras/auxiliares como publicação multifile e recuperar temporários de uma interrupção abrupta do processo.

No primeiro incremento, handles GDAL/VRT já abertos podiam conservar a visão antiga de overviews, e o callback apenas solicitava redraw. Essa limitação foi tratada no [segundo incremento: renovação coordenada](performance-raster-overview-refresh.md), com barreira de renderização, identidade da camada e renovação de fonte/VRT. As demais limitações de drivers, máscaras e disco descritas acima continuam aplicáveis.

## Verificação executada

Contratos com GDAL 3.12.1 real:

- GeoTIFF Float32 1024², paleta/NoData Byte 1025×1027 e três bandas Float32/NoData 1025×1027.
- Hash SHA-256 dos originais preservado; todos os pixels de todos os níveis comparados exatamente com overviews criados diretamente pelo GDAL em cópias de referência. Comparação de dimensões, tipo, NoData, paleta e CRS.
- Lock de renderização mantido ocupado durante a construção: trabalho chega à fase de publicação mesmo assim, e o nome final ainda não existe. Leitura de outro raster permanece possível pelo detentor do lock.
- Cancelamento enquanto aguarda publicação termina sem liberação do lock externo, sem `.ovr` parcial e sem redraw indevido.
- Outro publicador cria o destino antes do rename: arquivo preservado integralmente, sem sobrescrita.
- Fila limitada, deduplicação, cancelamento dos trabalhos pendentes, chamadas concorrentes de cancelamento após aposentadoria e flag de desativação.
- Overviews existentes e fontes auxiliares são preservados/ignorados pela preparação; temporários dos casos normais são limpos.
- Espera cancelável de leitura produz spans `gdal_wait`; logs expõem a fase.

Regressões aprovadas: raster, telemetria, sessão online e worker online, incluindo níveis/DPI/overzoom e igualdade de pixels das faixas. Erros HTTP 403 emitidos pelo ensaio online são falhas deliberadas do servidor de teste, com recuperação validada.

Build Release do aplicativo: zero erros, 191 avisos preexistentes. Build de PerfTest: zero erros, três avisos do código gerado pelo pacote GDAL. Os avisos de dependências NU1603/NU1903 permanecem e precisam de trabalho separado; esta entrega não declara prontidão de release de segurança.

Não houve benchmark interativo na WebView com ortofoto grande, nem comparação com QGIS nesta etapa. Os tempos dos fixtures pequenos não são uma estimativa de FPS em dados reais.

## Reprodução e operação

Na raiz do repositório, PowerShell:

```powershell
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false --no-restore
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --raster-overview-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --raster-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --telemetry-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-session-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-worker-contracts
dotnet build GeoNex/GeoNex.csproj -c Release -p:GeoNexBuildNative=false --no-restore
```

Esses comandos reutilizam os binários nativos Release existentes; a ABI não foi modificada. As entradas dos contratos são sintéticas em diretório temporário exclusivo; nenhum arquivo de LOTES ou ortofoto do usuário foi alterado.

Para observar frames reais, definir `GEONEX_RENDER_METRICS=1` antes de iniciar o aplicativo. Para suspender a preparação automática, definir `GEONEX_BUILD_OVERVIEWS=0` antes de iniciá-lo; isso mantém leitura e pirâmides existentes. `GEONEX_OVERVIEW_DELAY_MS` e `GEONEX_OVERVIEW_RESAMPLING` conservam suas funções anteriores. Não é necessário apagar caches para rollback operacional.

## Referências e decisões

As GeoAI Skills SWE/DevOps e engenharia de dados orientaram isolamento de recursos, publicação sem perda, contratos de pixels e limites explícitos. Não foi aplicada limpeza/reparação dos dados, por estar fora do escopo.

Documentação verificada em 13/09/2026: [GDAL VRT](https://gdal.org/en/stable/drivers/raster/vrt.html), [gdaladdo](https://gdal.org/en/stable/programs/gdaladdo.html) e [concorrência GDAL](https://gdal.org/en/stable/user/multithreading.html). O binding C# distribuído expõe tradução por `wrapper_GDALTranslate`; a implementação foi compilada e testada contra esse binding, sem atualização de pacotes.

Renovação segura das sessões: [implementada no incremento seguinte](performance-raster-overview-refresh.md). Permanecem o baseline raster real e, depois, o cache de imagens por camada (P1-C), reduzindo repintura vetorial quando a base online atualiza.
