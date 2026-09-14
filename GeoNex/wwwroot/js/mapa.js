// mapa.js - Motor de Geovisualização WMS e Vetorial (GeoNex Desktop)
// =========================================================================
// 0. ESCUDO DO WINDOWS E TRAVA DO RATO
// =========================================================================
document.addEventListener('mousedown', function (e) {
    if (e.button === 1) e.preventDefault(); // Mata o símbolo de scroll do Windows
}, { passive: false });

window.mapEngine = window.mapEngine || {};

window.mapEngine.prenderRato = function (pointerId) {
    var mapa = document.getElementById('map-container');
    if (mapa && mapa.setPointerCapture) mapa.setPointerCapture(pointerId);
};

window.mapEngine.soltarRato = function (pointerId) {
    var mapa = document.getElementById('map-container');
    if (mapa && mapa.hasPointerCapture && mapa.hasPointerCapture(pointerId)) {
        mapa.releasePointerCapture(pointerId);
    }
};
// 1. Variáveis Globais (Memória do Mapa)
window.dotnetReferencia = null;
window.geonexMap = null;
window.osmLayer = null;
window.camadaDinamicaWMS = null;
window.camadasGeoNex = {}; // CORREÇÃO: Memória para guardar os vetores

// 2. INICIALIZAÇÃO DO MAPA
window.iniciarMapa = function (dotnetHelper) {
    window.dotnetReferencia = dotnetHelper;

    if (window.geonexMap !== null) {
        window.geonexMap.remove();
    }

    // CORREÇÃO DE PERFORMANCE: preferCanvas: true força o uso de aceleração de hardware para vetores pesados
    window.geonexMap = L.map('map', {
        zoomControl: false,
        preferCanvas: true
    }).setView([-25.8828, -48.5747], 15);
    window.ativarSensorRaycast();
    // SENSOR DE PERFORMANCE: Se afastar muito a câmara (Zoom < 16), esconde os textos
    // (Dentro da função iniciarMapa, substitua o bloco do mapa-distante por isto:)

    // SENSOR DE PERFORMANCE: Sempre que o utilizador arrastar o mapa ou der zoom, verifica os rótulos!
    // SENSOR DE PERFORMANCE INTELIGENTE (Com Debounce / Anti-Travamento)
    window.timerRotulos = null;
    window.geonexMap.on('moveend zoomend', function () {
        // Cancela o cálculo se o engenheiro ainda estiver a rodar o scroll do rato
        if (window.timerRotulos) clearTimeout(window.timerRotulos);

        // Só manda calcular os textos 300 milissegundos DEPOIS de o mapa parar completamente
        window.timerRotulos = setTimeout(function () {
            if (typeof window.gerenciarRotulosDinamicamente === "function") {
                window.gerenciarRotulosDinamicamente();
            }
        }, 300);
    });
    // Força a checagem no arranque
    if (window.geonexMap.getZoom() < 16) document.getElementById('map').classList.add('mapa-distante');
    L.control.zoom({ position: 'topright' }).addTo(window.geonexMap);

    window.osmLayer = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 21,
        attribution: '© OpenStreetMap | GeoNex Systems'
    }).addTo(window.geonexMap);

    // TIER 1: DOUBLE BUFFERING (Cinematic Prediction)
    // Em vez de 1 overlay que pisca, usamos 2 que alternam opacidade.
    var cssRaster = document.createElement('style');
    cssRaster.type = 'text/css';
    cssRaster.innerHTML = '.smooth-transition { transition: opacity 0.15s ease-in-out; }';
    document.getElementsByTagName('head')[0].appendChild(cssRaster);

    window.bufferFront = L.imageOverlay('', [[0, 0], [0, 0]], {
        opacity: 1.0, interactive: true, className: 'geonex-raster-layer smooth-transition'
    });
    window.bufferBack = L.imageOverlay('', [[0, 0], [0, 0]], {
        opacity: 0.0, interactive: true, className: 'geonex-raster-layer smooth-transition'
    });
    
    // Adicionamos ambos a um LayerGroup para manter compatibilidade com o resto do sistema
    window.camadaDinamicaWMS = L.layerGroup([window.bufferBack, window.bufferFront]).addTo(window.geonexMap);
    window.activeBuffer = window.bufferFront;

    window.geonexMap.on('mousemove', function (e) {
        if (window.dotnetReferencia) {
            window.dotnetReferencia.invokeMethodAsync('AtualizarCoordenadas', e.latlng.lat, e.latlng.lng);
        }
    });
    
    // NOVO: DELEGAÇÃO DE HIT-TESTING AO C#
    window.geonexMap.on('click', function(e) {
        if (window.dotnetReferencia && !window.ferramentaVerticesAtiva && !window.modoMedicao && window.ferramentaDeAquisicao === 'NENHUMA') {
            window.dotnetReferencia.invokeMethodAsync('HitTestNativo', e.latlng.lat, e.latlng.lng)
                .catch(erro => console.warn("HitTest não retornou feição: " + erro));
        }
    });

    window.geonexMap.on('moveend', function () {
        if (window.dotnetReferencia) {
            var bounds = window.geonexMap.getBounds();
            var size = window.geonexMap.getSize();

            var dpr = window.devicePixelRatio || 1;
            window.dotnetReferencia.invokeMethodAsync('SolicitarRecorteAltaResolucao',
                bounds.getSouth(), bounds.getWest(), bounds.getNorth(), bounds.getEast(),
                Math.round(size.x * dpr), Math.round(size.y * dpr)
            );
        }
    });

    window.addEventListener('resize', function () {
        if (window.geonexMap) window.geonexMap.invalidateSize();
    });
};

// 3. FUNÇÕES RASTER (WMS)
window.adicionarRasterAoMapa = function (base64Image, minLat, minLng, maxLat, maxLng, nomeCamada) {
    if (window.geonexMap) {
        var limites = [[minLat, minLng], [maxLat, maxLng]];
        var url = 'data:image/webp;base64,' + base64Image;
        
        window.bufferFront.setBounds(limites);
        window.bufferFront.setUrl(url);
        window.bufferFront.setOpacity(1.0);
        window.bufferBack.setOpacity(0.0);
        window.activeBuffer = window.bufferFront;
        
        window.geonexMap.flyToBounds(limites, { padding: [50, 50], duration: 2.0 });
    }
};

window.atualizarImagemNoMapa = function (base64Image, minLat, minLng, maxLat, maxLng) {
    if (window.camadaDinamicaWMS) {
        var limites = [[minLat, minLng], [maxLat, maxLng]];
        var url = 'data:image/webp;base64,' + base64Image;
        
        // Determina quem é o buffer invisível
        var inactiveBuffer = (window.activeBuffer === window.bufferFront) ? window.bufferBack : window.bufferFront;
        
        // A MAGIA CINEMÁTICA: Pré-carrega a nova imagem antes de a desenhar
        var tempImg = new Image();
        tempImg.onload = function() {
            inactiveBuffer.setBounds(limites);
            inactiveBuffer.setUrl(url);
            
            // Swap: Esconde o velho, revela o novo (sem piscar!)
            inactiveBuffer.setOpacity(1.0);
            window.activeBuffer.setOpacity(0.0);
            
            window.activeBuffer = inactiveBuffer;
        };
        tempImg.src = url;
    }
};

// 4. MOTOR DE VETORES
window.adicionarVetorAoMapa = function (geoJsonString, nomeCamada) {
    if (window.geonexMap) {
        // [OTIMIZAÇÃO EXTREMA]: O Leaflet já NÃO recebe os vetores.
        // Toda a renderização é feita no backend (SkiaSharp) e o Hit-Testing (cliques) 
        // agora é delegado à R-Tree do C#.
        // Removemos o L.geoJSON para não travar o navegador com milhares de nós no DOM.
        
        // Criamos apenas uma layer de grupo vazia para compatibilidade do toggle de visibilidade.
        var camadaFantasma = L.featureGroup().addTo(window.geonexMap);
        window.camadasGeoNex[nomeCamada] = camadaFantasma;
    }
};

