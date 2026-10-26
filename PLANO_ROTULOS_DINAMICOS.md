# 🏷️ GeoNex: Plano Diretor de Engenharia Pesada — Motor de Rótulos Dinâmicos & Posicionamento Cartográfico (QGIS PAL / Maplex Standard)

> **Documento Oficial de Engenharia de Software & Algoritmos Cartográficos**  
> **Status:** 100% Concluído, Integrado e Validado com Sucesso (Fases 1 a 5)  
> **Alvo:** Equivalência e Superioridade Técnica em Relação ao QGIS PAL (Point-Line-Area Labeling) e ArcGIS Pro Maplex Label Engine  
> **Disciplina:** Zero-Allocation no loop SkiaSharp, SIMD/AVX2, Cache de Métricas de Fonte e Geometria Vetorial Concorrente

---

## 🎯 1. Visão Executiva & Objetivos Cartográficos

O subsistema de rotulagem do **GeoNex** transcendeu o modelo de "centroide + bounding box" e implementou um **Motor Cartográfico de Rótulos Dinâmicos (Dynamic Label Placement & Conflict Engine)** com rigor matemático e alto desempenho gráfico.

### Metas Estruturais Cumpridas:
1. **Posicionamento Conforme a Geometria (Geometry-Aware Placement):**
   - **Polígonos:** Algoritmo do **Polo de Inacessibilidade** (*Polylabel / Maximum Inscribed Circle*) para garantir que o rótulo seja posicionado na área visual mais aberta do polígono, evitando que caia fora em geometrias côncavas (ferraduras, lagos sinuosos ou arquipélagos).
   - **Linhas e Vias:** Orientação paralela aos segmentos com suavização angular, evitando que o texto fique de cabeça para baixo (*Auto-Flip angle guard* na faixa $90^\circ < \theta < 270^\circ$).
   - **Pontos:** Posicionamento em 8 quadrantes cartesianos com vetor de preferência e prioridade ponderada.
2. **Resolução de Conflitos e Anti-Colisão Determinística:**
   - Detecção de intersecção com caixas delimitadoras de tela e grid espacial bitwise em $O(1)$ (`((long)cellX << 32) | (uint)cellY`).
   - Estratégia de tentativa com fallback ordenado (*Alternative Candidate Evaluation*): tenta 8 posições alternativas antes de suprimir o rótulo.
3. **Chamadas Cartográficas e Linhas Guia (*Callout & Leader Lines*):**
   - Quando um ponto ou feição densa sofre deslocamento para resolver colisão, geração automática de linha conector (*leader line*) com ponta indicadora.
4. **Tipografia Avançada & Halos Cartográficos:**
   - Suporte a famílias tipográficas (*Segoe UI, Arial, Trebuchet MS, Consolas, Georgia, Times New Roman, Impact*), estilos (Normal, Negrito, Itálico), maiúsculas/minúsculas (*All Caps / Title Case*), opacidade independente de preenchimento e halo.
   - Halo suave antirruído renderizado via `SKStrokeJoin.Round` com buffer tipográfico.
5. **Filtragem de Escala & Descarte Inteligente (*Scale & Decluttering*):**
   - Denominador de escala mínimo e máximo (ex: visível apenas entre 1:5.000 e 1:50.000).
   - Limiar de área mínima em tela (suprime rótulos se o polígono projetado for menor que $N$ pixels no viewport).
6. **Interface Gráfica Profissional (Zero Emojis, Padrão ArcGIS Pro / QGIS):**
   - Organização modular em sub-abas dedicadas: **Texto & Tipografia**, **Posicionamento & Quadrantes** e **Visibilidade & Filtros**.
   - Live Preview tipográfico com renderização vetorial instantânea do texto com halo e cor de contraste.

---

## 📐 2. Algoritmos Matemáticos & Arquitetura de Posicionamento

```
                                 FLUXO DO MOTOR DE RÓTULOS (PER-FRAME)
                                 
   +---------------------------------------------------------------------------------+
   | 1. QUERY ESPACIAL: Feições candidatas no Viewport via NativeShapeSpatialIndex   |
   +---------------------------------------------------------------------------------+
                                           │
                                           ▼
   +---------------------------------------------------------------------------------+
   | 2. FILTRAGEM DE ESCALA: Descarte por Escala Min/Max e Tamanho Mínimo em Pixels  |
   +---------------------------------------------------------------------------------+
                                           │
                                           ▼
   +---------------------------------------------------------------------------------+
   | 3. CÁLCULO DE ÂNCORA ESPACIAL                                                   |
   |    - Ponto: Coordenada exata + Deslocamento por Quadrante                       |
   |    - Polígono: Polo de Inacessibilidade (Polylabel QuadTree)                     |
   |    - Linha: Segmento de Maior Comprimento / Vetor Médio com Auto-Flip           |
   +---------------------------------------------------------------------------------+
                                           │
                                           ▼
   +---------------------------------------------------------------------------------+
   | 4. GERAÇÃO DE CANDIDATOS ALTERNATIVOS EM 8 QUADRANTES CARTESIANOS               |
   +---------------------------------------------------------------------------------+
                                           │
                                           ▼
   +---------------------------------------------------------------------------------+
   | 5. MOTOR DE COLISÃO BITWISE: Rejeição ou Alocação no Grid Espacial de Tela O(1)  |
   +---------------------------------------------------------------------------------+
                                           │
                                           ▼
   +---------------------------------------------------------------------------------+
   | 6. RENDERIZAÇÃO SKIASHARP: Halo Round -> Texto Principal -> Leader Lines         |
   +---------------------------------------------------------------------------------+
```

