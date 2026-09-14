# Plano mestre de desempenho e qualidade do motor GIS

## 1. Objetivo e conclusão executiva

Evoluir o GeoNex para navegação fluida em rasters grandes, mapas web e vetores extensos, preservando precisão geométrica, atributos, legibilidade e qualidade final. QGIS é a referência técnica verificável; equivalência de desempenho é uma meta a medir, não um resultado já demonstrado.

A recomendação central é reduzir trabalho, antes de multiplicar threads: consultar somente dados relevantes, aproveitar representações multirresolução, conservar resultados úteis e invalidar apenas o necessário. Uma melhoria de download não resolve uma composição que redesenha todos os polígonos; um índice rápido não elimina o custo de pintar um milhão de vértices visíveis.

As prioridades são:

1. Instrumentar o percurso completo até a imagem apresentada, distinguindo rede, disco, bloqueios, geometria, pintura e WebView.
2. Separar manutenção de rasters da navegação; a construção atual de pirâmides compartilha o bloqueio das leituras.
3. Introduzir atualização independente por camada e tiles progressivos, sem apagar cobertura válida nem redesenhar vetores estáticos.
4. Remover a conversão obrigatória de outros formatos vetoriais para SHP; consultar providers e índices com contratos de fidelidade.
5. Evoluir caches geométricos e precisão para reaproveitar dados durante pan/zoom, inclusive em escalas muito altas.
6. Coordenar memória, CPU e I/O globalmente; avaliar C++ adicional ou GPU somente em gargalos comprovados.

## 2. Escopo, versões e força da evidência

Consolidação em 13/09/2026. Base local: HEAD `0663bc93723a5f161a1bc435f49efcc1d7ae7167`, com alterações locais adicionais. A implementação inspecionada usa .NET 10, SkiaSharp 3.119.2 e GDAL 3.12.1. O estado de trabalho, e não apenas o commit, é a referência deste diagnóstico.

Foram consultados o manual QGIS 3.44, APIs e arquivos específicos dos tags `final-3_44_0` e `final-4_2_0`. As evidências de implementação abaixo usam 4.2.0, salvo indicação explícita. Documentação `stable` do GDAL pode conter recursos posteriores a 3.12.1: cada recurso deve ser conferido contra a biblioteca efetivamente distribuída.

**Fato documentado** descreve manual/API/código citado. **Constatação local** descreve o GeoNex inspecionado. **Proposta** é uma decisão de engenharia ainda não implementada. Os números históricos da seção 13 não foram repetidos nesta análise.

Este relatório cobre os caminhos relevantes de renderização, providers, caches, concorrência e qualidade; não afirma ter auditado integralmente todos os módulos, plugins e drivers do QGIS. ArcGIS não é usado como evidência de algoritmos internos proprietários. Nenhuma implementação do QGIS foi copiada.

## 3. Como o QGIS organiza o trabalho

### 3.1 Renderização, camadas e apresentação

O `QgsMapRendererParallelJob` distribui trabalhos de camadas por `QtConcurrent::map`, executa rotulagem em etapa própria e admite segunda passagem. Isso não significa paralelizar arbitrariamente cada polígono ou ignorar dependências entre símbolos, máscaras e rótulos. [^q-parallel]

O `QgsMapRendererCache` guarda imagens associadas à extensão, transformação e camadas dependentes; eventos de repintura invalidam resultados relacionados. Existe reaproveitamento de imagem transformada. O cache de imagem de camada é diferente do cache de geometrias e do cache de tiles. [^q-rendercache]

No canvas estudado, o temporizador de atualização parcial é iniciado com 250 ms e pode ser configurado. É um intervalo de publicação durante renderização, não um limite de FPS nem uma obrigação de esperar 250 ms para toda interação. [^q-canvas]

**Aplicação ao GeoNex:** conservar o desenho de camadas que não mudaram e desacoplar conclusão dos dados de sua apresentação. Preservar a pilha real de composição: camadas transparentes, efeitos, seleção e rótulos podem exigir invalidação conjunta.

### 3.2 Rasters grandes: viewport, pipeline e blocos

O renderer raster recorta a área visível, calcula dimensões no dispositivo, copia o pipeline da camada e desenha por `QgsRasterIterator`/`QgsRasterDrawer`. O feedback suporta saída parcial. Não é necessário materializar a imagem inteira na resolução original para mostrar uma janela. [^q-raster-renderer]

O iterador particiona o pedido em blocos. Seu cabeçalho define padrão de 2000 × 2000 pixels, mas o construtor pode usar os passos indicados pelo provider e descontar sobreposição. Esse número não é tamanho de tile XYZ, nem uma recomendação universal para o GeoNex. [^q-raster-iterator] [^q-raster-iterator-h]

O provider GDAL realiza leituras de janelas/blocos e considera overviews e reamostragem. Seu gerenciamento inclui reaproveitamento de datasets clonados, com limite de cache de 50 datasets no código estudado; há tratamentos específicos para drivers. Não se deve substituir isso pela abertura irrestrita de um handle por tarefa. [^q-gdal]

O manual apresenta pirâmides internas/externas, escolha de reamostragem e seleção de resolução conforme zoom. Estatísticas podem ser estimadas ou exatas e calculadas em extensões diferentes. Reamostragem antecipada no provider é uma opção de qualidade. Criar pirâmides internas pode modificar o arquivo original. [^q-raster-manual]

### 3.3 Reprojeção raster

O `QgsRasterProjector` constrói uma malha de pontos de controle, refina linhas/colunas e dispõe de caminhos aproximado e exato. A malha também auxilia o cálculo da extensão de origem; o código registra limitações de uma transformação simples de bounding box. Aproximação é uma técnica controlada, não licença para deslocar feições ou eliminar bordas. [^q-projector]

No GDAL, `gdalwarp` separa memória de trabalho, seleção de overviews, reamostragem e erro de aproximação. `-multi` sobrepõe trabalho de I/O/processamento; paralelismo computacional depende também de `NUM_THREADS`. O erro padrão documentado de transformação é 0,125 pixel de origem, não 0,125 metro ou necessariamente pixel de tela. [^g-warp]

**Aplicação:** manter reprojeção sob demanda, com orçamento de erro definido no espaço correto; não pré-reprojetar ortofotos inteiras apenas para navegar. A escolha entre VRT, leitura direta e warp de janela depende da transformação e do driver.

### 3.4 Vetores grandes: pedidos seletivos ao provider

O renderer vetorial prepara um `QgsFeatureRequest` com retângulo de consulta e somente os atributos necessários, incluindo dependências de estilo/rotulagem. O renderer pode ampliar a extensão solicitada. A simplificação é calculada a partir de limiar de tela e unidades por pixel; há cancelamento durante iteração e tratamento próprio para níveis de símbolos. [^q-vector-renderer]

O iterador OGR adquire conexão do pool, aplica filtro espacial no OGR e completa o conjunto de atributos quando filtros/ordenação exigem campos adicionais. `NoGeometry` não é uma otimização aplicável ao desenho de polígonos: filtros espaciais e expressões podem continuar exigindo geometria. O acesso ocorre pelo provider, sem impor SHP como formato intermediário universal. [^q-ogr]

O simplificador de desenho possui algoritmos por distância, grade e Visvalingam, além de generalização por envelope em condições específicas. Isso não equivale a garantir topologia entre feições vizinhas nem a simplificação permanente do dataset. [^q-simplifier]

