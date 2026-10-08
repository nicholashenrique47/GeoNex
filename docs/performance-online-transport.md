# Transporte de tiles — 21/09/2026

`OnlineTileTransport` mantém conexões HTTP abertas entre leituras GDAL e
compartilha pedidos simultâneos da mesma URL. Um adaptador HTTP em loopback
recebe somente coordenadas de fontes TMS previamente registradas. O limite
de downloads vale para todos os leitores: Google/ESRI 4, OSM 2.

O GDAL continua responsável por seleção de zoom, RasterIO e reprojeção.
Prévia e imagem final mantêm as dimensões e os pixels anteriores. O transporte
é usado na navegação online com cache persistente; fontes não suportadas
continuam no transporte nativo.

O cache usa a URL original e a mesma hierarquia MD5 do GDAL 3.12.1, preservando
o conteúdo existente entre reinícios. Respeita `no-store`, `no-cache`,
`max-age`, `Expires` e `Age`; gravação atômica, tamanho limitado e descarte
dos arquivos mais antigos. Respostas HTTP inválidas ou imagens incompletas
não entram no cache. Timeouts e concorrência permanecem limitados.

## Evidência

Contratos com servidor local: oito pedidos concorrentes geram um download;
novo transporte reaproveita o cache; GDAL lê arquivos gravados pelo adaptador
e vice-versa; máximo de duas conexões no fixture OSM; falhas, validade HTTP,
imagem truncada e encerramento com download pendente verificados.

O teste progressivo cobre 36 URLs distintas com 36 downloads. Uma região
independente chega enquanto um tile de borda permanece retido. A imagem final
é idêntica à leitura integral em Mercator, coordenadas geográficas e SIRGAS/UTM.
Endpoint de produção, estado parcial/final, zoom fracionário, DPI 2,
alinhamento, sessão, fila e navegação JavaScript também passaram.

Medições pontuais em Guaratuba (-48.5747, -25.8828), SIRGAS/UTM 22S,
1024 × 768, área de 1400 × 1050 m, diretório de cache local novo por execução:

| Caminho | Primeira prévia | Imagem final |
|---|---:|---:|
| Google, leitura integral com HTTP nativo | — | 12,123 s |
| Google, progressivo com transporte compartilhado | 0,513 s | 0,863 s |
| OSM, progressivo com transporte compartilhado | 0,698 s | 1,359 s |

O cronômetro encerra antes da exportação PNG. São amostras de rede, não um
benchmark controlado de ganho percentual: execução sequencial, possível
aquecimento do servidor remoto e compilação concorrente na amostra nativa.
Não medem FPS nem substituem validação visual no WebView com o projeto real.
Revisita progressiva medida: Google 0,362 s; OSM 0,323 s.

Cancelamento de câmera ainda espera a chamada GDAL ativa retornar; fechamento
do transporte cancela o HTTP. Uma região que depende de um tile lento precisa
aguardá-lo. Não há pré-download de áreas externas à leitura solicitada.

## Comparação local com QGIS — 08/10/2026

O fixture determinístico agora usa HTTP/1.1 persistente, como os clientes
reais. HTTP/1.0 fechava cada conexão e provocava `SocketException` local em
rodadas concorrentes; com HTTP/1.1, quatro conexões completaram sem falhas.

Ensaio de zoom 8, viewport 1600 × 900, DPI 1 e pan de 32 px, com a margem de
navegação de 225 px dos dois lados (imagem 2050 × 1350). O fixture fornece a
mesma imagem PNG de 256 × 256 em todas as coordenadas, portanto estes tempos
medem transporte e composição, não complexidade visual de uma ortofoto.

- GeoNex, GDAL 3.12.1, quatro workers e regiões de 512 px: 15 regiões; primeira
  prévia entre 150–279 ms e quadro refinado frio entre 245–364 ms nas rodadas.
  A mediana de seis quadros refinados após o primeiro foi ~100 ms; o quadro
  HTTP já armazenado respondeu em ~20 ms.