// Função auxiliar: varre todas as camadas vetoriais e devolve a cor azul padrão
window.resetarEstilosVetores = function () {
    Object.values(window.camadasGeoNex).forEach(layerGroup => {
        if (layerGroup && typeof layerGroup.setStyle === 'function') {
            layerGroup.setStyle({ color: "#0ea5e9", weight: 1, fillOpacity: 0.3 });
        }
    });
};
// 5. MOTOR DE VISIBILIDADE (Checkboxes do Menu Lateral)
window.alternarVisibilidadeCamada = function (nomeCamada, visivel) {
    var camadaAlvo;

    if (nomeCamada === "OpenStreetMap") {
        camadaAlvo = window.osmLayer;
    } else if (nomeCamada.includes(".tif") || nomeCamada.includes(".ecw") || nomeCamada.includes("Ortofoto")) {
        camadaAlvo = window.camadaDinamicaWMS;
    } else {
        camadaAlvo = window.camadasGeoNex[nomeCamada]; // Pega as camadas vetoriais
    }

    if (camadaAlvo && window.geonexMap) {
        if (visivel) {
            window.geonexMap.addLayer(camadaAlvo);
        } else {
            window.geonexMap.removeLayer(camadaAlvo);
        }
    }
};
window.habilitarEdicaoGeometria = function (nomeCamada) {
    var camada = window.camadasGeoNex[nomeCamada];
    if (camada) {
        camada.eachLayer(function (l) {
            if (l.enableEdit) {
                l.enableEdit(); // Ativa os pontos de controle (alças) em cada vértice
            }
        });

        // Evento que avisa o C# quando um vértice terminou de ser movido
        window.geonexMap.on('editable:drawing:end editable:editing:end', function (e) {
            var novaGeometria = JSON.stringify(e.layer.toGeoJSON().geometry);
            var idFeicao = e.layer.feature.properties.ID;

            window.dotnetReferencia.invokeMethodAsync('SalvarNovaGeometria', idFeicao, novaGeometria);
        });
    }
    
};
// Motor de Simbologia
window.alterarEstiloCamada = function (nomeCamada, corPreenchimento, corBorda, espessura, opacidade, tamanhoPonto, tipoLinha) {
    var camadaAlvo = window.camadasGeoNex[nomeCamada];
    console.log("alterarEstiloCamada INICIO: " + nomeCamada, corPreenchimento, corBorda, espessura, opacidade, tamanhoPonto, tipoLinha);

    if (camadaAlvo) {
        var dashArray = null;
        if (tipoLinha === "Dash") dashArray = "5, 5";
        else if (tipoLinha === "Dot") dashArray = "1, 5";
        else if (tipoLinha === "DashDot") dashArray = "5, 5, 1, 5";

        // 1. O Leaflet agora atua 100% como uma camada "Fantasma" apenas para capturar cliques (Hitbox).
        // Toda a renderização visual pertence ao SkiaSharp. Para não corromper as cores da Placa Gráfica,
        // o Leaflet é tornado totalmente invisível (opacity 0).
        if (typeof camadaAlvo.eachLayer === 'function') {
            camadaAlvo.eachLayer(function(l) {
                if (typeof l.setStyle === 'function') {
                    l.setStyle({
                        fillColor: 'transparent',
                        color: 'transparent',
                        fillOpacity: 0,
                        opacity: 0,
                        weight: parseFloat(espessura) > 5 ? parseFloat(espessura) : 5, // Hitbox generosa para cliques
                        dashArray: null
                    });
                }
            });
        } else if (typeof camadaAlvo.setStyle === 'function') {
            camadaAlvo.setStyle({
                fillColor: 'transparent',
                color: 'transparent',
                fillOpacity: 0,
                opacity: 0,
                weight: parseFloat(espessura) > 5 ? parseFloat(espessura) : 5,
                dashArray: null
            });
        }
        
        // 2. A propriedade 'radius' não é um Path Option padrão e não é propagada. 
        // Temos que descer na árvore (útil para MultiPoints que são LayerGroups) e injetar o setRadius()
        var contagemPontos = 0;
        function propagarRaio(layer) {
            if (typeof layer.setRadius === 'function') {
                layer.setRadius(parseFloat(tamanhoPonto));
                contagemPontos++;
            }
            if (typeof layer.eachLayer === 'function') {
                layer.eachLayer(propagarRaio);
            }
        }
        
        propagarRaio(camadaAlvo);
        console.log("Pontos redimensionados para " + tamanhoPonto + ": " + contagemPontos);
        return true;
    }
    console.log("Camada alvo não encontrada!");
    return false;
};
// Motor de Rótulos Profissional (Fundo Transparente e Contorno)
// Motor de Rótulos Profissional (Fundo Transparente e Contorno)
// =======================================================
// MOTOR DE RÓTULOS DE ALTA PERFORMANCE (Viewport Culling)
// =======================================================
window.aplicarRotulosCamada = function (nomeCamada, coluna, tamanhoFonte, corHex) {
    var camadaAlvo = window.camadasGeoNex[nomeCamada];
    if (!camadaAlvo) return false;

    // 1. Limpa os rótulos antigos (Descarrega a RAM imediatamente)
    camadaAlvo.eachLayer(function (layer) {
        if (layer.getTooltip()) layer.unbindTooltip();
    });

    // 2. Se mandou limpar o rótulo ("Nenhum Rótulo"), para por aqui
    if (!coluna || coluna === "") {
        camadaAlvo.geonexLabelConfig = null;
        return true;
    }

    // 3. Cria e injeta o CSS profissional do Rótulo
    var nomeClasseCss = 'rotulo-geo-' + nomeCamada.replace(/[^a-zA-Z0-9]/g, '');
    var styleId = 'style-' + nomeClasseCss;
    var oldStyle = document.getElementById(styleId);
    if (oldStyle) oldStyle.remove();

    var css = `
        .${nomeClasseCss} {
            background: transparent !important;
            border: none !important;
            box-shadow: none !important;
            color: ${corHex} !important;
            font-size: ${tamanhoFonte}px !important;
            font-weight: bold;
            font-family: 'Segoe UI', Arial, sans-serif;
            text-align: center;
            padding: 0 !important;
            text-shadow: -1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000 !important;
        }
        .${nomeClasseCss}::before, .${nomeClasseCss}::after { display: none !important; } 
    `;
    var style = document.createElement('style');
    style.id = styleId;
    style.innerHTML = css;
    document.head.appendChild(style);

    // 4. Salva a "Receita" do rótulo na memória, mas NÃO GERA OS TEXTOS AINDA
    camadaAlvo.geonexLabelConfig = {
        coluna: coluna,
        className: nomeClasseCss
    };

    // 5. Acorda o Gerente de Performance para desenhar a primeira vez
    window.gerenciarRotulosDinamicamente();

    return true;
};

// =======================================================
// O GERENTE DE TELA (O verdadeiro herói da performance)
// =======================================================
// =======================================================
// O GERENTE DE TELA (O verdadeiro herói da performance)
// =======================================================
// =======================================================
// O GERENTE DE TELA (Motor Blindado contra Travamentos)
// =======================================================
window.gerenciarRotulosDinamicamente = function () {
    let mapa = obterMapaLeaflet();
    if (!mapa) return;

    let zoomAtual = mapa.getZoom();
    let limitesTela = mapa.getBounds();

    Object.keys(window.camadasGeoNex).forEach(function (nomeCamada) {
        let camadaAlvo = window.camadasGeoNex[nomeCamada];

        if (!camadaAlvo || !camadaAlvo.geonexLabelConfig) return;

        let config = camadaAlvo.geonexLabelConfig;
        let rotulosDesenhados = 0;
        let limiteSeguranca = 900; // MÁXIMO DE TEXTOS NA TELA (Disjuntor de RAM)

        // REGRA 1: Zoom muito distante -> Apaga tudo e sai imediatamente
        if (zoomAtual < 16) {
            camadaAlvo.eachLayer(function (layer) {
                if (layer.getTooltip()) layer.unbindTooltip();
            });
            console.log(`[GEONEX] Zoom ${zoomAtual}. Textos ocultos para performance.`);
            return;
        }

        // REGRA 2: Processamento com disjuntor de segurança
        camadaAlvo.eachLayer(function (layer) {
            let layerBounds = typeof layer.getBounds === 'function' ? layer.getBounds() : L.latLngBounds([layer.getLatLng()]);

            // Se o lote está dentro da tela visível do monitor
            if (limitesTela.intersects(layerBounds)) {

                // Se já chegámos a 400 textos na tela, destruímos o resto para não travar
                if (rotulosDesenhados >= limiteSeguranca) {
                    if (layer.getTooltip()) layer.unbindTooltip();
                    return;
                }

                let valorAtributo = layer.feature && layer.feature.properties ? layer.feature.properties[config.coluna] : null;

                if (valorAtributo !== null && valorAtributo !== undefined && String(valorAtributo).trim() !== "") {
                    if (!layer.getTooltip()) {
                        let centroExato = layerBounds.getCenter();
                        layer.bindTooltip(String(valorAtributo), {
                            permanent: true,
                            direction: 'center',
                            className: config.className,
                            interactive: false
                        }).openTooltip(centroExato);
                    }
                    rotulosDesenhados++;
                }
            } else {
                // Saiu da tela, destrói!
                if (layer.getTooltip()) layer.unbindTooltip();
            }
        });

        console.log(`[GEONEX] ${nomeCamada} | Zoom: ${zoomAtual} | Lotes Desenhados: ${rotulosDesenhados}`);
    });
};
// Navega a câmera para abraçar exatamente a extensão da camada
// O antigo zoomParaCamada chamava Leaflet. Agora não faz nada no JS,
// pois o C# tratará de calcular e definir a câmera (MapRenderingService).
// Para compatibilidade, mantemos a função vazia.
window.zoomParaCamada = function (nomeCamada) {
    return true;
};

// Destruição segura (Prevenção de Memory Leaks)
window.removerCamadaDoMapa = function (nomeCamada) {
    var camada = window.camadasGeoNex[nomeCamada];
    if (camada) {
        // 1. Remove da tela (Processamento Gráfico)
        if (camada.remove) {
            camada.remove();
        }
        // 2. Apaga da memória RAM (Garbage Collection)
        delete window.camadasGeoNex[nomeCamada];
        return true;
    }
    return false;
};
// Motor de Hierarquia Visual (Z-Index / Render Order)
window.atualizarOrdemRenderizacao = function (listaOrdenadaNomes) {
    // O C# envia a lista onde o índice 0 é o "Topo" (primeiro da lista na tela).
    // Para o Leaflet empilhar certo, varremos de baixo para cima,
    // trazendo cada camada para a frente da anterior.
    var total = listaOrdenadaNomes.length;
    for (var i = total - 1; i >= 0; i--) {
        var nome = listaOrdenadaNomes[i];
        var camada = window.camadasGeoNex[nome];

        if (camada) {
            // bringToFront() funciona nativamente para Vetores e Imagens (Rasters)
            if (typeof camada.bringToFront === 'function') {
                camada.bringToFront();
            } else if (camada.setZIndex) {
                // Fallback para TileLayers base
                camada.setZIndex(400 + (total - i));
            }
        }
    }
    return true;
};
// VARIÁVEIS GLOBAIS DE DESENHO
var modoDesenhoAtivo = false;
var pontosPoligono = [];
var linhaGuia = null;
var poligonoRascunho = null;
var camadaDestinoDesenho = null;

// ---> O SEGREDO ESTÁ AQUI: Esta função caça o objeto real do mapa <---
// ---> O SEGREDO ESTÁ AQUI: Esta função caça o objeto real do mapa <---
function obterMapaLeaflet() {
    // O seu código já guarda o mapa na memória com o nome window.geonexMap!
    return window.geonexMap;
}

// 1. INICIA A FERRAMENTA DE DESENHO MANUAL
window.iniciarDesenhoPoligono = function (nomeCamada) {
    let mapa = obterMapaLeaflet();
    if (!mapa) {
        alert("GEONEX ERRO: Variável do mapa Leaflet não encontrada no JavaScript.");
        return false;
    }

    modoDesenhoAtivo = true;
    pontosPoligono = [];
    camadaDestinoDesenho = nomeCamada;

    // Muda o cursor para "Mira"
    document.getElementById('map').style.cursor = 'crosshair';

    // Se a camada não existir, cria-a e anexa ao mapa real
    if (!window.camadasGeoNex[nomeCamada]) {
        window.camadasGeoNex[nomeCamada] = L.featureGroup().addTo(mapa);
    }

    // Liga os sensores
    mapa.on('click', adicionarVertice);
    mapa.on('mousemove', desenharLinhaGuia);
    mapa.on('contextmenu', fecharPoligono);

    return true;
};

function adicionarVertice(e) {
    if (!modoDesenhoAtivo) return;
    let mapa = obterMapaLeaflet();
    pontosPoligono.push(e.latlng);

    if (poligonoRascunho) mapa.removeLayer(poligonoRascunho);
    poligonoRascunho = L.polygon(pontosPoligono, { color: '#f59e0b', dashArray: '5, 5', fillOpacity: 0.2 }).addTo(mapa);
}

function desenharLinhaGuia(e) {
    if (!modoDesenhoAtivo || pontosPoligono.length === 0) return;
    let mapa = obterMapaLeaflet();

    if (linhaGuia) mapa.removeLayer(linhaGuia);
    let pontosGuia = [pontosPoligono[pontosPoligono.length - 1], e.latlng];
    linhaGuia = L.polyline(pontosGuia, { color: '#f59e0b', dashArray: '5, 5', weight: 2 }).addTo(mapa);
}

function fecharPoligono(e) {
    if (!modoDesenhoAtivo || pontosPoligono.length < 3) return; // Exige no mínimo 3 pontos
    let mapa = obterMapaLeaflet();

    mapa.removeLayer(linhaGuia);
    mapa.removeLayer(poligonoRascunho);

    let poligonoFinal = L.polygon(pontosPoligono, { color: '#0ea5e9', weight: 2, fillColor: '#38bdf8', fillOpacity: 0.4 });
    poligonoFinal.feature = { type: "Feature", properties: { ID: Math.floor(Math.random() * 10000) } };

    window.camadasGeoNex[camadaDestinoDesenho].addLayer(poligonoFinal);
    desligarDesenho();
}

