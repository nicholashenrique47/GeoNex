# Agendamento de navegação e refinamento final

12/09/2026. Continuação após relato de demora nos polígonos em zoom alto.

## Diagnóstico e escopo

O renderer entrega mapa base e vetores no mesmo quadro, sob um gate de renderização. A correção anterior de qualidade exige efetivamente ler mais pixels/tiles na fase final. No código, a pausa para iniciar essa fase era de apenas 60–120 ms, enquanto a prévia era agendada para 280 ms: pequenas pausas da roda/trackpad podiam iniciar trabalho completo antes de qualquer prévia. Além disso, a fase final cancelava uma prévia lenta mesmo sem mudança de câmera.

Essa é uma causa plausível da regressão percebida, não um diagnóstico exclusivo do relato: não houve captura de tempos na sessão do usuário e ele não soube confirmar se a demora também ocorre sem mapa base. O custo do próprio desenho de polígonos complexos continua existindo.

## Implementação

- Prévia agendada em 120 ms durante gestos.
- Refinamento final após pausa adaptativa de 250–400 ms, conforme latência observada.
- Refinamento da mesma câmera aguarda a apresentação da prévia, evitando cancelar trabalho útil. Mudanças reais de câmera continuam podendo substituir requisições lentas.
- Prévias idênticas à requisição em andamento são descartadas.
- Ao retomar um gesto, a requisição final pendente da pausa anterior volta a ser prévia; a nova pausa agenda novamente a qualidade completa.
- Mantidos IDs, rebase da câmera, rejeição de imagens obsoletas, limites de tentativas e recuperação após falha.

Não altera vértices, algoritmos de desenho, antialiasing final, resolução final do satélite, cache por qualidade ou filtros. Trade-off explícito: a fase final espera uma pausa maior, enquanto o retorno visual por prévia ocorre antes. Não separa I/O online em um backend independente e não torna chamadas GDAL em andamento interrompíveis.

## Verificação

`node PerfTest/MapEngineContracts.js`: simulação de dez eventos separados por 100 ms produz apenas prévias durante o gesto e exatamente um pedido final após a pausa. Cobertos também: prévia lenta seguida de final, ausência de duplo zoom, deduplicação, retomada de gesto, câmera/DPI, decode obsoleto e retries. Contratos de mapas base e cache de paths aprovados. Debug/Release compilados; avisos preexistentes permanecem.

Não foi medida melhoria de FPS ou latência na UI real. Reiniciar o aplicativo atualizado e repetir o mesmo zoom é necessário para avaliar o relato. Se persistir, a próxima prioridade é medir separadamente I/O do mapa base, construção da geometria e DrawPath; a separação da apresentação do mapa base e dos vetores exige preservar a ordem de camadas e a composição de transparências.
