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

- `dotnet run --project tests/DigitizingCore/DigitizingCore.csproj`: 85 contratos de cursor, escala, rotação, restrições, histórico, snap e medição.
- `node tests/digitizing-interaction.spec.cjs` com Playwright disponível em `NODE_PATH`: atalhos, foco, fila latest-only, quatro ferramentas, transformação visual e duplo clique.
- `dotnet build GeoNex/GeoNex.csproj -f net10.0-windows10.0.19041.0 -c Debug -p:GeoNexBuildNative=false --no-restore`: integração MAUI, zero erros.

## Próximos blocos necessários

1. Validação topológica e coleção independente para novas feições, sem FIDs/offsets sintéticos no índice de arquivos.
2. Gravação transacional e recarga para ponto, linha e polígono, preservando atributos e SRC.
3. Construções: retângulo, retângulo orientado, círculo, arco, anéis e multipartes.
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

## Medição por CRS — 02/10/2026

- `MapMeasurementService` usa as unidades lineares do CRS projetado e converte para metros; não assume que toda unidade de grade já seja metro.
- Em CRS geográfico, comprimento e área usam a geodésica do elipsoide do próprio CRS via GDAL/OGR.
- A interface informa o método utilizado, mostra erro de topologia da área sem apagar o esboço e exibe o azimute da última perna.
- A suíte cobre UTM métrica, pés topográficos, EPSG:4326, área e azimute; total atual: 85 asserções aprovadas.

## Encaixe por contorno e ponto médio — 02/10/2026

- `SnapSearch` percorre os contornos Skia sem achatar multipartes/anéis em uma linha. Considera o fechamento real de anéis e pontos isolados; não cria arestas entre partes nem usa controles Bézier como vértices.
- A consulta ignora camadas invisíveis, mantém os recursos nativos protegidos durante a leitura e evita copiar todos os vértices para arrays por feição. A busca continua linear nos vértices dos candidatos; ainda falta medir desempenho com datasets grandes.
- Encaixe em ponto médio disponível na vetorização e medição, inclusive nos segmentos do esboço corrente. Medição também permite encaixe em arestas. Modos independentes mantêm a tolerância em pixels e o resolvedor comum à prévia/clique.
- Encaixe em interseções agora consulta apenas segmentos próximos ao cursor (limite de 512 candidatos por quadro), calcula o cruzamento entre contornos independentes e mantém a prioridade vértice > ponto médio > aresta > interseção.
- O submenu de vetorização abre por clique/teclado usando `details`, sem depender de hover.
- Validação: 87 verificações do núcleo, contratos JS e build Debug sem erros. Na janela MAUI, ativar apenas o ponto médio atraiu a mira ao centro de um segmento apesar do cursor deslocado 8 px; captura `test-results/digitizing-app/midpoint-snap.png`. Testes anteriores de desenho, cancelamento e medição continuaram passando.