function desligarDesenho() {
    let mapa = obterMapaLeaflet();
    modoDesenhoAtivo = false;
    document.getElementById('map').style.cursor = '';

    if (mapa) {
        mapa.off('click', adicionarVertice);
        mapa.off('mousemove', desenharLinhaGuia);
        mapa.off('contextmenu', fecharPoligono);
    }
}

// 2. MOTOR DE DESENHO POR COORDENADAS (TOPOGRAFIA)
window.desenharPoligonoPorCoordenadas = function (nomeCamada, arrayCoordenadasLatLgn) {
    let mapa = obterMapaLeaflet();
    if (!mapa) return false;

    if (!window.camadasGeoNex[nomeCamada]) {
        window.camadasGeoNex[nomeCamada] = L.featureGroup().addTo(mapa);
    }

    let poligonoExato = L.polygon(arrayCoordenadasLatLgn, { color: '#10b981', weight: 2, fillColor: '#34d399', fillOpacity: 0.4 });
    poligonoExato.feature = { type: "Feature", properties: { ID: Math.floor(Math.random() * 10000), Origem: "Coordenadas" } };

    window.camadasGeoNex[nomeCamada].addLayer(poligonoExato);
    mapa.fitBounds(poligonoExato.getBounds(), { padding: [50, 50] });

    return true;
};
// 3. MOTOR DE AQUISIÇÃO AVANÇADA (Caderneta de Campo C# -> Leaflet)
window.desenharAquisicaoAvancada = function (nomeCamada, jsonPontos, tipoGeometria) {
    let mapa = obterMapaLeaflet();
    if (!mapa) return false;

    // Se a camada ainda não existir no motor gráfico, cria
    if (!window.camadasGeoNex[nomeCamada]) {
        window.camadasGeoNex[nomeCamada] = L.featureGroup().addTo(mapa);
    }
    let camadaAlvo = window.camadasGeoNex[nomeCamada];

    // Converte a string enviada pelo C# de volta para Objetos JS
    let listaPontos = JSON.parse(jsonPontos);
    let arrayCoordenadas = [];

    // Desenha as geometrias baseadas no tipo escolhido pelo Engenheiro
    if (tipoGeometria === "PONTOS") {

        listaPontos.forEach(pt => {
            let marker = L.circleMarker([parseFloat(pt.Lat), parseFloat(pt.Lng)], {
                radius: 6, color: '#ef4444', weight: 2, fillColor: '#fee2e2', fillOpacity: 0.8
            });

            // Adiciona o Rótulo do Ponto (Ex: P1)
            marker.bindTooltip(pt.Nome, { permanent: true, direction: 'right', className: 'rotulo-ponto' }).openTooltip();
            marker.feature = { type: "Feature", properties: { ID: pt.Nome, Origem: "Levantamento" } };
            camadaAlvo.addLayer(marker);
            arrayCoordenadas.push([parseFloat(pt.Lat), parseFloat(pt.Lng)]);
        });

    } else if (tipoGeometria === "LINHA") {

        listaPontos.forEach(pt => arrayCoordenadas.push([parseFloat(pt.Lat), parseFloat(pt.Lng)]));
        let linha = L.polyline(arrayCoordenadas, { color: '#f59e0b', weight: 4 });
        linha.feature = { type: "Feature", properties: { ID: "Eixo_" + Math.floor(Math.random() * 1000) } };
        camadaAlvo.addLayer(linha);

    } else if (tipoGeometria === "POLIGONO") {

        listaPontos.forEach(pt => arrayCoordenadas.push([parseFloat(pt.Lat), parseFloat(pt.Lng)]));
        let poligono = L.polygon(arrayCoordenadas, { color: '#10b981', weight: 2, fillColor: '#34d399', fillOpacity: 0.4 });
        poligono.feature = { type: "Feature", properties: { ID: "Area_" + Math.floor(Math.random() * 1000) } };
        camadaAlvo.addLayer(poligono);

    }

    // Dá o Zoom para abraçar o que acabou de ser desenhado
    if (arrayCoordenadas.length > 0) {
        let bounds = L.latLngBounds(arrayCoordenadas);
        mapa.fitBounds(bounds, { padding: [50, 50] });
    }

    return true;
};
// 5. MOTOR DE EDIÇÃO MANUAL (A MESA DE DESENHO)
window.camadaEmEdicaoGeoNex = null;
window.grupoVerticesEdit = null;

// Ativa as bolinhas (quadradinhos) brancas em cada canto do polígono selecionado
// 5. MOTOR DE EDIÇÃO MANUAL OMNI (TODAS AS CAMADAS)
// 5. MOTOR DE EDIÇÃO MANUAL (FOCADO NA FEIÇÃO SELECIONADA)
window.grupoVerticesEdit = null;
window.ferramentaVerticesAtiva = false;
window.feicaoSelecionadaAtual = null;
window.camadaDaFeicaoSelecionada = null;

// Liga/Desliga a ferramenta a partir do botão C#
window.alternarFerramentaVertices = function (estado) {
    let mapa = obterMapaLeaflet();
    window.ferramentaVerticesAtiva = estado;

    if (estado) {
        // Se ligou a ferramenta e já tem um lote amarelo selecionado, gera os vértices
        if (window.feicaoSelecionadaAtual) {
            window.gerarVerticesParaFeicaoSelecionada();
        }
    } else {
        // Se desligou, limpa o ecrã
        if (window.grupoVerticesEdit && mapa) {
            mapa.removeLayer(window.grupoVerticesEdit);
        }
        window.grupoVerticesEdit = null;
        window.verticeSobO_Mouse = null;
    }
};

// Gera as caixinhas APENAS para o polígono amarelo
window.gerarVerticesParaFeicaoSelecionada = function () {
    let mapa = obterMapaLeaflet();
    if (!mapa || !window.feicaoSelecionadaAtual) return;

    // Limpa a tela antes de desenhar
    if (window.grupoVerticesEdit) {
        mapa.removeLayer(window.grupoVerticesEdit);
    }
    window.grupoVerticesEdit = L.featureGroup().addTo(mapa);

    let poligonoLayer = window.feicaoSelecionadaAtual;
    let nomeCamadaPai = window.camadaDaFeicaoSelecionada;

    // Garante que é uma geometria editável
    if (typeof poligonoLayer.getLatLngs === 'function') {
        let latlngs = poligonoLayer.getLatLngs();
        let coordenadas = latlngs[0];
        if (Array.isArray(latlngs[0][0])) {
            coordenadas = latlngs[0][0];
        }

        // Desenha os bicos só deste polígono
        for (let i = 0; i < coordenadas.length; i++) {
            criarVerticeEditavel(coordenadas[i], i, poligonoLayer, nomeCamadaPai);
        }
    }
};

// O construtor do "quadradinho mágico" (Mantém-se igual, com sensores de atalho V)
function criarVerticeEditavel(latlng, indice, poligonoPai, nomeCamadaPai) {
    let vertice = L.marker(latlng, {
        draggable: true,
        icon: L.divIcon({ className: 'vertice-edit', iconSize: [12, 12], iconAnchor: [6, 6] })
    });

    vertice.on('drag', function (e) {
        let novaPosicao = e.target.getLatLng();
        let latlngsAtuais = poligonoPai.getLatLngs();
        if (Array.isArray(latlngsAtuais[0][0])) {
            latlngsAtuais[0][0][indice] = novaPosicao;
        } else {
            latlngsAtuais[0][indice] = novaPosicao;
        }
        poligonoPai.setLatLngs(latlngsAtuais);
    });

    vertice.on('mouseover', function (e) {
        window.verticeSobO_Mouse = { camada: nomeCamadaPai, indice: indice, lat: e.latlng.lat, lng: e.latlng.lng };
    });

    vertice.on('mouseout', function (e) { window.verticeSobO_Mouse = null; });

    vertice.on('dblclick', function (e) {
        if (window.dotnetReferencia) {
            window.dotnetReferencia.invokeMethodAsync('PedirCoordenadaVertice', nomeCamadaPai, indice, e.latlng.lat, e.latlng.lng);
        }
    });

    window.grupoVerticesEdit.addLayer(vertice);
}

// Força a mudança após injetar a coordenada na janela do C#
window.forcarPosicaoVertice = function (nomeCamada, indice, novaLat, novaLng) {
    let poligonoLayer = window.feicaoSelecionadaAtual;
    if (!poligonoLayer) return;

    if (typeof poligonoLayer.getLatLngs === 'function') {
        let latlngsAtuais = poligonoLayer.getLatLngs();
        let novaPosicao = new L.LatLng(novaLat, novaLng);

        if (Array.isArray(latlngsAtuais[0][0])) {
            latlngsAtuais[0][0][indice] = novaPosicao;
        } else {
            latlngsAtuais[0][indice] = novaPosicao;
        }
        poligonoLayer.setLatLngs(latlngsAtuais);
    }
    // Reposiciona as caixinhas na tela
    window.gerarVerticesParaFeicaoSelecionada();
};

window.desligarModoEdicaoVertice = function () {
    let mapa = obterMapaLeaflet();
    if (window.grupoVerticesEdit && mapa) {
        mapa.removeLayer(window.grupoVerticesEdit);
    }
    window.grupoVerticesEdit = null;
    window.verticeSobO_Mouse = null; // Limpa o sensor do atalho V
};

// Construtor do vértice inteligente
function criarVerticeEditavel(latlng, indice, poligonoPai, nomeCamadaPai) {
    let vertice = L.marker(latlng, {
        draggable: true,
        icon: L.divIcon({ className: 'vertice-edit', iconSize: [12, 12], iconAnchor: [6, 6] })
    });

    vertice.on('drag', function (e) {
        let novaPosicao = e.target.getLatLng();
        let latlngsAtuais = poligonoPai.getLatLngs();

        if (Array.isArray(latlngsAtuais[0][0])) {
            latlngsAtuais[0][0][indice] = novaPosicao;
        } else {
            latlngsAtuais[0][indice] = novaPosicao;
        }
        poligonoPai.setLatLngs(latlngsAtuais);
    });

    // SENSORES PARA A TECLA "V" E DUPLO CLIQUE
    vertice.on('mouseover', function (e) {
        window.verticeSobO_Mouse = {
            camada: nomeCamadaPai,
            indice: indice,
            lat: e.latlng.lat,
            lng: e.latlng.lng
        };
    });

    vertice.on('mouseout', function (e) {
        window.verticeSobO_Mouse = null;
    });

    vertice.on('dblclick', function (e) {
        if (window.dotnetReferencia) {
            window.dotnetReferencia.invokeMethodAsync('PedirCoordenadaVertice', nomeCamadaPai, indice, e.latlng.lat, e.latlng.lng);
        }
    });

    window.grupoVerticesEdit.addLayer(vertice);
}

