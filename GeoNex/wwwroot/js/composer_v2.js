// Motor do Compositor de Impressão (Drag, Drop, Resize)
let composerPaper = null;
let activeItem = null;
let isDragging = false;
let isResizing = false;
let startX, startY;
let initialX, initialY, initialW, initialH;
let resizeDir = "";

// Variáveis para a instância isolada do Leaflet
let composerLeafletMap = null;
let composerBasemapLayer = null;

window.composerInitV3 = function (paperId) {
    composerPaper = document.getElementById(paperId);
    
    // Deselecionar item se clicar fora
    composerPaper.addEventListener('mousedown', function (e) {
        if (e.target === composerPaper) {
            document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
        }
    });

    // Global mouse events para arrastar e soltar
    document.addEventListener('mousemove', handleMouseMove);
    document.addEventListener('mouseup', handleMouseUp);
};

window.composerAddItemV3 = function (type, text, x, y, w, h) {
    if (!composerPaper) return;

    let item = document.createElement('div');
    item.className = 'composer-item selected';
    item.style.position = 'absolute';
    item.style.left = x + 'px';
    item.style.top = y + 'px';
    item.style.width = w + 'px';
    item.style.height = h + 'px';
    item.dataset.type = type;

    // Deseleciona todos os outros
    document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));

    // Adiciona Handles de Resize
    const handles = ['tl', 'tr', 'bl', 'br', 't', 'b', 'l', 'r'];
    handles.forEach(dir => {
        let handle = document.createElement('div');
        handle.className = `resize-handle ${dir}`;
        handle.dataset.dir = dir;
        handle.addEventListener('mousedown', startResize);
        item.appendChild(handle);
    });

    // Conteúdo Visual dependendo do tipo
    let content = document.createElement('div');
    content.className = 'item-content';
    
    if (type === 'Map') {
        content.style.backgroundColor = '#f0f4f8';
        content.style.border = '2px solid #000';
        content.id = 'composer-map-container'; // ID único para o Leaflet ancorar
        // Removemos o placeholder de texto
        
        // Vamos inicializar o Leaflet logo a seguir a anexar ao DOM
    } else if (type === 'Text') {
        content.style.fontSize = '24px';
        content.style.fontWeight = 'bold';
        content.style.fontFamily = 'Arial, sans-serif';
        content.style.color = '#000';
        content.innerText = text;
    } else if (type === 'Legend') {
        content.style.backgroundColor = '#fff';
        content.style.border = '1px solid #000';
        content.style.padding = '10px';
        content.innerHTML = `<strong style="color:#000;">${text}</strong><br><br>
                             <div style="display:flex; align-items:center; margin-bottom:5px;"><div style="width:20px;height:20px;background:red;margin-right:8px;"></div> Zona A</div>
                             <div style="display:flex; align-items:center;"><div style="width:20px;height:20px;background:blue;margin-right:8px;"></div> Zona B</div>`;
    } else if (type === 'Image') {
        content.style.backgroundColor = '#fff';
        content.innerHTML = `<div style="width:100%;height:100%;border:2px dashed #ccc; display:flex; justify-content:center; align-items:center; color:#000;">N</div>`;
    }

    item.appendChild(content);

    // Eventos do Item
    item.addEventListener('mousedown', startDrag);

    composerPaper.appendChild(item);

    // Inicialização do Leaflet se for um Mapa
    if (type === 'Map') {
        console.log("GEONEX COMPOSER STARTING...");
        console.log("Is geonexMap present?", !!window.geonexMap);

        let center = [39.3999, -8.2245];
        let zoom = 6;
        if (window.geonexMap) {
            center = window.geonexMap.getCenter();
            zoom = window.geonexMap.getZoom();
            console.log("Copied center/zoom: ", center, zoom);
        }

        // Inicializa mapa sem controlos de zoom nativos para não poluir
        composerLeafletMap = L.map('composer-map-container', {
            zoomControl: false,
            attributionControl: false
        }).setView(center, zoom);

        // CLONAGEM EXPLÍCITA DAS CAMADAS DO GEONEX
        let hasBaseLayer = false;

        if (window.geonexMap) {
            // 1. Clonar Camada OSM (se estiver ativa no mapa principal)
            if (window.osmLayer && window.geonexMap.hasLayer(window.osmLayer)) {
                L.tileLayer(window.osmLayer._url, window.osmLayer.options).addTo(composerLeafletMap);
                hasBaseLayer = true;
            }

            // 2. Clonar Ortofoto / SkiaSharp WMS (Mesmo se o hasLayer falhar, forçamos se houver dados)
            if (window.camadaDinamicaWMS) {
                let imgUrl = window.camadaDinamicaWMS._url || (window.camadaDinamicaWMS._image ? window.camadaDinamicaWMS._image.src : null);
                let bounds = window.camadaDinamicaWMS._bounds;
                
                console.log("Ortho data check: URL Length = " + (imgUrl ? imgUrl.length : 0), "Bounds = ", bounds);

                // Se tivermos um base64 real (maior que 100 chars) e bounds válidos
                if (imgUrl && imgUrl.length > 100 && bounds) {
                    L.imageOverlay(imgUrl, bounds, window.camadaDinamicaWMS.options).addTo(composerLeafletMap);
                    hasBaseLayer = true;
                    console.log("Ortofoto injetada no compositor com sucesso!");
                }
            }

            // 3. Clonar Vetores
            if (window.camadasGeoNex) {
                for (let nomeCamada in window.camadasGeoNex) {
                    let layer = window.camadasGeoNex[nomeCamada];
                    if (layer && typeof layer.toGeoJSON === 'function') {
                        try {
                            let cloneOptions = {
                                opacity: 1,
                                fillOpacity: 0.4,
                                color: '#0ea5e9',
                                fillColor: '#0ea5e9',
                                weight: 2
                            };
                            
                            L.geoJSON(layer.toGeoJSON(), {
                                style: function(feature) { return cloneOptions; },
                                pointToLayer: function(feature, latlng) {
                                    return L.circleMarker(latlng, cloneOptions);
                                }
                            }).addTo(composerLeafletMap);
                            hasBaseLayer = true;
                        } catch(e) { console.error("Erro a clonar vetor: " + nomeCamada, e); }
                    }
                }
            }
        }
        
        if (!hasBaseLayer) {
            console.warn("NENHUMA CAMADA FOI COPIADA PARA O COMPOSITOR. CAINDO PARA FALLBACK VOYAGER.");
            composerBasemapLayer = L.tileLayer('https://{s}.basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}{r}.png', {
                maxZoom: 19
            }).addTo(composerLeafletMap);
        }

        // Por predefinição, quando criamos, estamos com a ferramenta "Selecionar/Mover Item"
        // Logo, o mapa Leaflet não pode intercetar os cliques do rato para Pan/Zoom, 
        // senão não conseguimos arrastar a moldura. Desligamos a interação nativa do Leaflet:
        composerLeafletMap.dragging.disable();
        composerLeafletMap.touchZoom.disable();
        composerLeafletMap.doubleClickZoom.disable();
        composerLeafletMap.scrollWheelZoom.disable();
        composerLeafletMap.boxZoom.disable();
        composerLeafletMap.keyboard.disable();
    }
};

