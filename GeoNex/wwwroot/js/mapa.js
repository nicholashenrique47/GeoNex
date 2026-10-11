// mapa.js - Motor de Geovisualização WMS e Vetorial (GeoNex Desktop)
// =========================================================================
// Mensagens de interface ficam dentro do GeoNex em vez de abrir o alert modal
// padrão do WebView/Windows, que interrompe o fluxo e usa uma aparência alheia ao app.
window.alert = function (message) {
    const texto = String(message ?? '');
    const normalizado = texto.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
    const erro = /erro|falha|nao foi possivel|nao pode|critico|invalido|cancelad/.test(normalizado);
    const aviso = !erro && /aviso|atencao|reconect|offline|nao foi/.test(normalizado);
    const hostId = 'geonex-alert-stack';
    let host = document.getElementById(hostId);
    if (!host && document.body) {
        host = document.createElement('div');
        host.id = hostId;
        host.className = 'geonex-alert-stack';
        host.setAttribute('aria-live', 'polite');
        host.setAttribute('aria-relevant', 'additions');
        document.body.appendChild(host);
    }
    if (!host) {
        console.warn('[GeoNex]', texto);
        return;
    }

    const toast = document.createElement('section');
    toast.className = `geonex-alert-toast${erro ? ' is-error' : aviso ? ' is-warning' : ''}`;
    toast.setAttribute('role', erro || aviso ? 'alert' : 'status');
    const mark = document.createElement('span');
    mark.className = 'geonex-alert-mark';
    mark.setAttribute('aria-hidden', 'true');
    mark.textContent = erro ? '!' : aviso ? '⚠' : 'i';
    const copy = document.createElement('div');
    copy.className = 'geonex-alert-copy';
    const title = document.createElement('div');
    title.className = 'geonex-alert-title';
    title.textContent = erro ? 'NÃO FOI POSSÍVEL CONCLUIR' : aviso ? 'ATENÇÃO' : 'GEONEX';
    const body = document.createElement('div');
    body.className = 'geonex-alert-message';
    body.textContent = texto;
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'geonex-alert-dismiss';
    close.setAttribute('aria-label', 'Fechar notificação');
    close.textContent = '×';
    copy.append(title, body);
    toast.append(mark, copy, close);
    host.prepend(toast);

    let timer = window.setTimeout(() => toast.remove(), erro || aviso ? 15000 : 8000);
    const pause = () => window.clearTimeout(timer);
    const resume = () => { timer = window.setTimeout(() => toast.remove(), 8000); };
    toast.addEventListener('mouseenter', pause);
    toast.addEventListener('mouseleave', resume);
    close.addEventListener('click', () => toast.remove());
    while (host.children.length > 5) host.lastElementChild?.remove();
};

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
        let linha = L.polyline(arrayCoordenadas, { color: '#3b82f6', weight: 4 });
        linha.feature = { type: "Feature", properties: { ID: "Eixo_" + Math.floor(Math.random() * 1000) } };
        camadaAlvo.addLayer(linha);

    } else if (tipoGeometria === "POLIGONO") {

        listaPontos.forEach(pt => arrayCoordenadas.push([parseFloat(pt.Lat), parseFloat(pt.Lng)]));
        let poligono = L.polygon(arrayCoordenadas, { color: '#3b82f6', weight: 2, fillColor: '#a855f7', fillOpacity: 0.2 });
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
        const map = document.getElementById('map-container');
        return {
            largura: Math.max(1, map ? map.clientWidth : Math.round(window.innerWidth)),
            altura: Math.max(1, map ? map.clientHeight : Math.round(window.innerHeight)),
            dpi: window.dimensoesJanela.obterDpi()
        };
    },
    registrarResize: function (dotnetHelper) {
        this.resizeObserver?.disconnect();
        if (this.resizeListener) window.removeEventListener('resize', this.resizeListener);
        let timer = null;
        const notificar = function () {
            if (window.mapEngine) window.mapEngine.viewportResizePending = true;
            clearTimeout(timer);
            timer = setTimeout(function () {
                const dimensoes = window.dimensoesJanela.obter();
                dotnetHelper.invokeMethodAsync(
                    'AtualizarDimensoesTela',
                    dimensoes.largura,
                    dimensoes.altura,
                    dimensoes.dpi).catch(err => console.warn('Erro ao redimensionar mapa:', err));
            }, 60);
        };
        window.addEventListener('resize', notificar);
        this.resizeListener = notificar;
        const map = document.getElementById('map-container');
        if (map && window.ResizeObserver) {
            this.resizeObserver = new ResizeObserver(notificar);
            this.resizeObserver.observe(map);
        }

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
    toolPointerPending: null,
    toolPointerLatest: null,
    toolPointerFrame: null,
    toolPointerSettleTimer: null,
    toolPointerThrottleTimer: null,
    toolPointerLastSentAt: 0,
    toolPointerPendingInteractive: true,
    toolPointerInFlight: false,
    toolPointerSequence: 0,
    vertexEditDragging: false,
    vertexEditPointerStart: null,
    lastPointerClientX: -1,
    lastPointerClientY: -1,

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
        if (this.uiModalAberto()) return;
        if (['Medicao', 'AquisicaoPoligono', 'AquisicaoLinha', 'AquisicaoPonto'].includes(this.ferramentaAtual)) return;
        this.aplicarZoomCentralizado(2.0, e.clientX, e.clientY, false);
    },

    onWheel: function (e) {
        if (this.uiModalAberto()) return;
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

    obterPontoImagem: function (clientX, clientY) {
        const rect = this.container.getBoundingClientRect();
        const x = clientX - rect.left - this.container.clientLeft;
        const y = clientY - rect.top - this.container.clientTop;
        const cx = this.container.clientWidth / 2;
        const cy = this.container.clientHeight / 2;
        return {
            x: (x - cx - this.currentX) / this.currentScale + cx,
            y: (y - cy - this.currentY) / this.currentScale + cy
        };
    },

    scheduleToolPointerFlush: function () {
        if (this.toolPointerFrame !== null || this.toolPointerInFlight ||
            !this.toolPointerPending || !this.dotNetHelper) return;
        if (this.ferramentaAtual === 'Medicao' && window.GeoNexGraphics?._medicaoCursorAtiva) {
            const wait = 34 - (performance.now() - this.toolPointerLastSentAt);
            if (wait > 0) {
                if (this.toolPointerThrottleTimer === null) {
                    this.toolPointerThrottleTimer = setTimeout(() => {
                        this.toolPointerThrottleTimer = null;
                        this.scheduleToolPointerFlush();
                    }, wait);
                }
                return;
            }
        }
        this.toolPointerFrame = requestAnimationFrame(() => this.flushToolPointer());
    },

    resetToolPointer: function () {
        this.toolPointerPending = null;
        this.toolPointerLatest = null;
        if (this.toolPointerFrame !== null) cancelAnimationFrame(this.toolPointerFrame);
        this.toolPointerFrame = null;
        if (this.toolPointerSettleTimer !== null) clearTimeout(this.toolPointerSettleTimer);
        this.toolPointerSettleTimer = null;
        if (this.toolPointerThrottleTimer !== null) clearTimeout(this.toolPointerThrottleTimer);
        this.toolPointerThrottleTimer = null;
        this.toolPointerPendingInteractive = true;
        this.toolPointerSequence++;
        if (window.GeoNexGraphics)
            window.GeoNexGraphics.invalidarCursorAquisicao(this.toolPointerSequence);
    },

    queueToolPointer: function (clientX, clientY) {
        if (this.viewportResizePending) {
            this.resetToolPointer();
            return;
        }
        const point = this.obterPontoImagem(clientX, clientY);
        const sequence = ++this.toolPointerSequence;
        const sample = {
            clientX, clientY, point, sequence,
            vertexEditStart: this.ferramentaAtual === 'EdicaoVertice' ? this.vertexEditPointerStart : null
        };
        this.toolPointerLatest = sample;
        this.toolPointerPending = sample;
        this.toolPointerPendingInteractive = true;
        if (this.ferramentaAtual === 'Medicao' && window.GeoNexGraphics)
            window.GeoNexGraphics.atualizarCursorMedicao(point.x, point.y, sequence);
        if (['AquisicaoPoligono', 'AquisicaoLinha', 'AquisicaoPonto'].includes(this.ferramentaAtual) && window.GeoNexGraphics)
            window.GeoNexGraphics.atualizarCursorAquisicao(point.x, point.y, sequence);
        if (this.toolPointerSettleTimer !== null) clearTimeout(this.toolPointerSettleTimer);
        this.toolPointerSettleTimer = setTimeout(() => {
            this.toolPointerSettleTimer = null;
            if (!this.toolPointerLatest || !this.dotNetHelper) return;
            this.toolPointerPending = this.toolPointerLatest;
            this.toolPointerPendingInteractive = false;
            this.scheduleToolPointerFlush();
        }, 120);
        this.scheduleToolPointerFlush();
    },

    flushToolPointer: function () {
        this.toolPointerFrame = null;
        if (!this.toolPointerPending || this.toolPointerInFlight || !this.dotNetHelper) return;
        const sample = this.toolPointerPending;
        const interacaoRapida = this.toolPointerPendingInteractive;
        this.toolPointerPending = null;
        if (this.ferramentaAtual === 'Medicao' && !window.GeoNexGraphics?._medicaoCursorAtiva) return;
        if (this.ferramentaAtual === 'Medicao') this.toolPointerLastSentAt = performance.now();
        const point = sample.point || this.obterPontoImagem(sample.clientX, sample.clientY);
        const digitizingMatrix = window.GeoNexGraphics
            ? window.GeoNexGraphics.obterMatrizApresentada()
            : null;
        this.toolPointerInFlight = true;
        this.dotNetHelper.invokeMethodAsync('ReceberMovimentoFerramentas',
            point.x, point.y, interacaoRapida, sample.sequence, digitizingMatrix,
            sample.vertexEditStart?.x ?? null, sample.vertexEditStart?.y ?? null)
            .catch(err => console.warn('Erro na prévia da ferramenta:', err))
            .finally(() => {
                this.toolPointerInFlight = false;
                this.scheduleToolPointerFlush();
            });
    },

    onPointerMove: function (e) {
        this.lastPointerClientX = e.clientX;
        this.lastPointerClientY = e.clientY;
        if (this.uiModalAberto()) {
            this.resetToolPointer();
            return;
        }
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
            if (['Medicao', 'AquisicaoPoligono', 'AquisicaoLinha', 'AquisicaoPonto', 'EdicaoVertice'].includes(this.ferramentaAtual))
                this.queueToolPointer(e.clientX, e.clientY);
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

    // Modais são uma superfície modal de verdade: nenhum gesto do mapa
    // escapa para trás enquanto o usuário edita propriedades ou atributos.
    uiModalAberto: function () {
        return !!document.querySelector('.modal-overlay[aria-modal="true"]');
    },

    setFerramenta: function (nomeFerramenta) {
        this.resetToolPointer();
        this.ferramentaAtual = nomeFerramenta;
        if (nomeFerramenta !== 'EdicaoVertice') {
            this.vertexEditDragging = false;
            this.vertexEditPointerStart = null;
        }
        if (this.container) {
            this.container.style.cursor =
                nomeFerramenta === 'Navegacao' ? 'grab' :
                    (nomeFerramenta === 'Medicao' || nomeFerramenta === 'AquisicaoPoligono' || nomeFerramenta === 'EdicaoVertice' ? 'crosshair' : 'default');
        }
    },

    setVertexEditDragging: function (active) {
        this.vertexEditDragging = active === true;
    },

    onPointerDown: function (e) {
        this.lastPointerClientX = e.clientX;
        this.lastPointerClientY = e.clientY;
        this.resetToolPointer();
        if (this.uiModalAberto()) {
            e.preventDefault();
            return;
        }
        if (e.pointerType === 'mouse' && e.button !== 0 && e.button !== 1) return;

        this.pointerIds.add(e.pointerId);
        try { this.container.setPointerCapture(e.pointerId); } catch (_) { }

        this.startX = e.clientX;
        this.startY = e.clientY;
        this.clickStartTime = performance.now();
        this.vertexEditPointerStart = this.ferramentaAtual === 'EdicaoVertice'
            ? this.obterPontoImagem(e.clientX, e.clientY)
            : null;

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
        if (this.uiModalAberto()) {
            this.pointerIds.delete(e.pointerId);
            return;
        }
        if (!this.pointerIds.has(e.pointerId)) return;
        this.pointerIds.delete(e.pointerId);
        if (e.pointerType === 'touch') this.activeTouches.delete(e.pointerId);
        try {
            if (this.container.hasPointerCapture(e.pointerId)) this.container.releasePointerCapture(e.pointerId);
        } catch (_) { }

        if (cancelado) {
            this.vertexEditDragging = false;
            this.vertexEditPointerStart = null;
            this.isDragging = false;
            this.isPinching = false;
            this.touchGestureMoved = false;
            this.velocityX = 0;
            this.velocityY = 0;
            this.scheduleRender();
            return;
        }

        if (this.ferramentaAtual === 'EdicaoVertice' && this.vertexEditDragging) {
            this.vertexEditDragging = false;
            this.vertexEditPointerStart = null;
            this.resetToolPointer();
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
        this.vertexEditPointerStart = null;
    },

    dispararRaycast: function (clientX, clientY) {
        if (this.viewportResizePending) return;
        if (!this.dotNetHelper) return;
        this.resetToolPointer();
        const point = this.obterPontoImagem(clientX, clientY);
        const digitizingMatrix = window.GeoNexGraphics
            ? window.GeoNexGraphics.obterMatrizApresentada()
            : null;
        this.dotNetHelper.invokeMethodAsync('ProcessarCliqueRaycast', point.x, point.y, digitizingMatrix)
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

    carregarNovoFrame: function (url, frameId, requestIdCamera, telemetryEnabled, padding = 0, visibleWidth = 0, visibleHeight = 0, cameraPanX = 0, cameraPanY = 0, cameraZoom = 1, refineOnline = false, digitizingMatrix = null, clearPreviewAfterFrameId = 0, clearPreviewRevision = 0) {
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
            this.presentedViewport = { width: visibleWidth, height: visibleHeight };
            const viewport = window.dimensoesJanela.obter();
            if (Math.abs(visibleWidth - viewport.largura) <= 1 && Math.abs(visibleHeight - viewport.altura) <= 1)
                this.viewportResizePending = false;
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
            if (window.GeoNexGraphics && digitizingMatrix)
                window.GeoNexGraphics.apresentarFrameAquisicao(
                    digitizingMatrix, frameId, clearPreviewAfterFrameId, clearPreviewRevision);
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
window.geonexCopiarTexto = async function (texto) {
    if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        await navigator.clipboard.writeText(String(texto ?? ''));
        return true;
    }
    const area = document.createElement('textarea');
    area.value = String(texto ?? '');
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    const copiado = document.execCommand('copy');
    area.remove();
    if (!copiado) throw new Error('A área de transferência não está disponível.');
    return true;
};

window.GeoNexGraphics = {
    _destaque: [], _medicao: [], _mouse: null, _snap: null,
    _futuroDestaque: null, _futuroMedicao: null,
    _mostrarArea: false, _futuroMostrarArea: null,
    _medicaoCursorAtiva: false, _medicaoCursorFrame: null,
    _medicaoPointerSequence: 0, _medicaoSnapTipo: null,
    _medicaoStaticCanvas: null, _medicaoStaticCtx: null,
    _medicaoCursorCanvas: null, _medicaoCursorCtx: null,
    _fase: 0, _animId: null,
    _aquisicao: {
        pontos: [], ativa: false, tipo: 'POLIGONO',
        matriz: [1, 0, 0, 1, 0, 0, 0, 0], revisao: 0,
        matrizApresentada: false,
        restricaoAtiva: false, restricaoRaio: 0, restricaoFixa: true,
        coordenadaAbsolutaFixa: false,
        cursorImagem: null, cursorLocal: null, snap: null,
        sequenciaPonteiro: 0, revisaoLimpaConfirmada: 0
    },
    _aquisicaoPath: null,
    _aquisicaoCacheSuja: true,
    _aquisicaoStaticNeedsComposition: true,
    _aquisicaoFillCanvas: null,
    _aquisicaoFillCtx: null,
    _aquisicaoLineCanvas: null,
    _aquisicaoLineCtx: null,
    _aquisicaoStaticCanvas: null,
    _aquisicaoStaticCtx: null,
    _aquisicaoCursorCanvas: null,
    _aquisicaoCursorCtx: null,
    _aquisicaoCursorAnterior: null,

    redimensionar: function () {
        const cvs = document.getElementById('overlayCanvas');
        if (cvs && cvs.parentElement) {
            const larguraCss = Math.max(1, cvs.parentElement.clientWidth);
            const alturaCss = Math.max(1, cvs.parentElement.clientHeight);
            const dpi = window.dimensoesJanela ? window.dimensoesJanela.obterDpi() : 1;
            cvs.width = Math.max(1, Math.round(larguraCss * dpi));
            cvs.height = Math.max(1, Math.round(alturaCss * dpi));
            if (this._aquisicao.ativa || this._aquisicao.pontos.length > 0 ||
                this._aquisicaoFillCanvas || this._aquisicaoLineCanvas)
                this._garantirCachesAquisicao(cvs);
            if (this._medicaoStaticCanvas || this._medicao.length > 0)
                this._garantirCamadasMedicao(cvs);
            this._aquisicaoCacheSuja = true;
            this.desenharFrameEstatico();
            if (this._medicaoStaticCanvas) this._desenharMedicaoEstatico();
            this._agendarCursorMedicao();
        }
    },

    _lerMatrizAquisicao: function (matriz) {
        if (!Array.isArray(matriz) || (matriz.length !== 6 && matriz.length !== 8)) return null;
        const result = matriz.map(Number);
        if (!result.every(Number.isFinite)) return null;
        const determinante = result[0] * result[3] - result[1] * result[2];
        if (Math.abs(determinante) <= 1e-18) return null;
        if (result.length === 6) result.push(0, 0);
        return result;
    },

    _pontoRelativoAquisicao: function (point) {
        const matrix = this._aquisicao.matriz;
        return { x: point.x - (matrix[6] || 0), y: point.y - (matrix[7] || 0) };
    },

    _localParaImagemAquisicao: function (point) {
        const [a, b, c, d, e, f] = this._aquisicao.matriz;
        const relative = this._pontoRelativoAquisicao(point);
        return { x: a * relative.x + c * relative.y + e,
            y: b * relative.x + d * relative.y + f };
    },

    _garantirCachesAquisicao: function (referencia) {
        if (!this._aquisicaoFillCanvas) {
            this._aquisicaoFillCanvas = document.createElement('canvas');
            this._aquisicaoFillCtx = this._aquisicaoFillCanvas.getContext('2d');
        }
        if (!this._aquisicaoLineCanvas) {
            this._aquisicaoLineCanvas = document.createElement('canvas');
            this._aquisicaoLineCtx = this._aquisicaoLineCanvas.getContext('2d');
        }
        let resized = false;
        for (const canvas of [this._aquisicaoFillCanvas, this._aquisicaoLineCanvas]) {
            if (canvas.width !== referencia.width || canvas.height !== referencia.height) {
                canvas.width = referencia.width;
                canvas.height = referencia.height;
                resized = true;
            }
        }
        if (resized) this._aquisicaoCacheSuja = true;
        return resized;
    },

    _garantirCamadasVisiveisAquisicao: function (referencia) {
        const parent = referencia?.parentElement;
        if (!parent) return false;
        let created = false;
        if (!this._aquisicaoStaticCanvas) {
            this._aquisicaoStaticCanvas = document.createElement('canvas');
            this._aquisicaoStaticCanvas.id = 'digitizing-static-preview';
            this._aquisicaoStaticCanvas.style.cssText =
                'width:100%;height:100%;position:absolute;left:0;top:0;pointer-events:none;z-index:11;';
            this._aquisicaoStaticCtx = this._aquisicaoStaticCanvas.getContext('2d');
            parent.appendChild(this._aquisicaoStaticCanvas);
            created = true;
        } else if (this._aquisicaoStaticCanvas.parentElement !== parent) {
            parent.appendChild(this._aquisicaoStaticCanvas);
            created = true;
        }
        if (!this._aquisicaoCursorCanvas) {
            this._aquisicaoCursorCanvas = document.createElement('canvas');
            this._aquisicaoCursorCanvas.id = 'digitizing-cursor-preview';
            this._aquisicaoCursorCanvas.style.cssText =
                'width:100%;height:100%;position:absolute;left:0;top:0;pointer-events:none;z-index:12;';
            this._aquisicaoCursorCtx = this._aquisicaoCursorCanvas.getContext('2d');
            parent.appendChild(this._aquisicaoCursorCanvas);
            created = true;
        } else if (this._aquisicaoCursorCanvas.parentElement !== parent) {
            parent.appendChild(this._aquisicaoCursorCanvas);
            created = true;
        }

        let resized = false;
        for (const canvas of [this._aquisicaoStaticCanvas, this._aquisicaoCursorCanvas]) {
            if (canvas.width !== referencia.width || canvas.height !== referencia.height) {
                canvas.width = referencia.width;
                canvas.height = referencia.height;
                resized = true;
            }
        }
        if (created || resized) {
            this._aquisicaoCacheSuja = true;
            this._aquisicaoStaticNeedsComposition = true;
            this._aquisicaoCursorAnterior = null;
        }
        this._aquisicaoStaticCanvas.style.display = this._aquisicao.pontos.length > 0 ? '' : 'none';
        this._aquisicaoCursorCanvas.style.display = this._aquisicao.ativa ? '' : 'none';
        return resized;
    },

    _garantirCamadasMedicao: function (referencia) {
        const parent = referencia?.parentElement;
        if (!parent) return false;
        let changed = false;
        if (!this._medicaoStaticCanvas) {
            this._medicaoStaticCanvas = document.createElement('canvas');
            this._medicaoStaticCanvas.id = 'measurement-static-preview';
            this._medicaoStaticCanvas.style.cssText =
                'width:100%;height:100%;position:absolute;left:0;top:0;pointer-events:none;z-index:13;';
            this._medicaoStaticCtx = this._medicaoStaticCanvas.getContext('2d');
            parent.appendChild(this._medicaoStaticCanvas);
            changed = true;
        } else if (this._medicaoStaticCanvas.parentElement !== parent) {
            parent.appendChild(this._medicaoStaticCanvas);
            changed = true;
        }
        if (!this._medicaoCursorCanvas) {
            this._medicaoCursorCanvas = document.createElement('canvas');
            this._medicaoCursorCanvas.id = 'measurement-cursor-preview';
            this._medicaoCursorCanvas.style.cssText =
                'width:100%;height:100%;position:absolute;left:0;top:0;pointer-events:none;z-index:14;';
            this._medicaoCursorCtx = this._medicaoCursorCanvas.getContext('2d');
            parent.appendChild(this._medicaoCursorCanvas);
            changed = true;
        } else if (this._medicaoCursorCanvas.parentElement !== parent) {
            parent.appendChild(this._medicaoCursorCanvas);
            changed = true;
        }

        let resized = false;
        for (const canvas of [this._medicaoStaticCanvas, this._medicaoCursorCanvas]) {
            if (canvas.width !== referencia.width || canvas.height !== referencia.height) {
                canvas.width = referencia.width;
                canvas.height = referencia.height;
                resized = true;
            }
        }
        this._medicaoStaticCanvas.style.display = this._medicao.length > 0 ? '' : 'none';
        this._medicaoCursorCanvas.style.display = this._medicaoCursorAtiva ? '' : 'none';
        return changed || resized;
    },

    _desenharMedicaoEstatico: function () {
        const referencia = document.getElementById('overlayCanvas');
        if (!referencia || !this._garantirCamadasMedicao(referencia)) {
            if (!this._medicaoStaticCanvas) return;
        }
        const canvas = this._medicaoStaticCanvas;
        const ctx = this._medicaoStaticCtx;
        if (!canvas || !ctx) return;
        const cssWidth = Math.max(1, canvas.clientWidth);
        const cssHeight = Math.max(1, canvas.clientHeight);
        const scaleX = canvas.width / cssWidth;
        const scaleY = canvas.height / cssHeight;
        const mapScale = Math.max(0.05, Math.abs(window.mapEngine?.currentScale || 1));
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        ctx.setTransform(scaleX, 0, 0, scaleY, 0, 0);
        if (this._medicao.length === 0) return;

        ctx.save();
        ctx.lineJoin = 'round';
        ctx.lineCap = 'round';
        ctx.beginPath();
        ctx.moveTo(this._medicao[0].x, this._medicao[0].y);
        for (let i = 1; i < this._medicao.length; i++)
            ctx.lineTo(this._medicao[i].x, this._medicao[i].y);

        if (this._mostrarArea && this._medicao.length > 2) {
            ctx.save();
            ctx.closePath();
            ctx.fillStyle = 'rgba(239, 68, 68, .38)';
            ctx.fill();
            ctx.restore();

            ctx.beginPath();
            ctx.moveTo(this._medicao[this._medicao.length - 1].x, this._medicao[this._medicao.length - 1].y);
            ctx.lineTo(this._medicao[0].x, this._medicao[0].y);
            ctx.strokeStyle = 'rgba(255,255,255,.72)';
            ctx.lineWidth = 1.5 / mapScale;
            ctx.setLineDash([10 / mapScale, 10 / mapScale]);
            ctx.stroke();
            ctx.setLineDash([]);
        }

        ctx.beginPath();
        ctx.moveTo(this._medicao[0].x, this._medicao[0].y);
        for (let i = 1; i < this._medicao.length; i++)
            ctx.lineTo(this._medicao[i].x, this._medicao[i].y);
        ctx.strokeStyle = '#2563eb';
        ctx.lineWidth = 2.5 / mapScale;
        ctx.stroke();
        for (const point of this._medicao) {
            ctx.beginPath();
            ctx.arc(point.x, point.y, 5 / mapScale, 0, Math.PI * 2);
            ctx.fillStyle = '#f59e0b';
            ctx.fill();
            ctx.lineWidth = 1.5 / mapScale;
            ctx.strokeStyle = '#fff7ed';
            ctx.stroke();
        }
        ctx.restore();
    },

    _agendarCursorMedicao: function () {
        if (this._medicaoCursorFrame !== null) return;
        this._medicaoCursorFrame = requestAnimationFrame(() => {
            this._medicaoCursorFrame = null;
            this._desenharCursorMedicao();
        });
    },

    _desenharCursorMedicao: function () {
        const referencia = document.getElementById('overlayCanvas');
        if (!referencia || !this._medicaoCursorCanvas) return;
        this._garantirCamadasMedicao(referencia);
        const canvas = this._medicaoCursorCanvas;
        const ctx = this._medicaoCursorCtx;
        if (!ctx) return;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        if (!this._medicaoCursorAtiva || !this._mouse) return;

        const scaleX = canvas.width / Math.max(1, canvas.clientWidth);
        const scaleY = canvas.height / Math.max(1, canvas.clientHeight);
        const cssWidth = Math.max(1, canvas.clientWidth);
        const cssHeight = Math.max(1, canvas.clientHeight);
        const mapScale = Math.max(0.05, Math.abs(window.mapEngine?.currentScale || 1));
        const cursor = this._snap || this._mouse;
        ctx.setTransform(scaleX, 0, 0, scaleY, 0, 0);
        ctx.save();
        if (this._medicao.length > 0) {
            const anchor = this._medicao[this._medicao.length - 1];
            ctx.beginPath();
            ctx.moveTo(anchor.x, anchor.y);
            ctx.lineTo(cursor.x, cursor.y);
            ctx.strokeStyle = 'rgba(37,99,235,.86)';
            ctx.lineWidth = 1.5 / mapScale;
            ctx.setLineDash([10 / mapScale, 10 / mapScale]);
            ctx.stroke();
            ctx.setLineDash([]);
        }

        ctx.beginPath();
        ctx.moveTo(0, cursor.y);
        ctx.lineTo(cssWidth, cursor.y);
        ctx.moveTo(cursor.x, 0);
        ctx.lineTo(cursor.x, cssHeight);
        ctx.strokeStyle = 'rgba(255,255,255,.20)';
        ctx.lineWidth = 1 / mapScale;
        ctx.stroke();

        if (this._snap) {
            const size = 7 / mapScale;
            const type = this._medicaoSnapTipo;
            const colors = { Vertex: '#fde047', Midpoint: '#22d3ee', Edge: '#fb923c', Intersection: '#e879f9' };
            ctx.strokeStyle = colors[type] || '#fde047';
            ctx.fillStyle = type === 'Vertex' ? 'rgba(253,224,71,.2)' : 'rgba(34,211,238,.18)';
            ctx.lineWidth = 2 / mapScale;
            if (type === 'Edge') {
                ctx.beginPath();
                ctx.moveTo(cursor.x, cursor.y - size);
                ctx.lineTo(cursor.x + size, cursor.y);
                ctx.lineTo(cursor.x, cursor.y + size);
                ctx.lineTo(cursor.x - size, cursor.y);
                ctx.closePath();
                ctx.fill(); ctx.stroke();
            } else {
                ctx.beginPath();
                ctx.arc(cursor.x, cursor.y, size * .58, 0, Math.PI * 2);
                ctx.fill(); ctx.stroke();
                if (type === 'Intersection') {
                    ctx.beginPath();
                    ctx.moveTo(cursor.x - size, cursor.y - size); ctx.lineTo(cursor.x + size, cursor.y + size);
                    ctx.moveTo(cursor.x + size, cursor.y - size); ctx.lineTo(cursor.x - size, cursor.y + size);
                    ctx.stroke();
                }
            }
        } else {
            ctx.beginPath();
            ctx.arc(cursor.x, cursor.y, 3.5 / mapScale, 0, Math.PI * 2);
            ctx.fillStyle = '#f8fafc';
            ctx.fill();
            ctx.lineWidth = 1.6 / mapScale;
            ctx.strokeStyle = '#2563eb';
            ctx.stroke();
        }
        ctx.restore();
    },

    _definirTransformacaoAquisicao: function (ctx, referencia) {
        const larguraCss = Math.max(1, referencia.clientWidth);
        const alturaCss = Math.max(1, referencia.clientHeight);
        const escalaX = referencia.width / larguraCss;
        const escalaY = referencia.height / alturaCss;
        const [a, b, c, d, e, f] = this._aquisicao.matriz;
        ctx.setTransform(escalaX * a, escalaY * b, escalaX * c, escalaY * d,
            escalaX * e, escalaY * f);
        return Math.max(1e-8, (Math.hypot(a, b) + Math.hypot(c, d)) / 2);
    },

    _limparCanvasAquisicao: function (ctx, canvas) {
        if (!ctx || !canvas) return;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
    },

    _criarPathAquisicao: function () {
        const pontos = this._aquisicao.pontos;
        if (typeof Path2D !== 'function' || pontos.length === 0) return null;
        const primeiro = this._pontoRelativoAquisicao(pontos[0]);
        const path = new Path2D();
        path.moveTo(primeiro.x, primeiro.y);
        for (let i = 1; i < pontos.length; i++) {
            const point = this._pontoRelativoAquisicao(pontos[i]);
            path.lineTo(point.x, point.y);
        }
        return path;
    },

    _desenharMarcadorAquisicao: function (ctx, point, index, escalaMapa) {
        const raio = 5 / escalaMapa;
        const cor = index === 0 && this._aquisicao.tipo !== 'PONTO' ? '#f59e0b' : '#e64f5d';
        const relative = this._pontoRelativoAquisicao(point);
        ctx.save();
        ctx.beginPath();
        ctx.arc(relative.x, relative.y, 6.25 / escalaMapa, 0, Math.PI * 2);
        ctx.fillStyle = 'rgba(9, 18, 28, .92)';
        ctx.fill();

        ctx.beginPath();
        ctx.arc(relative.x, relative.y, raio, 0, Math.PI * 2);
        ctx.fillStyle = cor;
        ctx.fill();
        ctx.lineWidth = 1.4 / escalaMapa;
        ctx.strokeStyle = '#f8fafc';
        ctx.stroke();

        ctx.beginPath();
        ctx.arc(relative.x, relative.y, 1.45 / escalaMapa, 0, Math.PI * 2);
        ctx.fillStyle = '#ffffff';
        ctx.fill();
        ctx.restore();
    },

    _desenharPathAquisicao: function (ctx, path, fechar = false) {
        const pontos = this._aquisicao.pontos;
        if (path) {
            if (fechar) {
                ctx.fill(path, 'evenodd');
            } else {
                ctx.stroke(path);
            }
            return;
        }
        if (pontos.length === 0) return;
        const first = this._pontoRelativoAquisicao(pontos[0]);
        ctx.beginPath();
        ctx.moveTo(first.x, first.y);
        for (let i = 1; i < pontos.length; i++) {
            const point = this._pontoRelativoAquisicao(pontos[i]);
            ctx.lineTo(point.x, point.y);
        }
        if (fechar) {
            ctx.closePath();
            ctx.fill('evenodd');
        } else {
            ctx.stroke();
        }
    },

    _reconstruirCacheAquisicao: function (referencia) {
        this._garantirCachesAquisicao(referencia);
        this._limparCanvasAquisicao(this._aquisicaoFillCtx, this._aquisicaoFillCanvas);
        this._limparCanvasAquisicao(this._aquisicaoLineCtx, this._aquisicaoLineCanvas);
        const pontos = this._aquisicao.pontos;
        if (pontos.length === 0) {
            this._aquisicaoPath = null;
            this._aquisicaoCacheSuja = false;
            return;
        }
        if (!this._aquisicaoPath) this._aquisicaoPath = this._criarPathAquisicao();

        const ctxFill = this._aquisicaoFillCtx;
        const ctxLine = this._aquisicaoLineCtx;
        const escalaMapa = this._definirTransformacaoAquisicao(ctxLine, referencia);
        if (this._aquisicao.tipo === 'POLIGONO' && pontos.length >= 3) {
            this._definirTransformacaoAquisicao(ctxFill, referencia);
            ctxFill.fillStyle = 'rgba(168, 85, 247, 0.18)';
            this._desenharPathAquisicao(ctxFill, this._aquisicaoPath, true);
        }

        ctxLine.lineWidth = 3.2 / escalaMapa;
        ctxLine.lineJoin = 'round';
        ctxLine.lineCap = 'round';
        ctxLine.strokeStyle = 'rgba(255,255,255,.94)';
        this._desenharPathAquisicao(ctxLine, this._aquisicaoPath);
        ctxLine.lineWidth = 1.8 / escalaMapa;
        ctxLine.strokeStyle = '#2563eb';
        this._desenharPathAquisicao(ctxLine, this._aquisicaoPath);

        const passoMarcador = Math.max(1, Math.ceil(pontos.length / 2048));
        for (let i = 0; i < pontos.length; i += passoMarcador)
            this._desenharMarcadorAquisicao(ctxLine, pontos[i], i, escalaMapa);
        if ((pontos.length - 1) % passoMarcador !== 0)
            this._desenharMarcadorAquisicao(ctxLine, pontos[pontos.length - 1], pontos.length - 1, escalaMapa);
        this._aquisicaoCacheSuja = false;
    },

    _atualizarCachePreenchimentoAquisicao: function (referencia) {
        this._limparCanvasAquisicao(this._aquisicaoFillCtx, this._aquisicaoFillCanvas);
        if (this._aquisicao.tipo !== 'POLIGONO' || this._aquisicao.pontos.length < 3) return;
        this._definirTransformacaoAquisicao(this._aquisicaoFillCtx, referencia);
        this._aquisicaoFillCtx.fillStyle = 'rgba(168, 85, 247, 0.18)';
        this._desenharPathAquisicao(this._aquisicaoFillCtx, this._aquisicaoPath, true);
    },

    definirAquisicao: function (points, active, matrix, revision, geometryType,
        restrictionActive, restrictionRadius, fixedRestriction) {
        const parsedMatrix = this._lerMatrizAquisicao(matrix);
        const aq = this._aquisicao;
        this._aquisicao.pontos = Array.isArray(points)
            ? points.filter(p => p && Number.isFinite(Number(p.x)) && Number.isFinite(Number(p.y)))
                .map(p => ({ x: Number(p.x), y: Number(p.y) }))
            : [];
        this._aquisicao.ativa = active === true;
        this._aquisicao.tipo = geometryType || 'POLIGONO';
        if (restrictionActive !== undefined) aq.restricaoAtiva = restrictionActive === true;
        if (restrictionRadius !== undefined) aq.restricaoRaio = Number(restrictionRadius);
        if (fixedRestriction !== undefined) aq.restricaoFixa = fixedRestriction === true;
        if (parsedMatrix) this._aquisicao.matriz = parsedMatrix;
        this._aquisicao.revisao = Number(revision) || 0;
        this._aquisicao.cursorImagem = null;
        this._aquisicao.cursorLocal = null;
        this._aquisicao.snap = null;
        this._aquisicao.coordenadaAbsolutaFixa = false;
        this._aquisicaoPath = null;
        this._aquisicaoCacheSuja = true;
        this.desenharFrameEstatico();
    },

    definirRestricaoDistanciaAquisicao: function (active, radius, fixed) {
        const aq = this._aquisicao;
        aq.restricaoAtiva = active === true;
        aq.restricaoRaio = Number(radius);
        aq.restricaoFixa = fixed === true;
        aq.cursorImagem = null;
        aq.cursorLocal = null;
        aq.snap = null;
        this._aquisicaoStaticNeedsComposition = true;
        this.desenharFrameEstatico();

        const engine = window.mapEngine;
        if (aq.ativa && engine?.container && !engine.isDragging && !engine.uiModalAberto()) {
            const x = engine.lastPointerClientX;
            const y = engine.lastPointerClientY;
            const rect = engine.container.getBoundingClientRect();
            if (x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom)
                requestAnimationFrame(() => engine.queueToolPointer(x, y));
        }
    },

    definirCoordenadaAbsolutaAquisicao: function (active, x, y) {
        const aq = this._aquisicao;
        const ponto = { x: Number(x), y: Number(y) };
        if (active && (!Number.isFinite(ponto.x) || !Number.isFinite(ponto.y))) return;
        aq.coordenadaAbsolutaFixa = active === true;
        aq.snap = null;
        if (!aq.coordenadaAbsolutaFixa) {
            aq.cursorImagem = null;
            aq.cursorLocal = null;
            this._desenharCursorAquisicao();
            return;
        }
        aq.cursorLocal = ponto;
        aq.cursorImagem = this._localParaImagemAquisicao(ponto);
        this._desenharCursorAquisicao();
    },

    definirAquisicaoAtiva: function (active) {
        this._aquisicao.ativa = active === true;
        this._aquisicao.cursorImagem = null;
        this._aquisicao.cursorLocal = null;
        this._aquisicao.snap = null;
        this._aquisicaoStaticNeedsComposition = true;
        this.desenharFrameEstatico();
    },

    obterMatrizAquisicao: function () {
        return this._aquisicao.ativa ? this._aquisicao.matriz.slice() : null;
    },

    obterMatrizApresentada: function () {
        return this._aquisicao.matrizApresentada ? this._aquisicao.matriz.slice() : null;
    },

    adicionarVerticeAquisicao: function (revision, x, y) {
        const point = { x: Number(x), y: Number(y) };
        revision = Number(revision) || 0;
        if (!this._aquisicao.ativa || revision <= this._aquisicao.revisao ||
            !Number.isFinite(point.x) || !Number.isFinite(point.y)) return;
        const anterior = this._aquisicao.pontos[this._aquisicao.pontos.length - 1];
        this._aquisicao.pontos.push(point);
        this._aquisicao.revisao = revision;
        const relativePoint = this._pontoRelativoAquisicao(point);
        if (this._aquisicaoPath) this._aquisicaoPath.lineTo(relativePoint.x, relativePoint.y);
        else this._aquisicaoPath = this._criarPathAquisicao();

        const referencia = document.getElementById('overlayCanvas');
        if (referencia && !this._aquisicaoCacheSuja && this._aquisicaoLineCtx) {
            const ctx = this._aquisicaoLineCtx;
            const escalaMapa = this._definirTransformacaoAquisicao(ctx, referencia);
            const relativePrevious = anterior ? this._pontoRelativoAquisicao(anterior) : null;
            ctx.lineWidth = 3.2 / escalaMapa;
            ctx.lineJoin = 'round';
            ctx.lineCap = 'round';
            ctx.strokeStyle = 'rgba(255,255,255,.94)';
            if (anterior) {
                ctx.beginPath();
                ctx.moveTo(relativePrevious.x, relativePrevious.y);
                ctx.lineTo(relativePoint.x, relativePoint.y);
                ctx.stroke();
                ctx.lineWidth = 1.8 / escalaMapa;
                ctx.strokeStyle = '#2563eb';
                ctx.stroke();
            }
            this._desenharMarcadorAquisicao(ctx, point, this._aquisicao.pontos.length - 1, escalaMapa);
            this._atualizarCachePreenchimentoAquisicao(referencia);
            this._aquisicaoStaticNeedsComposition = true;
        } else {
            this._aquisicaoCacheSuja = true;
            this._aquisicaoStaticNeedsComposition = true;
        }
        this._aquisicao.cursorImagem = null;
        this._aquisicao.cursorLocal = null;
        this._aquisicao.snap = null;
        this.desenharFrameEstatico();
        this._desenharCursorAquisicao();
    },

    atualizarMatrizAquisicao: function (matrix) {
        const parsedMatrix = this._lerMatrizAquisicao(matrix);
        if (!parsedMatrix) return;
        this._aquisicao.matriz = parsedMatrix;
        this._aquisicaoPath = null;
        this._aquisicao.cursorImagem = null;
        this._aquisicao.cursorLocal = null;
        this._aquisicao.snap = null;
        this._aquisicaoCacheSuja = true;
        this.desenharFrameEstatico();
    },

    apresentarFrameAquisicao: function (matrix, frameId, clearAfterFrameId, clearRevision) {
        const parsedMatrix = this._lerMatrizAquisicao(matrix);
        let matrizAlterada = false;
        if (parsedMatrix) {
            matrizAlterada = parsedMatrix.some((value, index) => value !== this._aquisicao.matriz[index]);
            this._aquisicao.matriz = parsedMatrix;
            this._aquisicao.matrizApresentada = true;
            if (matrizAlterada) {
                this._aquisicaoPath = null;
                this._aquisicao.cursorImagem = null;
                this._aquisicao.cursorLocal = null;
                this._aquisicao.snap = null;
                this._aquisicaoCacheSuja = true;
            }
        }

        frameId = Number(frameId) || 0;
        clearAfterFrameId = Number(clearAfterFrameId) || 0;
        clearRevision = Number(clearRevision) || 0;
        let estadoPreviaAlterado = false;
        if (clearAfterFrameId > 0 && frameId >= clearAfterFrameId && clearRevision > 0 &&
            this._aquisicao.revisaoLimpaConfirmada !== clearRevision) {
            if (this._aquisicao.revisao === clearRevision) {
                this._aquisicao.pontos = [];
                this._aquisicaoPath = null;
                this._aquisicaoCacheSuja = true;
            }
            this._aquisicao.cursorImagem = null;
            this._aquisicao.cursorLocal = null;
            this._aquisicao.snap = null;
            this._aquisicao.revisaoLimpaConfirmada = clearRevision;
            estadoPreviaAlterado = true;
            if (window.mapEngine && window.mapEngine.dotNetHelper)
                window.mapEngine.dotNetHelper.invokeMethodAsync(
                    'ConfirmarPreviaVetorizacaoApresentada', frameId, clearRevision).catch(() => { });
        }
        if (!this._aquisicao.ativa && this._aquisicao.pontos.length === 0 &&
            !estadoPreviaAlterado) {
            if (matrizAlterada && this._medicaoCursorAtiva)
                this._recalcularCursorFerramentaApresentado();
            return;
        }
        this.desenharFrameEstatico();
        if (matrizAlterada && (this._aquisicao.ativa || this._medicaoCursorAtiva))
            this._recalcularCursorFerramentaApresentado();
    },

    _recalcularCursorFerramentaApresentado: function () {
        const engine = window.mapEngine;
        if (!engine?.container || engine.isDragging || engine.uiModalAberto()) return;
        const x = engine.lastPointerClientX;
        const y = engine.lastPointerClientY;
        const rect = engine.container.getBoundingClientRect();
        if (x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom)
            requestAnimationFrame(() => engine.queueToolPointer(x, y));
    },

    invalidarCursorAquisicao: function (sequence) {
        sequence = Number(sequence) || 0;
        if (sequence < this._aquisicao.sequenciaPonteiro) return;
        this._aquisicao.sequenciaPonteiro = sequence;
        this._aquisicao.cursorImagem = null;
        this._aquisicao.cursorLocal = null;
        this._aquisicao.snap = null;
        this._desenharCursorAquisicao();
    },

    _aplicarRestricaoCursorAquisicao: function (point) {
        const aq = this._aquisicao;
        if (!aq.restricaoAtiva || !Number.isFinite(aq.restricaoRaio) ||
            aq.restricaoRaio <= 0 || aq.pontos.length === 0) return point;

        const anchor = aq.pontos[aq.pontos.length - 1];
        const dx = point.x - anchor.x;
        const dy = point.y - anchor.y;
        const length = Math.hypot(dx, dy);
        if (!Number.isFinite(length) || length <= 0 || (!aq.restricaoFixa && length <= aq.restricaoRaio))
            return point;

        const factor = aq.restricaoRaio / length;
        return { x: anchor.x + dx * factor, y: anchor.y + dy * factor };
    },

    atualizarCursorAquisicao: function (x, y, sequence) {
        const aq = this._aquisicao;
        if (!aq.ativa) return;
        if (aq.coordenadaAbsolutaFixa) return;
        sequence = Number(sequence) || 0;
        if (sequence < aq.sequenciaPonteiro) return;
        const ponto = { x: Number(x), y: Number(y) };
        if (!Number.isFinite(ponto.x) || !Number.isFinite(ponto.y)) return;
        const [a, b, c, d, e, f] = aq.matriz;
        const det = a * d - b * c;
        if (!Number.isFinite(det) || Math.abs(det) <= 1e-18) return;
        const dx = ponto.x - e;
        const dy = ponto.y - f;
        const originX = aq.matriz[6] || 0;
        const originY = aq.matriz[7] || 0;
        aq.sequenciaPonteiro = sequence;
        aq.cursorLocal = this._aplicarRestricaoCursorAquisicao({
            x: originX + (d * dx - c * dy) / det,
            y: originY + (-b * dx + a * dy) / det
        });
        aq.cursorImagem = this._localParaImagemAquisicao(aq.cursorLocal);
        aq.snap = null;
        this._desenharCursorAquisicao();
    },

    definirCursorResolvidoAquisicao: function (sequence, x, y, snapX, snapY, snapKind) {
        const aq = this._aquisicao;
        if (aq.coordenadaAbsolutaFixa) return;
        sequence = Number(sequence) || 0;
        if (!aq.ativa || sequence !== aq.sequenciaPonteiro ||
            !Number.isFinite(Number(x)) || !Number.isFinite(Number(y))) return;
        aq.cursorLocal = { x: Number(x), y: Number(y) };
        aq.cursorImagem = this._localParaImagemAquisicao(aq.cursorLocal);
        aq.snap = snapX !== null && snapX !== undefined && snapY !== null && snapY !== undefined
            ? { x: Number(snapX), y: Number(snapY), kind: snapKind || 'Vertex' }
            : null;
        this._desenharCursorAquisicao();
    },

    _desenharSnapAquisicao: function (ctx, snap, escalaMapa) {
        if (!snap) return;
        const size = 14 / escalaMapa;
        const color = snap.kind === 'Midpoint' ? '#22d3ee' :
            snap.kind === 'Edge' ? '#fb923c' :
                snap.kind === 'Intersection' ? '#e879f9' : '#facc15';
        const relative = this._pontoRelativoAquisicao(snap);
        ctx.save();
        ctx.lineWidth = 2 / escalaMapa;
        ctx.strokeStyle = color;
        ctx.fillStyle = `${color}55`;
        ctx.beginPath();
        if (snap.kind === 'Edge') {
            ctx.moveTo(relative.x, relative.y - size * 0.55);
            ctx.lineTo(relative.x + size * 0.55, relative.y);
            ctx.lineTo(relative.x, relative.y + size * 0.55);
            ctx.lineTo(relative.x - size * 0.55, relative.y);
            ctx.closePath();
        } else if (snap.kind === 'Midpoint') {
            ctx.arc(relative.x, relative.y, size * 0.45, 0, Math.PI * 2);
        } else {
            ctx.rect(relative.x - size / 2, relative.y - size / 2, size, size);
        }
        ctx.fill();
        ctx.stroke();
        if (snap.kind === 'Intersection') {
            ctx.beginPath();
            ctx.moveTo(relative.x - size, relative.y - size);
            ctx.lineTo(relative.x + size, relative.y + size);
            ctx.moveTo(relative.x + size, relative.y - size);
            ctx.lineTo(relative.x - size, relative.y + size);
            ctx.stroke();
        } else if (snap.kind === 'Vertex') {
            ctx.beginPath();
            ctx.moveTo(relative.x - size, relative.y);
            ctx.lineTo(relative.x + size, relative.y);
            ctx.moveTo(relative.x, relative.y - size);
            ctx.lineTo(relative.x, relative.y + size);
            ctx.stroke();
        }
        ctx.restore();
    },

    _desenharGuiaRestricaoAquisicao: function (ctx, referencia) {
        const aq = this._aquisicao;
        if (!aq.ativa || !aq.restricaoAtiva || !Number.isFinite(aq.restricaoRaio) ||
            aq.restricaoRaio <= 0 || aq.pontos.length === 0) return;

        const escalaMapa = this._definirTransformacaoAquisicao(ctx, referencia);
        const ancora = aq.pontos[aq.pontos.length - 1];
        const relativeAnchor = this._pontoRelativoAquisicao(ancora);
        ctx.save();
        ctx.beginPath();
        ctx.arc(relativeAnchor.x, relativeAnchor.y, aq.restricaoRaio, 0, Math.PI * 2);
        ctx.setLineDash([]);
        ctx.lineCap = 'round';
        ctx.strokeStyle = aq.restricaoFixa ? 'rgba(72,218,255,.96)' : 'rgba(115,205,237,.86)';
        ctx.lineWidth = 1.8 / escalaMapa;
        ctx.shadowColor = 'rgba(2,12,20,.78)';
        ctx.shadowBlur = 2;
        ctx.stroke();
        ctx.shadowBlur = 0;
        ctx.restore();
    },

    _comporPreviaEstaticoAquisicao: function (referencia) {
        if (!this._aquisicao.ativa && this._aquisicao.pontos.length === 0 &&
            !this._aquisicaoStaticCanvas) return;
        this._garantirCamadasVisiveisAquisicao(referencia);
        const cacheRedraw = this._aquisicaoCacheSuja;
        if (cacheRedraw) this._reconstruirCacheAquisicao(referencia);
        if (!cacheRedraw && !this._aquisicaoStaticNeedsComposition) return;

        const canvas = this._aquisicaoStaticCanvas;
        const ctx = this._aquisicaoStaticCtx;
        if (!canvas || !ctx) return;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        if (this._aquisicao.pontos.length > 0) {
            if (this._aquisicaoFillCanvas) ctx.drawImage(this._aquisicaoFillCanvas, 0, 0);
            if (this._aquisicaoLineCanvas) ctx.drawImage(this._aquisicaoLineCanvas, 0, 0);
        }
        this._desenharGuiaRestricaoAquisicao(ctx, referencia);
        this._aquisicaoStaticNeedsComposition = false;
    },

    _limparRetanguloCursorAquisicao: function (ctx, escalaX, escalaY, x, y, largura, altura) {
        ctx.clearRect(x * escalaX, y * escalaY, largura * escalaX, altura * escalaY);
    },

    _desenharCursorAquisicao: function () {
        const referencia = document.getElementById('overlayCanvas');
        if (!referencia) return;
        if (!this._aquisicaoCursorCanvas && !this._aquisicao.ativa) return;
        if (!this._aquisicaoCursorCanvas) this._garantirCamadasVisiveisAquisicao(referencia);
        const canvasCursor = this._aquisicaoCursorCanvas;
        const ctx = this._aquisicaoCursorCtx;
        if (!canvasCursor || !ctx) return;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        const escalaX = canvasCursor.width / Math.max(1, referencia.clientWidth);
        const escalaY = canvasCursor.height / Math.max(1, referencia.clientHeight);
        const anterior = this._aquisicaoCursorAnterior;
        if (anterior) {
            if (Number.isFinite(anterior.x) && Number.isFinite(anterior.y)) {
                // Snap glyphs can extend 14 CSS px from their anchor (intersection
                // and vertex markers). Clear their full footprint before repainting,
                // otherwise old arms remain as stray marks while the cursor moves.
                const margemMarcador = 18;
                this._limparRetanguloCursorAquisicao(ctx, escalaX, escalaY,
                    anterior.x - 2, 0, 4, referencia.clientHeight);
                this._limparRetanguloCursorAquisicao(ctx, escalaX, escalaY,
                    0, anterior.y - 2, referencia.clientWidth, 4);
                this._limparRetanguloCursorAquisicao(ctx, escalaX, escalaY,
                    anterior.x - margemMarcador, anterior.y - margemMarcador,
                    margemMarcador * 2, margemMarcador * 2);
            }
            if (anterior.segment) {
                const segment = anterior.segment;
                this._limparRetanguloCursorAquisicao(ctx, escalaX, escalaY,
                    segment.left, segment.top, segment.width, segment.height);
            }
        }
        this._aquisicaoCursorAnterior = null;
        const aq = this._aquisicao;
        if (!aq.ativa) return;

        const pontos = aq.pontos;
        const escalaMapa = this._definirTransformacaoAquisicao(ctx, referencia);
        if (aq.coordenadaAbsolutaFixa && aq.cursorLocal) {
            aq.cursorImagem = this._localParaImagemAquisicao(aq.cursorLocal);
        }
        if (!aq.cursorImagem) return;

        ctx.save();
        ctx.setTransform(escalaX, 0, 0, escalaY, 0, 0);
        ctx.beginPath();
        ctx.strokeStyle = aq.coordenadaAbsolutaFixa ? 'rgba(89,197,210,.32)' : 'rgba(255,255,255,.28)';
        ctx.lineWidth = 1;
        ctx.setLineDash([]);
        ctx.moveTo(aq.cursorImagem.x, 0);
        ctx.lineTo(aq.cursorImagem.x, referencia.clientHeight);
        ctx.moveTo(0, aq.cursorImagem.y);
        ctx.lineTo(referencia.clientWidth, aq.cursorImagem.y);
        ctx.stroke();
        if (aq.coordenadaAbsolutaFixa) {
            const arm = 6;
            ctx.beginPath();
            ctx.moveTo(aq.cursorImagem.x - arm, aq.cursorImagem.y);
            ctx.lineTo(aq.cursorImagem.x + arm, aq.cursorImagem.y);
            ctx.moveTo(aq.cursorImagem.x, aq.cursorImagem.y - arm);
            ctx.lineTo(aq.cursorImagem.x, aq.cursorImagem.y + arm);
            ctx.lineWidth = 1.5;
            ctx.strokeStyle = '#22c8ad';
            ctx.stroke();
            ctx.beginPath();
            ctx.arc(aq.cursorImagem.x, aq.cursorImagem.y, 4, 0, Math.PI * 2);
            ctx.fillStyle = 'rgba(9,20,28,.92)';
            ctx.fill();
            ctx.lineWidth = 1.5;
            ctx.strokeStyle = '#f8fafc';
            ctx.stroke();
            ctx.beginPath();
            ctx.arc(aq.cursorImagem.x, aq.cursorImagem.y, 1.35, 0, Math.PI * 2);
            ctx.fillStyle = '#22c8ad';
            ctx.fill();
        }
        ctx.restore();

        this._aquisicaoCursorAnterior = {
            x: aq.cursorImagem.x, y: aq.cursorImagem.y, segment: null
        };
        if (!aq.cursorLocal) return;
        let segmentBounds = null;
        if (pontos.length > 0) {
            const ancora = pontos[pontos.length - 1];
            const relativeAnchor = this._pontoRelativoAquisicao(ancora);
            const relativeCursor = this._pontoRelativoAquisicao(aq.cursorLocal);
            const anchorImage = this._localParaImagemAquisicao(ancora);
            const startX = anchorImage.x;
            const startY = anchorImage.y;
            segmentBounds = {
                left: Math.min(startX, aq.cursorImagem.x) - 4,
                top: Math.min(startY, aq.cursorImagem.y) - 4,
                width: Math.abs(aq.cursorImagem.x - startX) + 8,
                height: Math.abs(aq.cursorImagem.y - startY) + 8
            };
            ctx.save();
            ctx.beginPath();
            ctx.moveTo(relativeAnchor.x, relativeAnchor.y);
            ctx.lineTo(relativeCursor.x, relativeCursor.y);
            ctx.lineWidth = 3.2 / escalaMapa;
            ctx.strokeStyle = 'rgba(255,255,255,.94)';
            ctx.lineCap = 'round';
            ctx.setLineDash([]);
            ctx.stroke();
            ctx.beginPath();
            ctx.moveTo(relativeAnchor.x, relativeAnchor.y);
            ctx.lineTo(relativeCursor.x, relativeCursor.y);
            ctx.lineWidth = 1.8 / escalaMapa;
            ctx.strokeStyle = '#2563eb';
            ctx.stroke();
            ctx.restore();
        }
        if (aq.coordenadaAbsolutaFixa) {
            // The crosshair above already marks the exact point; drawing a second
            // cursor dot at the same coordinate creates a doubled, blurry node.
        } else if (aq.snap) {
            this._desenharSnapAquisicao(ctx, aq.snap, escalaMapa);
        } else {
            const relativeCursor = this._pontoRelativoAquisicao(aq.cursorLocal);
            ctx.beginPath();
            ctx.arc(relativeCursor.x, relativeCursor.y, 3.5 / escalaMapa, 0, Math.PI * 2);
            ctx.fillStyle = '#f8fafc';
            ctx.fill();
            ctx.lineWidth = 1.6 / escalaMapa;
            ctx.strokeStyle = '#2563eb';
            ctx.stroke();
        }
        this._aquisicaoCursorAnterior = {
            x: aq.cursorImagem.x,
            y: aq.cursorImagem.y,
            segment: segmentBounds
        };
    },

    definirAtivos: function (destaque, medicao, mostrarArea) {
        this._destaque = destaque || [];
        this._medicao = medicao || [];
        this._mostrarArea = mostrarArea || false;
        this.iniciar();
        this._desenharMedicaoEstatico();
        this._agendarCursorMedicao();
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
        this._desenharMedicaoEstatico();
        this._agendarCursorMedicao();
    },

    definirCursorMedicaoAtivo: function (ativo) {
        this._medicaoCursorAtiva = !!ativo;
        const referencia = document.getElementById('overlayCanvas');
        if (referencia) this._garantirCamadasMedicao(referencia);
        this._agendarCursorMedicao();
    },

    atualizarCursorMedicao: function (mx, my, pointerSequence) {
        if (!this._medicaoCursorAtiva) return;
        if (pointerSequence > 0 && pointerSequence < this._medicaoPointerSequence) return;
        if (pointerSequence > 0) this._medicaoPointerSequence = pointerSequence;
        this._mouse = Number.isFinite(mx) && Number.isFinite(my) ? { x: mx, y: my } : null;
        this._snap = null;
        this._medicaoSnapTipo = null;
        this._agendarCursorMedicao();
    },

    definirMouseESnap: function (mx, my, sx, sy, snapTipo, pointerSequence) {
        const limpar = mx === null || my === null;
        if (!limpar && pointerSequence > 0 && pointerSequence < this._medicaoPointerSequence) return;
        if (pointerSequence > 0) this._medicaoPointerSequence = pointerSequence;
        this._mouse = (mx !== null && my !== null) ? { x: mx, y: my } : null;
        this._snap = (sx !== null && sy !== null) ? { x: sx, y: sy } : null;
        this._medicaoSnapTipo = this._snap ? snapTipo : null;
        this._agendarCursorMedicao();
    },

    limpar: function () {
        this._destaque = []; this._medicao = []; this._mouse = null; this._snap = null;
        this._futuroDestaque = null; this._futuroMedicao = null; this._mostrarArea = false;
        this._medicaoCursorAtiva = false; this._medicaoSnapTipo = null;
        if (this._animId) { cancelAnimationFrame(this._animId); this._animId = null; }
        this.desenharFrameEstatico();
        this._desenharMedicaoEstatico();
        this._agendarCursorMedicao();
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
        if (this._aquisicao.ativa || this._aquisicao.pontos.length > 0 || this._aquisicaoStaticCanvas)
            this._comporPreviaEstaticoAquisicao(cvs);
        if (this._aquisicao.ativa || this._aquisicaoCursorCanvas)
            this._desenharCursorAquisicao();
    }
};


window.addEventListener('resize', () => { if (window.GeoNexGraphics) window.GeoNexGraphics.redimensionar(); });
// GESTOR GLOBAL DE ATALHOS (Resolve o problema do Foco no MAUI)
// =========================================================================
document.addEventListener('keydown', function (event) {
    const target = event.target;
    const engine = window.mapEngine;
    if (!engine?.dotNetHelper) return;

    const key = event.key.toLowerCase();
    const elementTarget = target instanceof Element ? target : null;
    const drawing = ['AquisicaoPoligono', 'AquisicaoLinha', 'AquisicaoPonto'].includes(engine.ferramentaAtual) ||
        !!document.querySelector('.geonex-ui.is-digitizing');
    const hasModifier = event.ctrlKey || event.altKey || event.metaKey || event.shiftKey;
    const insideDialog = !!elementTarget?.closest('[role="dialog"], [aria-modal="true"]');
    const insideMenu = !!elementTarget?.closest('.dropdown-content, [role="menu"]');
    const insideTextField = !!elementTarget?.closest('input, textarea, select, [contenteditable="true"]');
    const insideHud = !!elementTarget?.closest('.precision-hud');
        const historyCommand = event.ctrlKey && !event.altKey && !event.metaKey &&
            (key === 'z' || key === 'y');
        if (drawing && insideHud && historyCommand) {
            const command = key === 'y' || event.shiftKey ? 'ctrl+shift+z' : 'ctrl+z';
            event.preventDefault();
            event.stopPropagation();
            if (!event.repeat)
                engine.dotNetHelper.invokeMethodAsync('ProcessarTecladoGlobal', command,
                    engine.lastPointerClientX, engine.lastPointerClientY)
                    .catch(err => console.warn('Erro no histórico da vetorização:', err));
            return;
        }
    // Enter inside a precision HUD belongs to its active form (for example,
    // locking absolute coordinates), not to the global finish-sketch shortcut.
    if (drawing && insideHud && key === 'enter') return;
    const cogoShortcut = drawing && key === 'tab' && !hasModifier && !insideDialog && !insideMenu && !insideTextField && !insideHud;
    const coordinateShortcut = drawing && key === 'f6' && !hasModifier && !insideDialog && !insideMenu;

    // Tab e F6 pertencem à vetorização mesmo com o foco retido em um botão
    // da barra de ferramentas. Dentro de campos e dos próprios HUDs, Tab segue
    // navegando pelos controles como de costume.
    if (cogoShortcut || coordinateShortcut) {
        event.preventDefault();
        event.stopPropagation();
        if (event.repeat) return;
        engine.dotNetHelper.invokeMethodAsync('ProcessarTecladoGlobal', key, engine.lastPointerClientX, engine.lastPointerClientY)
            .catch(err => console.warn('Erro no atalho:', err));
        return;
    }

    if (elementTarget && (elementTarget.closest('input, textarea, select, button, a, summary, [role="dialog"]') || elementTarget.isContentEditable)) return;
    const command = event.ctrlKey && event.shiftKey && key === 'z' ? 'ctrl+shift+z' :
        event.ctrlKey && key === 'z' ? 'ctrl+z' :
        event.ctrlKey && key === 'y' ? 'ctrl+y' : key;
    const measuring = engine.ferramentaAtual === 'Medicao';
    const editingVector = engine.ferramentaAtual === 'EdicaoVertice';
    const global = ['escape', 'm', 'i', 'd', 'p'].includes(command);
    const edit = ['ctrl+z', 'ctrl+y', 'ctrl+shift+z', 'z', 'backspace'].includes(command);
    const construction = (drawing && ['enter', 'c'].includes(command)) ||
        (measuring && command === 'enter');
    if (!global && !(edit && (drawing || measuring || editingVector)) && !construction) return;
    if (event.repeat && ['enter', 'c'].includes(command)) return;

    event.preventDefault();
    engine.dotNetHelper.invokeMethodAsync('ProcessarTecladoGlobal', command, engine.lastPointerClientX, engine.lastPointerClientY)
        .catch(err => console.warn('Erro no atalho:', err));
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

function reposicionarHudFlutuante() {
    const dotNet = window.dotnetReferencia || window.mapEngine?.dotNetHelper;
    const cogo = document.getElementById('hud-cogo');
    const f6 = document.getElementById('hud-f6');
    if (cogo && f6) {
        const gap = 8;
        const cogoTransform = new DOMMatrix(window.getComputedStyle(cogo).transform);
        const f6Transform = new DOMMatrix(window.getComputedStyle(f6).transform);
        const stackWidth = Math.max(cogo.offsetWidth, f6.offsetWidth);
        const x = Math.max(8, Math.min(Math.min(cogoTransform.m41, f6Transform.m41), window.innerWidth - stackWidth - 8));
        const stackHeight = cogo.offsetHeight + gap + f6.offsetHeight;
        const cogoY = Math.max(8, Math.min(64, window.innerHeight - stackHeight - 8));
        const f6Y = cogoY + cogo.offsetHeight + gap;

        cogo.style.maxHeight = '';
        f6.style.maxHeight = '';
        cogo.style.transform = `translate3d(${x}px, ${cogoY}px, 0)`;
        f6.style.transform = `translate3d(${x}px, ${f6Y}px, 0)`;
        if (dotNet) {
            dotNet.invokeMethodAsync('AtualizarMemoriaHUD', 'hud-cogo', x, cogoY).catch(() => {});
            dotNet.invokeMethodAsync('AtualizarMemoriaHUD', 'hud-f6', x, f6Y).catch(() => {});
        }
        return;
    }

    for (const id of ['hud-f6', 'hud-cogo']) {
        const el = document.getElementById(id);
        if (!el) continue;
        el.style.maxHeight = '';
        const matrix = new DOMMatrix(window.getComputedStyle(el).transform);
        const x = Math.max(8, Math.min(matrix.m41, window.innerWidth - el.offsetWidth - 8));
        const y = Math.max(64, Math.min(matrix.m42, window.innerHeight - el.offsetHeight - 8));
        el.style.transform = `translate3d(${x}px, ${y}px, 0)`;
        if (dotNet) dotNet.invokeMethodAsync('AtualizarMemoriaHUD', id, x, y).catch(() => {});
    }
}
window.addEventListener('resize', () => requestAnimationFrame(() => {
    reposicionarHudFlutuante();
    reposicionarPainelMedicaoFlutuante();
}));

function reposicionarPainelMedicaoFlutuante() {
    const panel = document.getElementById('measurement-floating-panel');
    if (!panel) return;
    const matrix = new DOMMatrix(window.getComputedStyle(panel).transform);
    const x = Math.max(8, Math.min(matrix.m41, window.innerWidth - panel.offsetWidth - 8));
    const y = Math.max(64, Math.min(matrix.m42, window.innerHeight - panel.offsetHeight - 8));
    panel.style.transform = `translate3d(${x}px, ${y}px, 0)`;
    const dotNet = window.dotnetReferencia || window.mapEngine?.dotNetHelper;
    if (dotNet)
        dotNet.invokeMethodAsync('AtualizarMemoriaHUD', 'measurement-floating-panel', x, y).catch(() => {});
}

// =========================================================================
// MOTOR DE ARRASTO DE JANELAS (HARDWARE ACCELERATION BYPASS BLAZOR)
// =========================================================================
window.iniciarArrasteHUD = function (e, elementId) {
    if (e.target instanceof Element && e.target.closest('button, input, select, textarea, a')) return;
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
        finalX = Math.max(8, Math.min(startX + dx, window.innerWidth - el.offsetWidth - 8));
        finalY = Math.max(64, Math.min(startY + dy, window.innerHeight - el.offsetHeight - 8));

        // Atira para a GPU instantaneamente
        el.style.transform = `translate3d(${finalX}px, ${finalY}px, 0)`;
    }

    // Função que avisa o C# quando acabar
    function onMouseUp(event) {
        window.removeEventListener('pointermove', onMouseMove);
        window.removeEventListener('pointerup', onMouseUp);
        window.removeEventListener('pointercancel', onMouseUp);

        // Sincroniza silenciosamente com o C# para a janela não voltar para trás
        let dotNet = window.dotnetReferencia || (window.mapEngine && window.mapEngine.dotNetHelper);
        if (dotNet) {
            dotNet.invokeMethodAsync('AtualizarMemoriaHUD', elementId, finalX, finalY);
        }
    }

    // Liga os sensores de alta velocidade no ecrã inteiro
    window.addEventListener('pointermove', onMouseMove);
    window.addEventListener('pointerup', onMouseUp);
    window.addEventListener('pointercancel', onMouseUp);
};

// Redimensionamento dos painéis laterais sem alterar a área do mapa.
function geonexDockPanelAvailableHeight(panel, workspace) {
    const workspaceHeight = workspace.getBoundingClientRect().height;
    const workspaceLimit = Math.max(1, workspaceHeight - 8);
    const panelLimit = Number.parseFloat(window.getComputedStyle(panel).maxHeight);
    return Number.isFinite(panelLimit) ? Math.max(1, Math.min(workspaceLimit, panelLimit)) : workspaceLimit;
}

window.geonexResizeDockStart = function (event) {
    if (event.button !== undefined && event.button !== 0) return;
    const handle = event.currentTarget;
    const panel = handle && handle.closest('.side-panel');
    const workspace = panel && panel.closest('.workspace-area');
    if (!handle || !panel || !workspace) return;

    event.preventDefault();
    const startY = event.clientY;
    const panelBounds = panel.getBoundingClientRect();
    const startHeight = panelBounds.height;
    const available = geonexDockPanelAvailableHeight(panel, workspace);
    const min = Math.min(180, available);
    const max = Math.max(min, available);
    const applyHeight = height => {
        const bounded = Math.max(min, Math.min(max, height));
        panel.style.setProperty('--dock-panel-height', `${Math.round(bounded)}px`);
        handle.setAttribute('aria-valuenow', String(Math.round((bounded / available) * 100)));
    };
    const onMove = moveEvent => applyHeight(startHeight - (moveEvent.clientY - startY));
    const onFinish = () => {
        handle.removeEventListener('pointermove', onMove);
        handle.removeEventListener('pointerup', onFinish);
        handle.removeEventListener('pointercancel', onFinish);
        handle.classList.remove('is-resizing');
    };

    handle.classList.add('is-resizing');
    handle.addEventListener('pointermove', onMove);
    handle.addEventListener('pointerup', onFinish);
    handle.addEventListener('pointercancel', onFinish);
    if (handle.setPointerCapture) handle.setPointerCapture(event.pointerId);
};

window.geonexResizeDockKeydown = function (event) {
    const increments = { ArrowUp: 24, ArrowDown: -24 };
    if (!(event.key in increments) && event.key !== 'Home' && event.key !== 'End') return;
    const handle = event.currentTarget;
    const panel = handle && handle.closest('.side-panel');
    const workspace = panel && panel.closest('.workspace-area');
    if (!handle || !panel || !workspace) return;
    event.preventDefault();
    const available = geonexDockPanelAvailableHeight(panel, workspace);
    const min = Math.min(180, available);
    const current = panel.getBoundingClientRect().height;
    const next = event.key === 'Home' ? min : event.key === 'End' ? available : current + increments[event.key];
    const bounded = Math.max(min, Math.min(available, next));
    panel.style.setProperty('--dock-panel-height', `${Math.round(bounded)}px`);
    handle.setAttribute('aria-valuenow', String(Math.round((bounded / available) * 100)));
};
