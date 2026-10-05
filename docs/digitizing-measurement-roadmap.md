# Vetorização e medição profissionais

Objetivo ativo: evoluir as ferramentas até um fluxo GIS completo, rápido e responsivo. Os incrementos abaixo são evidência de progresso; ainda não demonstram equivalência ou superioridade ao ArcGIS/QGIS.

## Incremento iniciado após o rollback de 25/09/2026

- Prévia e clique usam `DigitizingCursor`, portanto snap e restrição de distância produzem a mesma coordenada final.
- A tolerância de snap é expressa em pixels de tela (6/10/15/20/30 px) e permanece estável com zoom e rotação.
- Um único canal de movimento JS → .NET atende medição, ponto, linha e polígono. Rajadas são condensadas para a posição mais recente e um backend lento mantém no máximo uma amostra pendente.
- Desfazer/refazer funciona no desenho corrente e na medição; um novo vértice após desfazer invalida a ramificação antiga.
- Atalhos são enviados por um único manipulador, respeitam campos, botões e diálogos, e evitam conclusão repetida ao manter Enter pressionado.
- Duplo clique durante desenho ou medição não aplica zoom acidental.
- A barra de vetorização quebra linhas em janelas estreitas, mostra o número de pontos, inclui tolerância e preserva o desenho quando o usuário tenta trocar ferramenta/camada antes de concluir ou cancelar.

## Evidência automatizada

- `dotnet run --project tests/DigitizingCore/DigitizingCore.csproj`: 128 contratos de cursor, escala, rotação, restrições, histórico, snap, medição e construções paramétricas.
- `node tests/digitizing-interaction.spec.cjs` com Playwright disponível em `NODE_PATH`: atalhos, foco, fila latest-only, quatro ferramentas, transformação visual e duplo clique.
- `dotnet build GeoNex/GeoNex.csproj -f net10.0-windows10.0.19041.0 -c Debug -p:GeoNexBuildNative=false --no-restore`: integração MAUI, zero erros.

## Próximos blocos necessários

1. Validação topológica e coleção independente para novas feições, sem FIDs/offsets sintéticos no índice de arquivos.
2. Gravação transacional e recarga para ponto, linha e polígono, preservando atributos e SRC.
3. Construções restantes: arco, anéis e multipartes; retângulo, retângulo orientado, círculo, elipse e polígono regular têm implementação e teste de interface descritos abaixo.
4. Edição: mover/inserir/remover vértices, seleção múltipla, mover/rotacionar/escalar, dividir e unir.
5. Snap por camada, meio de aresta, interseções, prioridade e feedback visual por tipo.
6. Medição adequada ao SRC/ellipsoide, unidades explícitas, segmentos, azimutes, resultados congelados e exportação.
7. Extração gradual da interface monolítica em componentes testáveis e validação visual em diferentes tamanhos de janela.

## Verificação de renderização — 02/10/2026

- Corrigido o cache global que devolvia o mapa anterior sem os vértices novos: o cache agora contém apenas as camadas; aquisição, medição, snap e destaque são compostos em cada resposta.
- Durante ferramentas ativas, a prévia de navegação passa pelo compositor que reutiliza as camadas e acrescenta as sobreposições atuais.
- O cache possui pixels próprios, copiados antes das sobreposições. Um primeiro teste detectou falha nativa com o snapshot do buffer reutilizável; a cópia independente corrigiu essa falha nos testes seguintes.
- Restaurado o segmento tracejado do último vértice até o cursor; caminhos temporários Skia passam a ser descartados após cada quadro.
- `dotnet run --project tests/DigitizingCore/DigitizingCore.csproj -c Debug`: 61 verificações aprovadas, incluindo validação topológica já presente no commit atual.
- `node tests/digitizing-interaction.spec.cjs`: aprovado.
- `dotnet build GeoNex/GeoNex.csproj -c Debug --no-restore -p:GeoNexBuildNative=false`: zero erros, 191 avisos; usa os binários nativos existentes.
- `tests/digitizing-app-smoke.cjs`, com `GEONEX_TEST_CDP` apontando para uma instância descartável do aplicativo: inicialização MAUI, criação de camada temporária, desenho efetivamente visível, rejeição de polígono cruzado, desfazer/refazer, ausência de pixels antigos após cancelar e medição visível. Capturas em `test-results/digitizing-app/`.
- Ambiente: SDK 10.0.401 estava sem workloads; instalado `maui-windows`. O teste cria apenas dados temporários e não grava edições em dados do usuário.

