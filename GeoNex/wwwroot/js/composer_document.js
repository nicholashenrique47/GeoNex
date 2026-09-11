// Loaded as an ES module after composer_v2.js. Files contain validated data, never HTML or scripts.
const layoutTypes = new Set(['Map','Text','Legend','Image','ScaleBar','Rectangle','Ellipse','Picture']);
const numericProps = { Rotation:[-360,360], ZIndex:[-100000,100000], BorderWidth:[0,100],
    FontSize:[1,500], Padding:[0,200], Opacity:[0,1], MapPreviewDpi:[.5,2], ...composerAdvancedNumbers, ...composerGridNumbers };
const booleanProps = ['HasBg','HasBorder','SyncWithMap','LegendHideTitle',...composerGridBooleans];
const colorProps = ['BgColor','BorderColor','TextColor','SvgFill','SvgStroke','GridColor'];
const enumProps = { GridMode:['lines','cross','frame'], TextDecoration:['none','underline','line-through'], TextTransform:['none','uppercase','lowercase','capitalize'], BorderStyle:['solid','dashed','dotted'], FontFamily:['Arial','Inter','Times New Roman'], FontWeight:['normal','bold'],
    FontStyle:['normal','italic'], TextAlignH:['left','center','right'],TextAlignV:['top','center','bottom'],
    SvgStyle:['Estilo 1','Estilo 2','Estilo 3','Estilo 4','Estilo 5'],
    BlendMode:['normal','multiply','screen','overlay','darken','lighten','color-dodge','color-burn','hard-light','soft-light','difference','exclusion','hue','saturation','color','luminosity'] };
