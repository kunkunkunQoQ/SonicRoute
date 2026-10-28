(function(){
  'use strict';
  // Local, code-native icons; no fonts, images, packages or external requests.
  const paths={
    wave:['M2 12h3l3-8 4 16 4-16 3 8h3'],
    overview:['M3 3h7v7H3z','M14 3h7v7h-7z','M3 14h7v7H3z','M14 14h7v7h-7z'],
    chart:['M3 3v18h18','M6 15l4-5 4 3 6-8'],
    evidence:['M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z','M14 2v6h6','M8 13h8','M8 17h5'],
    upload:['M12 16V3','M7 8l5-5 5 5','M4 15v5a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-5'],
    download:['M12 3v13','M7 11l5 5 5-5','M4 17v4h16v-4'],
    ai:['M12 3l2.4 6.6L21 12l-6.6 2.4L12 21l-2.4-6.6L3 12l6.6-2.4z','M20 2v4','M18 4h4'],
    cpu:['M6 6h12v12H6z','M9 9h6v6H9z','M9 2v4M15 2v4M9 18v4M15 18v4M2 9h4M2 15h4M18 9h4M18 15h4'],
    memory:['M3 7h18v10H3z','M7 10v4M11 10v4M15 10v4','M6 17v3M10 17v3M14 17v3M18 17v3'],
    layers:['M12 3L2 8l10 5 10-5z','M2 12l10 5 10-5','M2 16l10 5 10-5'],
    handles:['M8 6h8v12H8z','M4 3h4v6H4z','M16 15h4v6h-4z','M8 6H6M16 18h2'],
    check:['M20 6L9 17l-5-5'],
    warning:['M12 3L2 21h20z','M12 9v5','M12 17h.01'],
    help:['M9.1 9a3 3 0 0 1 5.8 1c0 2-3 2.5-3 4','M12 18h.01'],
    shield:['M12 2l8 3v6c0 5-8 11-8 11S4 16 4 11V5z','M8 12l3 3 5-6'],
    copy:['M9 9h12v12H9z','M5 15H3V3h12v2'],
    close:['M6 6l12 12','M18 6L6 18'],
    plus:['M12 5v14','M5 12h14'],
    clipboard:['M8 4H5v18h14V4h-3','M8 2h8v5H8z','M8 12h8','M8 16h5'],
    folder:['M3 5h6l2 2h10v13H3z'],
    compare:['M3 7h16','M15 3l4 4-4 4','M21 17H5','M9 13l-4 4 4 4'],
    clock:['M12 8v5l3 2'],
    arrow:['M5 12h14','M14 7l5 5-5 5']
  };
  function make(name,size=20){const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg');
    for(const[k,v]of Object.entries({viewBox:'0 0 24 24',width:size,height:size,fill:'none',stroke:'currentColor','stroke-width':1.7,'stroke-linecap':'round','stroke-linejoin':'round','aria-hidden':'true',focusable:'false',class:'ui-icon'}))svg.setAttribute(k,String(v));
    if(name==='clock'||name==='help'){const c=document.createElementNS(ns,'circle');c.setAttribute('cx','12');c.setAttribute('cy','12');c.setAttribute('r','10');svg.append(c);}
    for(const d of paths[name]||paths.evidence){const p=document.createElementNS(ns,'path');p.setAttribute('d',d);svg.append(p);}return svg;}
  function set(id,name,size=20){const node=typeof id==='string'?document.getElementById(id):id;if(node)node.replaceChildren(make(name,size));}
  function button(id,name){const node=document.getElementById(id);if(node){for(const child of [...node.children])if(child.tagName==='SPAN'||child.classList.contains('ui-icon'))child.remove();node.prepend(make(name,18));}}
  window.BenchmarkIcons={make,set,button};
})();