**Aplicação:** conservar a leitura nativa de SHP quando vantajosa, mas expô-la por um contrato comum. Desenho, seleção, edição e análise precisam de representações com finalidades distintas.

### 3.5 Tiles XYZ, WMTS e WMS

No provider WMS estudado, XYZ usa uma definição de matriz equivalente a WMTS. O nível é escolhido pela resolução mais próxima, com consideração de DPI quando disponível. Os pedidos cobrem a área intersectada e são ordenados pela distância de Chebyshev ao centro. A prévia busca imagens já em cache em até dois níveis mais grossos e um mais fino; não implica baixar esses níveis antecipadamente. O feedback comunica novos dados. [^q-wms]

Detalhes relevantes do mesmo código: `tilePixelRatio` participa da definição da fonte; alinhamento de pixels evita costuras; suavização é dispensada perto da escala 1:1; retry padrão é três; expiração sem metadados recebe fallback de 24 horas. O limite de 256 tiles encontrado no desenho está sob `QGISDEBUG`, não é um teto geral de produção. O tratamento de cache remove o cabeçalho literal `Cache-Control` nessa rotina: é comportamento observado da versão, não política recomendada para reprodução. [^q-wms]

O gerenciador de downloads trabalha separado da renderização, compartilha pedidos identificados por URL e cabeçalho `Range` e permite concluir downloads úteis mesmo quando consumidores desaparecem. A fila não estabelece, por si, um limite universal de seis downloads. [^q-download]

### 3.6 Cache web: o que realmente é armazenado

`QgsTileCache` usa `QCache<QUrl,QImage>` com custo máximo 256 e inserções com custo unitário: são **256 entradas**, não 256 MB. Consulta imagens decodificadas e depois o cache de rede. `QCache` remove objetos menos recentemente acessados quando precisa liberar custo. [^q-tilecache] [^qt-qcache]

Como estimativa própria, 256 imagens RGBA de 256² pixels somam 64 MiB de pixels; em 512², 256 MiB. Há outros custos e referências. Portanto, quantidade fixa de tiles não constitui orçamento de memória previsível.

Na implementação 3.44 estudada, o cache de rede em disco é compartilhado e protegido por mutex. A configuração de tamanho zero seleciona cálculo automático baseado no espaço em disco, com teto aproximado de 1000 MiB. Não é a mesma estrutura do cache de imagens decodificadas. [^q-disk] [^q-network]

O Qt documenta atualmente seis pedidos HTTP por host/porta em plataformas desktop, ressalvando dependência do protocolo. Isso é comportamento da camada de rede, não seis núcleos de CPU, nem parâmetro fixo de todo QGIS/HTTP2. [^qt-network]

## 4. Formatos e limites que a arquitetura precisa respeitar

| Fonte | O que a documentação sustenta | Consequência para o plano |
|---|---|---|
| GeoTIFF | Organização tiled/strip, compressões, overviews; decodificação multithread de múltiplos blocos em versões recentes. [^g-gtiff] | Medir blocos realmente lidos; não confundir tamanho comprimido com memória de trabalho. |
| COG | Organização otimizada e preparação de overviews; criação pode exigir armazenamento temporário importante. Recursos `Create()` citados como 3.13 não pertencem ao GDAL 3.12.1 atual. [^g-cog] | Oferecer derivado opcional, nunca conversão silenciosa; reservar disco e validar resultado. |
| HTTP raster | `/vsicurl/` permite acesso remoto por intervalos e dispõe de caches próprios. [^g-vsi] | COG remoto não é XYZ; observar suporte real a Range, tamanho de resposta e reaberturas. |
| ECW | Depende do SDK/driver e possui controles específicos de cache/decodificação. [^g-ecw] | Descobrir capacidades em runtime; não tentar criar `.ovr` como se fosse TIFF nem prometer suporte sem driver. |
| SHP | OGR usa `.qix` para índice espacial e lê `.sbn/.sbx`; DBF limita nomes de campos a dez caracteres e tipos disponíveis. Existem limites de tamanho/interoperabilidade. [^g-shp] | `.shx` localiza registros, não substitui índice espacial. Não converter todos os formatos para SHP. |
| GeoJSON | Driver suporta opções de leitura, schema e geometrias; não há garantia de consulta espacial indexada equivalente a um formato preparado. [^g-json] | Medir abertura e cada consulta; provider direto melhora fidelidade, mas não garante eliminar varreduras. |
| FlatGeobuf | Índice espacial habilitado por padrão; construção requer memória proporcional às feições; geometria nula com índice tem restrição documentada. [^g-fgb] | Candidato a derivado de leitura, condicionado a fidelidade e memória, não substituto obrigatório. |
| GeoPackage | Driver suporta múltiplas camadas, índices espaciais e edição. [^g-gpkg] | Candidato a armazenamento derivado/editável, com validação de schema e IDs; não converter como primeiro passo obrigatório. |

Para overviews, o GDAL relaciona janela de origem e buffer de destino e pode escolher uma resolução reduzida. A heurística documentada da classe base não deve ser tratada como fórmula obrigatória de todos os drivers. [^g-rasterio]

## 5. Diagnóstico do GeoNex atual

### 5.1 O que já existe e deve ser preservado

| Subsistema | Implementação local encontrada | Limitação ainda relevante |
|---|---|---|
| Tiles web | `OnlineRasterSession`, dois datasets retidos, leases, worker independente, câmera mais recente, preservação de último bitmap válido. | Publica mosaico após leitura; não há compositor incremental de tiles individuais. |
| Leitura online | `OnlineRasterFrameReader`, janela fracionária, caminho RGB direto em faixas, reprojeção alternativa. | Oportunidade de reuso ainda depende da sessão GDAL e da granularidade do mosaico. |
| Qualidade | DPI, limites de fonte, políticas de overzoom e precisão em alto zoom. | Qualidade também depende da resolução regional do provedor e de todas as etapas de composição. |
| Raster local | Warped VRT, leitura da janela, políticas de reamostragem, builder de overviews. | Bloqueio de manutenção compartilhado com leitura; buffers/caches precisam de orçamento conjunto. |
| Vetores | SHP mapeado em memória, índice C++, transformação em lotes, cache de paths, origem local em alto zoom. | Outros formatos são convertidos para SHP; custo de pintura continua relevante. |
| Apresentação | Cena Skia codificada em PNG, `Image.decode`, troca de canvas e transformações durante gesto. | Atualização de uma fonte pode exigir composição e transporte da cena inteira. |
| Hardware | Orçamentos adaptativos em subsistemas, limites de workers e cache GDAL. | Não existe um controlador global que impeça soma excessiva de todos os budgets. |

Referências locais: [sessão online](performance-online-decoded-session.md), [worker independente](performance-online-independent-worker.md), [RGB e precisão](performance-precise-cache-rgb-direct.md), [vetores adaptativos](performance-vectors-adaptive.md), [apresentação LOTES](performance-lotes-online-presentation.md).

### 5.2 Bloqueio de pirâmides: prioridade raster

`Home.razor` agenda `RasterOverviewBuilder.Schedule` passando `MapService.GdalRasterLock`. O builder executa todo `BuildOverviews` dentro desse lock; `LocalMapServer` usa o mesmo lock em `ReadRaster`. Assim, rodar o builder em `Task.Run` não impede que a leitura espere pela construção inteira.

Essa é uma dependência comprovada no código, não uma medição de que ela explica todos os atrasos relatados. Deve-se registrar duração de espera e testar raster grande sem overviews. Não basta remover o lock: escrita de sidecar, handles existentes e drivers precisam de um protocolo seguro de publicação/reabertura.