Object.assign(enumProps,composerGridEnums);
colorProps.push('GridLabelColor');
const persistProps = [...Object.keys(numericProps),...booleanProps,...colorProps,...Object.keys(enumProps),'LinkedMapId','Name'];
const numberIn = (value,min,max) => typeof value==='number' && Number.isFinite(value) && value>=min && value<=max;
const validColor = value => typeof value==='string' && value.length<=80 && CSS.supports('color',value) && !/var\(|url\(|expression/i.test(value);
const validate = (condition,message) => { if(!condition) throw new Error('Layout inválido: '+message); };

window.composerSerializeLayout = function() {
    return JSON.stringify({ format:'geonex-layout',version:1,
        page:{widthMm:Number(composerPaper.dataset.widthMm),heightMm:Number(composerPaper.dataset.heightMm),gridMm:composerGridMm,marginMm:composerMarginMm,snap:composerSnap},
        items:composerItems().map(item=>{
            const m=composerModel(item), properties={};
            for(const name of persistProps) if(m[name]!==undefined) properties[name]=m[name];
            return {id:m.Id,type:m.Type,text:m.TextContent,xMm:m.X/COMPOSER_PX_MM,yMm:m.Y/COMPOSER_PX_MM,
                widthMm:m.Width/COMPOSER_PX_MM,heightMm:m.Height/COMPOSER_PX_MM,locked:m.Locked,visible:m.Visible,
                properties,picture:m.Type==='Picture'?m.PictureSource:undefined,grids:m.Type==='Map'?m.Grids:undefined,legend:m.LegendItems,view:item.dataset.mapView?JSON.parse(item.dataset.mapView):null,
                panX:Number(item.dataset.panX||0),panY:Number(item.dataset.panY||0),mapRotation:Number(item.dataset.mapRotation||0),
                scaleBarMapId:m.ScaleBarMapId,scaleBarMeters:m.ScaleBarMeters};
        }) },null,2);
};
window.composerValidateLayout = function(text) {
    validate(typeof text==='string' && text.length<=20000000 && new TextEncoder().encode(text).byteLength<=20000000,'limite de 20 MB.');
    const doc=JSON.parse(text);
    validate(doc?.format==='geonex-layout' && doc.version===1,'formato/versão não suportado.');
    const p=doc.page;
    validate(p && [[210,297],[297,420],[420,594],[594,841],[841,1189]].some(([a,b])=>(p.widthMm===a&&p.heightMm===b)||(p.widthMm===b&&p.heightMm===a)),'tamanho de página não suportado.');
    validate(numberIn(p.gridMm,1,50)&&numberIn(p.marginMm,0,50)&&typeof p.snap==='boolean','guias.');
    validate(Array.isArray(doc.items)&&doc.items.length<=200,'máximo de 200 itens.');
    const ids=new Set();
    for(const item of doc.items) {
        validate(item && layoutTypes.has(item.type)&&typeof item.id==='string'&&/^item_[a-zA-Z0-9_]+$/.test(item.id)&&item.id.length<=100&&!ids.has(item.id),'tipo ou ID duplicado.');
        ids.add(item.id);
        if(item.type==='Picture')validate(typeof item.picture==='string'&&item.picture.length<=16000050&&/^data:image\/(png|jpeg);base64,[A-Za-z0-9+/=]+$/.test(item.picture),'referência PNG/JPEG incorporada');
        else validate(item.picture===undefined,'imagem em elemento incompatível');
        if(item.grids!==undefined){
            validate(item.type==='Map'&&Array.isArray(item.grids)&&item.grids.length<=8,'máximo de 8 grades por mapa');
            const gridIds=new Set();
            for(const grid of item.grids){
                validate(grid&&typeof grid==='object'&&!Array.isArray(grid)&&composerGridFieldValid('Id',grid.Id)&&composerGridFieldValid('Name',grid.Name)&&!gridIds.has(grid.Id),'identificação da grade');
                gridIds.add(grid.Id);
                for(const [key,value] of Object.entries(grid))validate(composerGridFieldValid(key,value),'configuração da grade: '+key);
            }
        }
        validate(typeof item.text==='string'&&item.text.length<=20000,'texto muito grande.');
        validate(numberIn(item.xMm,-2000,2000)&&numberIn(item.yMm,-2000,2000)&&numberIn(item.widthMm,.01,1189)&&numberIn(item.heightMm,.01,1189),'posição/tamanho.');
        validate(typeof item.locked==='boolean'&&typeof item.visible==='boolean','visibilidade/bloqueio.');
        validate(item.properties&&typeof item.properties==='object'&&!Array.isArray(item.properties),'propriedades.');
        for(const [name,value] of Object.entries(item.properties)) {
            if(name==='LegendColumns') validate(Number.isInteger(value),'colunas inteiras');
            if(name==='GridPrecision') validate(Number.isInteger(value),'precisão inteira');
            if(name==='MapPreviewDpi') validate([.5,1,2].includes(value),'qualidade de prévia');
            validate(persistProps.includes(name),'propriedade desconhecida: '+name);
            if(numericProps[name]) validate(numberIn(value,...numericProps[name]),name);
            else if(booleanProps.includes(name)) validate(typeof value==='boolean',name);
            else if(colorProps.includes(name)) validate(validColor(value),name);
            else if(enumProps[name]) validate(enumProps[name].includes(value),name);
            else if(name==='Name') validate(typeof value==='string'&&value.length<=120,name);
            else validate(value===null||typeof value==='string',name);
        }
        validate(numberIn(item.panX,-1e9,1e9)&&numberIn(item.panY,-1e9,1e9)&&numberIn(item.mapRotation,-360,360),'câmera.');
        validate(numberIn(item.scaleBarMeters,.001,1e9),'distância da barra.');
        validate(Array.isArray(item.legend)&&item.legend.length<=500,'legenda.');
        for(const row of item.legend) validate(row&&typeof row.Name==='string'&&row.Name.length<=2000&&validColor(row.Color)&&validColor(row.BorderColor)&&typeof row.IsVisible==='boolean','símbolo de legenda.');
        if(item.view!==null) {
            const v=item.view;
            if(v.originX!=null || v.originY!=null) {
                validate(numberIn(v.originX,-1e12,1e12)&&numberIn(v.originY,-1e12,1e12),'origem da grade');
                if(v.token===printMapContext?.token && Number.isFinite(printMapContext.originX))
                    validate(v.originX===printMapContext.originX&&v.originY===printMapContext.originY,'origem da grade divergente');
            }
            validate(item.type==='Map'&&v&&numberIn(v.scale,1,1e9)&&numberIn(v.metersPerUnit,1e-9,1e9)&&numberIn(v.centerX,-1e12,1e12)&&numberIn(v.centerY,-1e12,1e12)&&typeof v.token==='string'&&/^[a-zA-Z0-9_-]{1,128}$/.test(v.token)&&typeof v.crsName==='string'&&v.crsName.length<=1000,'enquadramento do mapa.');
            if(v.token===printMapContext?.token) validate(Math.abs(v.metersPerUnit-printMapContext.metersPerUnit)<1e-12,'unidade do SRC divergente.');
        }
    }
    for(const item of doc.items) for(const link of [item.properties.LinkedMapId,item.scaleBarMapId])
        validate(link==null||link===''||doc.items.some(m=>m.id===link&&m.type==='Map'),'vínculo sem mapa.');
    return doc;
};
function downloadText(text,name) {
    const url=URL.createObjectURL(new Blob([text],{type:'application/json'})),a=document.createElement('a');
    a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),30000);
}
function beforeTransaction() {
    return {html:composerPaper.innerHTML,selected:activeItem?.id,
        page:{widthMm:Number(composerPaper.dataset.widthMm),heightMm:Number(composerPaper.dataset.heightMm),gridMm:composerGridMm,marginMm:composerMarginMm,snap:composerSnap}};
}
function rollbackTransaction(before) {
    composerPaper.innerHTML=before.html;
    composerItems().forEach(item=>{item.addEventListener('mousedown',startDrag);item.querySelectorAll('.resize-handle').forEach(h=>h.addEventListener('mousedown',startResize));});
    activeItem=composerItems().find(i=>i.id===before.selected)||null;
    composerRestorePage(before.page);
}
window.composerSaveLayout = function() {
    const text=window.composerSerializeLayout();window.composerValidateLayout(text);
    downloadText(text,'composicao.geonex-layout.json');
};
window.composerImportLayout = async function(text) {
    if(composerExporting) throw new Error('Aguarde a exportação.');
    const doc=window.composerValidateLayout(text); // Validate fully before altering the current document.
    let totalPixels=0;
    for(const record of doc.items.filter(i=>i.type==='Picture')){
        const image=new Image();image.src=record.picture;await composerWaitImage(image);
        totalPixels+=image.naturalWidth*image.naturalHeight;
        validate(image.naturalWidth>0&&image.naturalHeight>0&&totalPixels<=32000000,'referências excedem 32 megapixels');
    }
    if(composerItems().length && !window.confirm('Substituir a composição atual? Você poderá desfazer esta operação.')) return false;
    const before=beforeTransaction();
    composerBatching=true;
    try {
        composerPaper.replaceChildren();activeItem=null;
        composerRestorePage(doc.page);
        for(const record of doc.items) {
            const id=window.composerAddItemV3(record.type,record.text,record.xMm*COMPOSER_PX_MM,record.yMm*COMPOSER_PX_MM,record.widthMm*COMPOSER_PX_MM,record.heightMm*COMPOSER_PX_MM);
            const item=document.getElementById(id);item.id=record.id;
            // Restore exact coordinates; additions normally constrain the initial position to the page.
            for(const [name,value] of Object.entries({X:record.xMm*COMPOSER_PX_MM,Y:record.yMm*COMPOSER_PX_MM,Width:record.widthMm*COMPOSER_PX_MM,Height:record.heightMm*COMPOSER_PX_MM,...record.properties}))
                window.composerUpdateItemProperty(item.id,name,value);
            if(record.view) item.dataset.mapView=JSON.stringify(record.view);else delete item.dataset.mapView;
            if(record.type==='Picture')window.composerUpdateItemProperty(item.id,'PictureSource',record.picture);
            if(record.grids!==undefined)item.dataset.grids=JSON.stringify(record.grids);
            item.dataset.panX=record.panX;item.dataset.panY=record.panY;item.dataset.mapRotation=record.mapRotation;
            const props=JSON.parse(item.dataset.properties||'{}');props.MapRotation=record.mapRotation;item.dataset.properties=JSON.stringify(props);
            item.dataset.scaleBarMapId=record.scaleBarMapId||'';item.dataset.scaleBarMeters=record.scaleBarMeters;
            if(record.type==='Legend') window.composerUpdateItemProperty(item.id,'LegendItems',JSON.stringify(record.legend));
            window.composerUpdateItemProperty(item.id,'Visible',record.visible);
            window.composerUpdateItemProperty(item.id,'Locked',record.locked);
        }
        activeItem=null;composerItems().forEach(i=>i.classList.remove('selected'));
    } catch(error) { rollbackTransaction(before); throw error; }
    finally { composerBatching=false; composerNotify(); }
    composerItems().filter(i=>i.dataset.type==='Map').forEach(composerRefreshMap);
    composerItems().filter(i=>i.dataset.type==='ScaleBar').forEach(composerRenderScaleBar);
    composerCommit();
    return true;
};

