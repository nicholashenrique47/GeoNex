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
let composerEvents = null;
let composerExporting = false;
let composerBatching = false;
let composerHistory = [], composerHistoryIndex = -1;
let composerClipboard = null;
let composerAppearanceClipboard = null;
const composerMapBookmarks = new Map();
let composerSnap = false;
let printMapContext = null;
let composerGridMm = 5, composerMarginMm = 10;
const COMPOSER_PX_MM = 96 / 25.4;
const composerGridNumbers={GridIntervalX:[.000001,1e12],GridIntervalY:[.000001,1e12],GridOffsetX:[-1e12,1e12],GridOffsetY:[-1e12,1e12],GridFontSize:[8,24],GridPrecision:[0,6],GridStroke:[.1,3],GridTickLength:[2,15]};
const composerGridBooleans=['GridEnabled','GridAuto','GridLabels','GridTicks','GridTop','GridBottom','GridLeft','GridRight'];
Object.assign(composerGridNumbers,{GridLabelDistance:[0,30]});
const composerGridEnums={GridMode:['lines','cross','frame'],GridLabelPosition:['inside','outside'],GridLabelOrientation:['horizontal','border'],GridLabelFormat:['axis','number'],GridLabelAxes:['both','x','y']};
function composerGridFieldValid(key,value) {
    if(key==='Id')return typeof value==='string'&&/^grid_[a-zA-Z0-9_-]{1,80}$/.test(value);
    if(key==='Name')return typeof value==='string'&&value.length<=100;
    if(composerGridNumbers[key]){const [min,max]=composerGridNumbers[key];return Number.isFinite(value)&&value>=min&&value<=max&&(key!=='GridPrecision'||Number.isInteger(value));}
    if(composerGridBooleans.includes(key))return typeof value==='boolean';
    if(composerGridEnums[key])return composerGridEnums[key].includes(value);
    if(['GridColor','GridLabelColor'].includes(key))return typeof value==='string'&&value.length<=80&&CSS.supports('color',value)&&!/var\(|url\(|expression/i.test(value);
    return false;
}
function composerGetGrids(item) {
    if(item.dataset.grids!==undefined)return JSON.parse(item.dataset.grids);
    const legacy=JSON.parse(item.dataset.properties||'{}');
    return [{Id:'grid_legacy',Name:'Grade principal',...Object.fromEntries(Object.entries(legacy).filter(([key])=>key.startsWith('Grid')&&key!=='GridWarning')),GridLabelColor:legacy.GridLabelColor||legacy.GridColor||'#334155'}];
}
window.composerGridAction=function(id,action,gridId,key,value){
    const item=composerItems().find(i=>i.id===id&&i.dataset.type==='Map');
    if(!item||item.dataset.locked==='true'||composerExporting||composerBatching)return null;
    const grids=composerGetGrids(item),index=grids.findIndex(g=>g.Id===gridId);
    let selected=gridId;
    if(action==='add'||action==='duplicate'){
        if(grids.length>=8||(action==='duplicate'&&index<0))return null;
        const g=action==='duplicate'?{...grids[index]}:{GridEnabled:true};
        g.Id='grid_'+[...crypto.getRandomValues(new Uint32Array(4))].map(n=>n.toString(16).padStart(8,'0')).join('');g.Name=action==='duplicate'?(g.Name.slice(0,90)+' (cópia)'):`Grade ${grids.length+1}`;
        grids.push(g);selected=g.Id;
    }else if(index<0)return null;
    else if(action==='remove'){grids.splice(index,1);selected=grids[Math.min(index,grids.length-1)]?.Id||'';}
    else if(action==='up'||action==='down'){
        const to=index+(action==='up'?-1:1);if(to<0||to>=grids.length)return null;
        [grids[index],grids[to]]=[grids[to],grids[index]];
    }else if(action==='update'&&key!=='Id'&&composerGridFieldValid(key,value))grids[index][key]=value;
    else return null;
    item.dataset.grids=JSON.stringify(grids);composerRenderGrid(item);composerCommit();return selected;
};

// Projected coordinates -> paper CSS pixels, using the same inverted Y and -rotation as Skia.
window.composerBuildGrid = function(view, width, height, rotation, p={}, occupied=[]) {
    if (![view?.originX,view?.originY,view?.centerX,view?.centerY,width,height,rotation].every(Number.isFinite) || width<=0 || height<=0 || !(view.metersPerUnit>0))
        return {warning:'Atualize a vista do projeto para obter origem e SRC projetado.'};
    const z=composerScaleToZoom(view.scale,view.metersPerUnit), a=rotation*Math.PI/180,c=Math.cos(a),s=Math.sin(a);
    const world=(x,y)=>{const u=(x-width/2)/z,v=(y-height/2)/z;return [view.originX+view.centerX+c*u-s*v,view.originY-view.centerY-s*u-c*v];};
    const screen=(x,y)=>{const dx=x-view.originX-view.centerX,dy=view.originY-y-view.centerY;return [width/2+z*(c*dx+s*dy),height/2+z*(-s*dx+c*dy)];};
    const corners=[[0,0],[width,0],[width,height],[0,height]].map(([x,y])=>world(x,y));
    const bounds=[0,1].map(i=>[Math.min(...corners.map(v=>v[i])),Math.max(...corners.map(v=>v[i]))]);
    const nice=n=>{const power=10**Math.floor(Math.log10(n)),r=n/power;return (r<=1?1:r<=2?2:r<=5?5:10)*power;};
    const steps=bounds.map(([min,max],i)=>p.GridAuto!==false?nice((max-min)/6):Number(p[i?'GridIntervalY':'GridIntervalX']??100));
    const values=bounds.map(([min,max],i)=>{
        const step=steps[i],offset=Number(p[i?'GridOffsetY':'GridOffsetX']??0);
        if (!(step>0) || !Number.isFinite(step) || !Number.isFinite(offset)) return null;
        const first=Math.ceil((min-offset)/step),last=Math.floor((max-offset)/step);
        if (!Number.isSafeInteger(first)||!Number.isSafeInteger(last)||last-first>79) return null;
        return Array.from({length:Math.max(0,last-first+1)},(_,j)=>offset+(first+j)*step);
    });
    if(values.some(v=>v===null) || (p.GridMode==='cross' && values[0].length*values[1].length>1600)) return {warning:'Grade muito densa. Aumente os intervalos ou use Automático.'};
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
    const outside=p.GridLabelPosition==='outside',distance=p.GridLabelDistance??2;
    const padding=outside?Math.ceil((p.GridFontSize??10)*24+distance+(p.GridTickLength??5)+10):0;
    for(const [k,v] of Object.entries({xmlns:'http://www.w3.org/2000/svg',width:width+2*padding,height:height+2*padding,viewBox:`${-padding} ${-padding} ${width+2*padding} ${height+2*padding}`})) svg.setAttribute(k,v);
    const add=(tag,attrs,text)=>{const el=document.createElementNS(svg.namespaceURI,tag);for(const [k,v] of Object.entries(attrs))el.setAttribute(k,String(v));if(text!==undefined)el.textContent=text;svg.appendChild(el);return el;};
    const color=p.GridColor||'#334155',stroke=p.GridStroke??.5,font=p.GridFontSize??10,tick=p.GridTickLength??5;
    const line=(x1,y1,x2,y2)=>add('line',{x1,y1,x2,y2,stroke:color,'stroke-width':stroke});
    const clip=(point,direction)=>{
        const hits=[];
        for(const [axis,edge,side] of [[0,0,'Left'],[0,width,'Right'],[1,0,'Top'],[1,height,'Bottom']]) {
            if(Math.abs(direction[axis])<1e-10)continue;
            const t=(edge-point[axis])/direction[axis],pt=[point[0]+t*direction[0],point[1]+t*direction[1]];
            if(pt[0]>=-1e-6&&pt[0]<=width+1e-6&&pt[1]>=-1e-6&&pt[1]<=height+1e-6&&!hits.some(h=>Math.hypot(h.x-pt[0],h.y-pt[1])<1e-5)) hits.push({x:pt[0],y:pt[1],side});
        }
        return hits;
    };
    const labels=[],lines=[],rectangles=occupied;
    const measure=document.createElement('canvas').getContext('2d');measure.font=`${font}px Arial`;
    const label=(hit,axis,value)=>{
        if(p['Grid'+hit.side]===false)return;
        if(p.GridTicks!==false){const dx=hit.side==='Left'?tick:hit.side==='Right'?-tick:0,dy=hit.side==='Top'?tick:hit.side==='Bottom'?-tick:0;line(hit.x,hit.y,hit.x+dx,hit.y+dy);}
        if(p.GridLabels===false || (p.GridLabelAxes==='x'&&axis!==0) || (p.GridLabelAxes==='y'&&axis!==1))return;
        const numeric=(Object.is(value,-0)?0:value).toFixed(p.GridPrecision??0);
        const text=p.GridLabelFormat==='number'?numeric:`${axis===0?'X':'Y'} ${numeric}`,tw=measure.measureText(text).width+4,th=font+4;
        const vertical=p.GridLabelOrientation==='border'&&['Left','Right'].includes(hit.side);
        const bw=vertical?th:tw,bh=vertical?tw:th,angle=vertical?(hit.side==='Left'?-90:90):0;
        let x=hit.x,y=hit.y;
        const gap=distance+(outside?0:(p.GridTicks===false?0:tick));
        if(hit.side==='Top'||hit.side==='Bottom'){
            x=Math.max(bw/2+2,Math.min(width-bw/2-2,x));
            y=hit.side==='Top'?(outside?-gap-bh/2:gap+bh/2):(outside?height+gap+bh/2:height-gap-bh/2);
        }else{
            x=hit.side==='Left'?(outside?-gap-bw/2:gap+bw/2):(outside?width+gap+bw/2:width-gap-bw/2);
            y=Math.max(bh/2+2,Math.min(height-bh/2-2,y));
        }
        const box={x:x-bw/2,y:y-bh/2,w:bw,h:bh};
        if((!outside&&(box.x<0||box.y<0||box.x+bw>width||box.y+bh>height))||rectangles.some(b=>box.x<b.x+b.w+2&&box.x+box.w+2>b.x&&box.y<b.y+b.h+2&&box.y+box.h+2>b.y))return;
        rectangles.push(box);labels.push({x,y,text,tw,th,angle,box});
    };
    for(let axis=0;axis<2;axis++) for(const value of values[axis]) {
        const point=axis===0?screen(value,view.originY-view.centerY):screen(view.originX+view.centerX,value);
        const hits=clip(point,axis===0?[s,c]:[c,-s]);
        if(hits.length!==2)continue;
        lines.push({axis,value,hits});
        if((p.GridMode??'lines')==='lines')line(hits[0].x,hits[0].y,hits[1].x,hits[1].y);
        hits.forEach(hit=>label(hit,axis,value));
    }
    if(p.GridMode==='cross')for(const x of values[0])for(const y of values[1]){const [u,v]=screen(x,y);if(u>=3&&u<=width-3&&v>=3&&v<=height-3){line(u-3,v,u+3,v);line(u,v-3,u,v+3);}}
    // Label backgrounds prevent map detail from obscuring coordinates. Collisions are suppressed.
    labels.forEach(({x,y,text,tw,th,angle})=>{const transform=`translate(${x} ${y}) rotate(${angle})`;add('rect',{x:-tw/2,y:-th/2,width:tw,height:th,transform,fill:'white','fill-opacity':.9});add('text',{x:0,y:font*.35,transform,fill:p.GridLabelColor||'#334155','font-family':'Arial','font-size':font,'text-anchor':'middle'},text);});
    return {svg:new XMLSerializer().serializeToString(svg),warning:'',lines,steps,padding,labels};
};
function composerRenderGrid(item) {
    item.querySelectorAll('.composer-grid-image').forEach(i=>i.remove());item.dataset.gridWarning='';
    const occupied=[],results=[],warnings=[],w=parseFloat(item.style.width),h=parseFloat(item.style.height);
    for(const grid of composerGetGrids(item).slice().reverse()){
        if(!grid.GridEnabled)continue;
        const result=window.composerBuildGrid(item.dataset.mapView?JSON.parse(item.dataset.mapView):null,w,h,Number(item.dataset.mapRotation||0),grid,occupied);
        if(result.warning)warnings.push(`${grid.Name}: ${result.warning}`);
        if(result.svg)results.push({grid,result});
    }
    item.dataset.gridWarning=warnings.join(' ');item.dataset.gridLabelBounds=JSON.stringify(occupied);
    for(const {grid,result} of results.reverse()){
        const pad=result.padding,img=document.createElement('img');img.className='composer-grid-image';img.alt=grid.Name;img.dataset.gridId=grid.Id;
        img.style.cssText=`position:absolute;left:${-pad}px;top:${-pad}px;width:${w+2*pad}px;height:${h+2*pad}px;max-width:none;pointer-events:none`;
        img.src='data:image/svg+xml;charset=utf-8,'+encodeURIComponent(result.svg);item.appendChild(img);
    }
}
const composerAdvancedNumbers = {LineHeight:[0.8,3],LetterSpacing:[-2,20],LegendColumns:[1,4],LegendFontSize:[6,48],LegendTitleSize:[6,72],LegendRowGap:[0,30],LegendColumnGap:[0,50],LegendSymbolWidth:[2,80],LegendSymbolHeight:[2,80]};

function composerItems() { return [...(composerPaper?.querySelectorAll('.composer-item') || [])]; }
function composerModel(item) {
    return { ...JSON.parse(item.dataset.properties || '{}'), Id: item.id, Type: item.dataset.type,
        X: parseFloat(item.style.left), Y: parseFloat(item.style.top),
        Width: parseFloat(item.style.width), Height: parseFloat(item.style.height),
        ZIndex: Number(item.style.zIndex || 10), TextContent: item.dataset.text || '',
        Locked: item.dataset.locked === 'true', Visible: item.style.display !== 'none',
        LegendItems: JSON.parse(item.dataset.legendData || '[]'),
        MapScale: item.dataset.mapView ? JSON.parse(item.dataset.mapView).scale : null,
        ScaleAvailable: !!item.dataset.mapView,
        GridAvailable: !!item.dataset.mapView && Number.isFinite(JSON.parse(item.dataset.mapView).originX) && Number.isFinite(JSON.parse(item.dataset.mapView).originY),
        GridWarning: item.dataset.gridWarning || '',
        Grids: item.dataset.type==='Map'?composerGetGrids(item):[],
        PictureSource: item.dataset.pictureSource || null,
        MapCrsName: item.dataset.mapView ? JSON.parse(item.dataset.mapView).crsName : '',
        ScaleBarMapId: item.dataset.scaleBarMapId || null,
        ScaleBarMeters: Number(item.dataset.scaleBarMeters || 100) };
}
function composerNotify() {
    if (composerBatching) return;
    if (dotnetHelper) return dotnetHelper.invokeMethodAsync('OnLayoutChanged', composerItems().map(composerModel),
        activeItem?.id || null, composerHistoryIndex > 0, composerHistoryIndex < composerHistory.length - 1);
}
function composerCommit() {
    if (!composerPaper || composerExporting || composerBatching) return;
    const page = { widthMm:Number(composerPaper.dataset.widthMm), heightMm:Number(composerPaper.dataset.heightMm),
        gridMm:composerGridMm, marginMm:composerMarginMm, snap:composerSnap };
    const snapshot = composerItems().map(item => {
        const clone = item.cloneNode(true);
        clone.classList.remove('selected');
        return clone.outerHTML;
    }).join('');
    if (composerHistory[composerHistoryIndex]?.html !== snapshot || JSON.stringify(composerHistory[composerHistoryIndex]?.page) !== JSON.stringify(page)) {
        composerHistory.splice(composerHistoryIndex + 1);
        composerHistory.push({ html: snapshot, selected: activeItem?.id, page });
        // Bounded session history; no project data is persisted or overwritten.
        while (composerHistory.length > 50 || (composerHistory.length > 1 && composerHistory.reduce((n, s) => n + s.html.length, 0) > 2000000)) composerHistory.shift();
        composerHistoryIndex = composerHistory.length - 1;
    }
    composerNotify();
}
window.composerUndo = function(redo = false) {
    if (composerExporting) return;
    const index = composerHistoryIndex + (redo ? 1 : -1);
    if (index < 0 || index >= composerHistory.length) return;
    isDragging = isResizing = isPanningMapContent = false;
    composerHistoryIndex = index;
    // Only internally generated, escaped markup enters this in-memory history.
    composerPaper.innerHTML = composerHistory[index].html;
    composerRestorePage(composerHistory[index].page);
    composerItems().forEach(item => {
        item.addEventListener('mousedown', startDrag);
        item.querySelectorAll('.resize-handle').forEach(h => h.addEventListener('mousedown', startResize));
    });
    activeItem = composerPaper.querySelector('#' + composerHistory[index].selected);
    activeItem?.classList.add('selected');
    composerNotify();
};
function composerRestorePage(page) {
    composerPaper.dataset.widthMm=page.widthMm; composerPaper.dataset.heightMm=page.heightMm;
    composerPaper.style.width=page.widthMm*COMPOSER_PX_MM+'px'; composerPaper.style.height=page.heightMm*COMPOSER_PX_MM+'px';
    window.composerConfigureGuides(page.gridMm,page.marginMm); window.composerSetGuides(page.snap);
    dotnetHelper?.invokeMethodAsync('OnPageRestored',page.widthMm,page.heightMm,page.gridMm,page.marginMm,page.snap);
    window.composerFitPage();
}
window.composerDispose = function() {
    composerEvents?.abort();
    composerEvents = null;
    composerPaper = activeItem = dotnetHelper = null;
    composerHistory = []; composerHistoryIndex = -1; composerClipboard = null;
    composerAppearanceClipboard = null;
    composerMapBookmarks.clear();
    isDragging = isResizing = isPanningMapContent = isMapContentInteractionActive = false;
    composerSnap = false; workspaceZoom = 1;
};

window.composerInitV3 = function (paperId, helper, mapServerUrl, mapContext = null) {
    window.composerDispose();
    composerPaper = document.getElementById(paperId);
    dotnetHelper = helper;
    window.geonexMapServerUrl = mapServerUrl;
    printMapContext = mapContext;
    composerGridMm = 5; composerMarginMm = 10;
    
    // Deselecionar item se clicar fora
    composerEvents = new AbortController();
    const options = { signal: composerEvents.signal };
    composerPaper.addEventListener('mousedown', function (e) {
        if (e.target === composerPaper) {
            activeItem = null;
            document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
            if (dotnetHelper) {
                dotnetHelper.invokeMethodAsync('OnItemDeselected');
            }
        }
    }, options);

    // Global mouse events para arrastar e soltar
    document.addEventListener('mousemove', handleMouseMove, options);
    document.addEventListener('mouseup', handleMouseUp, options);
    document.addEventListener('keydown', composerKeyDown, options);
    document.addEventListener('wheel', composerWheel, { ...options, passive: false });
    document.addEventListener('contextmenu', composerContextMenu, options);
    document.addEventListener('mousedown', composerCloseContextMenu, options);
    composerCommit();
    window.composerFitPage();
};

window.composerAddItemV3 = function (type, text, x, y, w, h) {
    if (!composerPaper || composerExporting) return;
    w = Math.min(w, parseFloat(composerPaper.style.width) - 40);
    h = Math.min(h, parseFloat(composerPaper.style.height) - 40);
    x = Math.max(0, Math.min(x, parseFloat(composerPaper.style.width) - w));
    y = Math.max(0, Math.min(y, parseFloat(composerPaper.style.height) - h));

    if (x === -1 || y === -1) {
        let canvasArea = document.querySelector('.composer-canvas-area');
        if (canvasArea) {
            let rect = composerPaper.getBoundingClientRect();
            let areaRect = canvasArea.getBoundingClientRect();
            x = (areaRect.left + (areaRect.width / 2) - rect.left) / workspaceZoom - (w / 2);
            y = (areaRect.top + (areaRect.height / 2) - rect.top) / workspaceZoom - (h / 2);
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
    if (type === 'Map' && printMapContext?.metersPerUnit > 0) item.dataset.mapView = JSON.stringify(printMapContext);

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
        content.style.border = 'none';
        // Removemos o placeholder de texto
        
        // Vamos inicializar o Leaflet logo a seguir a anexar ao DOM
    } else if (type === 'Picture') {
        const image=document.createElement('img');image.alt=text||'Referência';
        image.style.cssText='width:100%;height:100%;object-fit:contain;pointer-events:none';content.appendChild(image);
    } else if (type === 'Text') {
        content.style.fontSize = '14px';
        content.style.fontWeight = 'normal';
        content.style.fontFamily = 'Arial, sans-serif';
        content.style.color = '#000';
        content.style.backgroundColor = 'transparent';
        content.style.border = 'none';
        content.style.padding = '0px';
        content.style.whiteSpace = 'pre-wrap';
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
        window.renderLegendHTML(item, content);
    } else if (type === 'Rectangle' || type === 'Ellipse') {
        content.style.background = '#e2e8f0';
        content.style.border = '1px solid #334155';
        item.dataset.hasBg = item.dataset.hasBorder = 'true';
        item.dataset.bgColor = '#e2e8f0'; item.dataset.borderColor = '#334155'; item.dataset.borderWidth = '1';
        item.dataset.properties = JSON.stringify({HasBg:true,HasBorder:true,BgColor:'#e2e8f0',BorderColor:'#334155',BorderWidth:1});
        if (type === 'Ellipse') content.style.borderRadius = '50%';
    } else if (type === 'ScaleBar') {
        const map = composerItems().find(i => i.dataset.type === 'Map' && i.dataset.mapView);
        item.dataset.scaleBarMapId = map?.id || '';
        item.dataset.scaleBarMeters = 100;
    } else if (type === 'Image') {
        content.style.backgroundColor = 'transparent';
        item.dataset.svgStyle = 'Estilo 5';
        item.dataset.svgFill = '#333333';
        item.dataset.svgStroke = '#333333';
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
        content.style.border = 'none';
        content.style.overflow = 'hidden';
        content.style.position = 'relative';

        let innerContainer = document.createElement('div');
        innerContainer.className = 'composer-map-inner';
        innerContainer.style.position = 'absolute';
        innerContainer.style.width = '100%';
        innerContainer.style.height = '100%';
        innerContainer.style.left = '0px';
        innerContainer.style.top = '0px';
        
        let img1 = document.createElement('img');
        img1.crossOrigin = 'anonymous';
        img1.alt = 'Mapa indisponível';
        
        // Se a janela souber a URL do servidor local de imagens, busca o mapa mais recente
        if (window.geonexMapServerUrl && !composerBatching) {
            let reqW = Math.round(w) || 1920;
            let reqH = Math.round(h) || 1080;
            img1.src = composerMapUrl(item, 2);
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
    
    if (type === 'ScaleBar') composerRenderScaleBar(item);
    activeItem = item;
    // Populate the legend before recording creation as one undo operation.
    if (dotnetHelper && !composerBatching) dotnetHelper.invokeMethodAsync('OnItemSelected', composerModel(item)).then(() => composerCommit());
    else composerCommit();
    return item.id;
};

function composerKeyDown(e) {
    if (!composerPaper?.isConnected || composerExporting || e.target.closest('input,textarea,select,[contenteditable="true"]')) return;
    const command = e.ctrlKey || e.metaKey;
    if (command && ['z', 'y'].includes(e.key.toLowerCase())) {
        e.preventDefault(); window.composerUndo(e.shiftKey || e.key.toLowerCase() === 'y'); return;
    }
    if (e.key === 'Escape') { activeItem?.classList.remove('selected'); activeItem = null; composerNotify(); return; }
    if (e.key === 'Delete' && activeItem) { e.preventDefault(); window.composerDeleteItem(activeItem.id); return; }
    if (activeItem && activeItem.dataset.locked !== 'true' && ['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(e.key)) {
        e.preventDefault();
        const step = COMPOSER_PX_MM * (e.shiftKey ? 10 : 1);
        activeItem.style.left = (parseFloat(activeItem.style.left) + (e.key === 'ArrowRight' ? step : e.key === 'ArrowLeft' ? -step : 0)) + 'px';
        activeItem.style.top = (parseFloat(activeItem.style.top) + (e.key === 'ArrowDown' ? step : e.key === 'ArrowUp' ? -step : 0)) + 'px';
        composerCommit(); return;
    }

    if (command && e.key.toLowerCase() === 'c' && activeItem) {
        e.preventDefault(); window.composerCopyItem(activeItem.id);
    }
    if (command && e.key.toLowerCase() === 'v' && composerClipboard) {
        e.preventDefault(); window.composerPasteItem();
    }
}

function startDrag(e) {
    if (e.button !== 0 || composerExporting) return;
    
    // Se o clique foi no handle de resize, não fazemos drag
    if (e.target.classList.contains('resize-handle')) return;
    
    isDragging = true;
    activeItem = e.currentTarget;

    // Seleciona o item atual (remove seleção dos outros)
    document.querySelectorAll('.composer-item').forEach(i => i.classList.remove('selected'));
    activeItem.classList.add('selected');
    composerNotify();
    if (activeItem.dataset.locked === 'true') { isDragging = false; e.stopPropagation(); return; }

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
    
    composerNotify();

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
    let style = item.dataset.svgStyle || 'Estilo 5';
    let fill = item.dataset.svgFill || '#333333';
    let stroke = item.dataset.svgStroke || '#333333';
    if (compassSVGs[style]) {
        content.innerHTML = compassSVGs[style](fill, stroke);
    }
}

function startResize(e) {
    if (e.button !== 0 || composerExporting || e.currentTarget.parentElement.dataset.locked === 'true') return;
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

    composerNotify();

    e.stopPropagation();
    e.preventDefault(); // Prevenir seleção de texto acidental
}

function handleMouseMove(e) {
    if (isPanningMapContent && activeItem && activeItem.dataset.type === 'Map') {
        let dx = (e.clientX - startMapPanX) / workspaceZoom;
        let dy = (e.clientY - startMapPanY) / workspaceZoom;
        
        let innerMap = activeItem.querySelector('.composer-map-inner');
        if (innerMap) {
            innerMap.style.left = `calc(50% + ${dx}px)`;
            innerMap.style.top = `calc(50% + ${dy}px)`;
            innerMap.style.transform = 'translate(-50%, -50%)';
        }
        return;
    }

    if (isDragging && activeItem) {
        let dx = (e.clientX - startX) / workspaceZoom;
        let dy = (e.clientY - startY) / workspaceZoom;
        
        const snap = v => composerSnap && !e.altKey ? Math.round(v / (composerGridMm * COMPOSER_PX_MM)) * composerGridMm * COMPOSER_PX_MM : v;
        activeItem.style.left = snap(initialX + dx) + 'px';
        activeItem.style.top = snap(initialY + dy) + 'px';
    } 
    else if (isResizing && activeItem) {
        // Resize in the item's local axes, keeping the opposite edge/corner fixed.
        const angle = Number(JSON.parse(activeItem.dataset.properties || '{}').Rotation || 0) * Math.PI / 180;
        const dx = (e.clientX - startX) / workspaceZoom, dy = (e.clientY - startY) / workspaceZoom;
        const localX = dx * Math.cos(angle) + dy * Math.sin(angle);
        const localY = -dx * Math.sin(angle) + dy * Math.cos(angle);
        const sx = resizeDir.includes('r') ? 1 : resizeDir.includes('l') ? -1 : 0;
        const sy = resizeDir.includes('b') ? 1 : resizeDir.includes('t') ? -1 : 0;
        const snap = value => composerSnap && !e.altKey ? Math.round(value / (composerGridMm * COMPOSER_PX_MM)) * composerGridMm * COMPOSER_PX_MM : value;
        let w = sx ? Math.max(20, snap(initialW + sx * localX)) : initialW;
        let h = sy ? Math.max(20, snap(initialH + sy * localY)) : initialH;
        if (e.shiftKey && sx && sy) {
            const factor = Math.max(w / initialW, h / initialH);
            w = initialW * factor; h = initialH * factor;
        }
        const cx = sx * (w - initialW) / 2, cy = sy * (h - initialH) / 2;
        activeItem.style.left = (initialX + initialW / 2 + cx * Math.cos(angle) - cy * Math.sin(angle) - w / 2) + 'px';
        activeItem.style.top = (initialY + initialH / 2 + cx * Math.sin(angle) + cy * Math.cos(angle) - h / 2) + 'px';
        activeItem.style.width = w + 'px'; activeItem.style.height = h + 'px';
    }
}

function handleMouseUp(e) {
    const changed = isDragging || isResizing || isPanningMapContent;
    if (isPanningMapContent && activeItem) {
        const dx = (e.clientX - startMapPanX) / workspaceZoom, dy = (e.clientY - startMapPanY) / workspaceZoom;
        if (activeItem.dataset.mapView) {
            const view = JSON.parse(activeItem.dataset.mapView);
            const angle = Number(activeItem.dataset.mapRotation || 0) * Math.PI / 180;
            const zoom = composerScaleToZoom(view.scale, view.metersPerUnit);
            view.centerX -= (dx * Math.cos(angle) - dy * Math.sin(angle)) / zoom;
            view.centerY -= (dx * Math.sin(angle) + dy * Math.cos(angle)) / zoom;
            activeItem.dataset.mapView = JSON.stringify(view);
        } else {
            activeItem.dataset.panX = Number(activeItem.dataset.panX || 0) + dx;
            activeItem.dataset.panY = Number(activeItem.dataset.panY || 0) + dy;
        }
        const inner = activeItem.querySelector('.composer-map-inner');
        inner.style.left = inner.style.top = '0px'; inner.style.transform = 'none';
        composerRefreshMap(activeItem);
        isPanningMapContent = false; activeItem.style.cursor = 'grab';
    }
    if (isResizing && activeItem?.dataset.type === 'Map') composerRefreshMap(activeItem);
    if (isResizing && activeItem?.dataset.type === 'ScaleBar') composerRenderScaleBar(activeItem);

    isDragging = false;
    isResizing = false;
    if (changed) composerCommit();
}

// Escutar Zoom (Scroll) dentro do mapa ou da página
function composerWheel(e) {
    if (composerExporting) return;
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
}

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
    workspaceZoom = Math.max(0.05, Math.min(5, Number(zoom) || 1));
    let sheet = document.getElementById('paper-sheet');
    if (sheet) {
        sheet.style.transform = `scale(${workspaceZoom})`;
        sheet.style.transformOrigin = 'top left';
        const holder = document.getElementById('composer-paper-holder');
        if (holder) { holder.style.width = (parseFloat(sheet.style.width) * workspaceZoom) + 'px'; holder.style.height = (parseFloat(sheet.style.height) * workspaceZoom) + 'px'; }
    }
};

window.composerUpdateItemProperty = function (id, propName, propValue) {
    let item = document.getElementById(id);
    if (!item || composerExporting || (item.dataset.locked === 'true' && !['Locked', 'Visible'].includes(propName))) return;
    if (propName === 'MapScale') {
        if (!item.dataset.mapView) return;
        composerScaleToZoom(Number(propValue), JSON.parse(item.dataset.mapView).metersPerUnit);
    }
    if (propName === 'Name') propValue = String(propValue ?? '').slice(0,120);
    if (propName === 'MapPreviewDpi' && ![0.5,1,2].includes(propValue)) return;
    if (propName.startsWith('Grid')) {
        if (composerGridNumbers[propName]) {
            const [min,max]=composerGridNumbers[propName]; propValue=Number(propValue);
            if (!Number.isFinite(propValue) || propValue<min || propValue>max || (propName==='GridPrecision' && !Number.isInteger(propValue))) return;
        }
        if (propName==='GridMode' && !['lines','cross','frame'].includes(propValue)) return;
    }
    if (composerAdvancedNumbers[propName]) {
        propValue = Number(propValue);
        if (!Number.isFinite(propValue)) return;
        const [min,max] = composerAdvancedNumbers[propName];
        propValue = Math.max(min,Math.min(max,propValue));
        if (propName === 'LegendColumns') propValue = Math.round(propValue);
    }
    if (propName === 'TextDecoration' && !['none','underline','line-through'].includes(propValue)) return;
    if (propName === 'TextTransform' && !['none','uppercase','lowercase','capitalize'].includes(propValue)) return;
    if (propName === 'BorderStyle' && !['solid','dashed','dotted'].includes(propValue)) return;
    if (['X','Y','Width','Height','Rotation','FontSize','BorderWidth','Opacity','MapScale','MapRotation','Padding'].includes(propName)) {
        propValue = Number(propValue);
        if (!Number.isFinite(propValue)) return;
        if (['Width','Height','FontSize'].includes(propName)) propValue = Math.max(1, propValue);
        if (propName === 'Opacity') propValue = Math.max(0, Math.min(1, propValue));
        if (propName === 'BorderWidth') propValue = Math.max(0, Math.min(100, propValue));
        if (propName === 'Padding') propValue = Math.max(0, Math.min(200, propValue));
        if (['Rotation','MapRotation'].includes(propName)) propValue = ((propValue+180)%360+360)%360-180;
    }
    const properties = JSON.parse(item.dataset.properties || '{}');
    properties[propName === 'Text' ? 'TextContent' : propName] = propValue;
    item.dataset.properties = JSON.stringify(properties);
    if (propName === 'Locked') item.dataset.locked = String(propValue);
    if (propName === 'Visible') item.style.display = propValue ? '' : 'none';
    const itemContent = item.querySelector('.item-content');
    if(propName==='PictureSource'&&item.dataset.type==='Picture'){
        if(typeof propValue!=='string'||!/^data:image\/(png|jpeg);base64,[A-Za-z0-9+/=]+$/.test(propValue)||propValue.length>16000050)throw new Error('Imagem incorporada inválida.');
        item.dataset.pictureSource=propValue;itemContent.querySelector('img').src=propValue;
        delete properties.PictureSource;item.dataset.properties=JSON.stringify(properties);
    }
    if (propName.startsWith('Grid') && item.dataset.type==='Map' && !composerBatching) composerRenderGrid(item);
    if (propName === 'MapPreviewDpi' && item.dataset.type === 'Map') composerRefreshMap(item);
    if (propName === 'FontFamily') itemContent.style.fontFamily = propValue;
    if (propName === 'FontWeight') itemContent.style.fontWeight = propValue;
    if (propName === 'FontStyle') itemContent.style.fontStyle = propValue;
    if (propName === 'Padding') itemContent.style.padding = propValue + 'px';
    if (propName === 'LineHeight') itemContent.style.lineHeight = propValue;
    if (propName === 'LetterSpacing') itemContent.style.letterSpacing = propValue + 'px';
    if (propName === 'TextDecoration') itemContent.style.textDecoration = propValue;
    if (propName === 'TextTransform') itemContent.style.textTransform = propValue;
    if (propName.startsWith('Legend') && propName !== 'LegendItems' && item.dataset.type === 'Legend') window.renderLegendHTML(item,itemContent);

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
    
    if (propName === 'BorderStyle') item.dataset.borderStyle = propValue;
    if (['BorderColor','BorderWidth','HasBorder','BorderStyle'].includes(propName)) {
        let hw = item.dataset.hasBorder === 'true';
        let w = parseFloat(item.dataset.borderWidth || '1');
        let cw = item.dataset.borderColor || '#000000';
        
        if (propName === 'HasBorder') {
            hw = propValue;
            item.dataset.hasBorder = propValue;
        }
        if (propName === 'BorderWidth') {
            w = parseFloat(propValue);
            item.dataset.borderWidth = propValue;
        }
        if (propName === 'BorderColor') {
            cw = propValue;
            item.dataset.borderColor = propValue;
        }
        
        if (hw) {
            item.style.setProperty('--composer-frame', `${w}px ${item.dataset.borderStyle || 'solid'} ${cw}`);
        } else {
            item.style.setProperty('--composer-frame', 'none');
        }
    }

    if (propName === 'Text') {
        item.dataset.text = propValue;
        let content = item.querySelector('.item-content');
        if (content) {
            if (item.dataset.type === 'Text') {
                content.innerText = propValue;
            } else if (item.dataset.type === 'Legend') {
                window.renderLegendHTML(item, content);
            }
        }
    }

    if (propName === 'LegendItems') {
        item.dataset.legendData = propValue;
        let content = item.querySelector('.item-content');
        if (content && item.dataset.type === 'Legend') {
            window.renderLegendHTML(item, content);
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
        if (!item.dataset.mapView) return;
        const view = JSON.parse(item.dataset.mapView);
        view.scale = Number(propValue);
        composerScaleToZoom(view.scale, view.metersPerUnit);
        item.dataset.mapView = JSON.stringify(view);
        composerRefreshMap(item);
        composerItems().filter(i => i.dataset.type === 'ScaleBar' && i.dataset.scaleBarMapId === item.id).forEach(composerRenderScaleBar);
    }
    if (propName === 'ScaleBarMapId') item.dataset.scaleBarMapId = propValue;
    if (propName === 'ScaleBarMeters') item.dataset.scaleBarMeters = propValue;
    if (item.dataset.type === 'ScaleBar') composerRenderScaleBar(item);
    
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
        for (const north of composerItems().filter(i => i.dataset.type === 'Image')) {
            const props = JSON.parse(north.dataset.properties || '{}');
            if (props.SyncWithMap && props.LinkedMapId === item.id) {
                props.Rotation = newRot;
                north.dataset.properties = JSON.stringify(props);
                north.style.transform = `rotate(${-newRot}deg)`;
                props.Rotation = -newRot; north.dataset.properties = JSON.stringify(props);
            }
        }
        
        if (window.geonexMapServerUrl) {
            let img1 = item.querySelector('img');
            let w = Math.round(parseFloat(item.style.width));
            let h = Math.round(parseFloat(item.style.height));
            if (img1 && w > 0 && h > 0) {
                composerRefreshMap(item);
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
    if (['Width', 'Height'].includes(propName) && item.dataset.type === 'Map') composerRefreshMap(item);
    if (['Rectangle','Ellipse'].includes(item.dataset.type)) {
        item.style.backgroundColor = 'transparent'; item.style.setProperty('--composer-frame','none');
        itemContent.style.backgroundColor = item.dataset.hasBg === 'true' ? item.dataset.bgColor : 'transparent';
        itemContent.style.border = item.dataset.hasBorder === 'true' ? `${item.dataset.borderWidth || 1}px ${item.dataset.borderStyle || 'solid'} ${item.dataset.borderColor}` : 'none';
    }
    composerCommit();
};

function composerContextMenu(e) {
    if (composerExporting) return;
    let composerArea = e.target.closest('.composer-canvas-area');
    if (composerArea) {
        e.preventDefault();
        let item = e.target.closest('.composer-item');
        if (dotnetHelper) {
            let itemId = item ? item.id : null;
            dotnetHelper.invokeMethodAsync('OnContextMenu', itemId, e.clientX, e.clientY);
        }
    }
}

function composerCloseContextMenu(e) {
    if (e.button !== 2) { // Não é botão direito
        if (!e.target.closest('.context-menu')) {
            if (dotnetHelper) dotnetHelper.invokeMethodAsync('CloseContextMenu');
        }
    }
}


// Injeção de CSS Dinâmico para Handles de Resize
const style = document.createElement('style');
style.textContent = `
    .composer-item {
        box-sizing: border-box;
        cursor: move;
        color: #000;
    }
    .composer-item.selected {
        outline: 1px dashed #38bdf8;
    }
    .composer-item::after {
        content: ''; position:absolute; inset:0; pointer-events:none;
        border:var(--composer-frame, none); box-sizing:border-box;
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
    .composer-item.selected:not([data-locked="true"]) .resize-handle {
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

// Layout operations never mutate the map engine or its cache.
window.composerFitPage = function() {
    const area = document.getElementById('composer-canvas-area');
    if (!area || !composerPaper) return;
    const zoom = Math.max(0.05, Math.min(1, (area.clientWidth - 100) / parseFloat(composerPaper.style.width),
        (area.clientHeight - 100) / parseFloat(composerPaper.style.height)));
    window.composerSetWorkspaceZoom(zoom);
    dotnetHelper?.invokeMethodAsync('OnWorkspaceZoomChanged', zoom);
};
window.composerPageChanged = function() {
    window.composerFitPage();
    composerCommit();
};
window.composerSetGuides = function(enabled) {
    composerSnap = enabled;
    composerPaper?.classList.toggle('show-guides', enabled);
};
window.composerLinkNorth = function(id, mapId, enabled) {
    const item = composerItems().find(i => i.id === id && i.dataset.type === 'Image');
    const map = composerItems().find(i => i.id === mapId && i.dataset.type === 'Map');
    if (!item || composerExporting || item.dataset.locked === 'true') return;
    const props = JSON.parse(item.dataset.properties || '{}');
    props.LinkedMapId = map?.id || null;
    props.SyncWithMap = !!enabled && !!map;
    if (props.SyncWithMap) {
        props.Rotation = -Number(map.dataset.mapRotation || 0);
        item.style.transform = `rotate(${props.Rotation}deg)`;
    }
    item.dataset.properties = JSON.stringify(props);
    composerCommit();
};
window.composerScaleToZoom = function(scale, metersPerUnit) {
    if (!Number.isFinite(scale) || scale < 1 || scale > 1e9 || !Number.isFinite(metersPerUnit) || metersPerUnit <= 0)
        throw new Error('Escala inválida ou SRC sem unidades projetadas.');
    return 96 / .0254 * metersPerUnit / scale;
};
function composerMapUrl(item, dpi) {
    const url = new URL('mapa/', window.geonexMapServerUrl);
    Object.entries({ w: Math.max(1, Math.round(parseFloat(item.style.width))), h: Math.max(1, Math.round(parseFloat(item.style.height))),
        ox: item.dataset.panX || 0, oy: item.dataset.panY || 0, rot: item.dataset.mapRotation || 0,
        dpi, c: 1, t: Date.now() }).forEach(([k,v]) => url.searchParams.set(k, v));
    if (item.dataset.mapView) {
        const view = JSON.parse(item.dataset.mapView);
        Object.entries({cs:view.scale, cx:view.centerX, cy:view.centerY, ct:view.token,
            cw:parseFloat(item.style.width), ch:parseFloat(item.style.height)}).forEach(([k,v])=>url.searchParams.set(k,v));
        url.searchParams.set('ox','0'); url.searchParams.set('oy','0');
    }
    return url.href;
}
function composerRefreshMap(item) {
    if (composerBatching) return;
    composerRenderGrid(item);
    const img = item.querySelector('img');
    const quality = JSON.parse(item.dataset.properties || '{}').MapPreviewDpi ?? 2;
    if (img && window.geonexMapServerUrl) img.src = composerMapUrl(item, quality);
}
// All distances below are projected metres, not ellipsoidal/geodesic ground distances.
window.composerMapAction = function(id, action, value = 0, sourceId = '') {
    const item = composerItems().find(i=>i.id===id && i.dataset.type==='Map');
    if (!item || item.dataset.locked==='true' || composerExporting || composerBatching) return false;
    if (action === 'refresh') { composerRefreshMap(item); return true; }
    if (!item.dataset.mapView) return false;
    let view = JSON.parse(item.dataset.mapView), rotation = Number(item.dataset.mapRotation || 0);
    if (!(view.metersPerUnit>0) || (printMapContext && view.token!==printMapContext.token)) return false;
    if (action === 'remember') { composerMapBookmarks.set(id,{view:{...view},rotation}); return true; }
    if (action === 'restore') {
        const saved=composerMapBookmarks.get(id);
        if (!saved || saved.view.token!==view.token) return false;
        view={...saved.view}; rotation=saved.rotation;
    } else if (['view','scale','extent'].includes(action)) {
        const source=composerItems().find(i=>i.id===sourceId && i.dataset.type==='Map');
        if (!source?.dataset.mapView || source===item) return false;
        const other=JSON.parse(source.dataset.mapView);
        if (other.token!==view.token || other.metersPerUnit!==view.metersPerUnit) return false;
        if (action === 'scale') view.scale=other.scale;
        else {
            view={...other}; rotation=Number(source.dataset.mapRotation || 0);
            if (action === 'extent') view.scale *= Math.max(parseFloat(source.style.width)/parseFloat(item.style.width),parseFloat(source.style.height)/parseFloat(item.style.height));
        }
    } else if (action === 'factor' && Number.isFinite(value) && value>=.1 && value<=10) view.scale *= value;
    else if (['width','height'].includes(action) && Number.isFinite(value) && value>0 && value<=1e9) {
        const pixels=parseFloat(item.style[action]);
        view.scale=value/(pixels*.0254/96);
    } else if (action === 'rotate' && Number.isFinite(value)) rotation += value;
    else if (action === 'north') rotation=0;
    else {
        const directions={n:[0,-1],s:[0,1],e:[1,0],w:[-1,0],ne:[1,-1],nw:[-1,-1],se:[1,1],sw:[-1,1]};
        const d=directions[action];
        if (!d || !Number.isFinite(value) || value<=0 || value>1e7) return false;
        const distance=value/view.metersPerUnit/Math.hypot(...d);
        view.centerX+=d[0]*distance; view.centerY+=d[1]*distance;
    }
    if (![view.centerX,view.centerY].every(n=>Number.isFinite(n)&&Math.abs(n)<=1e12) || !Number.isFinite(view.scale) || view.scale<1 || view.scale>1e9 || !Number.isFinite(rotation)) return false;
    composerBatching=true;
    try {
        item.dataset.mapView=JSON.stringify(view);
        window.composerUpdateItemProperty(id,'MapRotation',rotation);
        window.composerUpdateItemProperty(id,'MapScale',view.scale);
    } finally { composerBatching=false; }
    composerRefreshMap(item);
    composerCommit();
    return true;
};
window.composerUseProjectView = function(id, context) {
    printMapContext = context;
    const item = composerItems().find(i=>i.id===id);
    if (!item || item.dataset.locked==='true' || composerExporting) return;
    if (context.metersPerUnit > 0) item.dataset.mapView = JSON.stringify(context);
    else delete item.dataset.mapView;
    item.dataset.panX = item.dataset.panY = 0;
    composerRefreshMap(item);
    composerItems().filter(i=>i.dataset.type==='ScaleBar').forEach(composerRenderScaleBar);
    composerCommit();
};
window.composerConfigureGuides = function(gridMm, marginMm) {
    composerGridMm = Math.max(1, Math.min(50, Number(gridMm) || 5));
    composerMarginMm = Math.max(0, Math.min(50, Number(marginMm) || 0));
    composerPaper.style.setProperty('--composer-grid', composerGridMm + 'mm');
    composerPaper.style.setProperty('--composer-margin', composerMarginMm + 'mm');
};
window.composerUpdateGuides = function(gridMm, marginMm, snap) {
    window.composerConfigureGuides(gridMm,marginMm); window.composerSetGuides(snap); composerCommit();
};
function composerRenderScaleBar(item) {
    const content = item.querySelector('.item-content');
    if (!content) return;
    content.replaceChildren();
    const map = composerItems().find(i=>i.id===item.dataset.scaleBarMapId && i.dataset.mapView);
    const distance = Number(item.dataset.scaleBarMeters || 100);
    if (!map || !Number.isFinite(distance) || distance <= 0) {
        content.textContent = 'Vincule a um mapa com escala projetada.'; item.dataset.invalidScaleBar = 'true'; return;
    }
    const view = JSON.parse(map.dataset.mapView), width = distance * (96/.0254) / view.scale;
    item.dataset.invalidScaleBar = String(width > parseFloat(item.style.width) || parseFloat(item.style.height) < 35);
    const label = document.createElement('div');
    label.textContent = '0                          ' + (distance >= 1000 ? distance/1000 + ' km' : distance + ' m');
    label.style.cssText = 'font:11px Arial;white-space:pre;display:flex;justify-content:space-between';
    label.textContent = ''; const zero=document.createElement('span'), end=document.createElement('span');
    zero.textContent='0'; end.textContent=distance>=1000 ? distance/1000+' km' : distance+' m'; label.append(zero,end);
    const bar=document.createElement('div'); bar.style.cssText='display:flex;height:10px;border:1px solid #111;box-sizing:border-box';
    for(let i=0;i<4;i++){const part=document.createElement('span');part.style.cssText='flex:1;background:'+(i%2?'#fff':'#111');bar.appendChild(part);}
    const box=document.createElement('div'); box.style.width=width+'px'; box.append(label,bar); content.appendChild(box);
}
window.composerDeleteItem = function(id) {
    const item = composerItems().find(i => i.id === id);
    if (!item || composerExporting || item.dataset.locked === 'true') return;
    item.remove();
    if (activeItem === item) activeItem = null;
    composerCommit();
};
window.composerSelectItem = function(id) {
    const item = composerItems().find(i => i.id === id);
    if (!item || composerExporting) return;
    composerItems().forEach(i => i.classList.remove('selected'));
    activeItem = item;
    item.classList.add('selected');
    composerNotify();
};
window.composerCopyItem = function(id) {
    const item = composerItems().find(i => i.id === id);
    if (item) composerClipboard = item.cloneNode(true);
};
window.composerPasteItem = function() {
    if (!composerClipboard || composerExporting) return;
    const item = composerClipboard.cloneNode(true);
    item.id = 'item_' + [...crypto.getRandomValues(new Uint32Array(4))].map(n => n.toString(16)).join('');
    item.dataset.locked = 'false';
    item.style.left = (parseFloat(item.style.left) + composerGridMm * COMPOSER_PX_MM) + 'px';
    item.style.top = (parseFloat(item.style.top) + composerGridMm * COMPOSER_PX_MM) + 'px';
    item.addEventListener('mousedown', startDrag);
    item.querySelectorAll('.resize-handle').forEach(h => h.addEventListener('mousedown', startResize));
    composerPaper.appendChild(item);
    window.composerSelectItem(item.id);
    composerCommit();
};
window.composerAlignItem = function(direction) {
    if (!activeItem || composerExporting || activeItem.dataset.locked === 'true') return;
    const margin = composerMarginMm * COMPOSER_PX_MM;
    const w = parseFloat(composerPaper.style.width), h = parseFloat(composerPaper.style.height);
    const iw = parseFloat(activeItem.style.width), ih = parseFloat(activeItem.style.height);
    const positions = { left: ['left', margin], center: ['left', (w-iw)/2], right: ['left', w-iw-margin],
        top: ['top', margin], middle: ['top', (h-ih)/2], bottom: ['top', h-ih-margin] };
    if (!positions[direction]) return;
    const [prop, value] = positions[direction];
    activeItem.style[prop] = value + 'px';
    composerCommit();
};
window.renderLegendHTML = function(item, content) {
    // Layer names and legend titles are data, never executable HTML.
    content.replaceChildren();
    const p = JSON.parse(item.dataset.properties || '{}');
    const columns = p.LegendColumns ?? 1, gap = p.LegendColumnGap ?? 8;
    const title = document.createElement('strong');
    title.textContent = item.dataset.text || 'Legenda';
    title.style.display = p.LegendHideTitle ? 'none' : 'block'; title.style.marginBottom = '10px';
    title.style.fontSize = (p.LegendTitleSize ?? 14) + 'px';
    content.appendChild(title);
    const list = document.createElement('div');
    list.style.cssText = 'display:flex;flex-wrap:wrap;align-items:flex-start;width:100%';
    content.appendChild(list);
    for (const entry of JSON.parse(item.dataset.legendData || '[]')) {
        if (!entry.IsVisible) continue;
        const row = document.createElement('div'), swatch = document.createElement('span'), label = document.createElement('span');
        row.style.cssText = `display:flex;align-items:center;gap:8px;box-sizing:border-box;width:${100/columns}%;padding-right:${gap}px;margin-bottom:${p.LegendRowGap ?? 5}px;min-width:0`;
        swatch.style.cssText = `width:${p.LegendSymbolWidth ?? 15}px;height:${p.LegendSymbolHeight ?? 15}px;flex-shrink:0;border:1px solid`;
        swatch.style.backgroundColor = entry.Color; swatch.style.borderColor = entry.BorderColor;
        label.textContent = entry.Name; label.style.fontSize = (p.LegendFontSize ?? 12)+'px';
        label.style.overflowWrap = 'anywhere'; label.style.minWidth = '0';
        row.append(swatch, label); list.appendChild(row);
    }
};

// Explicit output budget prevents A0/300dpi from exhausting a notebook WebView.
window.composerExportGeometry = function(widthMm, heightMm, dpi) {
    if (![widthMm, heightMm, dpi].every(v => Number.isFinite(v) && v > 0) || ![96,150,300].includes(dpi))
        throw new Error('Dimensões ou resolução inválidas.');
    const width = Math.round(widthMm * dpi / 25.4), height = Math.round(heightMm * dpi / 25.4);
    if (width * height > 40000000 || Math.max(width, height) > 16384)
        throw new Error('Página muito grande para esta resolução. Selecione 150 ou 96 DPI (limite: 40 megapixels).');
    return { width, height, scale: dpi / 96, widthMm, heightMm };
};
const composerLibraries = new Map();
function composerLoadLibrary(name, url, available) {
    if (available()) return Promise.resolve();
    if (composerLibraries.has(name)) return composerLibraries.get(name);
    const promise = new Promise((resolve, reject) => {
        const script = document.createElement('script');
        const timer = setTimeout(() => fail(), 20000);
        const fail = () => { clearTimeout(timer); script.remove(); reject(new Error('Não foi possível carregar ' + name + '. Verifique a conexão.')); };
        script.src = url;
        script.onload = () => { clearTimeout(timer); available() ? resolve() : fail(); };
        script.onerror = fail;
        document.head.appendChild(script);
    }).catch(error => { composerLibraries.delete(name); throw error; });
    composerLibraries.set(name, promise);
    return promise;
}
function composerWaitImage(img) {
    if (img.complete) return img.naturalWidth > 0 ? Promise.resolve() : Promise.reject(new Error('Imagem do layout indisponível.'));
    return new Promise((resolve, reject) => {
        const finish = error => { clearTimeout(timer); img.removeEventListener('load', loaded); img.removeEventListener('error', failed); error ? reject(error) : resolve(); };
        const loaded = () => finish();
        const failed = () => finish(new Error('Falha ao carregar imagem do layout.'));
        const timer = setTimeout(() => finish(new Error('Tempo limite ao renderizar o mapa para impressão.')), 20000);
        img.addEventListener('load', loaded, { once: true }); img.addEventListener('error', failed, { once: true });
    });
}
async function composerExport(filename, dpi, pdf) {
    if (composerExporting) throw new Error('Já existe uma exportação em andamento.');
    const paper = composerPaper;
    if (!paper || !composerItems().some(i => i.style.display !== 'none')) throw new Error('Adicione itens visíveis antes de exportar.');
    const geometry = window.composerExportGeometry(Number(paper.dataset.widthMm), Number(paper.dataset.heightMm), dpi);
    const auditError = window.composerAudit?.(dpi).find(issue => issue.severity === 'error');
    if (auditError) throw new Error(auditError.message);
    // Do not silently ship effects html2canvas cannot reproduce.
    if (composerItems().some(i => i.style.display !== 'none' && i.style.mixBlendMode && i.style.mixBlendMode !== 'normal'))
        throw new Error('Use o modo de mesclagem Normal para exportar com fidelidade.');
    const pageW = parseFloat(paper.style.width), pageH = parseFloat(paper.style.height);
    let mapPixels = 0;
    for (const item of composerItems().filter(i => i.style.display !== 'none')) {
        if (item.dataset.type === 'ScaleBar') {
            composerRenderScaleBar(item);
            if (item.dataset.invalidScaleBar === 'true') throw new Error('Barra de escala inválida: verifique vínculo, distância e tamanho do quadro.');
        }
        const m = composerModel(item), angle = (m.Rotation || 0) * Math.PI / 180;
        if (m.Type === 'Image' && m.SyncWithMap && !composerItems().some(i => i.id === m.LinkedMapId && i.dataset.type === 'Map'))
            throw new Error('A seta de norte está vinculada a um mapa removido. Escolha outro mapa ou desative o vínculo.');
        const extentX = (Math.abs(m.Width * Math.cos(angle)) + Math.abs(m.Height * Math.sin(angle))) / 2;
        const extentY = (Math.abs(m.Width * Math.sin(angle)) + Math.abs(m.Height * Math.cos(angle))) / 2;
        if (m.X + m.Width/2 - extentX < -.5 || m.Y + m.Height/2 - extentY < -.5 ||
            m.X + m.Width/2 + extentX > pageW + .5 || m.Y + m.Height/2 + extentY > pageH + .5)
            throw new Error('Há itens fora da página. Reposicione ou redimensione antes de exportar.');
        const content = item.querySelector('.item-content');
        if (['Text','Legend'].includes(m.Type) && (content.scrollHeight > content.clientHeight + 1 || content.scrollWidth > content.clientWidth + 1))
            throw new Error('Texto ou legenda excede o quadro. Aumente o tamanho do item.');
        if (m.Type === 'Map') mapPixels += m.Width * m.Height * geometry.scale ** 2;
    }
    if (mapPixels > 40000000) throw new Error('Os quadros de mapa excedem o orçamento de memória. Reduza o DPI ou a quantidade de mapas.');
    composerExporting = true;
    let host, canvas;
    try {
        await composerLoadLibrary('html2canvas', 'https://cdnjs.cloudflare.com/ajax/libs/html2canvas/1.4.1/html2canvas.min.js', () => typeof window.html2canvas === 'function');
        if (pdf) await composerLoadLibrary('jsPDF', 'https://cdnjs.cloudflare.com/ajax/libs/jspdf/2.5.1/jspdf.umd.min.js', () => !!window.jspdf?.jsPDF);
        await document.fonts.ready;
        // Render an isolated full-size page: workspace zoom, selection, scroll and previews stay untouched.
        host = document.createElement('div');
        host.className = 'composer-export-surface';
        host.style.cssText = 'position:fixed;left:-30000px;top:0;pointer-events:none;';
        const clone = paper.cloneNode(true);
        clone.removeAttribute('id'); clone.classList.remove('show-guides');
        clone.style.cssText = paper.style.cssText;
        clone.style.transform = 'none'; clone.style.transition = 'none'; clone.style.margin = '0';
        clone.style.boxShadow = 'none'; clone.style.overflow = 'hidden';
        const inherited = getComputedStyle(paper);
        for (const property of ['fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'color', 'direction'])
            clone.style[property] = inherited[property];
        clone.querySelectorAll('.resize-handle').forEach(h => h.remove());
        clone.querySelectorAll('.composer-item').forEach(i => { i.removeAttribute('id'); i.classList.remove('selected'); });
        host.appendChild(clone); document.body.appendChild(host);
        const images = [...clone.querySelectorAll('.composer-item:not([style*="display: none"]) img')];
        for (const img of images) {
            if (img.closest('[data-type="Map"]') && img.src.includes('dpi=')) {
                const url = new URL(img.src); url.searchParams.set('dpi', String(geometry.scale));
                url.searchParams.set('t', String(Date.now())); img.crossOrigin = 'anonymous'; img.src = url.href;
            }
        }
        await Promise.all(images.map(composerWaitImage));
        canvas = document.createElement('canvas'); canvas.width = geometry.width; canvas.height = geometry.height;
        await window.html2canvas(clone, { canvas, scale: geometry.scale, width: parseFloat(paper.style.width),
            height: parseFloat(paper.style.height), useCORS: true, backgroundColor: '#ffffff', logging: false,
            windowWidth: Math.ceil(parseFloat(paper.style.width)) + 100,
            windowHeight: Math.ceil(parseFloat(paper.style.height)) + 100 });
        if (pdf) {
            const doc = new window.jspdf.jsPDF({ orientation: geometry.widthMm > geometry.heightMm ? 'landscape' : 'portrait',
                unit: 'mm', format: [geometry.widthMm, geometry.heightMm], compress: true });
            doc.setProperties({ title: 'GeoNex — composição cartográfica', creator: 'GeoNex' });
            doc.addImage(canvas, 'PNG', 0, 0, geometry.widthMm, geometry.heightMm);
            await doc.save(filename, { returnPromise: true });
        } else {
            const blob = await new Promise((resolve, reject) => canvas.toBlob(b => b ? resolve(b) : reject(new Error('Falha ao codificar PNG.')), 'image/png'));
            const url = URL.createObjectURL(await composerPngDpi(blob, dpi)), link = document.createElement('a');
            link.href = url; link.download = filename; link.click();
            setTimeout(() => URL.revokeObjectURL(url), 30000);
        }
    } finally {
        host?.remove();
        if (canvas) { canvas.width = 0; canvas.height = 0; }
        composerExporting = false;
    }
}
window.composerExportPNG = (filename, dpi = 300) => composerExport(filename, dpi, false);
window.composerExportPDF = (filename, dpi = 300) => composerExport(filename, dpi, true);

// Geometry operations preserve map camera/scale; only the paper frame changes.
window.composerLegendAction = function(id, action, index = -1) {
    const item = composerItems().find(i=>i.id===id);
    if (!item || item.dataset.type !== 'Legend' || item.dataset.locked === 'true' || composerExporting) return false;
    const rows = JSON.parse(item.dataset.legendData || '[]');
    if (action === 'show' || action === 'hide') rows.forEach(r=>r.IsVisible=action==='show');
    else if (action === 'sort') rows.sort((a,b)=>a.Name.localeCompare(b.Name,'pt-BR',{numeric:true}));
    else if (action === 'reverse') rows.reverse();
    else if (['up','down'].includes(action)) {
        const to = index + (action==='up' ? -1 : 1);
        if (!Number.isInteger(index) || index<0 || index>=rows.length || to<0 || to>=rows.length) return false;
        [rows[index],rows[to]]=[rows[to],rows[index]];
    } else return false;
    window.composerUpdateItemProperty(id,'LegendItems',JSON.stringify(rows));
    return true;
};
// Repetition copies only layout elements, never project layers. One undo removes the complete array.
window.composerRepeatItem = function(id, rows, columns, gapXmm, gapYmm) {
    const item = composerItems().find(i=>i.id===id);
    if (!item || item.dataset.locked==='true' || composerExporting || composerBatching) return false;
    if (![rows,columns].every(n=>Number.isInteger(n)&&n>=1&&n<=5) || rows*columns<2 || composerItems().length+rows*columns-1>200) return false;
    if (![gapXmm,gapYmm].every(n=>Number.isFinite(n)&&n>=0&&n<=100)) return false;
    const m=composerModel(item), dx=m.Width+gapXmm*COMPOSER_PX_MM, dy=m.Height+gapYmm*COMPOSER_PX_MM;
    if (m.X+(columns-1)*dx>2000*COMPOSER_PX_MM || m.Y+(rows-1)*dy>2000*COMPOSER_PX_MM) return false;
    for (let r=0;r<rows;r++) for(let c=0;c<columns;c++) {
        if (!r&&!c) continue;
        const clone=item.cloneNode(true);
        clone.id='item_'+[...crypto.getRandomValues(new Uint32Array(4))].map(n=>n.toString(16)).join('');
        clone.classList.remove('selected');
        clone.style.left=(m.X+c*dx)+'px'; clone.style.top=(m.Y+r*dy)+'px';
        const p=JSON.parse(clone.dataset.properties||'{}');
        p.Name=((m.Name||m.TextContent||m.Type).slice(0,100)+` [${r+1},${c+1}]`); clone.dataset.properties=JSON.stringify(p);
        clone.addEventListener('mousedown',startDrag);
        clone.querySelectorAll('.resize-handle').forEach(h=>h.addEventListener('mousedown',startResize));
        composerPaper.appendChild(clone);
    }
    composerCommit();
    return true;
};
window.composerResizeFrame = function(id, mode, percent = 100) {
    const item = composerItems().find(i => i.id === id);
    if (!item || composerExporting || composerBatching || item.dataset.locked === 'true') return false;
    const model = composerModel(item), margin = composerMarginMm * COMPOSER_PX_MM;
    const availableW = parseFloat(composerPaper.style.width) - 2 * margin;
    const availableH = parseFloat(composerPaper.style.height) - 2 * margin;
    let factor;
    if (mode === 'fit') factor = Math.min(availableW / model.Width, availableH / model.Height);
    else if (mode === 'width') factor = availableW / model.Width;
    else if (mode === 'height') factor = availableH / model.Height;
    else if (mode === 'percent' && Number.isFinite(percent) && percent >= 10 && percent <= 400) factor = percent / 100;
    else return false;
    const width = model.Width * factor, height = model.Height * factor;
    if (![width, height].every(v => Number.isFinite(v) && v >= 1 && v <= 10000)) return false;
    // Page fitting is unrotated so the complete frame remains inside the margins.
    const x = mode === 'percent' ? model.X + (model.Width - width) / 2 : margin + (availableW - width) / 2;
    const y = mode === 'percent' ? model.Y + (model.Height - height) / 2 : margin + (availableH - height) / 2;
    composerBatching = true;
    try {
        for (const [key, value] of Object.entries({X:x,Y:y,Width:width,Height:height,...(mode === 'percent' ? {} : {Rotation:0})}))
            window.composerUpdateItemProperty(id,key,value);
    } finally { composerBatching = false; }
    if (item.dataset.type === 'Map') composerRefreshMap(item);
    composerCommit();
    return true;
};
window.composerCopyAppearance = function(id) {
    const item = composerItems().find(i => i.id === id);
    if (!item || composerExporting) return false;
    composerAppearanceClipboard = {
        HasBg:item.dataset.hasBg === 'true', BgColor:item.dataset.bgColor || '#ffffff',
        HasBorder:item.dataset.hasBorder === 'true', BorderColor:item.dataset.borderColor || '#000000',
        BorderWidth:Number(item.dataset.borderWidth || 1), BorderStyle:item.dataset.borderStyle || 'solid',
        Opacity:Number(item.style.opacity || 1), BlendMode:'normal'
    };
    return true;
};
window.composerPasteAppearance = function(id) {
    const item = composerItems().find(i => i.id === id);
    if (!item || !composerAppearanceClipboard || composerExporting || composerBatching || item.dataset.locked === 'true') return false;
    composerBatching = true;
    try {
        for (const [key,value] of Object.entries(composerAppearanceClipboard)) window.composerUpdateItemProperty(id,key,value);
    } finally { composerBatching = false; }
    composerCommit();
    return true;
};
window.composerApplyAppearance = function(id, style) {
    const item=composerItems().find(i=>i.id===id);
    if(!item || item.dataset.locked==='true' || composerExporting || composerBatching) return;
    const presets={clean:{HasBg:false,HasBorder:false},paper:{HasBg:true,BgColor:'#ffffff',HasBorder:true,BorderColor:'#334155',BorderWidth:0.25*COMPOSER_PX_MM},accent:{HasBg:true,BgColor:'#e8f5fa',HasBorder:true,BorderColor:'#147d9e',BorderWidth:0.5*COMPOSER_PX_MM}};
    if(!presets[style]) return;
    composerBatching=true;
    try { for(const [key,value] of Object.entries({Opacity:1,BlendMode:'normal',BorderStyle:'solid',...presets[style]})) window.composerUpdateItemProperty(id,key,value); }
    finally { composerBatching=false; }
    composerCommit();
};
// PNG canvas encoders default to 96dpi; replace pHYs so print applications honor the selected resolution.
async function composerPngDpi(blob, dpi) {
    const bytes = new Uint8Array(await blob.arrayBuffer()), view = new DataView(bytes.buffer);
    const chunk = new Uint8Array(21), data = new DataView(chunk.buffer);
    data.setUint32(0, 9); chunk.set([112,72,89,115], 4); // pHYs
    data.setUint32(8, Math.round(dpi / 0.0254)); data.setUint32(12, Math.round(dpi / 0.0254)); chunk[16] = 1;
    let crc = 0xffffffff;
    for (let i = 4; i < 17; i++) {
        crc ^= chunk[i];
        for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
    }
    data.setUint32(17, (crc ^ 0xffffffff) >>> 0);
    const parts = [bytes.slice(0, 8)]; let inserted = false;
    for (let offset = 8; offset + 12 <= bytes.length;) {
        const length = view.getUint32(offset) + 12;
        if (offset + length > bytes.length) throw new Error('PNG inválido.');
        const type = String.fromCharCode(...bytes.slice(offset + 4, offset + 8));
        if (!inserted && type === 'IDAT') { parts.push(chunk); inserted = true; }
        if (type !== 'pHYs') parts.push(bytes.slice(offset, offset + length));
        offset += length;
    }
    if (!inserted) throw new Error('PNG sem dados de imagem.');
    return new Blob(parts, { type: 'image/png' });
}