### 5.3 Conversão e precisão vetorial

`ProjetoService` utiliza `CopyDataSource` com driver SHP para entradas convertidas. Isso adiciona preparação e sujeita schema ao formato de destino. `ReprojetarMetadadosEmLotes` transforma cantos de envelope e centro; sob transformação não linear, esses pontos não provam que o envelope resultante contém toda a geometria. Falso negativo espacial pode esconder feições.

O caminho de alto zoom já subtrai uma origem local em double antes de criar coordenadas float. Entretanto, rebase associado ao centro da câmera pode invalidar reuso a cada pan, e caminhos legados de câmera, centroides, seleção e simbologia ainda exigem auditoria consistente. Referências: [precisão](fix-high-zoom-render-precision.md), [lotes de projeção](performance-vector-projection-batches.md).

### 5.4 Limites das otimizações anteriores

Caches rápidos não provam frames rápidos. Medições anteriores registraram custo residual de desenho e PNG/WebView. Experimentos de particionamento/recorte e faixas paralelas alteraram antialiasing; não devem ser reintroduzidos sem novos testes de costuras, winding e transparência. A alternativa WebP também não deve substituir PNG sem validar alfa e fidelidade. [Histórico de apresentação](performance-lotes-online-presentation.md), [histórico de polígonos](performance-projected-polygons.md).

## 6. Arquitetura proposta

```text
Câmera double + revisão do projeto + estilo + DPI + finalidade
                         |
                  Plano da cena visível
             /              |                 \
    TileMatrix/XYZ     Janela raster     Consulta vetorial
    HTTP + políticas   GDAL + overviews  Provider + índice
             \              |                 /
             Agendador com admissão CPU/I/O/memória
                         |
       Caches distintos: bytes / blocos / geometria / imagem
                         |
        Compositor por camada e grupo de dependências
                         |
        Publicação incremental, revisada e limitada
                         |
           WebView: cena válida + refinamento
```

O plano da cena deve ser imutável. Toda saída leva identidade de dados, estilo, CRS, DPI e revisão de câmera. Resultados atrasados podem alimentar caches compatíveis, mas nunca substituir a apresentação de uma revisão nova por uma antiga.

### 6.1 Contratos comuns

- **Fonte:** identidade estável, versão/fingerprint, capacidades, CRS completo, transform context e credenciais isoladas.
- **Consulta:** área conservadora, resolução de destino, atributos necessários, qualidade, cancelamento e orçamento.
- **Resultado:** cobertura, precisão/LOD, bytes estimados e reais, dependências, estado parcial/final/erro.
- **Recurso:** propriedade explícita e lease; descarte só depois do último consumidor.
- **Cache:** chave representa o conteúdo, não apenas nome da camada; limites por bytes e invalidação versionada.
- **Apresentação:** ordenação correta, nenhum frame obsoleto, progresso honesto e manutenção de conteúdo válido compatível.

Os nomes de componentes a seguir são propostas, não classes já disponíveis: `SceneRenderPlan`, `RenderResourceGovernor`, `TileScheduler`, `HttpTileCache`, `LayerImageCache`, `IVectorSource`, `VectorBlockCache` e `RasterReadSession`.

## 7. Frente W — mapas web

### W1. Identidade, matriz e nível de detalhe

Descrever cada fonte por provider, versão de estilo, matriz, CRS, limites, tile size, pixel ratio, intervalo temporal, idioma e partição de autenticação. Uma chave de conteúdo deve distinguir essas variantes e os cabeçalhos HTTP relevantes, sem gravar segredos em logs ou nomes de arquivos.

Para XYZ Web Mercator convencional, usar aritmética double para coordenadas e inteiros de 64 bits para endereçamento. Em um mundo de largura `W`, tile de `T` pixels e nível `z`, a resolução nominal é `W / (T * 2^z)`. Essa fórmula não se aplica indiscriminadamente a matrizes WMTS: metadados podem definir outra origem, CRS, resoluções e limites. [^ogc-tms]

Proposta de seleção: comparar resolução da fonte com unidades por pixel físico da saída, testar níveis vizinhos e escolher conforme política de qualidade validada. Adicionar histerese pequena e mensurável para evitar alternância de nível, sem prender a imagem em resolução inferior depois do gesto. Pixel ratio do tile e DPR da tela são conceitos separados.

Em 256 × `2^23`, uma dimensão mundial excede `Int32.MaxValue`. Endereçar tiles individualmente evita precisar representar o mundo inteiro como uma única dimensão raster GDAL. Isso não autoriza solicitar níveis ausentes ou indisponíveis regionalmente.

### W2. Agendador de downloads

Manter um pedido em voo por identidade completa e vários consumidores. Separar cancelamento de consumidor, cancelamento da demanda e decisão de terminar uma transferência útil. Repriorizar a fila com cada câmera, removendo trabalho obsoleto ainda não iniciado; permitir conclusão de trabalho em voo somente dentro do orçamento e da política do provider.

Prioridade proposta: cobertura nativa visível ausente; centro da tela; demais tiles visíveis; refinamentos. Trabalho especulativo fica desabilitado por padrão e só existe onde houver permissão expressa. Impor limites globais e por origem, incluindo bytes em voo; evitar que uma fonte lenta bloqueie outras.

Usar cliente HTTP de longa duração, pooling e tratamento de renovação DNS. Fila limitada não implica fila prioritária: uma heap de prioridades com despacho limitado pode alimentar workers. Leitura de headers antecipada requer timeout/cancelamento também no corpo e limite de bytes antes de decodificar. [^dotnet-http] [^dotnet-channels] [^dotnet-headers]

Retries devem ser limitados, com jitter e respeito a `Retry-After`; 401/403 não recebem tempestade de repetição; 404 pode representar ausência e exige política específica; resposta corrupta não vira tile preto permanente. [^http-semantics]

### W3. Cache HTTP e imagens decodificadas

Política de cache deve considerar `Cache-Control`, `Expires`, `Date/Age`, validadores e `Vary`. `no-cache` pede revalidação; `no-store` impede armazenamento/reuso em cache. Conteúdo vencido não é automaticamente reutilizável; respostas 304 atualizam metadados e conservam o corpo validado. Separar conteúdo autenticado e tratar `Vary:*` como não reutilizável por correspondência simples. [^http-cache]

`stale-while-revalidate` e `stale-if-error` só se aplicam quando autorizados pela política pertinente. Preservar temporariamente um frame de navegação não deve virar uma autorização geral de armazenamento indefinido de imagens do provedor. [^http-stale]

Proposta de armazenamento:

- Disco: corpo comprimido e metadados, publicação atômica, chave/fingerprint versionados, limites de bytes e espaço livre.
- RAM: imagem validada e decodificada; LRU por bytes, proteção temporária do conjunto visível e liberação por pressão.
- Em voo: deduplicação, progresso, consumidores e estimativa de memória de decode.
- Corrupção: rejeitar entrada inválida, registrar motivo e recuperar apenas a chave afetada.
- Segurança: não persistir tokens; limpar partição de sessão quando exigido; testar URLs assinadas sem conflar identidades.

Não adicionar um novo cache grande sem contar GDAL, imagens de camada, WebView e buffers temporários no mesmo orçamento. Cache cheio deve reduzir retenção, não bloquear apresentação nem forçar download repetido do conjunto visível por uma política mal dimensionada.

### W4. Cobertura progressiva e nitidez