window.composerApplyTemplate = function(style,legend) {
    if(composerExporting) return false;
    if(!['technical','wide'].includes(style)) throw new Error('Modelo não suportado.');
    if(composerItems().length&&!window.confirm('Substituir pelos itens do modelo? A composição atual poderá ser recuperada com Desfazer.')) return false;
    const width=Number(composerPaper.dataset.widthMm),height=Number(composerPaper.dataset.heightMm);
    const before=beforeTransaction();
    const add=(type,text,x,y,w,h)=>window.composerAddItemV3(type,text,x*COMPOSER_PX_MM,y*COMPOSER_PX_MM,w*COMPOSER_PX_MM,h*COMPOSER_PX_MM);
    composerBatching=true;
    let map;
    try {
        composerPaper.replaceChildren();activeItem=null;
        const title=add('Text','TÍTULO DO MAPA',10,10,width-20,14);
        window.composerUpdateItemProperty(title,'FontSize',24);window.composerUpdateItemProperty(title,'FontWeight','bold');
        const mapWidth=style==='wide'?width-20:width-83, mapHeight=height-65;
        map=add('Map','Mapa principal',10,30,mapWidth,mapHeight);
        window.composerUpdateItemProperty(map,'HasBorder',true);
        const lx=width-68,ly=style==='wide'?34:30;
        const legendId=add('Legend','Legenda',lx,ly,54,Math.min(65,mapHeight-20));
        window.composerUpdateItemProperty(legendId,'LegendItems',JSON.stringify(legend));
        window.composerUpdateItemProperty(legendId,'BgColor','#ffffff');window.composerUpdateItemProperty(legendId,'HasBg',true);
        const north=add('Image','Norte de quadrícula',width-32,height-60,12,18);
        window.composerLinkNorth(north,map,true);
        if(printMapContext?.metersPerUnit>0) {
            const bar=add('ScaleBar','Escala gráfica',10,height-27,60,12);
            const target=printMapContext.scale*.04;
            const power=10**Math.floor(Math.log10(target));
            const nice=[1,2,5,10].filter(n=>n*power<=target).at(-1)*power;
            window.composerUpdateItemProperty(bar,'ScaleBarMapId',map);window.composerUpdateItemProperty(bar,'ScaleBarMeters',Math.max(.001,nice));
        }
        const credits=add('Text','Fonte: [informar] | Autor: [informar] | Data: [informar] | SRC: '+(printMapContext?.crsName||'[informar]'),10,height-12,width-20,8);
        window.composerUpdateItemProperty(credits,'FontSize',10);
    } catch(error) { rollbackTransaction(before); throw error; }
    finally { composerBatching=false; composerNotify(); }
    composerItems().filter(i=>i.dataset.type==='Map').forEach(composerRefreshMap);
    window.composerSelectItem(map);composerCommit();return true;
};

