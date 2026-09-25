# Contrato de câmera precisa — implementação preparatória

Esta etapa prepara a correção dos pequenos movimentos em zoom alto. **A câmera
do aplicativo ainda usa o caminho anterior.** Não é uma correção entregue do
gesto, nem uma nova alegação de ganho de tempo.

O problema reproduzido é anterior: no centro local (-5408820,5; 2984029),
escala CSS 64, deslocar 4 pixels exige alterar o centro em 0,0625 unidade.
`SKPoint` mundial perde essa fração. Mudar apenas a prévia faria o desenho voltar
à posição antiga quando a pintura final chegasse.

## Implementado

- `MapLocalCoordinate` distingue coordenadas locais double das coordenadas
  mundiais (o eixo Y local é invertido).
- `MapCoordinateFrame.CreatePrecise/TryCreatePrecise` conserva o centro double.
  `LocalToPhysicalForOrigin` subtrai a origem próxima antes de converter a
  translação para a matriz float do Skia.
- Conversões precisas local↔CSS/físico permitem que desenho e ferramentas
  compartilhem o mesmo cálculo; não invertem a translação mundial já arredondada.
- `LocalViewportBoundsForOrigin` calcula o retângulo perto da origem e arredonda
  seus limites float para fora, evitando perder uma faixa fina da consulta.
- `RebasePrecisely` compõe o gesto em double. Ele é exclusivo de navegação sem
  rotação, como o cache global atual, e ainda não substitui `Rebase` no endpoint.
- `RasterPreviewMatrix` agora consulta `PreciseLocalCenter`. Nos frames antigos,
  esse valor é exatamente o centro float anterior convertido para double.

As propriedades legadas `LocalCenter`, matrizes mundiais e bounds mundiais
continuam aproximadas. **Criar um frame preciso e usar a matriz mundial antiga
não resolve a precisão**: o consumidor precisa usar a matriz relativa à origem
e as conversões novas. Os construtores antigos permanecem compatíveis.

## Evidência

`--precise-camera-contracts` passou com:

- 240 casos combinando Mercator/UTM e origem pequena, escalas 4/16/64/1000,
  DPI 0,5/1/1,25/2/4, rotações 0/17/90; pan fracionário preservado, ida/volta
  de picking e cobertura da consulta. Erro de coordenadas em CSS <0,002 pixel
  nesses casos, e <0,01 pixel na comparação entre matriz e conversão física.
- 240 composições de prévia com zoom relativo 0,5/1/2 e pan fracionário.
- 18 pinturas com fill/borda/fenda, DPI e rotação, incluindo layout de impressão
  fracionário: delta ≤1/255 contra o renderer existente operando perto de zero.
- rejeição de centros não finitos ou não representáveis pela interface legada.

Capturas reais do LOTES nos zooms 4, 16 e 64, respectivamente 1.538, 149 e 25
pontos: quatro deslocamentos por captura, comparados com a mesma geometria
no renderer próximo de zero. **12 frames completos, delta máximo zero.** Não
comparar o novo deslocamento correto com o frame antigo que ignorava o gesto.

Também passaram os contratos existentes de coordenadas, precisão de geometria,
alinhamento online, cache de paths/projeção e prévia HTTP em zoom alto.

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --precise-camera-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --precise-camera-capture "$env:TEMP/GeoNex-zoom-before-64/path-0.gpath" 3712 2312 128 0.015625
```

## Integração ainda necessária

1. `LocalMapServer`: ler/manter a câmera double; criar o frame preciso; utilizar
   a matriz/bounds relativos à origem do path na pintura final e na consulta.
2. Ativar `RebasePrecisely` no endpoint junto com a pintura final, verificando
   o ciclo gesto→frame final para que não haja salto de posição.
3. Alinhar raster offline e online pela mesma extensão precisa. Migrar caminhos
   categorizados, pontos, destaques e overlays ainda baseados em matrizes mundiais.
4. Migrar projeção/picking das ferramentas de `Home.razor` e conferir seleção,
   edição, medição e impressão. Não presumir que matrizes mundiais float ou
   listas de pontos float recuperem detalhe da fonte double automaticamente.
5. Repetir capturas reais e medição de CPU/RAM após essa integração, preservando
   os ganhos já entregues. SHP e índices originais permanecem somente leitura.