Representar cobertura e qualidade separadamente. Um tile pai em cache cobre provisoriamente uma região, mas não marca a região como resolvida na qualidade nativa. Imagens de nível inferior só preenchem lacunas; nunca pintam por cima de filhos mais detalhados que já chegaram.

Cada atualização deve verificar identidade e revisão. Coalescer notificações de tiles em pequenos lotes, medir o custo de composição e limitar frequência; não codificar um PNG completo por resposta HTTP. Tratar alpha, recorte de pais, bordas e coordenadas fracionárias com a mesma transformação global.

Estados propostos: `sem cobertura`, `prévia`, `nativo`, `limite da fonte`, `erro recuperável`. Ao exceder a resolução real, informar overzoom. Bilinear/cúbica alteram apresentação, mas não recuperam informação inexistente; super-resolução gerativa não pertence ao modo cartográfico fiel.

### W5. Contratos dos provedores

| Provedor | Evidência | Decisão necessária |
|---|---|---|
| Google | A API oficial usa sessão, atribuição e informação regional de máximo zoom; níveis anunciados não garantem detalhe uniforme. Suas políticas restringem armazenamento/prefetch conforme contrato. [^google-overview] [^google-policy] | Auditar o endpoint atual `mt1.google.com/vt`: a documentação da Map Tiles API não autoriza automaticamente esse endpoint. Definir integração suportada antes de ampliar cache ou zoom. |
| Esri World Imagery | Metadados consultados anunciam tiles 256, DPI 96, WKID 102100, LODs 0–23 e `exportTilesAllowed=false`. [^esri-metadata] | Usar metadados e atribuição atual; verificar disponibilidade regional e licença. `exportTilesAllowed=false` não é, sozinho, regra completa de cache interativo. |
| OSM público | Exige identificação/atribuição e respeito ao cache; proíbe funções de download em massa/prefetch. [^osm-policy] | Auditar overscan que gera rede; fallback de níveis adjacentes deve usar somente cache. Não imitar User-Agent do QGIS. |

A política atual GeoNex fixa Google em z20, Esri em z22, OSM em z19, TTL de sete dias e limites de conexão 4/4/2. Esses são parâmetros locais, não os defaults universais do QGIS nem prova de conformidade dos provedores. Não elevar todos os números como uma otimização automática.

## 8. Frente R — rasters grandes

### R1. Abertura rápida e manutenção sem bloqueio global

Separar abertura de metadados, primeira imagem, estatísticas e preparação de overviews. Mostrar a camada antes de qualquer varredura dispensável; se não houver extensão confiável, calculá-la com progresso, sem anunciar enquadramento incorreto.

Reestruturar `RasterOverviewBuilder` com admissão de I/O, cancelamento e publicação controlada de derivados. Construir em destino temporário isolado quando o driver permitir; publicar sidecar completo e renovar sessões afetadas em fronteira segura. Se isso não for seguro para um driver, suspender preparação durante uso ativo ou trabalhar sobre cópia derivada autorizada.

Não remover simplesmente `GdalRasterLock`. A alternativa deve provar isolamento de handles e ausência de leitura de sidecar incompleto. A coordenação deve ser por recurso/arquivo, sem prender rasters independentes durante toda a construção.

Overviews já existem no GeoNex: o trabalho é corrigir ciclo de vida, concorrência e política de armazenamento. Não reimplementar um segundo builder. Tornar explícitas as opções de criar sidecar ao lado do original, usar cache gerenciado ou não preparar; nunca substituir o original.

### R2. Janela de leitura e resolução

Conservar o fast path de janela/VRT. Instrumentar janela solicitada, bandas, nível efetivamente usado, blocos/bytes, tempo de decode, warp e cópias. Comparar leitura direta no mesmo CRS com VRT apenas quando necessário; VRT é uma representação virtual, não prova de que todos os pixels reprojetados ou pirâmides estão materializados.

Dimensionar saída por viewport, DPR e finalidade. Usar contas verificadas de tamanho/stride e limites de alocação; jamais reservar `largura_original × altura_original × bandas` para uma simples visualização. Alinhar pedidos aos blocos do driver quando o ganho de I/O superar o excesso de leitura.

Reprojeção deve incluir borda de suporte do kernel e extensão conservadora. Para rasters rotacionados, GCP, RPC ou geolocalização, preservar o caminho apropriado; não forçar inversão afim simples em dados que não a admitem.

### R3. Semântica radiométrica

Classificar dados por metadados e escolha explícita, não apenas por ter três bandas. Paleta/classes pedem preservação de valores; imagens contínuas podem usar interpolação; máscaras, alpha e NoData têm tratamento próprio. Três bandas podem representar variáveis científicas, não RGB.

Separar valores originais de seu mapeamento para Byte/RGBA. Identificação de pixel, estatísticas e análise devem ler os valores da fonte; o bitmap de visualização não é dado analítico. Invalidar cache de imagem ao alterar bandas, stretch, gamma, colormap ou NoData, sem invalidar blocos-fonte compatíveis.

Testar transparência premultiplicada, bordas contra NoData, halos de interpolação, máscaras externas e paletas. Comparar saída final com referência de alta qualidade; durante gesto, qualquer redução temporária deve terminar em refinamento nítido.

### R4. Overviews, COG e caches

Usar pirâmides existentes antes de criar novas. Decidir níveis pela dimensão e pelas escalas esperadas, com política categórica/contínua explícita. A presença de algum overview não prova que todos os níveis necessários existem ou que foram construídos com algoritmo adequado.

COG é opção de derivado quando houver benefício de acesso, não requisito para abrir TIFF/ECW. Registrar origem, checksum/fingerprint, bandas, resolução, CRS, algoritmo e versão do derivado. Planejar espaço temporário, falha, cancelamento e descarte recuperável.

Coordenar cache GDAL de blocos, cache VSI, sessões de leitura e imagens de camada. Cache VSI pode multiplicar custo por arquivo/handle: inventariar o total efetivo. Não esconder sidecars com opções que impedem sua descoberta apenas para acelerar abertura de diretórios.

### R5. Concorrência segura

GDAL é geralmente reentrante, mas não seguro para chamadas concorrentes sobre a mesma instância ou objetos relacionados. Desde 3.10 existe suporte específico a datasets raster read-only thread-safe; isso não torna OGR, atualização de overviews ou qualquer biblioteca de driver automaticamente segura. [^g-threads]

Começar com sessões de leitura de propriedade exclusiva e pool limitado. Avaliar a API thread-safe somente após confirmar binding, driver e custo de reabertura/cache. Não multiplicar `N camadas × N workers × ALL_CPUS`; reservar capacidade para a UI e para finalização de frames.

## 9. Frente V — vetores grandes

### V1. Provider comum sem perda de formato

Introduzir `IVectorSource`: metadados, capacidades, consulta espacial, campos necessários, leitura por ID, versão dos dados e cancelamento. Implementações iniciais: SHP nativo existente e OGR direto. Não exigir que todos os formatos tenham os mesmos custos ou garantias de acesso aleatório.

Retirar conversão GeoJSON→SHP do caminho obrigatório. Validar preservação de nomes longos, Unicode, null, inteiros de 64 bits, datas, listas/objetos quando suportados, Z/M e geometrias multipartes. Campo não suportado deve permanecer acessível na origem ou receber representação explícita; nunca truncar silenciosamente.

