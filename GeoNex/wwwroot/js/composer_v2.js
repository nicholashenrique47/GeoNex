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

window.composerInitV3 = function (paperId, helper, mapServerUrl) {
    composerPaper = document.getElementById(paperId);
    dotnetHelper = helper;
    window.geonexMapServerUrl = mapServerUrl;
    
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

    if (x === -1 || y === -1) {
        let canvasArea = document.querySelector('.composer-canvas-area');
        if (canvasArea) {
            let rect = composerPaper.getBoundingClientRect();
            let areaRect = canvasArea.getBoundingClientRect();
            x = ((areaRect.width / 2) - rect.left) / workspaceZoom - (w / 2);
            y = ((areaRect.height / 2) - rect.top) / workspaceZoom - (h / 2);
            if (x < 0) x = 50;
            if (y < 0) y = 50;
        } else {
            x = 50; y = 50;
        }
    }

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
        content.style.fontSize = '14px';
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
        content.style.backgroundColor = 'transparent';
        item.dataset.svgStyle = 'Estilo 2';
        item.dataset.svgFill = '#333333';
        item.dataset.svgStroke = '#ffffff';
        item.dataset.hasBg = 'false';
        item.dataset.hasBorder = 'false';
        updateImageItem(item, content);
    }

    item.appendChild(content);

    // Eventos do Item
    item.addEventListener('mousedown', startDrag);

    composerPaper.appendChild(item);

    // Inicialização se for um Mapa
    if (type === 'Map') {
        content.style.backgroundColor = 'transparent';
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
        
        // Se a janela souber a URL do servidor local de imagens, busca o mapa mais recente
        if (window.geonexMapServerUrl) {
            let reqW = Math.round(w) || 1920;
            let reqH = Math.round(h) || 1080;
            img1.src = window.geonexMapServerUrl + `mapa/?w=${reqW}&h=${reqH}&ox=0&oy=0&rot=0&dpi=2.0&c=1&t=` + new Date().getTime();
        } else {
            // Fallback para caso ainda esteja operando na mesma janela (Modo Antigo)
            let skiaLayer = document.getElementById('skia-layer');
            if (skiaLayer && skiaLayer.src) img1.src = skiaLayer.src;
        }
        
        img1.style.position = 'absolute';
        img1.style.width = '100%';
        img1.style.height = '100%';
        img1.style.objectFit = 'fill'; // Estica para preencher durante o redimensionamento. Ao largar, busca a resolução exata.
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

const compassSVGs = {
    'Estilo 1': (fill, stroke) => `<svg viewBox="-50 -50 100 100" style="width:100%;height:100%;"><polygon points="0,-40 15,40 0,25" fill="${fill}" stroke="${stroke}" stroke-width="2"/><polygon points="0,-40 -15,40 0,25" fill="rgba(255,255,255,0.3)" stroke="${stroke}" stroke-width="2"/></svg>`,
    'Estilo 2': (fill, stroke) => `<svg viewBox="-50 -50 100 100" style="width:100%;height:100%;"><circle cx="0" cy="0" r="45" fill="none" stroke="${stroke}" stroke-width="2"/><polygon points="0,-45 10,-10 45,0 10,10 0,45 -10,10 -45,0 -10,-10" fill="${fill}" stroke="${stroke}" stroke-width="1"/><polygon points="0,-45 0,45 -10,10 -45,0 -10,-10" fill="rgba(255,255,255,0.3)"/></svg>`,
    'Estilo 3': (fill, stroke) => `<svg viewBox="-50 -50 100 100" style="width:100%;height:100%;"><path d="M0,-40 L20,30 L0,20 L-20,30 Z" fill="${fill}" stroke="${stroke}" stroke-width="2" /></svg>`,
    'Estilo 4': (fill, stroke) => `<svg viewBox="-120 -120 240 240" style="width:100%;height:100%;"><circle cx="0" cy="0" r="55" fill="none" stroke="${stroke}" stroke-width="3"/><circle cx="0" cy="0" r="48" fill="none" stroke="${stroke}" stroke-width="1"/><g transform="rotate(45)"><polygon points="0,-45 7,-7 45,0 7,7 0,45 -7,7 -45,0 -7,-7" fill="rgba(255,255,255,0.2)" stroke="${stroke}" stroke-width="1"/></g><polygon points="0,-70 14,-14 0,0" fill="${fill}" stroke="${stroke}" stroke-width="1"/><polygon points="0,-70 -14,-14 0,0" fill="rgba(255,255,255,0.9)" stroke="${stroke}" stroke-width="1"/><polygon points="0,70 14,14 0,0" fill="rgba(255,255,255,0.9)" stroke="${stroke}" stroke-width="1"/><polygon points="0,70 -14,14 0,0" fill="${fill}" stroke="${stroke}" stroke-width="1"/><polygon points="70,0 14,-14 0,0" fill="${fill}" stroke="${stroke}" stroke-width="1"/><polygon points="70,0 14,14 0,0" fill="rgba(255,255,255,0.9)" stroke="${stroke}" stroke-width="1"/><polygon points="-70,0 -14,14 0,0" fill="${fill}" stroke="${stroke}" stroke-width="1"/><polygon points="-70,0 -14,-14 0,0" fill="rgba(255,255,255,0.9)" stroke="${stroke}" stroke-width="1"/><text x="0" y="-92" fill="${stroke}" font-size="38" font-family="'Arial Black', Impact, sans-serif" font-weight="900" text-anchor="middle" dominant-baseline="central">N</text><text x="0" y="92" fill="${stroke}" font-size="32" font-family="'Arial Black', Impact, sans-serif" font-weight="900" text-anchor="middle" dominant-baseline="central">S</text><text x="92" y="2" fill="${stroke}" font-size="32" font-family="'Arial Black', Impact, sans-serif" font-weight="900" text-anchor="middle" dominant-baseline="central">E</text><text x="-92" y="2" fill="${stroke}" font-size="32" font-family="'Arial Black', Impact, sans-serif" font-weight="900" text-anchor="middle" dominant-baseline="central">O</text></svg>`,
    'Estilo 5': (fill, stroke) => `<svg viewBox="-60 -120 120 240" style="width:100%;height:100%;"><polygon points="0,-60 25,45 0,20" fill="${fill}" stroke="${stroke}" stroke-width="2"/><polygon points="0,-60 -25,45 0,20" fill="rgba(255,255,255,0.9)" stroke="${stroke}" stroke-width="2"/><text x="0" y="-95" fill="${stroke}" font-size="48" font-family="'Arial Black', Impact, sans-serif" font-weight="900" text-anchor="middle" dominant-baseline="central">N</text></svg>`
};

function updateImageItem(item, content) {
    if (item.dataset.type !== 'Image') return;
    let style = item.dataset.svgStyle || 'Estilo 2';
    let fill = item.dataset.svgFill || '#333333';
    let stroke = item.dataset.svgStroke || '#ffffff';
    if (compassSVGs[style]) {
        content.innerHTML = compassSVGs[style](fill, stroke);
    }
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
        
        let innerMap = activeItem.querySelector('#composer-map-inner');
        if (innerMap) {
            innerMap.style.left = dx + 'px';
            innerMap.style.top = dy + 'px';
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
        
        let newPanX = (parseFloat(activeItem.dataset.panX) || 0) + dx;
        let newPanY = (parseFloat(activeItem.dataset.panY) || 0) + dy;
        activeItem.dataset.panX = newPanX;
        activeItem.dataset.panY = newPanY;
        
        // Pede a nova imagem com o Pan nativo incorporado no servidor!
        if (window.geonexMapServerUrl) {
            let img1 = activeItem.querySelector('img');
            let wBox = Math.round(parseFloat(activeItem.style.width));
            let hBox = Math.round(parseFloat(activeItem.style.height));
            if (img1 && wBox > 0 && hBox > 0) {
                // Retorna a DIV à origem (porque o offset agora já vem renderizado na imagem base)
                let innerMap = activeItem.querySelector('#composer-map-inner');
                if (innerMap) {
                    innerMap.style.left = '0px';
                    innerMap.style.top = '0px';
                }
                let rot = activeItem.dataset.mapRotation || 0;
                img1.src = window.geonexMapServerUrl + `mapa/?w=${wBox}&h=${hBox}&ox=${newPanX}&oy=${newPanY}&rot=${rot}&dpi=2.0&c=1&t=` + new Date().getTime();
            }
        }
        
        isPanningMapContent = false;
        activeItem.style.cursor = 'grab';
    }

    if (isDragging || isResizing) {
        if (isResizing && activeItem && activeItem.dataset.type === 'Map') {
            // Quando termina de redimensionar o mapa, pede ao motor gráfico uma nova imagem com as dimensões exatas da caixa!
            if (window.geonexMapServerUrl) {
                let img1 = activeItem.querySelector('img');
                let newW = Math.round(parseFloat(activeItem.style.width));
                let newH = Math.round(parseFloat(activeItem.style.height));
                let px = activeItem.dataset.panX || 0;
                let py = activeItem.dataset.panY || 0;
                let rot = activeItem.dataset.mapRotation || 0;
                if (img1 && newW > 0 && newH > 0) {
                    img1.src = window.geonexMapServerUrl + `mapa/?w=${newW}&h=${newH}&ox=${px}&oy=${py}&rot=${rot}&dpi=2.0&c=1&t=` + new Date().getTime();
                }
            }
        }

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
    if (propName === 'BgColor') {
        item.dataset.bgColor = propValue;
        if (item.dataset.hasBg === 'true') {
            item.style.backgroundColor = propValue;
        }
    }
    if (propName === 'HasBg') {
        item.dataset.hasBg = propValue;
        if (propValue) {
            item.style.backgroundColor = item.dataset.bgColor || '#ffffff';
        } else {
            item.style.backgroundColor = 'transparent';
        }
    }
    
    if (propName === 'Opacity') item.style.opacity = propValue;
    if (propName === 'BlendMode') item.style.mixBlendMode = propValue;
    
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
        item.dataset.textAlignH = propValue;
        let content = item.querySelector('.item-content');
        if (content && item.dataset.type === 'Text') {
            content.style.display = 'flex';
            content.style.flexDirection = 'column';
            if (propValue === 'left') content.style.alignItems = 'flex-start';
            if (propValue === 'center') content.style.alignItems = 'center';
            if (propValue === 'right') content.style.alignItems = 'flex-end';
            content.style.textAlign = propValue;
        }
    }
    
    if (propName === 'TextAlignV') {
        item.dataset.textAlignV = propValue;
        let content = item.querySelector('.item-content');
        if (content && item.dataset.type === 'Text') {
            content.style.display = 'flex';
            content.style.flexDirection = 'column';
            if (propValue === 'top') content.style.justifyContent = 'flex-start';
            if (propValue === 'center') content.style.justifyContent = 'center';
            if (propValue === 'bottom') content.style.justifyContent = 'flex-end';
        }
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
        let oldRot = parseFloat(item.dataset.mapRotation) || 0;
        let newRot = parseFloat(propValue) || 0;
        
        let delta = (oldRot - newRot) * Math.PI / 180.0;
        let px = parseFloat(item.dataset.panX) || 0;
        let py = parseFloat(item.dataset.panY) || 0;
        
        let newPx = px * Math.cos(delta) - py * Math.sin(delta);
        let newPy = px * Math.sin(delta) + py * Math.cos(delta);
        
        item.dataset.panX = newPx;
        item.dataset.panY = newPy;
        item.dataset.mapRotation = newRot;
        
        if (window.geonexMapServerUrl) {
            let img1 = item.querySelector('img');
            let w = Math.round(parseFloat(item.style.width));
            let h = Math.round(parseFloat(item.style.height));
            if (img1 && w > 0 && h > 0) {
                img1.src = window.geonexMapServerUrl + `mapa/?w=${w}&h=${h}&ox=${newPx}&oy=${newPy}&rot=${newRot}&dpi=2.0&c=1&t=` + new Date().getTime();
            }
        }
    }
    
    if (propName === 'SvgStyle') item.dataset.svgStyle = propValue;
    if (propName === 'SvgFill') item.dataset.svgFill = propValue;
    if (propName === 'SvgStroke') item.dataset.svgStroke = propValue;
    if (['SvgStyle', 'SvgFill', 'SvgStroke'].includes(propName) && item.dataset.type === 'Image') {
        let content = item.querySelector('.item-content');
        if (content) updateImageItem(item, content);
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

async function prepareMapsForExport(highDpi) {
    let maps = document.querySelectorAll('.composer-item[data-type="Map"] img');
    let promises = [];
    maps.forEach(img => {
        let currentSrc = img.src;
        if(currentSrc.includes('dpi=')) {
            let newSrc = currentSrc.replace(/dpi=[0-9\.]+/, `dpi=${highDpi}`);
            if (newSrc !== currentSrc) {
                let p = new Promise(resolve => {
                    let done = false;
                    const finish = () => { if(!done){ done=true; resolve(); } };
                    img.onload = finish;
                    img.onerror = finish;
                    img.src = newSrc;
                    setTimeout(finish, 15000); // 15s max
                });
                promises.push(p);
            }
        }
    });
    await Promise.all(promises);
}

async function execExportPNG(filename) {
    let paper = document.getElementById('paper-sheet');
    document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
    if (dotnetHelper) dotnetHelper.invokeMethodAsync('OnItemDeselected');
    
    await prepareMapsForExport("4.0");
    
    html2canvas(paper, { scale: 4, useCORS: true }).then(async canvas => {
        let link = document.createElement('a');
        link.download = filename;
        link.href = canvas.toDataURL('image/png');
        link.click();
        
        await prepareMapsForExport("2.0");
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

async function execExportPDF(filename) {
    let paper = document.getElementById('paper-sheet');
    document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
    if (dotnetHelper) dotnetHelper.invokeMethodAsync('OnItemDeselected');
    
    await prepareMapsForExport("4.0");
    
    html2canvas(paper, { scale: 4, useCORS: true }).then(async canvas => {
        let imgData = canvas.toDataURL('image/png');
        const { jsPDF } = window.jspdf;
        let orientation = paper.offsetWidth > paper.offsetHeight ? 'l' : 'p';
        let pdf = new jsPDF(orientation, 'mm', 'a4');
        let pdfWidth = pdf.internal.pageSize.getWidth();
        let pdfHeight = (canvas.height * pdfWidth) / canvas.width;
        pdf.addImage(imgData, 'PNG', 0, 0, pdfWidth, pdfHeight);
        pdf.save(filename);
        
        await prepareMapsForExport("2.0");
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