Ainda falta validar gravação/recarga de feições novas, eliminar seus offsets sintéticos, ampliar construções e edição, corrigir unidades/azimutes e medir desempenho em conjuntos grandes. O teste visual não comprova esses requisitos nem superioridade a outros SIGs.

## Barra minimalista de vetorização — 04/10/2026

- Mantém o cabeçalho e a navegação geral do aplicativo; a vetorização ocupa somente sua barra compacta dentro da área de trabalho, sem abrir uma tela dedicada ou tomar a janela inteira.
- Seletor explícito **Simples / Avançada**: no modo simples ficam destino, ações do esboço e instrução para desenhar vértices; o avançado revela construções paramétricas, encaixes e entrada precisa. Trocar para Simples fecha a entrada precisa. Se houver construção paramétrica em andamento, primeiro é preciso concluí-la ou cancelá-la.
- O atalho de clique direito fica na própria barra; a ação polar só é mostrada no modo avançado. Camadas permanecem opcionais e encaixadas ao lado do mapa em desktop ou abaixo dele em telas estreitas.
- A área do mapa continua independente da barra, evitando que seus controles capturem cliques destinados ao mapa.
- Verificação deste incremento: build MAUI Windows Debug sem erros (187 avisos no build completo; avisos NuGet/de nulabilidade já presentes). Ainda falta inspeção visual específica dos modos Simples/Avançada em 1440/760/390 px.

## Medição por CRS — 02/10/2026

- `MapMeasurementService` usa as unidades lineares do CRS projetado e converte para metros; não assume que toda unidade de grade já seja metro.
- Em CRS geográfico, comprimento e área usam a geodésica do elipsoide do próprio CRS via GDAL/OGR.
- A interface informa o método utilizado, mostra erro de topologia da área sem apagar o esboço, exibe o azimute da última perna e permite apresentar distância em metros, quilômetros, pés ou milhas.
- Áreas podem ser apresentadas em m², hectares, km² ou acres; as conversões ocorrem apenas na apresentação, preservando o valor em m² calculado pelo CRS.
- O comando **Copiar resultado** exporta um resumo textual com distância, área, azimute, SRC e método de cálculo para relatórios de campo; usa a API de clipboard com fallback compatível.
- A suíte cobre UTM métrica, pés topográficos, EPSG:4326, área, azimute e métricas do esboço; total atual: 128 asserções aprovadas.

## Encaixe por contorno e ponto médio — 02/10/2026