FID do provider não é universalmente um identificador de negócio estável. Manter identidade da fonte, FID e ID original separados, com mapa de correspondência quando existir derivado. Reordenação espacial para leitura não pode trocar atributos de feições, seleção ou resultados de edição.

Para GeoJSON sem acesso seletivo eficiente, oferecer preparação persistente opcional. Escolher entre índice próprio e derivado conforme capacidades/fidelidade e tempo de amortização. FlatGeobuf e GeoPackage são candidatos a comparar, não decisões já fechadas. O original permanece a autoridade.

### V2. Índices e envelopes conservadores

Separar índice de offsets, índice espacial, índice de partes e cache de geometria. Um `.shx` resolve localização de registros; não responde sozinho quais registros cruzam a tela.

Usar índice existente compatível ou construir índice gerenciado com fingerprint da fonte e seus sidecars. Testar resultados contra consulta de referência, incluindo envelopes degenerados, coordenadas inválidas e alterações externas. Publicação deve ser atômica; índice parcial não pode ser tratado como completo.

Consultar preferencialmente no CRS da fonte usando transformação conservadora da área visível. Densificar/adaptar bordas transformadas, tratar antimeridiano e singularidades. Quando não for possível garantir conservação, ampliar ou usar consulta segura mais abrangente; falso positivo custa tempo, falso negativo elimina conteúdo.

Índice por componente ajuda quando uma MultiPolygon tem envelope enorme e poucos componentes visíveis. Porém, preservar a relação exterior/interiores e o ID da feição. Não pintar buracos como polígonos independentes nem duplicar preenchimento por fragmentação.

### V3. Streaming e caches geométricos

Iterar em lotes limitados por bytes/vértices, não só contagem de feições. Uma única feição pode conter milhões de vértices e exige processamento por anéis/blocos com estado. Evitar listas intermediárias completas de WKB, coordenadas, paths e atributos simultaneamente.

Separar cache de geometria original, coordenadas projetadas e representação de desenho. Chaves incluem revisão dos dados, operação CRS, origem local, qualidade/LOD e semântica de desenho. Estilo pode reaproveitar coordenadas sem reaproveitar imagem final.

Proposta para alto zoom: origens locais estáveis por bloco espacial, em double, com tradução até a câmera também em double antes da entrega ao rasterizador. Quantização da origem deve atender limite de erro em pixels e ampliar capacidade de reuso; não basta fixar blocos arbitrariamente grandes.

Testar rebase em toda a cadeia: polígonos uniformes/categorizados, linhas, pontos, rótulos, hit-test, seleção e impressão. Tratar câmera, envelopes e conversão tela↔mundo como um contrato único. Não rebaixar dados armazenados para float mundial.

### V4. Qualidade, LOD e pintura

LOD é representação de visualização, nunca edição da geometria fonte. Definir limite em pixels físicos e documentar diferenças entre prévia, final e exportação. Caminho exato deve existir para validação, seleção geométrica e necessidades de precisão.

Priorizar redução de pintura repetida através de imagem de camada antes de nova simplificação agressiva. Em alto zoom, reduzir área candidata com índice; em visão geral, medir se predomina preenchimento, contornos, símbolos, transparência ou rótulos.

Batching deve respeitar ordem, winding, alpha e mistura por feição. Agrupar polígonos num único path pode alterar sobreposições transparentes; somente aplicar quando o renderer comprovar equivalência semântica. Manter fallback por feição para estilos incompatíveis.

Recorte para desenho pode reduzir coordenadas extremas e custo, mas precisa preservar feições envolventes, buracos, stroke e contexto do antialiasing. Clipping não deve modificar os dados de seleção/edição. Mudanças que alteram pixels fora da tolerância acordada bloqueiam ativação.

Para contornos cadastrais, não ocultar limites em nome do FPS. Topologia entre lotes vizinhos requer teste específico: simplificar cada feição independentemente pode gerar fendas/sobreposição mesmo quando cada polígono parece válido isoladamente.

### V5. Atributos, símbolos e rótulos

Carregar apenas campos usados por estilo, filtros, ordenação e rótulos, com fallback quando a expressão não permite inferência estática. Preparar estilos e expressões uma vez por revisão; evitar alocação por vértice/feição quando reutilizável.

Atributos completos devem ser carregados sob demanda para tabela/inspeção com paginação. Preservar contagem e estado de carregamento sem varrer toda a tabela apenas para preencher o popup inicial.

Separar layout de rótulos da pintura geométrica, mas manter dependências de colisão entre camadas. Limites de escala e prioridades devem ser escolhas cartográficas explícitas. Não converter arbitrariamente polígonos em agregados/heatmap para anunciar desempenho equivalente.

## 10. Memória, CPU e escolha tecnológica

### 10.1 Orçamento global

Controlar conjuntamente: metadados/índices residentes, geometria, paths, blocos GDAL, VSI, tiles decodificados, imagens de camadas, bitmaps transitórios, encoder e memória observável da WebView. `GC` não representa toda a memória nativa.

Proposta de admissão:

`residentes + reservas_em_voo + pico_estimado_da_tarefa <= orçamento_operacional`

O orçamento deve considerar RAM disponível, teto configurável e margem para sistema/UI; não é simplesmente toda RAM instalada. Em pressão: interromper manutenção/prefetch permitido, reduzir retenção de caches não visíveis e limitar simultaneidade antes de degradar qualidade final. Se uma tarefa não cabe, dividi-la ou informar limitação, sem OOM silencioso.

Exemplo matemático próprio: uma imagem RGBA 3840×2160 ocupa cerca de 31,6 MiB; quatro imagens dessa dimensão consomem aproximadamente 126,6 MiB só em pixels. Manter duas cenas, várias camadas e buffers de encode exige contabilidade explícita.

Perfis iniciais devem distinguir notebooks de 8 GB, máquinas de 16 GB e estações de 32 GB+, mas adaptar-se também a memória livre, modo de energia e carga. Valores finais de workers/cache dependem de benchmark; não há número universal que explore bem todo hardware.

### 10.2 C++, C# e GPU

**Decisão proposta:** continuar com C# para orquestração, HTTP e ciclo de vida, aproveitando GDAL/PROJ/Skia e o núcleo C++ existente. Atravessar a ABI em lotes grandes e estáveis, com limites verificados, cancelamento e ownership explícito.

C++ adicional é apropriado se perfil identificar transformação, parsing, consultas ou preparação geométrica como custo dominante e o ganho sobreviver no teste end-to-end. Não acelera servidor remoto, lock global ou transporte PNG por si só.

GPU é uma etapa experimental posterior: tesselação de polígonos com buracos, linhas consistentes, precisão local, transparência e sincronização de recursos precisam de prova. Um backend GPU não deve exigir que mapas impressos ou máquinas sem aceleração percam qualidade. Manter fallback CPU validado.

## 11. Plano de execução priorizado

Estado atualizado em 13/09/2026: P0 e P1-R iniciados no [primeiro incremento raster](performance-raster-overview-publication.md), com medição/cancelamento de espera GDAL e preparação isolada de overviews GeoTIFF. A [renovação coordenada de sessões/VRTs](performance-raster-overview-refresh.md) também foi implementada e testada na DLL real, com preservação de leases e espera não canceladora entre frames. P0 ainda exige baseline interativo comparativo; P1-R ainda exige cobertura adicional de drivers/máscaras e política de espaço em disco. **P1-C iniciado:** [cache de imagem de polígonos uniformes](performance-polygon-image-cache.md), com câmera/DPI exatos, invalidação vetorial separada da chegada de tiles e orçamento adaptativo. LOTES e endpoint real passaram; faltam grupos, simbologia categorizada/rótulos e invalidação granular por camada. As demais etapas continuam pendentes; funcionalidades pré-existentes estão descritas na seção 5. O diagnóstico da seção 5 registra o snapshot anterior aos incrementos.

