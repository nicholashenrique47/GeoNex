# Enquadrar vetor com mapa base — 11/09/2026

Relato: vetor isolado aparecia; ao carregar mapa base antes, a importação não aproximava e o botão de camada falhava.

Causas identificadas no fluxo: importação redefinia a câmera para a extensão global (incluindo o mundo inteiro do mapa base); botão usava escala/centro do último frame, potencialmente desatualizados; reset JavaScript substituía a base da câmera por 0/0/1.

Correção: importação e botão usam `EnquadrarCamadaAsync`, com limites já reprojetados da camada em O(1), extensão atual compartilhada com o renderer e dimensões CSS visíveis. Sincronização cancela inércia, timers e decodes antigos, preserva a câmera calculada e devolve a sequência de requisições antigas para rejeitá-las no C#. A camada é inserida na hierarquia antes do enquadramento.

Testes executados: `--layer-camera-contracts`, `MapEngineContracts.js`, `--coordinate-contracts`, `--large-shp-contracts` e as 60 verificações de impressão. Cobertura de cálculo: mapa base + vetor, vetor isolado, extensão atual, três tamanhos de tela, três DPIs e margem de navegação. Teste JS cobre resposta atrasada do mapa base e o gesto seguinte ao enquadramento. Os testes usam limites projetados sintéticos; não substituem validação visual com o SHP do usuário.

Reproduzir: iniciar o aplicativo atualizado, adicionar OSM/ESRI, importar vetor com SRC válido e conferir aproximação automática. Afastar e usar “aproximar camada”; verificar se o gesto seguinte mantém a posição. Repetir sem mapa base. Limites vazios/degenerados ou SRC ausente/inválido não são corrigidos por esta etapa.
