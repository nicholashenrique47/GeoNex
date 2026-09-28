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

- `dotnet run --project tests/DigitizingCore/DigitizingCore.csproj`: 39 contratos de cursor, escala, rotação, restrições e histórico.
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