| Etapa | Entrega | Principais pontos locais | Dependência | Critério de saída |
|---|---|---|---|---|
| P0 | Baseline rastreável e matriz de qualidade | `PerfTest`, `LocalMapServer`, `mapa.js` | Nenhuma | Medir entrada→apresentação, separar fases e reproduzir atrasos com fixtures controladas. |
| P1-R | Retirar manutenção raster do bloqueio global de navegação | `RasterOverviewBuilder`, `GdalRasterLock`, leases | P0 | Construir/cancelar overview sem prender leitura de outro raster; sem sidecar parcial publicado. |
| P1-C | Cache de imagem por camada/grupo e invalidação | `MapRenderingService`, `LocalMapServer` | P0 | Atualizar base com câmera fixa não refaz geometria/pintura vetorial inalterada. |
| P2-W | Descritor de fonte, scheduler e cache HTTP | `OnlineBasemapPolicy`, sessão/worker; novos componentes | P0, auditoria de provider | Deduplicação, cancelamento, 304/no-store/auth, bytes limitados e qualidade de nível testados. |
| P3-W | Cobertura progressiva e transporte incremental | Compositor, `mapa.js` | P1-C, P2-W | Tile novo refina região atual sem sumir cobertura válida nem publicar câmera antiga. |
| P2-V | Provider OGR direto e identidade fiel | `ProjetoService`, leitores, atributos/seleção | P0 | Abrir GeoJSON sem SHP obrigatório; equivalência de schema/IDs/geometria em contratos. |
| P3-V | Índices persistentes/conservadores e streaming | `NativeShapeSpatialIndex`, provider, preparação | P2-V | Consulta não perde nenhuma feição de referência; memória limitada inclusive com feição gigante. |
| P4-V | Blocos projetados com origem estável e LOD validado | `RenderPathCache`, `WkbSkiaParser`, C++ se medido | P3-V, P1-C | Pan/zoom alto reutiliza sem mesclar contornos; seleção/print continuam corretos. |
| P2-R | Sessões raster, bandas/semântica e janelas instrumentadas | `RasterDatasetPolicy`, VRT, leitor | P1-R | TIFF grande/COG/ECW disponível sem alocação integral, pixels/NoData corretos. |
| P5 | Governador global de recursos | Políticas vetorial/raster/GDAL/HTTP | P1, P2 | Pressão reduz trabalho em voo, sem crescimento ilimitado nem starvation do frame final. |
| P6 | Backend de apresentação ou GPU experimental | Compositor, WebView, nativo | P3-W, P4-V, benchmark | Ganho end-to-end e qualidade aprovados; fallback e impressão intactos. |
| P7 | Comparação controlada com QGIS e ativação gradual | Harness, configurações, documentação | Etapas candidatas concluídas | Resultado publicado por cenário/hardware; sem promessa global não medida. |

Atualização em 14/09/2026: **P2-W iniciado** pelo [ciclo de vida e retries do agendador online](performance-online-scheduler-lifecycle.md). Camadas ocultas/removidas deixam de manter trabalho ativo, pendente ou retries relevantes; pedidos tardios são filtrados. Timer preserva o primeiro prazo e a fila cheia não apaga tentativas vencidas. Testes unitários e DLL real com tiles locais lentos passaram. Descritores completos, scheduler por tile, `Retry-After` e semântica HTTP/cache permanecem pendentes.

### Primeiro incremento recomendado

Ensaio de transporte em 14/09/2026: seletor PNG por compressibilidade de amostra disponível somente com `GEONEX_PNG_COMPRESSION_PROBE=1`. Reduziu LOTES de 11,09 MB para 2,12 MB, mas aumentou encode/latência; **não ativado por padrão**. Contratos de pixels passaram; não é conclusão de P3-W. Estado e próxima investigação em [RETOMADA-OTIMIZACAO.md](RETOMADA-OTIMIZACAO.md). Não comparar estes tempos de smoke como benchmark estatístico; houve variação de carga e execuções paralelas.

Implementar P0 e P1-R como mudança pequena e verificável; em seguida P1-C. Isso ataca uma dependência de bloqueio concreta e o trabalho repetido compartilhado por vetores e tiles. Não começar por reescrever todo o renderer nem por aumentar cache/threads indiscriminadamente.

P2-W e P2-V podem ser desenvolvidos independentemente depois dos contratos comuns, mas integrar separadamente. Mudanças futuras de raster/cache vindas de outra máquina precisam ser reconciliadas antes de editar os mesmos componentes; não presumir que este snapshot representa um merge futuro.

## 12. Testes obrigatórios e critérios de qualidade

### 12.1 Matriz funcional

| Grupo | Casos mínimos |
|---|---|
| Raster | TIFF tiled e striped; sem/com overviews; BigTIFF; RGB/RGBA/paleta/Float32; NoData/máscara; bandas não RGB; arquivo somente leitura; ECW apenas com driver disponível. |
| Transformação | Mesmo CRS; 4326↔3857; UTM local; rotação; limites do raster; antimeridiano; GCP/RPC quando suportados; erro/CRS ausente explícito. |
| Web | Tiles 256/512; DPI 1/1,25/1,5/2; limites regionais; XYZ/TMS Y invertido; WMTS não padrão; zoom fracionário e extremo; respostas fora de ordem. |
| HTTP | Cache frio/RAM/disco; 200/304/404/429/503; 401/403; timeout de corpo; DNS; corrupção; `no-store`, `no-cache`, `Vary`; troca de credenciais; limite de disco. |
| Vetor | LOTES original; SHP derivado existente; MultiPolygon com muitos anéis; buracos; ilhas; transparência; campos longos/Unicode/64 bits/null; Z/M; SHX corrompido. |
| Interação | Pan contínuo, zoom rápido, retorno, esconder/reordenar camada, mudar estilo/CRS, remover fonte durante leitura, seleção e tabela, abrir/fechar projeto. |
| Concorrência | Navegação durante overview/indexação, impressão simultânea, várias fontes lentas, descarte durante I/O, pressão de RAM/disco e encerramento. |

### 12.2 Invariantes de aprovação

- Nenhum falso negativo na consulta espacial contra referência exata, inclusive em bordas reprojetadas.
- Nenhuma troca de atributos/IDs por reordenação ou conversão.
- Nenhuma feição/furo descartado silenciosamente; geometria inválida reportada, sem reparo destrutivo automático.
- Nenhum tile de outra fonte/conta/estilo/câmera publicado como atual.
- Nenhuma perda de conteúdo válido apenas porque a próxima câmera começou a carregar.
- Nenhum parent tile substitui região nativa mais detalhada.
- Nenhum deadlock/use-after-dispose; filas, caches e reservas permanecem limitados.
- Frame final não permanece preso em preview após cessar interação.
- Exportação respeita escala/DPI e não reutiliza bitmap de tela inadequado como qualidade final.

Referência visual: comparar com o caminho exato do próprio GeoNex, usando mesmos dados/estilo/câmera. Exigir igualdade quando o contrato é reutilização sem mudança de algoritmo. Quando houver nova reamostragem/AA, estabelecer limiar explícito por região e inspeção de contornos, alpha e pequenos objetos. SSIM global sozinho não detecta desaparecimento de um limite cadastral.