function startDrag(e) {
    if (e.target.classList.contains('resize-handle')) return; // Se clicou num handle, não arrasta.

    isDragging = true;
    activeItem = e.currentTarget;
    
    // Seleciona
    document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
    activeItem.classList.add('selected');

    startX = e.clientX;
    startY = e.clientY;
    initialX = parseFloat(activeItem.style.left) || 0;
    initialY = parseFloat(activeItem.style.top) || 0;
    
    e.stopPropagation();
}

function startResize(e) {
    isResizing = true;
    activeItem = e.currentTarget.parentElement;
    resizeDir = e.currentTarget.dataset.dir;

    // Seleciona
    document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
    activeItem.classList.add('selected');

    startX = e.clientX;
    startY = e.clientY;
    initialX = parseFloat(activeItem.style.left) || 0;
    initialY = parseFloat(activeItem.style.top) || 0;
    initialW = parseFloat(activeItem.style.width) || 0;
    initialH = parseFloat(activeItem.style.height) || 0;

    e.stopPropagation();
    e.preventDefault(); // Prevenir seleção de texto acidental
}

function handleMouseMove(e) {
    if (isDragging && activeItem) {
        let dx = e.clientX - startX;
        let dy = e.clientY - startY;
        
        let zoom = 1; // Pode ser implementado mais tarde se fizermos zoom na folha
        
        activeItem.style.left = (initialX + dx/zoom) + 'px';
        activeItem.style.top = (initialY + dy/zoom) + 'px';
    } 
    else if (isResizing && activeItem) {
        let dx = e.clientX - startX;
        let dy = e.clientY - startY;
        let zoom = 1;

        dx = dx / zoom;
        dy = dy / zoom;

        if (resizeDir.includes('e') || resizeDir.includes('r')) {
            activeItem.style.width = Math.max(20, initialW + dx) + 'px';
        }
        if (resizeDir.includes('s') || resizeDir.includes('b')) {
            activeItem.style.height = Math.max(20, initialH + dy) + 'px';
        }
        if (resizeDir.includes('w') || resizeDir.includes('l')) {
            let newW = Math.max(20, initialW - dx);
            if (newW > 20) {
                activeItem.style.left = (initialX + dx) + 'px';
                activeItem.style.width = newW + 'px';
            }
        }
        if (resizeDir.includes('n') || resizeDir.includes('t')) {
            let newH = Math.max(20, initialH - dy);
            if (newH > 20) {
                activeItem.style.top = (initialY + dy) + 'px';
                activeItem.style.height = newH + 'px';
            }
        }

        // Se o item que estamos a redimensionar é o mapa, atualizamos o Leaflet
        if (activeItem.dataset.type === 'Map' && composerLeafletMap) {
            composerLeafletMap.invalidateSize();
        }
    }
}

