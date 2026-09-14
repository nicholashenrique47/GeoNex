# Qualidade dos mapas base em zoom alto / HiDPI

12/09/2026. Diagnóstico de código, sem inspeção visual do local relatado pelo usuário.

## Causas e correção

1. O cache raster identificava câmera/camada, mas não distinguia prévia e qualidade final. Uma imagem online reduzida a até 512 px durante o movimento podia satisfazer a requisição final na mesma câmera e acabar no cache global. `RasterRenderingPolicy.CacheKeyForQuality` agora separa as identidades. A correção também impede esse reaproveitamento indevido nos outros rasters, sem mudar seus algoritmos de leitura.
2. Mesmo no quadro final, os mapas online eram limitados a 1280/1600/2048 px pela disponibilidade de RAM. Isso reduzia a resolução antes da leitura GDAL, inclusive em telas HiDPI. Agora o quadro final segue as dimensões físicas solicitadas, respeitando o orçamento existente de pixels e a dimensão máxima de 8192. A prévia continua limitada a 512 px.

Os vetores, filtros de reamostragem, URLs, concorrência de rede e cache de tiles em disco não mudaram. A imagem final pode solicitar mais tiles para efetivamente obter o detalhe correspondente; não se trata de aumentar artificialmente a nitidez de uma prévia.

## Verificação e limites

Contratos aprovados: separação prévia/final na mesma câmera, Full HD com pouca RAM, 4K/HiDPI com RAM suficiente, orçamento sob pressão, ausência de ampliação desnecessária e regressões de raster. Builds Debug/Release verificadas; avisos de dependências preexistentes permanecem. Nenhuma alteração nas DLLs C++.

Não foi feita validação visual ao vivo nem consulta de disponibilidade de imagens em uma região específica. Os tetos configurados permanecem Google z20, ESRI z22 e OSM z19; não se presumiu que todo local possui dados nativos até esses níveis. Acima do detalhe disponível/configurado, ampliação não pode produzir informação nova. A hipótese de baixa resolução do provedor é independente dos dois defeitos corrigidos.

Após reiniciar o aplicativo atualizado: abrir o mesmo mapa/local, ampliar, aguardar o refinamento depois de parar o movimento e comparar. Não é necessário apagar o cache de tiles. Para reproduzir os contratos: `dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-basemap-contracts` e `--raster-contracts`.