Comparações entre QGIS e Skia não devem exigir identidade byte a byte entre rasterizadores distintos. Devem verificar posição, resolução, conteúdo, simbologia equivalente e erro visual/geométrico acordado.

## 13. Benchmark reproduzível

### 13.1 Dados locais e histórico

O histórico local documenta LOTES com 57.454 feições e 324.191 vértices, além de uma amostra de inundação com oito MultiPolygons e 1.078.031 vértices. As contagens derivam de verificações anteriores; precisam ser registradas novamente com checksum ao estabelecer P0. Fonte original de LOTES: `C:\Users\Nicho\Downloads\LOTES.geojson`; não deve ser modificada.

O teste anterior de sessão online demonstrou uma requisição inicial e zero adicionais em pan/retorno dentro do mesmo tile, com cache de disco desativado e pixels iguais. Isso comprova aquele reuso, não ausência de rede para áreas novas. O histórico vetorial registra consultas de cache muito rápidas e desenho ainda custoso: lookup não é latência do mapa. [Sessões](performance-online-decoded-session.md), [cache vetorial](performance-vectors-adaptive.md).

Não há comparação controlada GeoNex×QGIS nesta consolidação. Nenhum valor abaixo deve ser interpretado como resultado medido.

### 13.2 Procedimento

1. Fixar build, bibliotecas, hardware, drivers, resolução/DPR, energia, dataset e estilo. Guardar checksum e roteiro de câmeras em double.
2. Executar cenários independentes: raster local, vetor sem base, web sem vetor e composição mista. Para formatos, separar custo de abrir/preparar de navegar após preparação.
3. Separar cache frio de aplicativo, disco quente e RAM quente. Cache do SO/CDN não é automaticamente frio após limpar cache do aplicativo; registrar o que foi realmente controlado.
4. Para testes determinísticos, usar servidor local com tiles próprios e latência/falhas programadas. Para provedores públicos, apenas tráfego permitido, sem benchmark de carga/prefetch em massa.
5. Repetir ao menos 30 trajetórias pareadas por cenário, alternando ordem dos aplicativos; registrar p50/p95, dispersão e outliers com causa, não só melhor frame.
6. No QGIS, usar mesmo viewport físico, dados, CRS, estilo equivalente, nível nativo, pirâmides/índices e política de cache. Registrar configurações reais, não defaults presumidos.
7. Validar screenshots e métricas antes de comparar velocidade. Uma imagem borrada/incompleta não vence um frame correto.

### 13.3 Métricas

- Abertura→primeira cobertura; término do gesto→imagem final; input→apresentação no navegador.
- Tempo até 90% e 100% da área disponível em resolução nativa; contabilizar ausência legítima do provedor separadamente.
- Consulta espacial, feições/vértices candidatos e realmente desenhados, projeção, construção de path, pintura, composição, encode e decode.
- Tiles pedidos/deduplicados/cancelados, cache RAM/disco, 304, bytes recebidos, tempo de fila/rede/corpo/decode.
- Raster: bytes/blocos lidos, overviews, janela de origem/saída, tempo esperando lock e criando VRT.
- RSS/commit/pico nativo, memória WebView observável, alocações, GC, CPU total/por fase e número real de workers.
- Frames obsoletos rejeitados, invalidações desnecessárias, tamanho máximo de fila e tempo até cancelar.

### 13.4 Metas provisórias, não promessas

Propor resposta visual ao gesto com p95 até 50 ms em notebook de referência; convergência final deve ser medida separadamente de rede. Para cenários preparados/localmente servidos, perseguir p95 de conclusão dentro de 20% do QGIS equivalente como primeiro marco, refinando para 10% após estabilização. Não exigir essa razão quando estilos ou conteúdo não forem equivalentes.

Para cada mudança, nenhuma regressão de correção é tolerada. Regressão de desempenho superior a 5% em cenários críticos exige investigação além do ruído experimental. Aceitar uma otimização apenas se seu ganho end-to-end justificar complexidade, memória e risco; microbenchmark isolado não basta.

## 14. Migração, observabilidade e rollback

Ativar por feature flags separadas: cache por camada, novo scheduler web, provider direto, cache geométrico por blocos e pool raster. Fazer fallback por fonte/capacidade; evitar duas implementações concorrentes disputando os mesmos recursos e duplicando pedidos.

Versionar formatos de cache e contratos de projeto. Cache incompatível deve ser ignorado/reconstruído sem apagar originais ou derivados de terceiros. Invalidação por edição de estilo não deve eliminar bytes da fonte; troca de credenciais não pode conservar conteúdo de outra conta.

Log estruturado deve usar IDs anônimos de fonte/câmera, duração e motivos de miss/cancelamento/erro. Não registrar tokens, URLs assinadas completas, atributos privados ou geometrias do usuário por padrão. Relatório de desempenho pode operar inteiramente local.

Rollback: desligar a flag do incremento, voltar ao caminho conhecido e preservar dados/projetos. Só remover artefatos gerenciados identificados por manifesto, dentro de diretório validado, com política recuperável. Nenhum rollback exige reset do repositório ou exclusão ampla de cache do usuário.

## 15. Decisões pendentes e riscos

- Contrato de integração e armazenamento para o endpoint Google atualmente usado; níveis e qualidade regional dos provedores.
- Local e autorização para sidecars/derivados; política para unidades sem espaço ou somente leitura.
- Dataset raster grande representativo e screenshots de referência nos níveis problemáticos.
- Tolerância visual de LOD em pixels físicos e exigências específicas de cadastro/impressão.
- Preservação de schema complexo/IDs em derivados vetoriais e seleção do formato por capacidade.
- Dependências de blend/labels que restringem separação da cena no navegador.
- Conciliação com alterações raster/cache ainda não integradas ao snapshot local.
- Dependências/binários disponíveis, incluindo driver ECW; validação das APIs usadas contra GDAL 3.12.1.

O caminho recomendado é incremental e mensurável. A referência do QGIS é uma combinação de providers seletivos, múltiplos caches, níveis de detalhe, tarefas controladas e publicação parcial. Reproduzir um único parâmetro não reproduz o comportamento do sistema inteiro.

## 16. Fontes primárias e rastreabilidade

Fontes consultadas em 13/09/2026. Tags fixados identificam a versão do código; páginas `stable`/serviços vivos devem ser revalidados antes da implementação. As notas abaixo constituem o inventário de fontes; cada referência aparece junto da afirmação correspondente.