// Força a mudança após injetar UTM
window.forcarPosicaoVertice = function (nomeCamada, indice, novaLat, novaLng) {
    let camadaAlvo = window.camadasGeoNex[nomeCamada];
    if (!camadaAlvo) return;

    camadaAlvo.eachLayer(function (poligonoLayer) {
        if (typeof poligonoLayer.getLatLngs === 'function') {
            let latlngsAtuais = poligonoLayer.getLatLngs();
            let novaPosicao = new L.LatLng(novaLat, novaLng);

            if (Array.isArray(latlngsAtuais[0][0])) {
                latlngsAtuais[0][0][indice] = novaPosicao;
            } else {
                latlngsAtuais[0][indice] = novaPosicao;
            }
            poligonoLayer.setLatLngs(latlngsAtuais);
        }
    });

    // Recarrega todos os vértices de todas as camadas para atualizar a tela
    window.ativarModoEdicaoVertice();
};
// 6. GESTOR DE ATALHOS DE TECLADO (SHORTCUTS)
// 6. GESTOR DE ATALHOS DE TECLADO INTELIGENTES
// GESTOR GLOBAL DE ATALHOS (Resolve o problema do Foco no MAUI)
// =========================================================================
// GESTOR GLOBAL DE ATALHOS (Resolve o problema do Foco no MAUI)
// =========================================================================
// GESTOR GLOBAL DE ATALHOS (Resolve o problema do Foco no MAUI)
// =========================================================================
// GESTOR GLOBAL DE ATALHOS - UNIFICADO
document.addEventListener('keydown', function (event) {
    // 1. Não deixar o navegador processar estas teclas específicas
    const teclasProtegidas = ['tab', 'f6', 'escape', 'm', 'i', 'd', 'p', 'enter', 'c', 'backspace', 'z', 'ctrl+z'];    let tecla = event.key.toLowerCase();

    // Suporte para Ctrl+Z
    if (event.ctrlKey && tecla === 'z') tecla = 'ctrl+z';

    if (teclasProtegidas.includes(tecla)) {
        // Ignora se estiver a escrever num input
        const tagsIgnoradas = ['INPUT', 'TEXTAREA', 'SELECT'];
        if (tagsIgnoradas.includes(event.target.tagName)) return;

        event.preventDefault(); // Impede o navegador de saltar para a barra de endereços (F6) ou mudar foco (Tab)

        // Envia para o C#
        if (window.mapEngine && window.mapEngine.dotNetHelper) {
            window.mapEngine.dotNetHelper.invokeMethodAsync('ProcessarTecladoGlobal', tecla)
                .catch(err => console.warn("Erro ao enviar tecla para o C#: ", err));
        }
    }
}, { passive: false });
// 7. MOTOR DE SIMBOLOGIA TEMÁTICA (AUTO-CATEGORIZADOR)
window.aplicarSimbologiaCategorizada = function (nomeCamada, colunaAtributo, espessura, opacidade) {
    let camada = window.camadasGeoNex[nomeCamada];
    if (!camada) return false;

    // 1. Varre o polígono super rápido e descobre todos os valores únicos que existem nessa coluna
    let valoresUnicos = new Set();
    camada.eachLayer(function (l) {
        if (l.feature && l.feature.properties && l.feature.properties[colunaAtributo] !== undefined) {
            valoresUnicos.add(String(l.feature.properties[colunaAtributo]).trim());
        }
    });

    // 2. Paleta de Cores Profissional de Engenharia (Cores amigáveis para mapas)
    let paleta = ['#ef4444', '#3b82f6', '#10b981', '#f59e0b', '#8b5cf6', '#ec4899', '#06b6d4', '#84cc16', '#f97316', '#64748b'];
    let mapaCores = {};

    // Distribui uma cor para cada valor único
    let i = 0;
    valoresUnicos.forEach(valor => {
        mapaCores[valor] = paleta[i % paleta.length];
        i++;
    });

    // 3. Pinta instantaneamente a malha baseando-se na cor escolhida para aquela palavra
    camada.setStyle(function (feature) {
        let valor = feature.properties[colunaAtributo] ? String(feature.properties[colunaAtributo]).trim() : '';
        let cor = mapaCores[valor] || '#94a3b8'; // Cor neutra se não tiver atributo

        return {
            fillColor: cor,
            color: cor, // A borda fica da mesma cor do interior
            weight: parseFloat(espessura),
            fillOpacity: parseFloat(opacidade)
        };
    });

    console.log("[GEONEX] Simbologia Categorizada aplicada para a coluna: " + colunaAtributo);
    return true;
};
window.atualizarCanvasMapa = function (base64Image) {
    var canvas = document.getElementById('canvasMapa');
    if (!canvas) return;

    // Desliga o canal Alpha (transparência) para ganhar mais FPS na placa de vídeo
    var ctx = canvas.getContext('2d', { alpha: false });
    var img = new Image();

    img.onload = function () {
        // Só redimensiona se a janela tiver mudado, poupando processamento
        if (canvas.width !== canvas.clientWidth) canvas.width = canvas.clientWidth;
        if (canvas.height !== canvas.clientHeight) canvas.height = canvas.clientHeight;

        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
    };

    // Garante que o navegador sabe que é um JPEG ultra-rápido
    img.src = 'data:image/jpeg;base64,' + base64Image;
};

window.dimensoesJanela = {
    obterDpi: function () {
        const dpi = Number(window.devicePixelRatio) || 1;
        return Math.max(0.5, Math.min(4, dpi));
    },
    obter: function () {
        return {
            largura: Math.max(1, Math.round(window.innerWidth)),
            altura: Math.max(1, Math.round(window.innerHeight)),
            dpi: window.dimensoesJanela.obterDpi()
        };
    },
    registrarResize: function (dotnetHelper) {
        let timer = null;
        const notificar = function () {
            clearTimeout(timer);
            timer = setTimeout(function () {
                const dimensoes = window.dimensoesJanela.obter();
                dotnetHelper.invokeMethodAsync(
                    'AtualizarDimensoesTela',
                    dimensoes.largura,
                    dimensoes.altura,
                    dimensoes.dpi);
            }, 60);
        };
        window.addEventListener('resize', notificar);

        // WebView2 pode trocar o devicePixelRatio ao mover a janela entre o
        // ecrã do notebook e um monitor externo sem alterar o tamanho CSS.
        const observarDpi = function () {
            if (!window.matchMedia) return;
            const query = window.matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
            query.addEventListener('change', function () {
                notificar();
                observarDpi();
            }, { once: true });
        };
        observarDpi();
    }
};
// === MOTOR DE FÍSICA E RENDERIZAÇÃO (144Hz LERP GPU + INÉRCIA CINÉTICA) ===
// === MOTOR DE FÍSICA E RENDERIZAÇÃO (144Hz LERP GPU + INÉRCIA + RUBBER-BANDING) ===
// === MOTOR DE FÍSICA E RENDERIZAÇÃO (Tempo-Delta VRAM GPU + INÉRCIA + RUBBER-BANDING) ===
// === MOTOR DE FÍSICA E RENDERIZAÇÃO (Tempo-Delta VRAM GPU + LIMITES ABSOLUTOS) ===