---

### 2.1. Algoritmo do Polo de Inacessibilidade (*Polylabel*) para Polígonos

O centroide de um polígono frequentemente cai fora de geometrias em formato de "C", "L", atóis ou ilhas. O **Polylabel** utiliza uma QuadTree celular hierárquica para encontrar o ponto interno com maior distância de borda:

$$\text{Polo} = \arg\max_{P \in \text{Polígono}} \min_{e \in \text{Bordas}} \text{dist}(P, e)$$

* **Fast-Path:** Se o centroide já estiver no interior do polígono com folga, aproveita-o em $O(1)$.
* **Refinamento:** Amostragem por grade QuadTree iterativa para encontrar a célula mais profunda no interior da geometria.

---

### 2.2. Sistema de Posicionamento em 8 Quadrantes para Pontos

Para camadas pontuais, o motor gera até 8 candidatos espaciais ordenados por prioridade cartográfica:

```
          [ Top-Left (4) ]     [ Top (2) ]     [ Top-Right (1 - Preferencial) ]
                                   ▲
          [ Left (6)     ] ─── ( PONTO ) ───   [ Right (3)                    ]
                                   ▼
          [ Bottom-Left(8)]   [ Bottom(7)]     [ Bottom-Right (5)             ]
```

1. **Top-Right (1):** Padrão cartográfico universal por menor chance de obstrução e leitura natural ocidental.
2. **Fallback ordenado:** Top, Right, Top-Left, Bottom-Right, Left, Bottom, Bottom-Left.
3. **Deslocamento (*Offset*):** Raio do marcador de ponto + margem de respiro configurável.

---

### 2.3. Linhas e Vias: Orientação de Vetores & Proteção Contra Inversão (*Auto-Flip*)

Para vias, ferrovias e rios:
* **Cálculo Angular:**
  $$\theta = \text{atan2}(\Delta y, \Delta x)$$
* **Regra Cartográfica Anti-Cabeça-para-Baixo:**
  Se $\theta \in (90^\circ, 270^\circ)$, rotaciona $180^\circ$ para que o leitor nunca veja o nome da via invertido na tela.

---

## ⚡ 3. Motor Anti-Colisão & Estratégia de Alto Desempenho

### 3.1. Grid Espacial de Ocupação de Tela em $O(1)$
* Células uniformes de 56px a 64px no espaço de tela.
* Chave hash bitwise de 64 bits compacta: `key = ((long)cellX << 32) | (uint)cellY`.
* Processa mais de 10.000 avaliações espaciais em menos de 15ms.

### 3.2. Linhas de Chamada (*Callouts / Leader Lines*)
* Ativadas automaticamente sempre que um candidato alternativo deslocado é escolhido.
* Renderizadas com caneta SkiaSharp suave e círculo indicador de âncora sobre o ponto original da feição.

---

## 🎨 4. Design da Interface Gráfica (UI/UX) — Padrão Profissional (Zero Emojis)

A aba **Rótulos** do modal de propriedades da camada foi reestruturada em um painel técnico profissional:
* **Sub-Navegação Segmentada:**
  - `Tipografia & Estilo`: Seleção de atributo, família da fonte, tamanho, estilo (Negrito/Itálico), transformação de caixa (Normal, Maiúsculas, Minúsculas, Título), cores e opacidades de texto e halo.
  - `Posicionamento & Quadrantes`: Modos Superior Direito, 8 Quadrantes Dinâmicos, Sobre o Ponto e Fixo com offsets manuais X/Y. Checkbox de Polo de Inacessibilidade (*Polylabel*) para polígonos.
  - `Visibilidade & Filtros`: Anti-colisão determinística, ativação de linhas de chamada (Callouts), escalas mínima e máxima e tamanho mínimo de feição em pixels.
* **Live Preview Tipográfico ao Vivo:** Renderização instantânea com grade milimétrica cartográfica, texto configurado e halo com contraste visual.
* **Conformidade Estrita de Design:** Ausência total de emojis, ícones SVG refinados e tipografia enterprise.

---

## 🏗️ 5. Componentes de Código Entregues

| Componente | Arquivo de Destino | Status |
| :--- | :--- | :--- |
| **`LabelPlacementEngine.cs`** | `GeoNex/Services/LabelPlacementEngine.cs` | Concluído e Validado |
| **`LabelConflictGrid.cs`** | `GeoNex/Services/LabelConflictGrid.cs` | Concluído e Validado |
| **`EstiloCamada.cs`** | `GeoNex/Services/EstiloCamada.cs` | Concluído e Validado |
| **`LocalMapServer.cs`** | `GeoNex/Services/LocalMapServer.cs` | Concluído e Validado |
| **`Home.razor`** | `GeoNex/Components/Pages/Home.razor` | Concluído e Validado |
| **`SymbologyCore`** | `tests/SymbologyCore/Program.cs` | 194 asserções aprovadas |

---

## 🧪 6. Resultados dos Testes Automatizados

Todas as 5 suítes de testes foram executadas e validadas:
* **`SymbologyCore`:** 194 asserções aprovadas com 100% de sucesso (incluindo todos os 11 módulos cartográficos de classificação, hachuras, marcadores, SLD, Flannery, tipografia, Polylabel, 8 quadrantes, Auto-Flip e Grid O(1)).
* **`DigitizingCore`:** 179 asserções aprovadas.
* **`PrintScale`:** 60 asserções aprovadas.
* **`VectorRenderIndex`:** 26 asserções aprovadas.
* **`ProjectPersistence`:** Contratos de persistência e integridade SQLite aprovados.
* **Compilação Release do GeoNex:** 0 Erros.
