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

let dotnetHelper = null;
let workspaceZoom = 1.0;

window.composerInitV3 = function (paperId, helper) {
    composerPaper = document.getElementById(paperId);
    dotnetHelper = helper;
    
    // Deselecionar item se clicar fora
    composerPaper.addEventListener('mousedown', function (e) {
        if (e.target === composerPaper) {
            document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnItemDeselected');
            }
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
    item.id = 'item_' + new Date().getTime() + '_' + Math.floor(Math.random() * 1000);
    item.style.position = 'absolute';
    item.style.left = x + 'px';
    item.style.top = y + 'px';
    item.style.width = w + 'px';
    item.style.height = h + 'px';
    item.dataset.type = type;
    item.dataset.text = text || "";

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
        content.style.backgroundColor = 'transparent';
        content.style.border = 'none';
        content.style.padding = '5px';
        content.innerText = text;
        
        item.dataset.hasBg = 'false';
        item.dataset.hasBorder = 'false';
        item.dataset.bgColor = '#ffffff';
        item.dataset.borderColor = '#000000';
        item.dataset.borderWidth = '1';
        item.dataset.textAlignH = 'left';
        item.dataset.textAlignV = 'top';
        item.style.display = 'flex';
        item.style.alignItems = 'flex-start';
        item.style.justifyContent = 'flex-start';
        
    } else if (type === 'Legend') {
        content.style.backgroundColor = 'transparent';
        content.style.border = 'none';
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

        let innerContainer = document.createElement('div');
        innerContainer.id = 'composer-map-inner';
        innerContainer.style.position = 'absolute';
        innerContainer.style.width = '100%';
        innerContainer.style.height = '100%';
        innerContainer.style.left = '0px';
        innerContainer.style.top = '0px';
        
        let img1 = document.createElement('img');
        img1.src = '/images/orthophoto.jpg'; // Dummy base map for now
        let skiaLayer = document.getElementById('skia-layer');
        if (skiaLayer && skiaLayer.src) img1.src = skiaLayer.src;
        
        img1.style.position = 'absolute';
        img1.style.width = '100%';
        img1.style.height = '100%';
        img1.style.objectFit = 'contain';
        img1.style.pointerEvents = 'none';
        innerContainer.appendChild(img1);
        
        content.appendChild(innerContainer);

        item.dataset.panX = 0;
        item.dataset.panY = 0;
        item.dataset.scale = 1000;
        
        console.log("GEONEX COMPOSER: Mapa renderizado nativamente (SkiaSharp) sem Leaflet!");
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
    
    // Notifica Blazor sobre seleção
    if (dotnetHelper) {
        dotnetHelper.invokeMethodAsync('OnItemSelected', {
            Id: activeItem.id,
            Type: activeItem.dataset.type,
            X: initialX,
            Y: initialY,
            Width: parseFloat(activeItem.style.width) || 0,
            Height: parseFloat(activeItem.style.height) || 0,
            TextContent: activeItem.dataset.text || ""
        });
    }

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

    // Notifica Blazor sobre seleção
    if (dotnetHelper) {
        dotnetHelper.invokeMethodAsync('OnItemSelected', {
            Id: activeItem.id,
            Type: activeItem.dataset.type,
            X: initialX,
            Y: initialY,
            Width: initialW,
            Height: initialH,
            TextContent: activeItem.dataset.text || ""
        });
    }

    e.stopPropagation();
    e.preventDefault(); // Prevenir seleção de texto acidental
}

function handleMouseMove(e) {
    if (isPanningMapContent && activeItem && activeItem.dataset.type === 'Map') {
        let dx = (e.clientX - startMapPanX) / workspaceZoom;
        let dy = (e.clientY - startMapPanY) / workspaceZoom;
        
        let currentPanX = parseFloat(activeItem.dataset.panX) || 0;
        let currentPanY = parseFloat(activeItem.dataset.panY) || 0;
        
        let newPanX = currentPanX + dx;
        let newPanY = currentPanY + dy;
        
        let innerMap = activeItem.querySelector('#composer-map-inner');
        if (innerMap) {
            innerMap.style.left = newPanX + 'px';
            innerMap.style.top = newPanY + 'px';
        }
        return;
    }

    if (isDragging && activeItem) {
        let dx = (e.clientX - startX) / workspaceZoom;
        let dy = (e.clientY - startY) / workspaceZoom;
        
        activeItem.style.left = (initialX + dx) + 'px';
        activeItem.style.top = (initialY + dy) + 'px';
    } 
    else if (isResizing && activeItem) {
        let dx = (e.clientX - startX) / workspaceZoom;
        let dy = (e.clientY - startY) / workspaceZoom;

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
        let dx = (e.clientX - startMapPanX) / workspaceZoom;
        let dy = (e.clientY - startMapPanY) / workspaceZoom;
        
        // Grava a nova posição de Pan
        activeItem.dataset.panX = (parseFloat(activeItem.dataset.panX) || 0) + dx;
        activeItem.dataset.panY = (parseFloat(activeItem.dataset.panY) || 0) + dy;
        
        isPanningMapContent = false;
        activeItem.style.cursor = 'grab';
    }

    if (isDragging || isResizing) {
        if (dotnetHelper && activeItem) {
            dotnetHelper.invokeMethodAsync('OnItemMoved', {
                Id: activeItem.id,
                Type: activeItem.dataset.type,
                X: parseFloat(activeItem.style.left) || 0,
                Y: parseFloat(activeItem.style.top) || 0,
                Width: parseFloat(activeItem.style.width) || 0,
                Height: parseFloat(activeItem.style.height) || 0,
                TextContent: activeItem.dataset.text || ""
            });
        }
    }

    isDragging = false;
    isResizing = false;
}

// Escutar Zoom (Scroll) dentro do mapa ou da página
document.addEventListener('wheel', function(e) {
    // 1. Zoom do MAPA (Pan/Zoom Interativo do Item Mapa)
    if (isMapContentInteractionActive && activeItem && activeItem.dataset.type === 'Map') {
        if (e.target.closest('.composer-item')) {
            // Removido o Zoom de scroll no mapa internamente conforme requisito
        }
    }
    
    // 2. Zoom da ÁREA DE TRABALHO (Papel) via Ctrl + Roda do Rato
    if (e.ctrlKey) {
        let isComposerArea = e.target.closest('.composer-canvas-area');
        if (isComposerArea || e.target.closest('.composer-overlay')) {
            e.preventDefault();
            
            let newZoom = workspaceZoom;
            if (e.deltaY < 0) {
                newZoom += 0.1; // Zoom In
            } else {
                newZoom -= 0.1; // Zoom Out
            }

            // Limites de Zoom
            if (newZoom < 0.2) newZoom = 0.2;
            if (newZoom > 5.0) newZoom = 5.0;

            window.composerSetWorkspaceZoom(newZoom);
            
            // Avisar o C# para atualizar a UI do Zoom
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnWorkspaceZoomChanged', newZoom);
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

window.composerSetWorkspaceZoom = function(zoom) {
    workspaceZoom = zoom;
    let sheet = document.getElementById('paper-sheet');
    if (sheet) {
        sheet.style.transform = `scale(${workspaceZoom})`;
        sheet.style.transformOrigin = 'top left';
    }
};

window.composerUpdateItemProperty = function (id, propName, propValue) {
    let item = document.getElementById(id);
    if (!item) return;

    if (propName === 'X') item.style.left = propValue + 'px';
    if (propName === 'Y') item.style.top = propValue + 'px';
    if (propName === 'Width') item.style.width = propValue + 'px';
    if (propName === 'Height') item.style.height = propValue + 'px';
    if (propName === 'ZIndex') item.style.zIndex = propValue;
    if (propName === 'BgColor') item.style.backgroundColor = propValue;
    if (propName === 'HasBg') {
        if (!propValue) item.style.backgroundColor = 'transparent';
    }
    
    if (propName === 'Rotation') item.style.transform = `rotate(${propValue}deg)`;
    
    if (propName === 'BorderColor' || propName === 'BorderWidth' || propName === 'HasBorder') {
        let hw = item.dataset.hasBorder === 'true';
        if (propName === 'HasBorder') {
            hw = propValue;
            item.dataset.hasBorder = propValue;
        }
        
        let bw = item.dataset.borderWidth || '1';
        let bc = item.dataset.borderColor || 'transparent';
        if (propName === 'BorderWidth') bw = propValue;
        if (propName === 'BorderColor') bc = propValue;
        
        item.dataset.borderWidth = bw;
        item.dataset.borderColor = bc;
        
        if (hw) {
            item.style.border = `${bw}px solid ${bc}`;
        } else {
            item.style.border = 'none';
        }
    }

    if (propName === 'Text') {
        item.dataset.text = propValue;
        let content = item.querySelector('.item-content');
        if (content) {
            if (item.dataset.type === 'Text') {
                content.innerText = propValue;
            } else if (item.dataset.type === 'Legend') {
                content.innerHTML = `<strong style="color:#000;">${propValue}</strong><br><br>
                                     <div style="display:flex; align-items:center; margin-bottom:5px;"><div style="width:20px;height:20px;background:red;margin-right:8px;"></div> Zona A</div>
                                     <div style="display:flex; align-items:center;"><div style="width:20px;height:20px;background:blue;margin-right:8px;"></div> Zona B</div>`;
            }
        }
    }

    if (propName === 'TextColor') {
        let content = item.querySelector('.item-content');
        if (content) content.style.color = propValue;
    }

    if (propName === 'FontSize') {
        let content = item.querySelector('.item-content');
        if (content) content.style.fontSize = propValue + 'px';
    }
    
    if (propName === 'TextAlignH') {
        if (propValue === 'left') item.style.justifyContent = 'flex-start';
        if (propValue === 'center') item.style.justifyContent = 'center';
        if (propValue === 'right') item.style.justifyContent = 'flex-end';
        item.dataset.textAlignH = propValue;
        
        let content = item.querySelector('.item-content');
        if (content) content.style.textAlign = propValue;
    }
    
    if (propName === 'TextAlignV') {
        if (propValue === 'top') item.style.alignItems = 'flex-start';
        if (propValue === 'center') item.style.alignItems = 'center';
        if (propValue === 'bottom') item.style.alignItems = 'flex-end';
        item.dataset.textAlignV = propValue;
    }
    
    if (propName === 'MapScale' && item.dataset.type === 'Map') {
        let numValue = parseFloat(propValue);
        if (isNaN(numValue) || numValue <= 0) numValue = 1000;
        
        // Ex: 1000 = 100%. 500 = 200% (zoom in). Limit to max scale of 1000 (100%) to prevent white border.
        let mapScaleFactor = 1000 / numValue;
        if (mapScaleFactor < 1.0) mapScaleFactor = 1.0;
        
        item.dataset.scale = numValue;
        
        let innerMap = item.querySelector('#composer-map-inner');
        if (innerMap) {
            innerMap.style.width = (mapScaleFactor * 100) + '%';
            innerMap.style.height = (mapScaleFactor * 100) + '%';
        }
    }
    
    if (propName === 'MapRotation' && item.dataset.type === 'Map') {
        let innerMap = item.querySelector('#composer-map-inner');
        if (innerMap) {
            innerMap.style.transform = `rotate(${propValue}deg)`;
        }
    }
};

document.addEventListener('contextmenu', function(e) {
    let composerArea = e.target.closest('.composer-canvas-area');
    if (composerArea) {
        e.preventDefault();
        let item = e.target.closest('.composer-item');
        if (dotnetHelper) {
            let itemId = item ? item.id : null;
            dotnetHelper.invokeMethodAsync('OnContextMenu', itemId, e.clientX, e.clientY);
        }
    }
});

document.addEventListener('mousedown', function(e) {
    if (e.button !== 2) { // Não é botão direito
        if (!e.target.closest('.context-menu')) {
            if (dotnetHelper) dotnetHelper.invokeMethodAsync('CloseContextMenu');
        }
    }
});


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
        width: 10px;
        height: 10px;
        background-color: #fff;
        border: 1px solid #38bdf8;
        display: none;
    }
    .composer-item.selected .resize-handle {
        display: block;
    }
    /* Posições dos Handles */
    .resize-handle.tl { top: -5px; left: -5px; cursor: nwse-resize; }
    .resize-handle.tr { top: -5px; right: -5px; cursor: nesw-resize; }
    .resize-handle.bl { bottom: -5px; left: -5px; cursor: nesw-resize; }
    .resize-handle.br { bottom: -5px; right: -5px; cursor: nwse-resize; }
    .resize-handle.t { top: -5px; left: 50%; transform: translateX(-50%); cursor: ns-resize; }
    .resize-handle.b { bottom: -5px; left: 50%; transform: translateX(-50%); cursor: ns-resize; }
    .resize-handle.l { top: 50%; left: -5px; transform: translateY(-50%); cursor: ew-resize; }
    .resize-handle.r { top: 50%; right: -5px; transform: translateY(-50%); cursor: ew-resize; }
`;
document.head.appendChild(style);

// Exportação Dinâmica (Carrega bibliotecas apenas quando necessário)
window.composerExportPNG = function(filename) {
    if (typeof html2canvas === 'undefined') {
        let script = document.createElement('script');
        script.src = 'https://cdnjs.cloudflare.com/ajax/libs/html2canvas/1.4.1/html2canvas.min.js';
        script.onload = () => execExportPNG(filename);
        document.head.appendChild(script);
    } else {
        execExportPNG(filename);
    }
};

function execExportPNG(filename) {
    let paper = document.getElementById('paper-sheet');
    document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
    if (dotnetHelper) dotnetHelper.invokeMethodAsync('OnItemDeselected');
    
    html2canvas(paper, { scale: 2, useCORS: true }).then(canvas => {
        let link = document.createElement('a');
        link.download = filename;
        link.href = canvas.toDataURL('image/png');
        link.click();
    });
}

window.composerExportPDF = function(filename) {
    if (typeof html2canvas === 'undefined' || typeof window.jspdf === 'undefined') {
        let script1 = document.createElement('script');
        script1.src = 'https://cdnjs.cloudflare.com/ajax/libs/html2canvas/1.4.1/html2canvas.min.js';
        script1.onload = function() {
            let script2 = document.createElement('script');
            script2.src = 'https://cdnjs.cloudflare.com/ajax/libs/jspdf/2.5.1/jspdf.umd.min.js';
            script2.onload = () => execExportPDF(filename);
            document.head.appendChild(script2);
        };
        document.head.appendChild(script1);
    } else {
        execExportPDF(filename);
    }
};

function execExportPDF(filename) {
    let paper = document.getElementById('paper-sheet');
    document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
    if (dotnetHelper) dotnetHelper.invokeMethodAsync('OnItemDeselected');
    
    html2canvas(paper, { scale: 2, useCORS: true }).then(canvas => {
        let imgData = canvas.toDataURL('image/png');
        const { jsPDF } = window.jspdf;
        let orientation = paper.offsetWidth > paper.offsetHeight ? 'l' : 'p';
        let pdf = new jsPDF(orientation, 'mm', 'a4');
        let pdfWidth = pdf.internal.pageSize.getWidth();
        let pdfHeight = (canvas.height * pdfWidth) / canvas.width;
        pdf.addImage(imgData, 'PNG', 0, 0, pdfWidth, pdfHeight);
        pdf.save(filename);
    });
}

window.composerDeleteItem = function(id) {
    let item = document.getElementById(id);
    if (item) {
        item.remove();
    }
    if (activeItem && activeItem.id === id) {
        activeItem = null;
        hideAllResizeHandles();
    }
};

window.composerSelectItem = function(id) {
    let item = document.getElementById(id);
    if (item) {
        selectItem(item);
    }
};