- `SnapSearch` percorre os contornos Skia sem achatar multipartes/anéis em uma linha. Considera o fechamento real de anéis e pontos isolados; não cria arestas entre partes nem usa controles Bézier como vértices.
- A consulta ignora camadas invisíveis, mantém os recursos nativos protegidos durante a leitura e evita copiar todos os vértices para arrays por feição. A busca continua linear nos vértices dos candidatos; ainda falta medir desempenho com datasets grandes.
- Encaixe em ponto médio disponível na vetorização e medição, inclusive nos segmentos do esboço corrente. Medição também permite encaixe em arestas. Modos independentes mantêm a tolerância em pixels e o resolvedor comum à prévia/clique.
- Encaixe em interseções agora consulta apenas segmentos próximos ao cursor (limite de 512 candidatos por quadro), calcula o cruzamento entre contornos independentes e mantém a prioridade vértice > ponto médio > aresta > interseção.
- O marcador de snap diferencia vértice (amarelo), ponto médio (ciano), aresta (laranja) e interseção (magenta); a legenda fica no próprio submenu para reduzir ambiguidade durante a edição.
- Modo ortogonal (90°) disponível no submenu da vetorização: alinha ao eixo dominante antes do snap e continua compatível com a trava de distância/COGO.
- O submenu de vetorização abre por clique/teclado usando `details`, sem depender de hover.
- Validação: 90 verificações do núcleo, contratos JS e build Debug sem erros. Na janela MAUI, ativar apenas o ponto médio atraiu a mira ao centro de um segmento apesar do cursor deslocado 8 px; captura `test-results/digitizing-app/midpoint-snap.png`. Testes anteriores de desenho, cancelamento e medição continuaram passando.

## Construções e conclusão em memória — 03/10/2026

- `SketchConstruction` gera retângulos de dois cantos, retângulos orientados (base + ponto que determina lado e largura perpendicular), círculos por centro/raio, elipses por centro + dois eixos e polígonos regulares de 3 a 32 lados. São construções planas na grade do SRC. Círculo e elipse são polígonos inscritos de 128 segmentos, indicados na interface; não são buffers geodésicos.
- Prévia e conclusão usam o mesmo gerador. O histórico armazena controles, não os vértices expandidos. Enter conclui explicitamente; mudança de destino/modo é bloqueada com controles ativos. Desfazer/refazer, entrada absoluta e COGO usam a mesma inserção de controles.
- Construções de área nula, coordenadas não finitas ou que colapsam na precisão float são rejeitadas preservando o esboço. Restrições ortogonais/de distância são desativadas ao escolher construção paramétrica; coordenadas explícitas continuam disponíveis.
- Feições em memória são desenhadas por seus paths próprios e excluídas do parser de registros SHP. Camadas compostas somente por novas feições usam STRtree; índices publicados retêm uma cópia estável da lista. Índices mistos passam envelopes explícitos para o C++, evitando interpretar offset zero como registro. A conclusão invalida o cache de cena.
- Barra responsiva com instruções por etapa; abaixo de 820 px os painéis laterais recolhem durante a vetorização e podem ser reabertos com **Painéis**.
- Verificação: 135 contratos do núcleo; testes JS; teste no MAUI com camada temporária EPSG:31982, cinco formas visíveis/concluídas, métricas ao vivo, undo/redo, snap no círculo concluído, barra a 760 px e medição. Capturas: `test-results/digitizing-app/{Rectangle,OrientedRectangle,Circle,Ellipse,RegularPolygon,construction-narrow}.png`.
- Limite da primeira verificação: o binário nativo existente falhou ao carregar e `GeoNexNative/build.bat Debug` informou ausência das ferramentas C++ do Visual Studio naquele momento. O build C# usou `-p:GeoNexBuildNative=false`. O fluxo de novas feições em memória foi exercitado; o caminho misto com registros SHP, gravação/reabertura, grandes datasets e superioridade a outros SIGs ainda não estão comprovados.
- Reproduzir testes JS: apontar `NODE_PATH` aos pacotes Node disponibilizados pelo runtime do Codex; `GEONEX_TEST_CDP` deve apontar a uma instância descartável do app com WebView2 debugging. O smoke cria apenas uma camada temporária e não salva dados do usuário.

## Redesign dos instrumentos — 03/10/2026

