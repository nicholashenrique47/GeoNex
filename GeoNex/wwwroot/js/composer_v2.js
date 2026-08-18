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

    // Inicialização se for um Mapa
    if (type === 'Map') {
        content.style.backgroundColor = '#fff';
        content.style.border = '2px solid #000';
        content.style.overflow = 'hidden';
        content.style.position = 'relative';
        content.id = 'composer-map-container';

        // Container interno que vai sofrer o Pan/Zoom
        let innerContainer = document.createElement('div');
        innerContainer.id = 'composer-map-inner';
        innerContainer.style.position = 'absolute';
        innerContainer.style.width = '100%';
        innerContainer.style.height = '100%';
        innerContainer.style.left = '0px';
        innerContainer.style.top = '0px';
        innerContainer.style.transformOrigin = 'center center';
        innerContainer.style.transition = 'transform 0.1s ease-out';
        
        // 1. Clonar Skia-Layer (Raster Background)
        let skiaLayer = document.getElementById('skia-layer');
        if (skiaLayer && skiaLayer.src) {
            let img1 = document.createElement('img');
            img1.src = skiaLayer.src;
            img1.style.position = 'absolute';
            img1.style.width = '100%';
            img1.style.height = '100%';
            img1.style.objectFit = 'contain'; // Mantém proporção da tela
            img1.style.pointerEvents = 'none';
            innerContainer.appendChild(img1);
        }

        // 2. Clonar Overlay Canvas (Vetores)
        let overlayCanvas = document.getElementById('overlayCanvas');
        if (overlayCanvas) {
            let img2 = document.createElement('img');
            img2.src = overlayCanvas.toDataURL('image/png');
            img2.style.position = 'absolute';
            img2.style.width = '100%';
            img2.style.height = '100%';
            img2.style.objectFit = 'contain';
            img2.style.pointerEvents = 'none';
            innerContainer.appendChild(img2);
        }

        content.appendChild(innerContainer);
        
        // Estado inicial de Pan/Zoom
        content.dataset.panX = 0;
        content.dataset.panY = 0;
        content.dataset.zoom = 1;
        
        console.log("GEONEX COMPOSER: Mapa renderizado nativamente (SkiaSharp) sem Leaflet!");

        // Por predefinição, quando criamos, estamos com a ferramenta "Selecionar/Mover Item"
        // Logo, o mapa Leaflet não pode intercetar os cliques do rato para Pan/Zoom, 
        // senão não conseguimos arrastar a moldura. Desligamos a interação nativa do Leaflet:
        composerLeafletMap.dragging.disable();
        composerLeafletMap.touchZoom.disable();
        composerLeafletMap.doubleClickZoom.disable();
        composerLeafletMap.scrollWheelZoom.disable();
    }
};

function startDrag(e) {
    if (e.button !== 0) return;
    
    // Se o clique foi no handle de resize, não fazemos drag
    if (e.target.classList.contains('resize-handle')) return;
    
    isDragging = true;
    activeItem = e.currentTarget;

    // Seleciona o item atual (remove seleção dos outros)
    document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
    activeItem.classList.add('selected');

    // Se estivermos no modo "Mover Conteúdo", NÃO arrastamos o frame, apenas o mapa interior
    if (isMapContentInteractionActive && activeItem.dataset.type === 'Map') {
        isDragging = false;
        isPanningMapContent = true;
        startMapPanX = e.clientX;
        startMapPanY = e.clientY;
        activeItem.style.cursor = 'grabbing';
        e.stopPropagation();
        return;
    }

    startX = e.clientX;
    startY = e.clientY;
    initialX = parseFloat(activeItem.style.left) || 0;
    initialY = parseFloat(activeItem.style.top) || 0;
    
    e.stopPropagation();
}

let isMapContentInteractionActive = false;
let isPanningMapContent = false;
let startMapPanX = 0;
let startMapPanY = 0;

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
    if (isPanningMapContent && activeItem && activeItem.dataset.type === 'Map') {
        let dx = e.clientX - startMapPanX;
        let dy = e.clientY - startMapPanY;
        
        // Pega as coordenadas base gravadas no DOM (ou 0)
        let currentPanX = parseFloat(activeItem.dataset.panX) || 0;
        let currentPanY = parseFloat(activeItem.dataset.panY) || 0;
        let currentZoom = parseFloat(activeItem.dataset.zoom) || 1;
        
        let newPanX = currentPanX + dx;
        let newPanY = currentPanY + dy;
        
        let innerMap = activeItem.querySelector('#composer-map-inner');
        if (innerMap) {
            // Removemos a transição temporariamente para não "lagar" ao arrastar
            innerMap.style.transition = 'none';
            innerMap.style.transform = `translate(${newPanX}px, ${newPanY}px) scale(${currentZoom})`;
        }
        return;
    }

    if (isDragging && activeItem) {
        let dx = e.clientX - startX;
        let dy = e.clientY - startY;
        
        activeItem.style.left = (initialX + dx) + 'px';
        activeItem.style.top = (initialY + dy) + 'px';
    } 
    else if (isResizing && activeItem) {
        let dx = e.clientX - startX;
        let dy = e.clientY - startY;

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
    }
}

function handleMouseUp(e) {
    if (isPanningMapContent && activeItem && activeItem.dataset.type === 'Map') {
        let dx = e.clientX - startMapPanX;
        let dy = e.clientY - startMapPanY;
        
        // Grava a nova posição de Pan
        activeItem.dataset.panX = (parseFloat(activeItem.dataset.panX) || 0) + dx;
        activeItem.dataset.panY = (parseFloat(activeItem.dataset.panY) || 0) + dy;
        
        isPanningMapContent = false;
        activeItem.style.cursor = 'grab';
        
        let innerMap = activeItem.querySelector('#composer-map-inner');
        if (innerMap) {
            innerMap.style.transition = 'transform 0.1s ease-out';
        }
    }

    isDragging = false;
    isResizing = false;
}

// Escutar Zoom (Scroll) dentro do mapa
document.addEventListener('wheel', function(e) {
    if (isMapContentInteractionActive && activeItem && activeItem.dataset.type === 'Map') {
        if (e.target.closest('.composer-item') === activeItem) {
            e.preventDefault();
            let currentZoom = parseFloat(activeItem.dataset.zoom) || 1;
            
            // Fator de zoom (10%)
            let zoomFactor = 1.1;
            if (e.deltaY < 0) {
                currentZoom *= zoomFactor; // Zoom in
            } else {
                currentZoom /= zoomFactor; // Zoom out
            }
            
            activeItem.dataset.zoom = currentZoom;
            
            let innerMap = activeItem.querySelector('#composer-map-inner');
            if (innerMap) {
                let panX = parseFloat(activeItem.dataset.panX) || 0;
                let panY = parseFloat(activeItem.dataset.panY) || 0;
                innerMap.style.transition = 'transform 0.1s ease-out';
                innerMap.style.transform = `translate(${panX}px, ${panY}px) scale(${currentZoom})`;
            }
        }
    }
}, {passive: false});

// Funções para ativar/desativar a interação DENTRO do Mapa (Mover Conteúdo vs Mover Item)
window.composerSetMapInteractionModeV3 = function (isActive) {
    isMapContentInteractionActive = isActive;
    
    let mapItems = document.querySelectorAll('.composer-item[data-type="Map"]');
    mapItems.forEach(item => {
        if (isActive) {
            item.style.cursor = 'grab';
        } else {
            item.style.cursor = 'move';
        }
    });
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