// === MOTOR DE FÍSICA E RENDERIZAÇÃO (144Hz LERP GPU + INÉRCIA + DOUBLE BUFFERING) ===
// === MOTOR DE FÍSICA E RENDERIZAÇÃO (PRECISÃO GIS 1:1 - MOVIMENTO SECO) ===
window.mapEngine = {
    container: null,
    renderSurface: null,
    activeLayer: null,
    bufferLayer: null,
    dotNetHelper: null,

    targetX: 0, targetY: 0, targetScale: 1,
    currentX: 0, currentY: 0, currentScale: 1,

    pendingX: 0, pendingY: 0, pendingScale: 1,
    isFetching: false, hasPendingRequest: false,

    isDragging: false,
    startX: 0, startY: 0, startPanX: 0, startPanY: 0,

    velocityX: 0, velocityY: 0,
    lastX: 0, lastY: 0, lastEventTime: 0,
    lastFrameTime: 0,

    absoluteZoom: 1.0,

    rafId: null, renderTimeout: null, previewTimeout: null, fetchWatchdog: null,
    lastInteractionTime: 0,
    requestSerial: 0, activeRequestId: 0,
    lastCommittedRequestId: 0,
    latestFrameRequested: 0, latestFramePresented: 0,
    pendingInteractive: false,
    wheelGestureMode: null, wheelGestureUntil: 0,
    pointerIds: new Set(), activeTouches: new Map(),
    isPinching: false, touchGestureMoved: false,
    lastPinchDistance: 0, lastPinchCenterX: 0, lastPinchCenterY: 0,
    initialized: false,
    cameraEpoch: 0, retryCount: 0,
    committedCamera: { panX: 0, panY: 0, zoom: 1 },

    init: function (dotNetRef) {
        this.container = document.getElementById('map-container');
        this.renderSurface = document.getElementById('map-render-surface') || this.container;
        this.skiaCanvas = this.skiaCanvas || document.getElementById('skia-canvas');
        this.backCanvas = this.backCanvas || document.getElementById('skia-canvas-back');
        if (this.skiaCanvas) this.skiaCtx = this.skiaCanvas.getContext('2d', { alpha: true });
        this.dotNetHelper = dotNetRef;

        if (this.container && !this.initialized) {
            this.container.style.touchAction = 'none';
            this.container.style.overscrollBehavior = 'none';
            if (this.renderSurface) this.renderSurface.style.willChange = 'transform';

            this.container.addEventListener('dblclick', (e) => this.onDoubleClick(e));
            this.container.addEventListener('wheel', (e) => this.onWheel(e), { passive: false });
            this.container.addEventListener('pointerdown', (e) => this.onPointerDown(e));
            this.container.addEventListener('pointermove', (e) => this.onPointerMove(e));
            this.container.addEventListener('pointerup', (e) => this.onPointerUp(e, false));
            this.container.addEventListener('pointercancel', (e) => this.onPointerUp(e, true));

            this.initialized = true;
            this.aplicarTransformacao();
        }
    },

    resetarCamera: function () {
        this.refineAfterRequestId = 0;
        this.cameraEpoch++;
        this.frameLoadController?.abort();
        this.decodedPayload = null;
        this.frameReuseUnavailableFor = null;
        this.committedCamera = { panX: 0, panY: 0, zoom: 1 };
        if (this.rafId !== null) cancelAnimationFrame(this.rafId);
        clearTimeout(this.renderTimeout);
        clearTimeout(this.previewTimeout);
        clearTimeout(this.fetchWatchdog);
        this.rafId = null;
        this.renderTimeout = null;
        this.previewTimeout = null;
        this.fetchWatchdog = null;
        this.lastFrameTime = 0;
        this.velocityX = 0; this.velocityY = 0;
        this.isFetching = false;
        this.activeRequestId = 0;
        this.hasPendingRequest = false;
        this.pendingInteractive = false;
        this.lastRequestStart = null;
        this.absoluteZoom = 1.0;
        this.targetX = 0; this.targetY = 0; this.targetScale = 1;
        this.currentX = 0; this.currentY = 0; this.currentScale = 1;
        this.pendingX = 0; this.pendingY = 0; this.pendingScale = 1;
        this.aplicarTransformacao();
    },

    sincronizarCameraComBlazor: function (panX, panY, zoomBase) {
        // Reset pending timers and inertia as well as in-flight decodes, but keep the requested camera.
        this.resetarCamera();
        this.committedCamera = { panX, panY, zoom: zoomBase };
        clearTimeout(this.fetchWatchdog);
        this.fetchWatchdog = null;
        this.isFetching = false;
        this.activeRequestId = 0;
        this.hasPendingRequest = false;
        this.lastRequestStart = null;
        this.absoluteZoom = zoomBase;
        // Zera os deltas visuais do JavaScript, pois a nova imagem do Blazor já vem com o pan absoluto!
        this.targetX = 0; this.targetY = 0; this.targetScale = 1;
        this.currentX = 0; this.currentY = 0; this.currentScale = 1;
        this.pendingX = 0; this.pendingY = 0; this.pendingScale = 1;
        this.velocityX = 0; this.velocityY = 0;
        this.aplicarTransformacao();
        return this.requestSerial;
    },

    aplicarTransformacao: function () {
        if (this.renderSurface) {
            this.renderSurface.style.transform =
                `translate3d(${this.currentX}px, ${this.currentY}px, 0) scale(${this.currentScale})`;
        }
    },

    solicitarAnimacao: function () {
        if (this.rafId === null) {
            this.lastFrameTime = 0;
            this.rafId = requestAnimationFrame((ts) => this.animate(ts));
        }
    },

    obterPontoViewport: function (clientX, clientY) {
        const rect = this.container.getBoundingClientRect();
        return {
            x: (clientX - rect.left) - (rect.width / 2),
            y: (clientY - rect.top) - (rect.height / 2)
        };
    },

    limitarProporcaoZoom: function (proporcao) {
        if (!Number.isFinite(proporcao) || proporcao <= 0) return 1;
        const limiteMaximo = 1000000.0;
        const limiteMinimo = 0.05;
        const zoomProjetado = this.absoluteZoom * proporcao;
        if (zoomProjetado > limiteMaximo) return limiteMaximo / this.absoluteZoom;
        if (zoomProjetado < limiteMinimo) return limiteMinimo / this.absoluteZoom;
        return proporcao;
    },

    normalizarDeltaRoda: function (e) {
        const altura = Math.max(1, this.container.clientHeight);
        const fator = e.deltaMode === 1 ? 16 : (e.deltaMode === 2 ? altura : 1);
        const limitar = (valor) => Math.max(-240, Math.min(240, valor * fator));
        return { x: limitar(e.deltaX), y: limitar(e.deltaY) };
    },

    classificarEntradaRoda: function (e, deltaX, deltaY) {
        if (e.ctrlKey) return 'zoom';

        const agora = performance.now();
        const legacyY = Math.abs(Number(e.wheelDeltaY) || 0);
        const evidenciaPrecisao = e.deltaMode === 0 &&
            (Math.abs(deltaX) > 0.01 || Math.abs(deltaY) < 80 ||
                !Number.isInteger(e.deltaY) ||
                (legacyY > 0 && Math.abs(legacyY % 120) > 0.01));

        if (agora > this.wheelGestureUntil) {
            this.wheelGestureMode = evidenciaPrecisao ? 'pan' : 'zoom';
        } else if (evidenciaPrecisao) {
            // Um gesto que revelou precisão ou eixo horizontal é touchpad.
            this.wheelGestureMode = 'pan';
        }
        this.wheelGestureUntil = agora + 180;
        return this.wheelGestureMode || 'zoom';
    },

    onDoubleClick: function (e) {
        e.preventDefault();
        this.aplicarZoomCentralizado(2.0, e.clientX, e.clientY, false);
    },

    onWheel: function (e) {
        e.preventDefault();
        if (e.buttons & 4) return;

        const delta = this.normalizarDeltaRoda(e);
        if (delta.x === 0 && delta.y === 0) return;
        const modo = this.classificarEntradaRoda(e, delta.x, delta.y);

        if (modo === 'pan') {
            // Movimento 1:1 para touchpads de precisão, inclusive na diagonal.
            this.targetX -= delta.x;
            this.targetY -= delta.y;
            this.currentX = this.targetX;
            this.currentY = this.targetY;
            this.solicitarAnimacao();
            this.scheduleRender();
            return;
        }

        // Pinch do touchpad chega com Ctrl; a roda física mantém passos suaves.
        const sensibilidade = e.ctrlKey ? 0.0025 : 0.0014;
        const proporcao = Math.max(0.75, Math.min(1.333, Math.exp(-delta.y * sensibilidade)));
        this.aplicarZoomCentralizado(proporcao, e.clientX, e.clientY, e.ctrlKey);
    },

    aplicarZoomCentralizado: function (proporcao, clientX, clientY, imediato) {
        proporcao = this.limitarProporcaoZoom(proporcao);
        if (Math.abs(proporcao - 1) < 1e-9) return;

        this.absoluteZoom *= proporcao;
        this.targetScale *= proporcao;

        const ponto = this.obterPontoViewport(clientX, clientY);
        this.targetX = (this.targetX * proporcao) + (ponto.x * (1 - proporcao));
        this.targetY = (this.targetY * proporcao) + (ponto.y * (1 - proporcao));

        if (imediato) {
            this.currentX = this.targetX;
            this.currentY = this.targetY;
            this.currentScale = this.targetScale;
        }

        this.solicitarAnimacao();
        this.scheduleRender();
    },

    obterEstadoPinch: function () {
        const iterador = this.activeTouches.values();
        const primeiro = iterador.next();
        const segundo = iterador.next();
        if (primeiro.done || segundo.done) return null;

        const dx = segundo.value.x - primeiro.value.x;
        const dy = segundo.value.y - primeiro.value.y;
        return {
            distancia: Math.max(1, Math.hypot(dx, dy)),
            centroX: (primeiro.value.x + segundo.value.x) / 2,
            centroY: (primeiro.value.y + segundo.value.y) / 2
        };
    },

    onPointerMove: function (e) {
        if (e.pointerType === 'touch' && this.activeTouches.has(e.pointerId)) {
            this.activeTouches.set(e.pointerId, { x: e.clientX, y: e.clientY });
            if (this.activeTouches.size >= 2) {
                const pinch = this.obterEstadoPinch();
                if (!pinch) return;

                if (!this.isPinching) {
                    this.isPinching = true;
                    this.lastPinchDistance = pinch.distancia;
                    this.lastPinchCenterX = pinch.centroX;
                    this.lastPinchCenterY = pinch.centroY;
                    return;
                }

                let proporcao = this.limitarProporcaoZoom(
                    Math.max(0.75, Math.min(1.333, pinch.distancia / this.lastPinchDistance)));
                const ponto = this.obterPontoViewport(this.lastPinchCenterX, this.lastPinchCenterY);
                const moverX = pinch.centroX - this.lastPinchCenterX;
                const moverY = pinch.centroY - this.lastPinchCenterY;

                this.absoluteZoom *= proporcao;
                this.targetScale *= proporcao;
                this.targetX = this.targetX * proporcao + ponto.x * (1 - proporcao) + moverX;
                this.targetY = this.targetY * proporcao + ponto.y * (1 - proporcao) + moverY;
                this.currentX = this.targetX;
                this.currentY = this.targetY;
                this.currentScale = this.targetScale;
                this.touchGestureMoved = true;
                this.lastPinchDistance = pinch.distancia;
                this.lastPinchCenterX = pinch.centroX;
                this.lastPinchCenterY = pinch.centroY;
                this.solicitarAnimacao();
                this.scheduleRender();
                e.preventDefault();
                return;
            }
        }

        if (!this.isDragging) {
            if ((this.ferramentaAtual === 'Medicao' || this.ferramentaAtual === 'AquisicaoPoligono') && this.dotNetHelper) {
                const rect = this.container.getBoundingClientRect();
                this.dotNetHelper.invokeMethodAsync('ReceberMovimentoFerramentas', e.clientX - rect.left, e.clientY - rect.top);
            }
            return;
        }

        const now = performance.now();
        const deltaTime = now - this.lastEventTime;

        // Otimização Extrema: Pan Incremental (Delta) em vez de absoluto
        // Permite que o utilizador faça Zoom In/Out *ENQUANTO* arrasta o mapa, sem que a coordenada dê um "salto" para a origem do clique!
        const dx = e.clientX - this.lastX;
        const dy = e.clientY - this.lastY;

        this.targetX += dx;
        this.targetY += dy;

        this.currentX = this.targetX;
        this.currentY = this.targetY;

        if (deltaTime > 0) {
            this.velocityX = dx / deltaTime;
            this.velocityY = dy / deltaTime;
        }

        this.lastX = e.clientX;
        this.lastY = e.clientY;
        this.lastEventTime = now;

        this.solicitarAnimacao();
        this.scheduleRender();
    },

    ferramentaAtual: 'Identificacao',

    setFerramenta: function (nomeFerramenta) {
        this.ferramentaAtual = nomeFerramenta;
        if (this.container) {
            this.container.style.cursor =
                nomeFerramenta === 'Navegacao' ? 'grab' :
                    (nomeFerramenta === 'Medicao' || nomeFerramenta === 'AquisicaoPoligono' ? 'crosshair' : 'default');
        }
    },

    onPointerDown: function (e) {
        if (e.pointerType === 'mouse' && e.button !== 0 && e.button !== 1) return;

        this.pointerIds.add(e.pointerId);
        try { this.container.setPointerCapture(e.pointerId); } catch (_) { }

        this.startX = e.clientX;
        this.startY = e.clientY;
        this.clickStartTime = performance.now();

        if (e.pointerType === 'touch') {
            this.activeTouches.set(e.pointerId, { x: e.clientX, y: e.clientY });
            if (this.activeTouches.size >= 2) {
                const pinch = this.obterEstadoPinch();
                if (pinch) {
                    this.isPinching = true;
                    this.isDragging = true;
                    this.touchGestureMoved = true;
                    this.lastPinchDistance = pinch.distancia;
                    this.lastPinchCenterX = pinch.centroX;
                    this.lastPinchCenterY = pinch.centroY;
                    this.velocityX = 0;
                    this.velocityY = 0;
                    e.preventDefault();
                    return;
                }
            }
        }

        if (e.button === 1 || (e.button === 0 && this.ferramentaAtual === 'Navegacao')) {
            this.isDragging = true;
            this.startPanX = this.targetX;
            this.startPanY = this.targetY;

            this.lastX = e.clientX;
            this.lastY = e.clientY;
            this.lastEventTime = performance.now();

            this.velocityX = 0;
            this.velocityY = 0;

            this.container.style.cursor = 'grabbing';
        }
    },

    onPointerUp: function (e, cancelado) {
        if (!this.pointerIds.has(e.pointerId)) return;
        this.pointerIds.delete(e.pointerId);
        if (e.pointerType === 'touch') this.activeTouches.delete(e.pointerId);
        try {
            if (this.container.hasPointerCapture(e.pointerId)) this.container.releasePointerCapture(e.pointerId);
        } catch (_) { }

        if (cancelado) {
            this.isDragging = false;
            this.isPinching = false;
            this.touchGestureMoved = false;
            this.velocityX = 0;
            this.velocityY = 0;
            this.scheduleRender();
            return;
        }

        if (e.pointerType === 'touch' && (this.isPinching || this.touchGestureMoved)) {
            this.isPinching = false;
            this.velocityX = 0;
            this.velocityY = 0;

            if (this.activeTouches.size === 1 && this.ferramentaAtual === 'Navegacao') {
                const restante = this.activeTouches.values().next().value;
                this.isDragging = true;
                this.lastX = restante.x;
                this.lastY = restante.y;
                this.lastEventTime = performance.now();
            } else {
                this.isDragging = false;
            }

            if (this.activeTouches.size === 0) this.touchGestureMoved = false;
            this.scheduleRender();
            return;
        }

        const tempoDecorrido = performance.now() - this.clickStartTime;
        const distMovida = Math.abs(e.clientX - this.startX) + Math.abs(e.clientY - this.startY);

        if (this.isDragging) {
            this.isDragging = false;

            this.container.style.cursor =
                this.ferramentaAtual === 'Navegacao' ? 'grab' :
                    (this.ferramentaAtual === 'Medicao' ? 'crosshair' : 'default');

            if (distMovida > 10) {
                // INÉRCIA ATIVADA: Deslizar estilo Leaflet / Google Maps
                const multiplicadorFriccao = 180;
                if (Math.abs(this.velocityX) > 0.3 || Math.abs(this.velocityY) > 0.3) {
                    this.targetX += this.velocityX * multiplicadorFriccao;
                    this.targetY += this.velocityY * multiplicadorFriccao;
                }
                this.velocityX = 0;
                this.velocityY = 0;
                this.solicitarAnimacao();
                this.scheduleRender();
                return;
            }
        }

        if (e.button === 0 && tempoDecorrido < 300 && distMovida < 10) {
            this.dispararRaycast(e.clientX, e.clientY);
        }
    },

    dispararRaycast: function (clientX, clientY) {
        if (!this.dotNetHelper) return;

        const rect = this.container.getBoundingClientRect();
        const mouseX = clientX - rect.left;
        const mouseY = clientY - rect.top;

        const cx = rect.width / 2;
        const cy = rect.height / 2;

        const pixelImagemX = (mouseX - cx - this.currentX) / this.currentScale + cx;
        const pixelImagemY = (mouseY - cy - this.currentY) / this.currentScale + cy;

        this.dotNetHelper.invokeMethodAsync('ProcessarCliqueRaycast', pixelImagemX, pixelImagemY)
            .catch(err => console.warn("Erro no Túnel:", err));
    },

    animate: function (timestamp) {
        this.rafId = null;
        let deltaTime = this.lastFrameTime ? timestamp - this.lastFrameTime : 16;
        this.lastFrameTime = timestamp;

        if (deltaTime > 50) deltaTime = 16;

        // OTIMIZAÇÃO EXTREMA: lerpFactor 0.035 = zoom/pan crava em ~100ms (QGIS-like)
        const lerpFactor = 1 - Math.exp(-0.035 * deltaTime);

        // A suavização elástica (lerp) agora funciona APENAS para o Zoom ficar suave.
        // O Pan fica cravado no rato sem flutuações, graças à lógica do onPointerMove.
        if (!this.isDragging && !this.isPinching) {
            this.currentX += (this.targetX - this.currentX) * lerpFactor;
            this.currentY += (this.targetY - this.currentY) * lerpFactor;
        }

        if (!this.isPinching)
            this.currentScale += (this.targetScale - this.currentScale) * lerpFactor;

        if (Math.abs(this.targetX - this.currentX) < 0.1) this.currentX = this.targetX;
        if (Math.abs(this.targetY - this.currentY) < 0.1) this.currentY = this.targetY;
        if (Math.abs(this.targetScale - this.currentScale) < 0.0005) this.currentScale = this.targetScale;

        this.aplicarTransformacao();

        const emMovimento = Math.abs(this.targetX - this.currentX) >= 0.1 ||
            Math.abs(this.targetY - this.currentY) >= 0.1 ||
            Math.abs(this.targetScale - this.currentScale) >= 0.0005;
        if (emMovimento) {
            this.rafId = requestAnimationFrame((ts) => this.animate(ts));
        } else {
            this.lastFrameTime = 0;
        }
    },

    obterAtrasoAssentamento: function () {
        const latencia = Number.isFinite(this.currentLatency) ? this.currentLatency : 16;
        // Short wheel/trackpad pauses are not the end of a gesture. Final frames
        // can involve uncancellable online RasterIO, so debounce them separately.
        return Math.max(250, Math.min(400, latencia * 0.3));
    },

    scheduleRender: function () {
        this.lastInteractionTime = performance.now();
        // New input invalidates a queued settle request from an earlier pause.
        // Keep navigation responsive; the debounce below will request final quality again.
        if (this.hasPendingRequest) this.pendingInteractive = true;
        clearTimeout(this.renderTimeout);
        let delayAdaptativo = this.obterAtrasoAssentamento();
        this.renderTimeout = setTimeout(() => {
            this.renderTimeout = null;
            if (this.previewTimeout !== null) clearTimeout(this.previewTimeout);
            this.previewTimeout = null;
            this.solicitarFrame(false);
        }, delayAdaptativo);

        // Em gestos longos, atualiza as bordas com o cache barato sem saturar a CPU.
        if (this.previewTimeout === null) {
            this.previewTimeout = setTimeout(() => {
                this.previewTimeout = null;
                if (performance.now() - this.lastInteractionTime < this.obterAtrasoAssentamento() + 24)
                    this.solicitarFrame(true);
            }, 120);
        }
    },

    solicitarFrame: function (interacaoRapida) {
        if (!this.dotNetHelper) return;
        if (this.isFetching) {
            const cameraChanged = this.targetX !== this.pendingX ||
                this.targetY !== this.pendingY || this.targetScale !== this.pendingScale;
            const exhausted = this.fetchWatchdog === null && this.retryCount > 2;
            if (!cameraChanged && interacaoRapida && !exhausted) return;
            // Supersede slow frames. The next request uses the same presented
            // camera basis, so canceling a pending frame never doubles pan/zoom.
            // A final refinement of the SAME camera waits for its preview to be
            // presented: canceling it wastes I/O and prevents any early feedback.
            if (performance.now() - this.lastRequestStart >= 150 && (cameraChanged || exhausted)) {
                this.executarRequisicao(interacaoRapida);
                return;
            }
            if (this.fetchWatchdog === null && this.retryCount > 2) {
                this.retryCount = 0;
                this.falharRequisicao(this.activeRequestId);
            }
            if (!this.hasPendingRequest) this.pendingInteractive = interacaoRapida;
            else this.pendingInteractive = this.pendingInteractive && interacaoRapida;
            this.hasPendingRequest = true;
            return;
        }
        this.executarRequisicao(interacaoRapida);
    },

    executarRequisicao: function (interacaoRapida, refineAfterPresentation = false) {
        this.isFetching = true;
        this.hasPendingRequest = false;
        this.pendingInteractive = false;

        this.retryCount = 0;
        this.requestInteractive = !!interacaoRapida;
        this.pendingX = this.targetX;
        this.pendingY = this.targetY;
        this.pendingScale = this.targetScale;
        this.pendingCameraBasis = { ...this.committedCamera };

        const requestId = ++this.requestSerial;
        this.refineAfterRequestId = refineAfterPresentation ? requestId : 0;
        this.activeRequestId = requestId;
        this.lastRequestStart = performance.now();
        const limiteEspera = Math.max(3000, Math.min(15000, (this.currentLatency || 250) * 4 + 2000));
        clearTimeout(this.fetchWatchdog);
        this.fetchWatchdog = setTimeout(() => this.falharRequisicao(requestId), limiteEspera);

        this.dotNetHelper.invokeMethodAsync(
            'AtualizarCameraJS',
            this.pendingX,
            this.pendingY,
            this.pendingScale,
            !!interacaoRapida,
            requestId, this.pendingCameraBasis.panX, this.pendingCameraBasis.panY, this.pendingCameraBasis.zoom)
            .catch((erro) => {
                console.warn('[GEONEX] Falha ao enviar a câmera:', erro);
                this.falharRequisicao(requestId);
            });
    },

    falharRequisicao: function (requestId) {
        if (!this.isFetching || this.activeRequestId !== requestId) return;
        clearTimeout(this.fetchWatchdog);
        if (++this.retryCount > 2) {
            // Preserve the last complete image. A later user gesture may retry the
            // same transaction; never reapply the relative camera with a new ID.
            this.fetchWatchdog = null;
            console.warn('[GEONEX] Render interrompido após três tentativas.');
            return;
        }
        this.fetchWatchdog = setTimeout(() => this.falharRequisicao(requestId), 10000);
        this.dotNetHelper.invokeMethodAsync('AtualizarCameraJS',
            this.pendingX, this.pendingY, this.pendingScale, this.requestInteractive, requestId,
            this.pendingCameraBasis.panX, this.pendingCameraBasis.panY, this.pendingCameraBasis.zoom)
            .catch(() => this.falharRequisicao(requestId));
    },

    carregarImagemFrame: async function (url) {
        this.frameLoadController?.abort();
        this.frameLoadController = null;
        const direct = () => {
            const image = new Image(); image.decoding = 'async'; image.src = url;
            return image.decode().then(() => ({ image, payloadId: null, endpoint: null, reused: false, networkUrl: url }));
        };
        const target = new URL(url, document.baseURI);
        // Changing tiles rarely reuse identical pixels. Keep the native Image
        // loader as default: fetch/blob waits for the entire PNG before decode.
        // Enable reuse only for explicit WebView A/B measurements.
        if (window.GEONEX_FRAME_REUSE !== true || typeof fetch !== 'function' ||
            this.frameReuseUnavailableFor === target.origin + target.pathname ||
            typeof AbortController !== 'function' || target.searchParams.get('i') === '1' ||
            target.searchParams.get('c') === '1') return direct();
        const controller = new AbortController();
        this.frameLoadController = controller;
        const endpoint = target.origin + target.pathname;
        const base = this.decodedPayload?.endpoint === endpoint ? this.decodedPayload : null;
        target.searchParams.delete('reuse');
        if (base) target.searchParams.set('reuse', base.payloadId);
        try {
            for (let attempt = 0; attempt < 2; attempt++) {
                const response = await fetch(target.href, { signal: controller.signal, cache: 'no-store', credentials: 'omit' });
                const id = response.headers.get('X-GeoNex-Payload-Id');
                if (response.status === 204 && response.headers.get('X-GeoNex-Reused') === '1') {
                    if (base && this.decodedPayload === base && id === base.payloadId && base.image.width > 0 && base.image.height > 0)
                        return { ...base, reused: true, networkUrl: target.href };
                    target.searchParams.delete('reuse');
                    continue; // Lost base: request a full frame once.
                }
                if (response.status !== 200) throw new Error(`Frame HTTP ${response.status}`);
                const objectUrl = URL.createObjectURL(await response.blob());
                try {
                    const image = new Image(); image.decoding = 'async'; image.src = objectUrl;
                    await image.decode();
                    const payloadId = /^[a-f0-9]{32}$/.test(id || '') && image.width * image.height * 4 <= 16 * 1024 * 1024 ? id : null;
                    return { image, payloadId, endpoint, reused: false, networkUrl: target.href };
                } finally { URL.revokeObjectURL(objectUrl); }
            }
            throw new Error('Frame reutilizado sem imagem base válida');
        } catch (error) {
            if (controller.signal.aborted) throw new DOMException('Frame superseded', 'AbortError');
            if (error instanceof TypeError) {
                this.frameReuseUnavailableFor = endpoint;
                return direct(); // Avoid repeating double requests in unsupported hosts.
            }
            throw error;
        }
    },

    carregarNovoFrame: function (url, frameId, requestIdCamera, telemetryEnabled, padding = 0, visibleWidth = 0, visibleHeight = 0, cameraPanX = 0, cameraPanY = 0, cameraZoom = 1, refineOnline = false) {
        frameId = Number(frameId) || 0;
        requestIdCamera = Number(requestIdCamera) || 0;
        telemetryEnabled = telemetryEnabled === true;
        if (requestIdCamera > 0 && requestIdCamera !== this.activeRequestId) return;
        if (frameId > 0 && frameId <= this.latestFramePresented) return;
        if (requestIdCamera === 0 && this.isFetching) {
            this.hasPendingRequest = true;
            this.pendingInteractive = false;
            return;
        }
        const cameraEpoch = this.cameraEpoch;
        if (frameId > 0) {
            if (frameId < this.latestFrameRequested) return;
            this.latestFrameRequested = frameId;
        }

        var canvas = this.backCanvas;
        if (!canvas) {
            if (requestIdCamera === this.activeRequestId) this.falharRequisicao(requestIdCamera);
            return;
        }

        // TIER 3 (GOD TIER): DESCODIFICAÇÃO OFF-MAIN-THREAD (GPU Zero-Stutter)
        // Em vez de atirar a imagem para o DOM (o que bloqueia a Thread UI do Browser),
        // pedimos ao Browser para descodificar o PNG numa thread paralela.
        let tempImg, loadedFrame;
        const frameLoadStarted = performance.now();
        const requestStartedAt = this.lastRequestStart || frameLoadStarted;
        this.carregarImagemFrame(url).then(frame => {
            loadedFrame = frame; tempImg = frame.image;
            return new Promise(resolve => requestAnimationFrame(resolve));
        }).then(() => {
            const decodedAt = performance.now();
            if (cameraEpoch !== this.cameraEpoch) return;
            if (frameId > 0 && frameId <= this.latestFramePresented) return;
            if (frameId > 0 && frameId < this.latestFrameRequested) return;
            if (requestIdCamera > 0) {
                if (this.isFetching && requestIdCamera !== this.activeRequestId) return;
                if (!this.isFetching) return;
            }

            let networkMilliseconds = 0;
            let decodeMilliseconds = Math.max(0, decodedAt - frameLoadStarted);
            let transferBytes = 0;
            if (telemetryEnabled && performance.getEntriesByName) {
                const absoluteUrl = new URL(loadedFrame.networkUrl, document.baseURI).href;
                const resourceEntries = performance.getEntriesByName(absoluteUrl, 'resource');
                const resource = resourceEntries.length > 0
                    ? resourceEntries[resourceEntries.length - 1]
                    : null;
                if (resource) {
                    networkMilliseconds = Math.max(0, resource.responseEnd - resource.startTime);
                    decodeMilliseconds = Math.max(0, decodedAt - resource.responseEnd);
                    transferBytes = Math.max(0, resource.encodedBodySize || resource.transferSize || 0);
                }
            }

            if (loadedFrame.reused) { decodeMilliseconds = 0; transferBytes = 0; }
            // A imagem está 100% pronta e calculada na RAM da GPU
            const confirmouCamera = this.isFetching && requestIdCamera === this.activeRequestId;

            if (window.GeoNexGraphics) window.GeoNexGraphics.aplicarFuturo();
            
            // Swap imediato! Zero lag na UI. Desenhando diretamente no Canvas.
            if (canvas.width !== tempImg.width || canvas.height !== tempImg.height) {
                canvas.width = tempImg.width;
                canvas.height = tempImg.height;
            }
            var ctx = canvas.getContext('2d');
            const drawStarted = performance.now();
            // Only the hidden canvas is cleared/resized. Commit image and camera
            // before the browser paints; the visible canvas always stays complete.
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            ctx.drawImage(tempImg, 0, 0);
            canvas.style.width = visibleWidth > 0 ? `${visibleWidth + padding * 2}px` : '100%';
            canvas.style.height = visibleHeight > 0 ? `${visibleHeight + padding * 2}px` : '100%';
            canvas.style.left = `${-padding}px`;
            canvas.style.top = `${-padding}px`;
            canvas.style.visibility = 'visible';
            this.skiaCanvas.style.visibility = 'hidden';
            this.backCanvas = this.skiaCanvas;
            this.skiaCanvas = canvas;
            this.skiaCtx = ctx;
            const presentedAt = performance.now();
            const drawMilliseconds = Math.max(0, presentedAt - drawStarted);
            const endToEndMilliseconds = Math.max(0, presentedAt - requestStartedAt);

            if (confirmouCamera) {
                this.currentLatency = endToEndMilliseconds;
                this.lastRequestStart = null;
                if (this.dotNetHelper) {
                    this.dotNetHelper.invokeMethodAsync('AtualizarTelemetria', this.currentLatency)
                        .catch(() => { });
                }
            }
            if (telemetryEnabled && frameId > 0 && this.dotNetHelper) {
                this.dotNetHelper.invokeMethodAsync(
                    'RegistrarApresentacaoFrame',
                    frameId,
                    Number.isFinite(networkMilliseconds) ? networkMilliseconds : 0,
                    Number.isFinite(decodeMilliseconds) ? decodeMilliseconds : 0,
                    Number.isFinite(drawMilliseconds) ? drawMilliseconds : 0,
                    Number.isFinite(endToEndMilliseconds) ? endToEndMilliseconds : 0,
                    Number.isFinite(transferBytes) ? Math.trunc(transferBytes) : 0)
                    .catch(() => { });
            }

            if (confirmouCamera) {
                if (this.refineAfterRequestId === requestIdCamera) {
                    this.refineAfterRequestId = 0;
                    this.hasPendingRequest = true;
                    this.pendingInteractive = false;
                }
                this.targetScale = this.targetScale / this.pendingScale;
                this.currentScale = this.currentScale / this.pendingScale;
                this.targetX = this.targetX - (this.pendingX * this.targetScale);
                this.targetY = this.targetY - (this.pendingY * this.targetScale);
                this.currentX = this.currentX - (this.pendingX * this.currentScale);
                this.currentY = this.currentY - (this.pendingY * this.currentScale);
                clearTimeout(this.fetchWatchdog);
                this.fetchWatchdog = null;
                this.isFetching = false;
                this.lastCommittedRequestId = Math.max(this.lastCommittedRequestId, requestIdCamera);
                this.activeRequestId = 0;
                this.pendingScale = 1;
                this.pendingX = 0;
                this.pendingY = 0;
            }

            this.committedCamera = { panX: cameraPanX, panY: cameraPanY, zoom: cameraZoom };
            this.absoluteZoom = cameraZoom * this.targetScale;
            this.latestFramePresented = Math.max(this.latestFramePresented, frameId);
            this.decodedPayload = loadedFrame.payloadId ? loadedFrame : null;
            this.aplicarTransformacao();
            this.solicitarAnimacao();

            // A scene update may present sharp vectors before online tiles arrive.
            // Refine only after that frame commits, using its absolute camera.
            if (refineOnline && !this.hasPendingRequest) {
                this.hasPendingRequest = true;
                this.pendingInteractive = performance.now() - this.lastInteractionTime < this.obterAtrasoAssentamento();
            }
            if (this.hasPendingRequest) {
                const interacaoPendente = this.pendingInteractive;
                this.hasPendingRequest = false;
                this.pendingInteractive = false;
                const assentado = performance.now() - this.lastInteractionTime >= this.obterAtrasoAssentamento();
                setTimeout(() => this.solicitarFrame(assentado ? false : interacaoPendente), 0);
            }
        }).catch(e => {
            if (cameraEpoch !== this.cameraEpoch || e.name === 'AbortError') return;
            if (frameId > 0 && frameId < this.latestFrameRequested) return;
            if (requestIdCamera === this.activeRequestId) this.falharRequisicao(requestIdCamera);
            console.warn("[GEONEX] Descodificação do frame abortada: ", e);
        });
    }
};
// =======================================================
// MÓDULO DE ENGENHARIA E INTERAÇÃO ESPACIAL
// =======================================================