- Vetorização reorganizada em destino/ações, construção/encaixes e estado/métricas. Configurações de encaixe expandem dentro da barra, com indicação do número de modos ativos. Polígonos regulares oferecem todos os valores de 3 a 32 lados.
- Medição prioriza resultados, seleção explícita de distância/área, unidades, ações de histórico/cópia e ajustes de precisão recolhíveis. Em telas estreitas, usa painel inferior com rolagem, preservando parte do mapa.
- Tema sóbrio, tipografia consistente, estados selecionados/desabilitados, foco de teclado e redução de movimento. Menu principal recolhível abaixo de 1000 px; painel de camadas opcional durante desenho.
- Removidos um workspace e um rodapé vazios duplicados que consumiam espaço. Telemetria fica oculta nas duas ferramentas. A barra de desenho participa do layout, sem cobrir os painéis laterais.
- Métricas do esboço notificam a interface no mesmo intervalo limitado a 30 Hz utilizado pelo cálculo; isso não é uma medição de FPS nem um benchmark de desempenho.
- Evidência: build MAUI Debug com zero erros e 191 avisos; smoke do aplicativo, contratos JS e 135 asserções do núcleo aprovados. Novo `tests/tool-layout-app.cjs` verifica alcance dos controles por hit-test, seletores, menus, ausência de pontos adicionados ao alterar encaixes e espaço restante para o mapa em 1440, 760 e 390 px (altura 900 px).
- Capturas inspecionadas em `test-results/digitizing-app/{digitizing,measurement}-layout-{1440,760,390}.png`. Testes usam instância descartável e camada temporária, sem gravar dados do usuário.
- Pendências mantidas: gravação transacional/reabertura, edição nativa de vértices, construções restantes e benchmarks em bases grandes. O redesign e estes testes não demonstram superioridade ao ArcGIS/QGIS.

## Medições confirmadas — 03/10/2026

- Estados explícitos: aguardando pontos, edição, prévia com cursor e resultado confirmado. **Concluir medição / Enter** calcula apenas pontos marcados (mínimo dois para distância, três para área); contorno inválido ou erro de SRC impede confirmação e mantém a edição disponível.
- Resultado confirmado ignora movimentos/cliques no mapa até **Continuar medição**. Desfazer/refazer reabre a edição; limpar remove pontos, resultado e mensagens. Não há persistência de sessões nem histórico de resultados independentes nesta entrega.
- **Copiar resultado** usa um snapshot dos pontos marcados, exclui o cursor mesmo durante prévia e informa quantidade de pontos, método e SRC. Área inválida/insuficiente é identificada no texto; falha de cálculo impede copiar um resultado zerado enganoso.
- `tests/measurement-session-app.cjs` aprovado no MAUI após o smoke: cópia idêntica em duas posições do cursor, igualdade entre comprimento copiado e confirmado, conclusão por Enter, imutabilidade após movimento/clique, continuação, undo/redo, área válida, rejeição de contorno cruzado e limpeza. A captura do payload intercepta a API de clipboard durante o teste e restaura-a no encerramento.
- Contratos JS agora cobrem Enter na medição e bloqueio da repetição da tecla. Smoke e layout 1440/760/390 px aprovados novamente; build zero erros, 191 avisos preexistentes (incluindo alertas NuGet de dependências vulneráveis, ainda pendentes). Captura inspecionada: `test-results/digitizing-app/measurement-confirmed.png`.

## Entrada precisa integrada — 04/10/2026

