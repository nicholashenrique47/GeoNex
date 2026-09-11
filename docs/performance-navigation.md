# Navegação de SHPs grandes — 11/09/2026

Implementado sobre `7ec0da62cb27d802fa1c42905f3df59d89ce0e4c`.

## Comportamento

- Câmera enviada com base na imagem apresentada, snapshot no URL e ID idempotente. Requisições lentas podem ser substituídas sem reaplicar pan/zoom; respostas antigas e decodes anteriores a um reset são rejeitados.
- Dois canvases alternam no ciclo de apresentação. Só o oculto é redimensionado/limpo. Margem de até 256 pixels físicos, reduzida em telas grandes; compositor não recebe margem.
- Cache usa matrizes completas, cobertura, DPI e revisão da cena. Pan na mesma escala pode reutilizar o frame final; zoom usa a imagem escalada apenas como prévia.
- Prévia de cache tem um único slot independente da fila pesada: não espera um DrawPath/GDAL antigo terminar. Recursos são mantidos por leases. Sem cobertura, o renderer normal produz novo conteúdo; raster sem cache não é omitido.
- Catálogo limitado a 262.144 geometrias distintas para camadas com pelo menos 100 mil registros. Confirma igualdade byte a byte depois do hash. Índice auxiliar somente para prévias de polígonos com simbologia única, linha sólida e sem reprojeção. FIDs, atributos, índice de seleção, dados, exportação e frame final mantêm todos os registros.
- PNG continua sem perdas, com filtro Sub e compressão nível 1 para reduzir latência local. Pode gerar mais bytes.

## Medição real

LOTES: 4.937.409 PolygonZ, 28.028.933 vértices, SHP 1.637.692.896 bytes e DBF 3.042.541.154 bytes. SRC declarado SIRGAS 2000 / UTM 22S. Catálogo: 154.266 conteúdos geométricos distintos; construção observada 576 ms.

Teste isolado, 1920×1080, DPI 1, um passe por cenário, sem WebView/raster, sem margem extra e sem medição de abertura completa. Não são percentis nem FPS do aplicativo. Soma de geometria + desenho + PNG das prévias: visão geral 43,7 ms; bairro 19,5 ms; rua 13,4 ms. Consulta do índice auxiliar: 0–0,7 ms.

Na MESMA imagem final, PNG antigo → novo: visão geral 58,4 → 9,6 ms; bairro 83,7 → 20,7 ms; rua 58,5 → 10,6 ms. Igualdade após decodificação, incluindo alpha, coberta por contrato.

O frame final completo continua caro: só geometria/desenho na visão geral levaram aproximadamente 5 s. Comparar essa qualidade com a prévia reduzida como se fossem trabalhos equivalentes seria incorreto. Remover duplicatas do frame final mudou bordas/opacidade no teste, por isso a redução ficou restrita à interação. Divisão do desenho final em blocos foi descartada por regressão de desempenho e diferenças de pixels.

## Reprodução e validação

```powershell
$env:GEONEX_VS = 'C:\Program Files\Microsoft Visual Studio\18\Insiders'
dotnet build GeoNex/GeoNex.csproj -c Release
dotnet build PerfTest/PerfTest.csproj -c Release
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --large-shp-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --large-shp-metrics 'CAMINHO/LOTES.shp'
node PerfTest/MapEngineContracts.js
dotnet run --project tests/PrintScale/PrintScale.csproj -c Release
```

PASS: contratos novos, coordenadas, render, raster, leases, telemetria, interop, índice, sintaxe JS e 60 verificações de impressão. MAUI Release final: 0 erros, 97 avisos na compilação incremental (rebuild anterior: 191 avisos). Não foi realizado smoke visual no WebView, medição de raster real ou comparação controlada com QGIS/ArcGIS.

Para conferir na UI: importar LOTES; navegar antes/depois do primeiro frame completo; alternar pan curto/longo, roda/touchpad e DPI; conferir transparência e seleção após assentar; acrescentar raster e verificar alinhamento. A margem é finita, e chamadas GDAL/Skia em execução não são interrompidas internamente. Fora da cobertura do cache, a prévia ainda pode esperar a fila pesada.

Mudanças locais, sem commit/push. Para reverter, revisar o diff contra o commit acima e restaurar apenas os arquivos desta alteração; não restaurar alterações posteriores indiscriminadamente.