// --- 1. O LASER DE INSPEÇÃO (RAYCASTING SERVER-SIDE) ---
window.camadaDestaqueRaycast = null;

window.ativarSensorRaycast = function () {
    let mapa = obterMapaLeaflet();
    if (!mapa) return;

    mapa.on('click', function (e) {
        // Trava cega contra botão direito/meio
        if (e.originalEvent && e.originalEvent.button !== 0) return;

        // O motor nativo Leaflet não deve enviar o clique para o Raycast do C#
        // porque o nosso window.mapEngine (Motor de Física) JÁ FAZ ISSO no onPointerUp!
        // Se deixarmos isto ligado, gera o BUG DOS DOIS PONTOS NA ORIGEM.

        /* 
        if (window.dotnetReferencia) {
            window.dotnetReferencia.invokeMethodAsync('ProcessarCliqueRaycast', e.latlng.lat, e.latlng.lng)
                .catch(err => console.warn("Falha no túnel de Raycast:", err));
        }
        */
    });
};

window.destacarGeometria = function (geoJsonString) {
    let mapa = obterMapaLeaflet();
    if (!mapa) return;

    // Limpa a máscara do lote anterior
    if (window.camadaDestaqueRaycast) {
        mapa.removeLayer(window.camadaDestaqueRaycast);
    }

    if (!geoJsonString || geoJsonString.trim() === "") return;

    let dados = JSON.parse(geoJsonString);

    // Renderiza instantaneamente apenas a geometria clicada com máscara topográfica amarela
    window.camadaDestaqueRaycast = L.geoJSON(dados, {
        style: {
            color: "#facc15",
            weight: 3,
            fillColor: "#facc15",
            fillOpacity: 0.35,
            className: 'neon-vector'
        }
    }).addTo(mapa);
};