- Removidas as duas janelas flutuantes COGO/F6. Coordenadas absolutas, vetor polar e restrição de distância agora ocupam uma seção da barra de trabalho, com abas, fechamento explícito e rolagem em janelas baixas. Campos permanecem abertos para inserção repetida; F6/Tab focam o campo correto após a renderização; Enter no formulário adiciona somente um controle, sem concluir o desenho.
- Distância polar e restrição recebem metros de grade e convertem para a unidade linear real do SRC projetado. SRC geográfico é rejeitado para essas operações planas com orientação para usar coordenadas, sem confundir graus com metros. Azimute é da grade, não geodésico. A distância não representa uma correção terreno/grade.
- Rejeição de distância não positiva/não finita, azimute inválido, alcance numérico, coincidência com último ponto e ausência de destino. Coordenadas mantêm valores digitados; métricas do esboço atualizam imediatamente. Mensagens não ocultam a contagem de pontos.
- `tests/precision-input-app.cjs` aprovado no MAUI: atalhos e foco, formulário via Enter, rejeição de repetição/negativo, duas projeções de 100 m com comprimento 200 m e área 5000 m², ativação/desativação de restrição, seleção de modo e hit-tests dos controles em 1440×900, 760×600 e 390×600. Capturas `test-results/digitizing-app/precision-{1440,760,390}.png`.
- Núcleo: 146 asserções aprovadas, incluindo metros/pés topográficos, normalização de azimute, valores inválidos, SRC geográfico e colapso de precisão. Smoke, layout e contratos JS aprovados. Build completo: zero erros, 189 avisos; dependências vulneráveis continuam pendentes.
- A eliminação de sobreposição nessa etapa cobria as antigas janelas de entrada precisa. A separação do viewport do mapa foi implementada na etapa seguinte; a auditoria dos demais menus e diálogos permanece necessária.

## Área exclusiva do mapa — 04/10/2026

- Mapa movido para a grade da área de trabalho, entre painéis laterais, fora da área de barras e rodapé. Em largura até 700 px, medição/atributos ocupam uma linha abaixo do mapa; o painel de camadas da vetorização, quando solicitado, também fica em linha separada. A tabela de atributos passa a ocupar espaço no fluxo do layout.
- Menu compacto ocupa espaço no cabeçalho expandido, sem cobrir a barra de desenho. Barras têm altura limitada e rolagem; o rodapé da vetorização reserva espaço para as métricas, evitando redimensionar o mapa a cada início de desenho.
- `dimensoesJanela` agora lê o tamanho interno do mapa. `ResizeObserver` comunica mudanças causadas por painéis, não só pela janela; movimento de prévia/cliques são ignorados durante a curta transição de tamanho até a apresentação de um frame compatível. Coordenadas de entrada descontam posição e borda do mapa.
- Testes aprovados no MAUI: smoke de desenho/renderização/snap/construções, sessão de medição e entrada precisa. Teste de layout verifica interseções entre retângulos de mapa/barras/painéis visíveis e alcançabilidade dos controles em 1440/760/390 px, incluindo painéis alternados e menu compacto expandido. Verifica também mapa dentro da janela em 760×600 e 390×600.
- Contratos JS aprovados: transformação de coordenadas em mapa deslocado e com borda, dimensões do elemento, notificação de resize sem mudança da janela, atalhos e fila de movimento. Capturas `test-results/digitizing-app/{measurement,digitizing}-layout-{1440,760,390}.png` inspecionadas. Build sem erros; avisos de dependências conhecidos permanecem.
- Limite: esta evidência cobre a área principal das duas ferramentas. Menus de contexto, dropdowns de desktop, diálogos secundários, gravação/reabertura e benchmarks de grandes bases ainda exigem auditoria; não é uma afirmação de ausência de sobreposição em todas as telas do aplicativo.

## Painéis automáticos e prévia de desenho — 04/10/2026

- A primeira camada adicionada expande Camadas; um clique que encontra feição expande Identificar, mesmo se o painel tiver sido recolhido.
- Em polígonos por vértices, o esboço pode ser concluído clicando no primeiro vértice dentro da tolerância em pixels; a instrução aparece após três pontos e não é gravado um vértice duplicado.
- A fila do cursor continua latest-only e alinhada ao `requestAnimationFrame`: enquanto move pede quadro interativo de menor custo; após 120 ms sem movimento pede o quadro final de qualidade normal.
- Verificação executada: build Debug sem erros, 146 asserções do núcleo, verificações de contrato e sintaxe JavaScript. Os testes Playwright de integração foram ampliados, mas não executados neste ambiente (dependência Playwright ausente); ainda é necessário medir FPS e latência numa instância real com datasets representativos. 60 FPS continua meta, não resultado confirmado.
