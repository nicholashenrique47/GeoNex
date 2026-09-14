# Pesquisa GDAL — 14/09/2026

Escopo: pesquisa, sem alterações no motor. Consultada a tag v3.12.1, correspondente aos pacotes declarados pelo GeoNex; não pressupõe que master tenha o mesmo comportamento. Não foi auditado o repositório inteiro.

## Achados confirmados e implicações

1. **Lotes HTTP:** `WMSHTTPFetchMulti` usa curl multi, limita conexões por MAXCONN e retorna após terminar o lote. Não publica tiles progressivamente para a interface. [Código](https://github.com/OSGeo/gdal/blob/v3.12.1/frmts/wms/gdalhttp.cpp#L129).
2. **Janela influencia lote:** `GDALWMSRasterBand::IRasterIO` registra a janela como hint; `IReadBlock` usa esse hint para agrupar blocos, com limite espacial. `ReadBlocks` chama o downloader antes de processar as respostas. [Código](https://github.com/OSGeo/gdal/blob/v3.12.1/frmts/wms/gdalwmsrasterband.cpp#L341).
3. **Hipótese prioritária no GeoNex:** `OnlineRasterFrameReader.ReadDirect` faz RasterIO sequencial em faixas de 128 linhas de saída. Isso pode fragmentar lotes e introduzir esperas sucessivas. Não está comprovado como causa da regressão; overviews, cache e o caminho dataset/banda influenciam o resultado. Faixas maiores também podem piorar cancelamento, memória e espera pelo lote mais lento.
4. **Escolha de resolução:** `GDALBandGetBestOverviewLevel2` considera redução solicitada e algoritmo. Na tag consultada, o limiar padrão é 1.0 para reamostragem não nearest e 1.2 para nearest. Não aumentar o limiar indiscriminadamente: pode selecionar overview mais grosseiro. [Código](https://github.com/OSGeo/gdal/blob/v3.12.1/gcore/rasterio.cpp#L3881).
5. **Cache WMS:** cache em arquivos, identificação por URL, expiração e limpeza. `MaxSize` não deve ser interpretado como teto rígido de RAM ou LRU equivalente ao QGIS; a documentação descreve remoção de arquivos expirados. `MaxConnections` controla concorrência; `AdviseRead` habilita downloads para cache, não apresentação progressiva. [Documentação no repositório](https://github.com/OSGeo/gdal/blob/v3.12.1/doc/source/drivers/raster/wms.rst).
6. **GeoTIFF grande:** NUM_THREADS permite decode paralelo quando a chamada RasterIO cruza múltiplos tiles/faixas, desde GDAL 3.6. É uma capacidade do driver; não significa que ALL_CPUS acelere downloads WMS. [Documentação](https://github.com/OSGeo/gdal/blob/v3.12.1/doc/source/drivers/raster/gtiff.rst).
7. **SHP grande:** índice espacial .qix acelera leitura com filtro espacial; também há leitura de .sbn/.sbx. Não acelera automaticamente desenho de todas as feições, nem beneficia o leitor C++ próprio se ele não consultar esse índice. [Documentação](https://github.com/OSGeo/gdal/blob/v3.12.1/doc/source/drivers/vector/shapefile.rst#spatial-and-attribute-indexing).

## Próximo teste, antes de implementar

- Fixture HTTP local com tiles determinísticos e latência controlada: comparar faixas de 128/256/512 linhas e janela completa, mesma câmera/DPI/resolução e limite de conexões.
- Separar cache frio de quente; medir tempo até imagem nítida, chamadas RasterIO, downloads/conexões simultâneas, RAM e atraso de cancelamento.
- Comparar pixels, alpha, bordas e janelas fracionárias em zoom alto; testar RGB/RGBA e preservar a última imagem válida em falhas.
- Só adotar leitura adaptativa se houver ganho comprovado sem regressões. Não aumentar limites dos provedores ou baixar áreas não visíveis para mascarar gargalos.
- Pesquisa futura: código do cache WMS, caminho dataset→banda e readers GeoJSON; não foram auditados em profundidade nesta rodada.

Não houve benchmark novo, mudança de dependências, promessa de equivalência QGIS/ArcGIS ou alteração do arquivo LOTES.