// --- 2. RÉGUA TOPOGRÁFICA DINÂMICA (Medição em Tempo Real) ---
window.ferramentaMedicaoAtiva = false;
window.grupoMedicao = null;
window.medicaoPontos = [];
window.medicaoLinhas = null;
window.medicaoRascunho = null;
window.medicaoTooltip = null;

window.alternarFerramentaMedicao = function (estado) {
    let mapa = obterMapaLeaflet();
    if (!mapa) return;

    window.ferramentaMedicaoAtiva = estado;

    if (estado) {
        mapa.getContainer().style.cursor = 'crosshair';
        window.grupoMedicao = L.layerGroup().addTo(mapa);
        window.medicaoPontos = [];

        mapa.on('click', adicionarPontoMedicao);
        mapa.on('mousemove', moverRascunhoMedicao);
        mapa.on('contextmenu', finalizarMedicao); // Clique direito trava a linha
    } else {
        mapa.getContainer().style.cursor = '';
        mapa.off('click', adicionarPontoMedicao);
        mapa.off('mousemove', moverRascunhoMedicao);
        mapa.off('contextmenu', finalizarMedicao);

        if (window.grupoMedicao) mapa.removeLayer(window.grupoMedicao);
        if (window.medicaoTooltip) {
            mapa.removeLayer(window.medicaoTooltip);
            window.medicaoTooltip = null;
        }
    }
};

