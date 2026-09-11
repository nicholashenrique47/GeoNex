// Local template interchange. Imported HTML is parsed in an inert template, never mounted/executed.
function exchangeRecord(type,text,x,y,w,h) {
    return {id:'item_'+[...crypto.getRandomValues(new Uint32Array(4))].map(n=>n.toString(16)).join(''),type,text,
        xMm:x,yMm:y,widthMm:w,heightMm:h,locked:false,visible:true,properties:{},legend:[],view:null,
        panX:0,panY:0,mapRotation:0,scaleBarMapId:null,scaleBarMeters:100};
}
function exchangeDocument(){const current=JSON.parse(composerSerializeLayout());return {format:'geonex-layout',version:1,page:current.page,items:[]};}
window.composerReferenceLayout = function(reference,name) {
    if(!reference||![reference.width,reference.height].every(v=>Number.isFinite(v)&&v>0)||!Array.isArray(reference.texts)||reference.texts.length>150)throw new Error('Referência inválida.');
    const doc=exchangeDocument(),p=doc.page,scale=Math.min((p.widthMm-20)/reference.width,(p.heightMm-20)/reference.height);
    const w=reference.width*scale,h=reference.height*scale,x=(p.widthMm-w)/2,y=(p.heightMm-h)/2;
    const picture=exchangeRecord('Picture',String(name).slice(0,120),x,y,w,h);
    picture.picture=reference.dataUrl;picture.locked=true;picture.visible=reference.texts.length===0;picture.properties={Name:'Original · '+String(name).slice(0,90),ZIndex:-100};doc.items.push(picture);
    for(const box of reference.texts){
        if(typeof box.text!=='string'||![box.x,box.y,box.width,box.height].every(Number.isFinite)||box.x<0||box.y<0||box.width<=0||box.height<=0||box.x+box.width>reference.width+1||box.y+box.height>reference.height+1)throw new Error('Caixa OCR inválida.');
        const item=exchangeRecord('Text',box.text,x+box.x*scale,y+box.y*scale,Math.max(.1,box.width*scale),Math.max(.1,box.height*scale*1.2));
        item.properties={Name:'OCR · '+box.text.slice(0,100),FontSize:Math.max(1,Math.min(500,box.height*scale*96/25.4*.85)),FontFamily:'Arial',LineHeight:1.1};doc.items.push(item);
    }
    const json=JSON.stringify(doc);composerValidateLayout(json);return json;
};
window.composerParseHTML = function(html) {
    if(typeof html!=='string'||new TextEncoder().encode(html).length>40000000)throw new Error('HTML excede 40 MB.');
    const template=document.createElement('template');template.innerHTML=html;
    const root=template.content,payload=root.querySelector('script#geonex-layout[type="application/json"]');
    if(payload){composerValidateLayout(payload.textContent);return {json:payload.textContent,warning:'Modelo GeoNex recuperado. Mapas continuam vinculados ao projeto atual.'};}
    const doc=exchangeDocument();let skipped=0;
    const mm=(text)=>{const m=/^(-?\d+(?:\.\d+)?)(mm|px|pt)?$/.exec(text.trim());return m?Number(m[1])*(m[2]==='mm'?1:m[2]==='pt'?25.4/72:25.4/96):NaN;};
    for(const node of root.querySelectorAll('[style]')){
        if(!['DIV','P','SPAN','H1','H2','H3','IMG'].includes(node.tagName)||node.style.position!=='absolute')continue;
        // Nested absolute layouts need a CSS layout engine; do not silently guess their coordinates.
        if(node.parentElement?.closest('[style*="position"]')){skipped++;continue;}
        const x=mm(node.style.left||'0'),y=mm(node.style.top||'0'),w=mm(node.style.width),h=mm(node.style.height);
        if(![x,y,w,h].every(Number.isFinite)||w<=0||h<=0){skipped++;continue;}
        const type=node.tagName==='IMG'?'Picture':node.textContent.trim()?'Text':'Rectangle';
        if(type==='Picture'&&!/^data:image\/(png|jpeg);base64,/.test(node.getAttribute('src')||'')){skipped++;continue;}
        const item=exchangeRecord(type,node.textContent.trim(),x,y,w,h);
        if(type==='Picture')item.picture=node.getAttribute('src');
        if(type==='Text'){
            const size=mm(node.style.fontSize);if(Number.isFinite(size)&&size>0)item.properties.FontSize=size*96/25.4;
            if(['Arial','Inter','Times New Roman'].includes(node.style.fontFamily))item.properties.FontFamily=node.style.fontFamily;
            if(['normal','bold'].includes(node.style.fontWeight))item.properties.FontWeight=node.style.fontWeight;
            if(node.style.color)item.properties.TextColor=node.style.color;
        }
        if(node.style.backgroundColor){item.properties.HasBg=true;item.properties.BgColor=node.style.backgroundColor;}
        doc.items.push(item);if(doc.items.length>200)throw new Error('HTML possui mais de 200 elementos.');
    }
    if(!doc.items.length)throw new Error('HTML sem layout compatível. Use HTML GeoNex ou elementos absolutos com left/top/width/height em px, pt ou mm.');
    const json=JSON.stringify(doc);composerValidateLayout(json);
    return {json,warning:`${doc.items.length} elementos convertidos. ${skipped} ignorados. CSS externo, scripts, layout responsivo e imagens remotas não são importados; revise o resultado.`};
};
window.composerExportHTML = function() {
    const json=composerSerializeLayout();composerValidateLayout(json);
    const preview=composerPaper.cloneNode(true);preview.removeAttribute('id');preview.style.transform='none';
    preview.querySelectorAll('.resize-handle,.composer-grid-image').forEach(n=>n.remove());
    preview.querySelectorAll('[data-type="Map"] .composer-map-inner').forEach(n=>{n.replaceChildren();n.textContent='Mapa do projeto · prévia no GeoNex';n.style.cssText='display:grid;place-items:center;width:100%;height:100%;background:#e8eef2;color:#475569;font:14px Arial';});
    for(const element of [preview,...preview.querySelectorAll('*')]){
        element.classList.remove('selected');
        for(const a of [...element.attributes])if(a.name==='id'||a.name.startsWith('on')||a.name.startsWith('data-'))element.removeAttribute(a.name);
        if(element.tagName==='IMG'&&!element.src.startsWith('data:image/'))element.removeAttribute('src');
    }
    const payload=json.replace(/</g,'\\u003c');
    const html=`<!doctype html><html lang="pt-BR"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Modelo GeoNex</title><style>body{margin:24px;background:#edf1f5;font-family:Arial}.paper-sheet{position:relative;background:white;overflow:hidden}.composer-item{position:absolute;box-sizing:border-box}.item-content{width:100%;height:100%;box-sizing:border-box}.composer-item::after{content:'';position:absolute;inset:0;border:var(--composer-frame,none);pointer-events:none;box-sizing:border-box}</style><p>Modelo GeoNex — importe no compositor para editar. Mapas dependem do projeto; prévia não georreferenciada.</p>${preview.outerHTML}<script type="application/json" id="geonex-layout">${payload}</script></html>`;
    if(new TextEncoder().encode(html).length>40000000)throw new Error('HTML excede 40 MB. Salve como JSON.');
    const url=URL.createObjectURL(new Blob([html],{type:'text/html;charset=utf-8'})),a=document.createElement('a');a.href=url;a.download='composicao.geonex-layout.html';a.click();setTimeout(()=>URL.revokeObjectURL(url),30000);
    return html;
};

// Native <details> keeps keyboard semantics; delegation also handles dynamically rendered Blazor menus.
document.addEventListener('click',e=>{
    const current=e.target.closest?.('.composer-menu');
    document.querySelectorAll('.composer-menu[open]').forEach(menu=>{if(menu!==current)menu.open=false;});
    if(current && e.target.closest('button'))current.open=false;
});
document.addEventListener('keydown',e=>{if(e.key==='Escape'){const menu=document.querySelector('.composer-menu[open]');if(menu){menu.open=false;menu.querySelector('summary')?.focus();}}});