- QGIS 4.2.0, GDAL 3.13.1, mesma fonte e tamanho: render p50 47 ms, p95 137 ms
  em sete quadros; o primeiro levou 303 ms.
- O servidor recebeu 104 URLs únicas no ensaio GeoNex e 105 no ensaio QGIS.
  A contagem de tiles não explica a diferença de tempo; a etapa de refinamento
completo do GeoNex ainda ficou mais lenta que o render aquecido do QGIS.

### Validação de cache aquecido

Depois da validação completa, o transporte mantém os bytes do tile e o tipo em
uma LRU de até 32 MiB e 4096 entradas. Um hit aquecido usa os bytes em memória,
sem consultar ou reler o arquivo, nem decodificar o PNG outra vez. Quando a
entrada expira ou é removida da LRU, o cache em disco continua passando pela
validação completa antes do uso. A LRU é liberada ao fechar o transporte.

O teste de produção realizou três pares alternados, com e sem a LRU, no fixture
local. Os p50 de seis quadros finais foram 97,7/105,7 ms, 104,4/94,4 ms e
95,8/99,5 ms (sem/com LRU). Após remover uma consulta redundante ao sistema de
arquivos no caminho em memória, três execuções ficaram em 100,6, 105,1 e
90,7 ms. O fixture é pequeno e altamente compressível, e os pares variam; não
há ganho consistente de tempo total demonstrado. O contrato passou para hits
repetidos e pixels idênticos, cache expirado, tile corrompido com tamanho e
data preservados e reparo após reiniciar o transporte. A LRU elimina trabalho
de disco e decodificação para tiles aquecidos, mas a paridade de tempo com QGIS
continua pendente de medição em ortofoto online real.

Testei regiões de 256, 512 e 1024 px com a mesma fonte. Na primeira amostra
fria, 512 px foi o mais rápido (245 ms, contra 372 ms e 286 ms); a repetição
variou bastante, então não considero esse ganho universal. 256 px elevou o
trabalho para 54 regiões e 1024 px atrasou a primeira prévia. Mantive 512 px
como baseline. Esta fixture não comprova paridade visual nem desempenho em
rede pública; a otimização de tiles continua aberta.

### PNG raster e provedor real — 08/10/2026

No Google Satellite, centrado em Guaratuba, zoom 8, a imagem local de
2050 × 1350 (2,77 MP) gastava p50 de 80,5 ms na codificação PNG comprimida.
PNG sem DEFLATE levou 10,8 ms e gerou 8,32 MB, contra 3,95 MB. Os sete quadros
decodificados ficaram pixel a pixel idênticos. No endpoint local, o p50 do
quadro já renderizado caiu de 95,9 para 33,1 ms (−65%); o payload aumentou
4,36 MB. A política agora escolhe esse caminho para raster a partir de 2 MP
somente quando o orçamento adaptativo comporta os bytes e, se necessário, o
alpha. Sob pressão de memória, mantém a codificação comprimida.

No mesmo enquadramento do Google, MaxConnections 4 e 2 empataram no p50
`final_ready_ms` das seis amostras aquecidas: 135,2 e 135,7 ms, com caudas
variáveis. Mantive os limites do provedor. Na fixture local, forçar uma leitura
integral em vez do caminho progressivo foi mais lento: p50 final de ~147 ms
contra ~119 ms e sem prévia antecipada. A redução de CPU PNG é o ganho medido
que permaneceu.

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-transport-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-progressive-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-progressive-production GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll
# Diagnósticos manuais acessam os provedores e gravam PNGs em TEMP.
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-provider-diagnostics Google EPSG:31982 direct
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-provider-diagnostics Google EPSG:31982
```

Referências: [GDAL WMS](https://gdal.org/en/stable/drivers/raster/wms.html),
[HTTP do WMS 3.12.1](https://github.com/OSGeo/gdal/blob/v3.12.1/frmts/wms/gdalhttp.cpp),
[cache do WMS 3.12.1](https://github.com/OSGeo/gdal/blob/v3.12.1/frmts/wms/gdalwmscache.cpp).