window.composerAudit = function(dpi=300) {
    const issues=[],items=composerItems().filter(i=>i.style.display!=='none');
    const add=(severity,message,item=null)=>issues.push({severity,message,id:item?.id||null});
    try { window.composerExportGeometry(Number(composerPaper.dataset.widthMm),Number(composerPaper.dataset.heightMm),dpi); }
    catch(e) { add('error',e.message); }
    if(!items.length) add('error','A página está vazia.');
    const pageW=parseFloat(composerPaper.style.width),pageH=parseFloat(composerPaper.style.height);
    for(const item of items) {
        const m=composerModel(item),angle=(m.Rotation||0)*Math.PI/180;
        const ex=(Math.abs(m.Width*Math.cos(angle))+Math.abs(m.Height*Math.sin(angle)))/2;
        const ey=(Math.abs(m.Width*Math.sin(angle))+Math.abs(m.Height*Math.cos(angle)))/2;
        if(m.X+m.Width/2-ex<-.5||m.Y+m.Height/2-ey<-.5||m.X+m.Width/2+ex>pageW+.5||m.Y+m.Height/2+ey>pageH+.5) add('error','Item fora da página.',item);
        const c=item.querySelector('.item-content');
        if(['Text','Legend'].includes(m.Type)&&(c.scrollHeight>c.clientHeight+1||c.scrollWidth>c.clientWidth+1)) add('error','Conteúdo excede o quadro.',item);
        if(m.Type==='Map') {
            if(m.Grids.some(g=>g.GridEnabled)){
                composerRenderGrid(item);if(item.dataset.gridWarning)add('error',item.dataset.gridWarning,item);
                const cos=Math.cos(angle),sin=Math.sin(angle);
                for(const b of JSON.parse(item.dataset.gridLabelBounds||'[]')){
                    const dx=b.x+b.w/2-m.Width/2,dy=b.y+b.h/2-m.Height/2;
                    const x=m.X+m.Width/2+cos*dx-sin*dy,y=m.Y+m.Height/2+sin*dx+cos*dy;
                    const ex=(Math.abs(cos*b.w)+Math.abs(sin*b.h))/2,ey=(Math.abs(sin*b.w)+Math.abs(cos*b.h))/2;
                    if(x-ex<0||y-ey<0||x+ex>pageW||y+ey>pageH){add('error','Coordenadas da grade fora da página. Afaste o mapa da borda ou use rótulos internos.',item);break;}
                }
            }
            if(!m.ScaleAvailable) add('warning','Mapa sem escala projetada. Use um SRC projetado para escala 1:N.',item);
            else if(JSON.parse(item.dataset.mapView).token!==printMapContext?.token) add('error','O projeto/SRC mudou. Atualize a vista do mapa.',item);
            const image=item.querySelector('img');if(!image?.complete||!image.naturalWidth) add('warning','A imagem do mapa ainda não está disponível.',item);
        }
        if(m.Type==='ScaleBar') {composerRenderScaleBar(item);if(item.dataset.invalidScaleBar==='true') add('error','Barra de escala sem vínculo ou maior que o quadro.',item);}
        if(m.Type==='Text' && /\[informar\]|TÍTULO DO MAPA|Título Principal/.test(m.TextContent)) add('warning','Preencha o texto de exemplo antes de publicar.',item);
        if(m.Type==='Text' && Number(m.FontSize||14)<8) add('warning','Texto menor que 6 pt pode ficar ilegível na impressão.',item);
        if(m.BlendMode&&m.BlendMode!=='normal') add('error','Use mesclagem Normal para exportar com fidelidade.',item);
    }
    if(items.some(i=>i.dataset.type==='Map')&&!items.some(i=>i.dataset.type==='Legend')) add('warning','A composição não contém legenda.');
    if(!items.some(i=>i.dataset.type==='Text'&&/fonte|source|©/i.test(i.dataset.text||''))) add('warning','Confira fonte dos dados, autoria e atribuição do mapa base.');
    return issues;
};
