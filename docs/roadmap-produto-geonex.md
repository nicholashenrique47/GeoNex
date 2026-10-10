# Roadmap de produto do GeoNex

## Objetivo

Construir um SIG profissional que seja confiável para projetos reais e claramente melhor que as alternativas em fluxos específicos. O objetivo de superar ArcGIS Pro e QGIS é uma direção de produto, não uma afirmação sobre a capacidade atual. Cada vantagem deverá ser demonstrada em tarefas, dados e equipamentos definidos.

Este roadmap organiza o trabalho por dependências e critérios de conclusão. Não atribui datas sem estimativas de implementação. Os planos técnicos detalhados continuam nos documentos referenciados ao final.

## Estratégia

O GeoNex não deve competir inicialmente pela quantidade total de ferramentas dos dois produtos. Deve:

1. Ganhar confiança: projeto não pode perder estado, abrir parcialmente sem explicar, ou falhar silenciosamente.
2. Entregar resultados profissionais: mapas e dados precisam sair corretos, portáveis e prontos para uso.
3. Tornar tarefas repetitivas mais simples: análise reproduzível, operação em lote e fluxos de campo integrados.
4. Medir superioridade em nichos úteis, especialmente topografia, cadastro, engenharia e coleta GNSS/RTK no Brasil.

## Fases e ordem de execução

### Fase 0 — Confiabilidade do projeto e da sessão

**Prioridade imediata.** As falhas observadas ao abrir projetos e o estado recente da tela inicial tornam esta a base das demais entregas.

Entregas:

- Auditoria do ciclo de vida: novo, abrir, salvar, salvar como, fechar, trocar de projeto e sair do aplicativo.
- Salvamento resistente a interrupções, com gravação temporária, substituição segura e cópia recuperável da versão anterior.
- Estado de alterações não salvas claro; confirmação coerente ao trocar de projeto ou fechar.
- Recuperação de autosave após encerramento inesperado, com escolha explícita entre recuperar e abrir o último arquivo salvo.
- Validação do `.gnx` antes de modificar a sessão atual; erro de camada deve indicar fonte, causa provável e ação de reparo.
- Reparo de fontes ausentes em lote, preservando ordem, nomes, estilos, visibilidade e configurações das camadas.
- Migração versionada do formato de projeto e relatório legível do que foi convertido ou descartado.
- Teste manual de compatibilidade com projetos existentes, incluindo camadas locais, raster, serviços e conexões.

**Critério de saída:** abrir, salvar, fechar e reabrir um projeto mantém camadas, ordem, visibilidade, estilos, CRS, extensão e configurações; uma fonte ausente não apaga o restante nem impede reparo.

#### Estado da execução — 10/10/2026

- Já existiam gravação transacional SQLite, esquema versionado, validação de fontes antes de substituir o mapa, rollback para o projeto anterior quando a abertura falha e autosave periódico.
- Implementado neste ciclo: o salvamento explícito cria um snapshot SQLite consistente da versão anterior, verifica o arquivo antes de publicá-lo e o expõe como projeto de recuperação separado. O autosave periódico agora grava uma sessão `.autosave.gnx` separada, sem alterar o último salvamento manual. A tela inicial oferece recuperação explícita; ao recuperar, o arquivo principal recebe a sessão e a versão manual anterior vira ponto de restauração. Projetos sem alterações não criam snapshots redundantes.
- Ainda pendente nesta fase: estado de alterações não salvas confiável para todas as operações, reparar várias fontes de uma vez, apresentar migrações/erros em uma experiência de recuperação completa e concluir auditoria manual do ciclo abrir/salvar/fechar/sair e de compatibilidade dos projetos reais existentes.

### Fase 1 — Projetos portáteis e intercâmbio de dados

Entregas:

- Distinguir fontes referenciadas de recursos incorporados ao projeto.
- Criar um pacote portátil que reúna o `.gnx`, dados locais selecionados, estilos e metadados, com manifesto e verificação de integridade.
- Gerar um relatório de portabilidade antes de compartilhar: arquivos incluídos, conexões externas, fontes inacessíveis e tamanho estimado.
- Adotar caminhos relativos quando fizerem sentido e permitir relocalizar pastas mantendo a estrutura do projeto.
- Ampliar interoperabilidade com formatos abertos e preservar campos, tipos, nulos, geometria, CRS e codificação.
- Documentar claramente o que pode e o que não pode ser importado de projetos QGIS/ArcGIS; não prometer compatibilidade integral com formatos fechados.

