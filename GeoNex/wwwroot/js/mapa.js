// mapa.js - Motor de Geovisualização WMS e Vetorial (GeoNex Desktop)

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

    window.camadaDinamicaWMS = L.imageOverlay('', [[0, 0], [0, 0]], {
        opacity: 1.0,
        interactive: true,
        className: 'geonex-raster-layer'
    }).addTo(window.geonexMap);

    window.geonexMap.on('mousemove', function (e) {
        if (window.dotnetReferencia) {
            window.dotnetReferencia.invokeMethodAsync('AtualizarCoordenadas', e.latlng.lat, e.latlng.lng);
        }
    });

    window.geonexMap.on('moveend', function () {
        if (window.dotnetReferencia) {
            var bounds = window.geonexMap.getBounds();
            var size = window.geonexMap.getSize();

            window.dotnetReferencia.invokeMethodAsync('SolicitarRecorteAltaResolucao',
                bounds.getSouth(), bounds.getWest(), bounds.getNorth(), bounds.getEast(),
                Math.round(size.x), Math.round(size.y)
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
        window.camadaDinamicaWMS.setUrl('data:image/png;base64,' + base64Image);
        window.camadaDinamicaWMS.setBounds(limites);
        window.geonexMap.flyToBounds(limites, { padding: [50, 50], duration: 2.0 });
    }
};

window.atualizarImagemNoMapa = function (base64Image, minLat, minLng, maxLat, maxLng) {
    if (window.camadaDinamicaWMS) {
        var limites = [[minLat, minLng], [maxLat, maxLng]];
        window.camadaDinamicaWMS.setUrl('data:image/png;base64,' + base64Image);
        window.camadaDinamicaWMS.setBounds(limites);
    }
};

// 4. MOTOR DE VETORES
window.adicionarVetorAoMapa = function (geoJsonString, nomeCamada) {
    if (window.geonexMap) {
        var dados = JSON.parse(geoJsonString);

        var camadaVetor = L.geoJSON(dados, {
            style: function (feature) {
                return {
                    color: "#0ea5e9",
                    weight: 1,
                    fillColor: "#38bdf8",
                    fillOpacity: 0.3
                };
            },
            onEachFeature: function (feature, layer) {
                layer.on('click', function (e) {
                    L.DomEvent.stopPropagation(e); // Impede que o clique "vaze"

                    // 1. Limpa o destaque anterior e pinta o lote atual de amarelo
                    window.resetarEstilosVetores();
                    layer.setStyle({ color: "#facc15", weight: 3, fillOpacity: 0.6 });

                    // ==========================================================
                    // NOVO: MEMÓRIA DA FEIÇÃO SELECIONADA
                    // ==========================================================
                    window.feicaoSelecionadaAtual = layer;
                    window.camadaDaFeicaoSelecionada = nomeCamada;

                    // Se a ferramenta de vértices estiver ligada, desenha as caixas só aqui!
                    if (window.ferramentaVerticesAtiva) {
                        window.gerarVerticesParaFeicaoSelecionada();
                    }
                    // ==========================================================

                    // 2. Envia os dados pelo túnel até ao Blazor (C#)
                    if (window.dotnetReferencia) {
                        window.dotnetReferencia.invokeMethodAsync('SelecionarFeicaoNoMapa',
                            nomeCamada,
                            JSON.stringify(feature.properties)
                        ).catch(erro => alert("Erro na ponte JS -> C#: " + erro));
                    }
                });
            }
        }).addTo(window.geonexMap);

        // Guarda na memória
        window.camadasGeoNex[nomeCamada] = camadaVetor;

        // Voa até ao vetor
        window.geonexMap.flyToBounds(camadaVetor.getBounds(), { padding: [50, 50], duration: 2.0 });
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
window.alterarEstiloCamada = function (nomeCamada, corPreenchimento, corBorda, espessura, opacidade) {
    var camadaAlvo = window.camadasGeoNex[nomeCamada];

    if (camadaAlvo) {
        camadaAlvo.setStyle({
            fillColor: corPreenchimento,
            color: corBorda,
            weight: parseFloat(espessura),
            fillOpacity: parseFloat(opacidade)
        });
        return true;
    }
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
window.zoomParaCamada = function (nomeCamada) {
    var camada = window.camadasGeoNex[nomeCamada];

    // Verifica se a camada existe, se tem uma extensão física e se está no mapa
    if (camada && typeof camada.getBounds === 'function' && camada._map) {
        // O padding garante que o mapa não fica colado à borda do ecrã
        camada._map.fitBounds(camada.getBounds(), { padding: [30, 30] });
        return true;
    }
    return false;
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
document.addEventListener('keydown', function (event) {
    // Evita disparar se estivermos a escrever o nome de uma rua ou lote
    const tagsIgnoradas = ['INPUT', 'TEXTAREA', 'SELECT'];
    if (tagsIgnoradas.includes(event.target.tagName)) {
        return;
    }

    if (event.key === 'v' || event.key === 'V') {
        event.preventDefault(); // Impede comportamentos estranhos do navegador

        if (window.dotnetReferencia) {
            // CENÁRIO 1: O rato está em cima de um Vértice? Abre a Janela de Edição daquele Vértice!
            if (window.verticeSobO_Mouse) {
                window.dotnetReferencia.invokeMethodAsync('PedirCoordenadaVertice',
                    window.verticeSobO_Mouse.camada,
                    window.verticeSobO_Mouse.indice,
                    window.verticeSobO_Mouse.lat,
                    window.verticeSobO_Mouse.lng
                ).catch(erro => console.error("Erro na edição de vértice: ", erro));
            }
            // CENÁRIO 2: O rato está vazio? Abre a Caderneta de Campo (Novo Polígono).
            else {
                window.dotnetReferencia.invokeMethodAsync('AbrirModalCoordenadasAtalho')
                    .catch(erro => console.error("Erro ao invocar atalho V: ", erro));
            }
        }
    }
});
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
    obter: function () {
        return { largura: window.innerWidth, altura: window.innerHeight };
    },
    registrarResize: function (dotnetHelper) {
        window.addEventListener('resize', function () {
            dotnetHelper.invokeMethodAsync('AtualizarDimensoesTela', window.innerWidth, window.innerHeight);
        });
    }
};
// === MOTOR DE RENDERIZAÇÃO SKIASHARP (DOUBLE BUFFERING) ===
window.mapEngine = {
    container: null,
    imgAtiva: null,

    init: function () {
        // Certifique-se de que o ID bate com a div principal do seu mapa no Home.razor
        this.container = document.getElementById('map-container');
        this.imgAtiva = document.getElementById('skia-layer');
    },

    transformar: function (x, y, scale) {
        if (this.container) {
            this.container.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
        }
    },

    carregarNovoFrame: function (url) {
        if (!this.imgAtiva) return;
        var imgClone = new Image();
        imgClone.onload = () => {
            this.imgAtiva.src = url;
            this.container.style.transform = `translate(0px, 0px) scale(1)`;
        };
        imgClone.src = url;
    }
};