function adicionarPontoMedicao(e) {
    let mapa = obterMapaLeaflet();
    window.medicaoPontos.push(e.latlng);

    // Vértice de ancoragem
    L.circleMarker(e.latlng, {
        radius: 4, color: '#0ea5e9', fillColor: '#161921', fillOpacity: 1, weight: 2
    }).addTo(window.grupoMedicao);

    // Solidifica o segmento anterior
    if (window.medicaoPontos.length > 1) {
        if (window.medicaoLinhas) mapa.removeLayer(window.medicaoLinhas);
        window.medicaoLinhas = L.polyline(window.medicaoPontos, { color: '#0ea5e9', weight: 3 }).addTo(window.grupoMedicao);
    }
}

function moverRascunhoMedicao(e) {
    if (window.medicaoPontos.length === 0) return;
    let mapa = obterMapaLeaflet();
    let ultimoPonto = window.medicaoPontos[window.medicaoPontos.length - 1];

    // Desenha a linha de projeção tracejada até ao rato
    if (window.medicaoRascunho) mapa.removeLayer(window.medicaoRascunho);
    window.medicaoRascunho = L.polyline([ultimoPonto, e.latlng], {
        color: '#0ea5e9', weight: 2, dashArray: '5, 5'
    }).addTo(window.grupoMedicao);

    // Executa o cálculo trigonométrico da distância total
    let distanciaMetros = 0;
    for (let i = 0; i < window.medicaoPontos.length - 1; i++) {
        distanciaMetros += window.medicaoPontos[i].distanceTo(window.medicaoPontos[i + 1]);
    }
    distanciaMetros += ultimoPonto.distanceTo(e.latlng);

    let texto = distanciaMetros > 1000 ? (distanciaMetros / 1000).toFixed(2) + ' km' : distanciaMetros.toFixed(2) + ' m';
    let uiVidro = `<div class="glass-tooltip">${texto}</div>`;

    if (window.medicaoTooltip) {
        window.medicaoTooltip.setLatLng(e.latlng).setContent(uiVidro);
    } else {
        window.medicaoTooltip = L.tooltip({
            permanent: true, direction: 'right', offset: [15, 0], className: 'ferramenta-tooltip-invisivel'
        })
            .setLatLng(e.latlng)
            .setContent(uiVidro)
            .addTo(mapa);
    }
}

function finalizarMedicao(e) {
    e.originalEvent.preventDefault(); // Impede o menu do sistema operativo de abrir
    let mapa = obterMapaLeaflet();
    if (window.medicaoRascunho) mapa.removeLayer(window.medicaoRascunho);

    window.medicaoPontos = [];
    if (window.medicaoTooltip) {
        mapa.removeLayer(window.medicaoTooltip);
        window.medicaoTooltip = null;
    }
}
window.GeoNexGraphics = {
    _destaque: [], _medicao: [], _mouse: null, _snap: null,
    _futuroDestaque: null, _futuroMedicao: null,
    _mostrarArea: false, _futuroMostrarArea: null,
    _fase: 0, _animId: null,

    redimensionar: function () {
        const cvs = document.getElementById('overlayCanvas');
        if (cvs && cvs.parentElement) {
            const larguraCss = Math.max(1, cvs.parentElement.clientWidth);
            const alturaCss = Math.max(1, cvs.parentElement.clientHeight);
            const dpi = window.dimensoesJanela ? window.dimensoesJanela.obterDpi() : 1;
            cvs.width = Math.max(1, Math.round(larguraCss * dpi));
            cvs.height = Math.max(1, Math.round(alturaCss * dpi));
            this.desenharFrameEstatico();
        }
    },

    definirAtivos: function (destaque, medicao, mostrarArea) {
        this._destaque = destaque || [];
        this._medicao = medicao || [];
        this._mostrarArea = mostrarArea || false;
        this.iniciar();
    },

    prepararFuturo: function (destaque, medicao, mostrarArea) {
        this._futuroDestaque = destaque || [];
        this._futuroMedicao = medicao || [];
        this._futuroMostrarArea = mostrarArea || false;
    },

    aplicarFuturo: function () {
        if (this._futuroDestaque !== null) { this._destaque = this._futuroDestaque; this._futuroDestaque = null; }
        if (this._futuroMedicao !== null) { this._medicao = this._futuroMedicao; this._futuroMedicao = null; }
        if (this._futuroMostrarArea !== null) { this._mostrarArea = this._futuroMostrarArea; this._futuroMostrarArea = null; }
        this.redimensionar();
    },

    definirMouseESnap: function (mx, my, sx, sy) {
        this._mouse = (mx !== null && my !== null) ? { x: mx, y: my } : null;
        this._snap = (sx !== null && sy !== null) ? { x: sx, y: sy } : null;
        this.desenharFrameEstatico();
    },

    limpar: function () {
        this._destaque = []; this._medicao = []; this._mouse = null; this._snap = null;
        this._futuroDestaque = null; this._futuroMedicao = null; this._mostrarArea = false;
        if (this._animId) { cancelAnimationFrame(this._animId); this._animId = null; }
        this.desenharFrameEstatico();
    },

    iniciar: function () {
        this.redimensionar();
        if (!this._animId && this._destaque.length > 0) this._loop();
        else if (this._destaque.length === 0) this.desenharFrameEstatico();
    },

    _loop: function () {
        // LIMITADOR DE GPU: Roda a animação a 30 FPS em vez do máximo do monitor
        setTimeout(() => {
            this._fase = (this._fase + 0.5) % 15;
            this.desenharFrameEstatico();

            if (this._destaque.length > 0) {
                this._animId = requestAnimationFrame(() => this._loop());
            } else {
                this._animId = null;
            }
        }, 33);
    },

    desenharFrameEstatico: function () {
        const cvs = document.getElementById('overlayCanvas');
        if (!cvs) return;
        const ctx = cvs.getContext('2d');
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, cvs.width, cvs.height);
        const escalaX = cvs.width / Math.max(1, cvs.clientWidth);
        const escalaY = cvs.height / Math.max(1, cvs.clientHeight);
        ctx.setTransform(escalaX, 0, 0, escalaY, 0, 0);

        // DESENHA APENAS O CONTORNO ANIMADO (A régua e o Snap agora são desenhados perfeitamente pelo C#)
        if (this._destaque && this._destaque.length > 0) {
            ctx.save(); ctx.beginPath(); ctx.strokeStyle = '#facc15'; ctx.lineWidth = 3; ctx.lineJoin = 'round';
            ctx.setLineDash([10, 5]); ctx.lineDashOffset = -this._fase;
            this._destaque.forEach(anel => {
                if (anel.length < 2) return;
                ctx.moveTo(anel[0].x, anel[0].y);
                for (let i = 1; i < anel.length; i++) ctx.lineTo(anel[i].x, anel[i].y);
                ctx.closePath();
            });
            ctx.stroke(); ctx.restore();
        }
    }
};


window.addEventListener('resize', () => { if (window.GeoNexGraphics) window.GeoNexGraphics.redimensionar(); });
// GESTOR GLOBAL DE ATALHOS (Resolve o problema do Foco no MAUI)
// =========================================================================
document.addEventListener('keydown', function (event) {
    // Ignora se o engenheiro estiver a digitar dentro de um campo de texto
    const tagsIgnoradas = ['INPUT', 'TEXTAREA', 'SELECT'];
    if (tagsIgnoradas.includes(event.target.tagName)) return;

    const tecla = event.key.toLowerCase();

    // Se for uma das nossas teclas de atalho (m, i, escape)
    if (tecla === 'escape' || tecla === 'm' || tecla === 'i') {
        if (window.mapEngine && window.mapEngine.dotNetHelper) {
            event.preventDefault(); // Impede comportamentos estranhos do navegador

            // Atira o comando diretamente para a máquina de estados do C#
            window.mapEngine.dotNetHelper.invokeMethodAsync('ProcessarTecladoGlobal', tecla)
                .catch(err => console.warn("Erro no atalho: ", err));
        }
    }
});
// === BLOQUEIO DO WINDOWS E TRAVA DO RATO ===
document.addEventListener('mousedown', function (e) {
    // Se for o botão do meio (1), impede o Windows de abrir a "bolinha de scroll"
    if (e.button === 1) {
        e.preventDefault();
    }
}, { passive: false });

window.mapEngine = window.mapEngine || {};

window.mapEngine.prenderRato = function (pointerId) {
    var mapa = document.getElementById('map-container');
    if (mapa && mapa.setPointerCapture) mapa.setPointerCapture(pointerId);
};

window.mapEngine.soltarRato = function (pointerId) {
    var mapa = document.getElementById('map-container');
    if (mapa && mapa.hasPointerCapture && mapa.hasPointerCapture(pointerId)) {
        mapa.releasePointerCapture(pointerId);
    }
};
// =========================================================================
// MOTOR DE ARRASTO DE JANELAS (HARDWARE ACCELERATION BYPASS BLAZOR)
// =========================================================================
window.iniciarArrasteHUD = function (e, elementId) {
    e.preventDefault(); // Impede que selecione texto enquanto arrasta
    const el = document.getElementById(elementId);
    if (!el) return;

    // Lê a coordenada exata onde o ecrã desenhou a janela agora
    const style = window.getComputedStyle(el);
    const matrix = new DOMMatrix(style.transform);
    let startX = matrix.m41;
    let startY = matrix.m42;

    const initialMouseX = e.clientX;
    const initialMouseY = e.clientY;

    let finalX = startX;
    let finalY = startY;

    // Função que mexe a janela na velocidade nativa do monitor
    function onMouseMove(event) {
        const dx = event.clientX - initialMouseX;
        const dy = event.clientY - initialMouseY;
        finalX = startX + dx;
        finalY = startY + dy;

        // Atira para a GPU instantaneamente
        el.style.transform = `translate3d(${finalX}px, ${finalY}px, 0)`;
    }

    // Função que avisa o C# quando acabar
    function onMouseUp(event) {
        window.removeEventListener('pointermove', onMouseMove);
        window.removeEventListener('pointerup', onMouseUp);

        // Sincroniza silenciosamente com o C# para a janela não voltar para trás
        let dotNet = window.dotnetReferencia || (window.mapEngine && window.mapEngine.dotNetHelper);
        if (dotNet) {
            dotNet.invokeMethodAsync('AtualizarMemoriaHUD', finalX, finalY);
        }
    }

    // Liga os sensores de alta velocidade no ecrã inteiro
    window.addEventListener('pointermove', onMouseMove);
    window.addEventListener('pointerup', onMouseUp);
};