O ArcGIS documenta pacotes que podem conter mapas e dados, enquanto os projetos QGIS registram configuração de camadas, estilos, visualização e layouts. O GeoNex deve tornar explícito se cada origem é copiada ou apenas referenciada. [ArcGIS project packages](https://pro.arcgis.com/en/pro-app/3.3/help/sharing/overview/project-package.htm) · [QGIS project files](https://docs.qgis.org/4.2/en/docs/user_manual/introduction/project_files.html)

**Critério de saída:** um pacote transferido para outra pasta ou computador abre com fontes incorporadas intactas e aponta com precisão qualquer dependência externa.

### Fase 2 — Cartografia e publicação profissional

Entregas:

- Exportação PDF vetorial quando as camadas permitirem, mantendo raster somente onde necessário.
- Layout com tamanho e orientação de página, margens, múltiplas páginas, vários quadros de mapa e controle de escala/extensão.
- Legenda ligada às camadas e à simbologia atual; barra de escala, norte, grade/coordenadas, rótulos, imagens, tabelas e formas.
- Modelos de layout reutilizáveis, alinhamento, guias, agrupamento e propriedades numéricas dos elementos.
- Exportação configurável para PDF, SVG e imagem, com DPI, fontes incorporadas quando viável e indicação de conteúdo rasterizado.
- Atlas/mapas em série por feição, com nomeação e exportação em lote.
- Pré-visualização final e avisos para página vazia, legenda desatualizada, escala ilegível ou fonte ausente.

QGIS documenta layouts com itens editáveis, múltiplas páginas, exportação e atlas; ArcGIS Pro organiza layouts e elementos de mapa no projeto. [QGIS Print Layout](https://docs.qgis.org/3.44/en/docs/user_manual/print_composer/overview_composer.html) · [ArcGIS Pro layouts](https://pro.arcgis.com/en/pro-app/latest/help/layouts/add-a-layout-to-your-project.htm)

**Critério de saída:** um mapa com vetores permanece vetorial no PDF quando suportado; impressão e exportação preservam escala, símbolos, texto e alinhamento na página.

### Fase 3 — Análise espacial reproduzível

Entregas:

- Caixa de ferramentas pesquisável, com parâmetros, unidades, CRS, validação de entrada, progresso, cancelamento e mensagens de erro úteis.
- Primeira coleção de operações priorizada por casos reais: buffer, recorte, interseção, união, dissolução, reprojeção, centróide, distância e validação de geometria.
- Saídas temporárias e persistentes com nome, formato e destino selecionáveis; opção explícita para adicionar o resultado ao mapa.
- Histórico com entradas, parâmetros, versões relevantes e caminhos das saídas; permitir repetir uma operação.
- Execução em lote com relatório por item e possibilidade de continuar quando uma entrada individual falhar.
- Modelador visual de fluxos; validação do modelo antes de executar e execução parcial quando os resultados anteriores forem reutilizáveis.
- Basear operações em GDAL/OGR, GEOS e PROJ disponíveis no produto, sem anunciar algoritmos que não estejam incluídos e validados.

O QGIS reúne algoritmos em provedores, oferece histórico, execução em lote e modelos de várias etapas. [QGIS Processing](https://docs.qgis.org/3.44/en/docs/user_manual/processing/intro.html) · [QGIS Model Designer](https://docs.qgis.org/3.44/en/docs/user_manual/processing/modeler.html)

**Critério de saída:** o usuário consegue repetir uma análise com os mesmos parâmetros, localizar a saída e entender falhas sem reconstruir o procedimento de memória.

### Fase 4 — Edição, medição e qualidade geométrica

Entregas:

- Gravação transacional de feições para pontos, linhas e polígonos, com atributos, esquema e CRS preservados.
- Desfazer/refazer consistente entre desenho, edição, salvar e reabrir.
- Seleção simples e múltipla; mover, inserir e remover vértices; mover, dividir, unir e transformar feições.
- Snap configurável por camada, vértice, segmento, interseção e tolerância em pixels, com indicação visual do tipo de captura.
- Regras topológicas configuráveis, validação em segundo plano e lista de erros navegável; distinguir erro, aviso e exceção autorizada.
- Medição geodésica com elipsoide, CRS, unidades e azimute declarados; congelar e exportar resultados.
- Conservar geometrias originais e evitar que simplificação visual altere dados editados ou resultados analíticos.

**Critério de saída:** edição e validação preservam dados originais até a confirmação da transação; regras inválidas são explicadas e podem ser corrigidas ou registradas como exceções.

### Fase 5 — Diferencial de campo: GNSS/RTK e trabalho offline

Entregas:

- Fluxo de conexão GNSS com estado da antena/receptor, qualidade da solução, precisão estimada, satélites, correções e idade da correção.
- Registro de trilhas e pontos com timestamp, CRS, atributos de coleta e proveniência da precisão.
- Formulários de coleta configuráveis, valores obrigatórios e validações específicas do projeto.
- Mapas e dados escolhidos para uso offline, com indicação de cobertura, atualização e espaço em disco.
- Fila de sincronização retomável, relatório de conflitos e decisão explícita para cada conflito; nunca substituir alterações sem informar.
- Integração ponta a ponta: preparar projeto no desktop, coletar em campo, sincronizar e revisar no projeto original.
- Exportações e relatórios adequados a levantamento, cadastro e engenharia no contexto brasileiro.

**Critério de saída:** uma coleta sem internet pode ser retomada e sincronizada sem perda silenciosa, com origem e qualidade de cada observação verificáveis.

### Fase 6 — Ecossistema, automação e colaboração

Começar somente depois de estabilizar o formato de projeto, as operações e os contratos de dados.

Entregas possíveis:

- Interface de extensão versionada e documentada para ferramentas, formatos e painéis.
- Automação por linha de comando e scripts com os mesmos parâmetros e resultados da interface.
- Conexões centralizadas com credenciais protegidas, sem gravar senhas em texto aberto no `.gnx`.
- Comparação de versões do projeto, trilha de alterações e colaboração com resolução explícita de conflitos.
- Catálogo de complementos com compatibilidade, assinatura/versão e política clara de suporte.

**Critério de saída:** extensões e automações falham isoladamente, não corrompem o projeto e podem ser reproduzidas em outra instalação compatível.

## Trabalho transversal: desempenho e qualidade

Aplicar em todas as fases:

- Construir uma coleção de projetos de referência pequenos, médios e grandes, com raster, vetor, reprojeção, serviços e geometrias complexas.
- Medir abertura, primeiro quadro útil, navegação, edição, salvamento, exportação, memória e recuperação após falha.
- Registrar equipamento, versão, dados, CRS, configurações e qualidade visual de cada medição.
- Comparar com QGIS e ArcGIS Pro na mesma máquina, nos mesmos dados e no mesmo resultado esperado quando as licenças e formatos permitirem.
- Manter invariantes de dados: reprojeção correta, buracos multipartes, precisão, atributos e ordem de desenho.
- Definir orçamento de memória e cancelamento responsivo; mostrar progresso real em operações longas.
- Só afirmar que o GeoNex é superior em uma tarefa depois de repetir a comparação e publicar os limites da medição.

O plano técnico de renderização vetorial já descreve provider, indexação, níveis de detalhe, cache e composição incremental. Ele complementa este roadmap; não é substituído por ele. [Arquitetura vetorial do GeoNex](arquitetura-vetorial-qgis-arcgis.md) · [Plano mestre do motor GIS](plano-mestre-motor-gis-qgis.md)

## Ordem recomendada do próximo ciclo

1. Fechar a Fase 0: confiabilidade de projetos e correções de interface que impeçam uso normal.
2. Definir e implementar o formato de pacote da Fase 1, mantendo compatibilidade com `.gnx` existente.
3. Modernizar a saída cartográfica da Fase 2, começando por PDF vetorial e layouts reproduzíveis.
4. Entregar um primeiro conjunto pequeno e sólido de análises da Fase 3 com histórico e execução em lote.
5. Avançar edição e topologia em sincronia com os contratos de gravação da Fase 4.
6. Tornar GNSS/RTK e offline o diferencial de produto, validando o fluxo com tarefas reais de campo.
7. Abrir extensões e colaboração só quando projetos e APIs estiverem estáveis.

## Regras para manter o roadmap útil

- Cada item precisa de responsável, escopo, estimativa e critério de aceite antes de entrar em uma versão.
- Separar recurso protótipo, recurso funcional e recurso pronto para produção.
- Não iniciar grandes módulos paralelos enquanto a Fase 0 tiver falhas que possam perder trabalho.
- Priorizar interoperabilidade e formatos abertos; não copiar código de QGIS sem avaliação de licença.
- Atualizar este documento quando uma fase mudar, apontando implementação e evidência em vez de marcar progresso apenas por intenção.

## Referências de capacidade

- [QGIS — project files](https://docs.qgis.org/4.2/en/docs/user_manual/introduction/project_files.html)
- [ArcGIS Pro — project packages](https://pro.arcgis.com/en/pro-app/3.3/help/sharing/overview/project-package.htm)
- [QGIS — processing framework](https://docs.qgis.org/3.44/en/docs/user_manual/processing/intro.html)
- [QGIS — model designer](https://docs.qgis.org/3.44/en/docs/user_manual/processing/modeler.html)
- [QGIS — print layouts](https://docs.qgis.org/3.44/en/docs/user_manual/print_composer/overview_composer.html)
- [ArcGIS Pro — layouts](https://pro.arcgis.com/en/pro-app/latest/help/layouts/add-a-layout-to-your-project.htm)
- [ArcGIS Pro — topology validation](https://pro.arcgis.com/en/pro-app/3.4/help/data/topologies/validating-a-topology.htm)