function handleMouseUp(e) {
    isDragging = false;
    isResizing = false;
}

// Funções para ativar/desativar a interação DENTRO do Mapa (Mover Conteúdo vs Mover Item)
window.composerSetMapInteractionModeV3 = function (isActive) {
    if (!composerLeafletMap) return;

    if (isActive) {
        composerLeafletMap.dragging.enable();
        composerLeafletMap.touchZoom.enable();
        composerLeafletMap.doubleClickZoom.enable();
        composerLeafletMap.scrollWheelZoom.enable();
        
        // Remove a classe que bloqueia eventos para o conteúdo do mapa
        let mapItem = document.querySelector('.composer-item[data-type="Map"] .item-content');
        if (mapItem) mapItem.style.pointerEvents = 'auto';
    } else {
        composerLeafletMap.dragging.disable();
        composerLeafletMap.touchZoom.disable();
        composerLeafletMap.doubleClickZoom.disable();
        composerLeafletMap.scrollWheelZoom.disable();
        
        // Restaura o bloqueio para permitir arrastar a moldura inteira
        let mapItem = document.querySelector('.composer-item[data-type="Map"] .item-content');
        if (mapItem) mapItem.style.pointerEvents = 'none';
    }
};

// Injeção de CSS Dinâmico para Handles de Resize
const style = document.createElement('style');
style.textContent = `
    .composer-item {
        box-sizing: border-box;
        cursor: move;
    }
    .composer-item.selected {
        outline: 1px dashed #38bdf8;
    }
    .item-content {
        width: 100%;
        height: 100%;
        pointer-events: none; /* Para o clique passar para o item */
        box-sizing: border-box;
    }
    .resize-handle {
        position: absolute;
        width: 8px;
        height: 8px;
        background-color: #fff;
        border: 1px solid #38bdf8;
        display: none;
        z-index: 100;
    }
    .composer-item.selected .resize-handle {
        display: block;
    }
    .resize-handle.tl { top: -4px; left: -4px; cursor: nwse-resize; }
    .resize-handle.tr { top: -4px; right: -4px; cursor: nesw-resize; }
    .resize-handle.bl { bottom: -4px; left: -4px; cursor: nesw-resize; }
    .resize-handle.br { bottom: -4px; right: -4px; cursor: nwse-resize; }
    .resize-handle.t { top: -4px; left: calc(50% - 4px); cursor: ns-resize; }
    .resize-handle.b { bottom: -4px; left: calc(50% - 4px); cursor: ns-resize; }
    .resize-handle.l { left: -4px; top: calc(50% - 4px); cursor: ew-resize; }
    .resize-handle.r { right: -4px; top: calc(50% - 4px); cursor: ew-resize; }
`;
document.head.appendChild(style);