[^q-parallel]: QGIS, [`QgsMapRendererParallelJob`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererparalleljob.cpp). Distribuição de camadas, rótulos e segunda passagem.
[^q-rendercache]: QGIS, [`QgsMapRendererCache`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprenderercache.cpp). Dependências e imagens de camada.
[^q-canvas]: QGIS, [`QgsMapCanvas`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/gui/qgsmapcanvas.cpp). Temporizador e atualização de renderização.
[^q-raster-renderer]: QGIS, [`QgsRasterLayerRenderer`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/raster/qgsrasterlayerrenderer.cpp). Viewport, pipeline e feedback.
[^q-raster-iterator]: QGIS, [`QgsRasterIterator`, implementação 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/raster/qgsrasteriterator.cpp). Particionamento e passos do provider.
[^q-raster-iterator-h]: QGIS, [`QgsRasterIterator`, cabeçalho 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/raster/qgsrasteriterator.h). Dimensões padrão.
[^q-gdal]: QGIS, [`QgsGdalProvider`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/providers/gdal/qgsgdalprovider.cpp). Janelas, overviews e cache de datasets.
[^q-raster-manual]: QGIS, [Manual 3.44 — Raster Properties](https://docs.qgis.org/3.44/en/docs/user_manual/working_with_raster/raster_properties.html). Estatísticas, resampling e pirâmides.
[^q-projector]: QGIS, [`QgsRasterProjector`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/raster/qgsrasterprojector.cpp). Malha de transformação e precisão.
[^q-vector-renderer]: QGIS, [`QgsVectorLayerRenderer`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/vector/qgsvectorlayerrenderer.cpp). Requests, atributos, simplificação e símbolos.
[^q-ogr]: QGIS, [`QgsOgrFeatureIterator`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/providers/ogr/qgsogrfeatureiterator.cpp). Pool, filtros e campos necessários.
[^q-simplifier]: QGIS, [`QgsMapToPixelSimplifier`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/qgsmaptopixelgeometrysimplifier.cpp). Algoritmos de simplificação para desenho.
[^q-wms]: QGIS, [`QgsWmsProvider`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/providers/wms/qgswmsprovider.cpp). Matrizes, resolução, prioridade, fallback e cache; comparação anterior com [3.44.0](https://raw.githubusercontent.com/qgis/QGIS/final-3_44_0/src/providers/wms/qgswmsprovider.cpp).
[^q-download]: QGIS, [`QgsTileDownloadManager`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/qgstiledownloadmanager.cpp) e [API](https://api.qgis.org/api/classQgsTileDownloadManager.html). Deduplicação, worker e ciclo de vida.
[^q-tilecache]: QGIS, [`QgsTileCache`, tag 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/qgstilecache.cpp). Imagens decodificadas, chave e custo.
[^q-disk]: QGIS, [`QgsNetworkDiskCache`, tag 3.44.0](https://raw.githubusercontent.com/qgis/QGIS/final-3_44_0/src/core/network/qgsnetworkdiskcache.cpp). Instância compartilhada e sincronização.
[^q-network]: QGIS, [`QgsNetworkAccessManager`, tag 3.44.0](https://raw.githubusercontent.com/qgis/QGIS/final-3_44_0/src/core/network/qgsnetworkaccessmanager.cpp) e [registro de configurações](https://raw.githubusercontent.com/qgis/QGIS/final-3_44_0/src/core/settings/qgssettingsregistrycore.cpp). Configuração do cache de rede.
[^qt-qcache]: Qt 6, [`QCache`](https://doc.qt.io/qt-6/qcache.html). Custo máximo e política de remoção.
[^qt-network]: Qt 6, [`QNetworkAccessManager`](https://doc.qt.io/qt-6/qnetworkaccessmanager.html). Assincronia e concorrência HTTP.
[^g-gtiff]: GDAL, [GTiff](https://gdal.org/en/stable/drivers/raster/gtiff.html). Blocos, overviews e `NUM_THREADS`.
[^g-cog]: GDAL, [COG](https://gdal.org/en/stable/drivers/raster/cog.html). Organização, opções e custos de criação.
[^g-vsi]: GDAL, [Virtual File Systems](https://gdal.org/en/stable/user/virtual_file_systems.html). `/vsicurl/`, acesso parcial e caches.
[^g-ecw]: GDAL, [ECW](https://gdal.org/en/stable/drivers/raster/ecw.html). Driver, SDK e opções.
[^g-shp]: GDAL, [ESRI Shapefile/DBF](https://gdal.org/en/stable/drivers/vector/shapefile.html). Índices, tipos e limites.
[^g-json]: GDAL, [GeoJSON](https://gdal.org/en/stable/drivers/vector/geojson.html). Leitura, schema e opções.
[^g-fgb]: GDAL, [FlatGeobuf](https://gdal.org/en/stable/drivers/vector/flatgeobuf.html). Índice, memória de construção e restrições.
[^g-gpkg]: GDAL, [GeoPackage vetorial](https://gdal.org/en/stable/drivers/vector/gpkg.html). Capacidades e índice espacial.
[^g-rasterio]: GDAL, [`GDALRasterBand::RasterIO`](https://gdal.org/en/stable/api/gdalrasterband_cpp.html). Janela, buffer e seleção de overviews.
[^g-warp]: GDAL, [`gdalwarp`](https://gdal.org/en/stable/programs/gdalwarp.html). Memória, multithreading, erro e reamostragem.
[^g-threads]: GDAL, [Multi-threading](https://gdal.org/en/stable/user/multithreading.html). Ownership e datasets read-only thread-safe.
[^ogc-tms]: OGC, [Two Dimensional Tile Matrix Set](https://www.ogc.org/standards/tms/). Referência de matrizes e CRS.
[^dotnet-http]: Microsoft, [HttpClient guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines). Pooling e ciclo de vida.
[^dotnet-channels]: Microsoft, [`BoundedChannelOptions`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.channels.boundedchanneloptions?view=net-10.0). Filas limitadas.
[^dotnet-headers]: Microsoft, [`HttpCompletionOption`](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0). Limites de timeout na leitura de headers/corpo.
[^http-semantics]: IETF, [RFC 9110 — HTTP Semantics](https://www.rfc-editor.org/rfc/rfc9110.html#name-retry-after), 2022. `Retry-After` e respostas HTTP.
[^http-cache]: IETF, [RFC 9111 — HTTP Caching](https://www.rfc-editor.org/rfc/rfc9111.html), 2022. Freshness, validação e variantes.
[^http-stale]: IETF, [RFC 5861 — HTTP Cache-Control Extensions for Stale Content](https://www.rfc-editor.org/info/rfc5861/), 2010. Uso condicionado de conteúdo vencido.
[^google-overview]: Google, [2D Tiles overview](https://developers.google.com/maps/documentation/tile/2d-tiles-overview). Sessão, níveis regionais e atribuição.
[^google-policy]: Google, [Map Tiles API policies](https://developers.google.com/maps/documentation/tile/policies). Restrições e obrigações da API oficial.
[^esri-metadata]: Esri, [World Imagery — metadados REST](https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer?f=pjson). Estado consultado em 13/09/2026; não substitui licença do serviço.
[^osm-policy]: OpenStreetMap Foundation, [Tile Usage Policy](https://operations.osmfoundation.org/policies/tiles/). Identificação, atribuição, cache e proibição de prefetch/bulk.

### Documentação local complementar

- [Arquitetura vetorial QGIS/ArcGIS](arquitetura-vetorial-qgis-arcgis.md).
- [Vetores adaptativos](performance-vectors-adaptive.md).
- [Cache geométrico](performance-geometry-cache.md).
- [Polígonos reprojetados](performance-projected-polygons.md).
- [Lotes de reprojeção](performance-vector-projection-batches.md).
- [Precisão em alto zoom](fix-high-zoom-render-precision.md).
- [Qualidade de basemap em alto zoom](fix-basemap-high-zoom-quality.md).
- [LOTES e apresentação online](performance-lotes-online-presentation.md).
- [Worker online independente](performance-online-independent-worker.md).
- [Sessões online e blocos decodificados](performance-online-decoded-session.md).
- [Cache preciso e RGB direto](performance-precise-cache-rgb-direct.md).
- [Cache de imagem de polígonos e composição sobre tiles](performance-polygon-image-cache.md).
- [Ciclo de vida e tentativas do agendador online](performance-online-scheduler-lifecycle.md).
