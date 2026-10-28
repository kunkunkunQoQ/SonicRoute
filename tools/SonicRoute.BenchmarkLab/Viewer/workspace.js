(function(){
  'use strict';
  window.BenchmarkWorkspace={create(options){
    const D=window.BenchmarkData,A=window.BenchmarkAnalysis,$=id=>document.getElementById(id),cache=new WeakMap();
    let dialogOwner=null,dialogMode='request';
    const el=(tag,cls,value)=>{const n=document.createElement(tag);if(cls)n.className=cls;if(value!==undefined)n.textContent=value;return n;};
    const fmt=options.format,model=options.getModel;
    const fingerprint=m=>{if(!cache.has(m))cache.set(m,A.fingerprint(m));return cache.get(m);};
    const download=(name,value,type)=>{const url=URL.createObjectURL(new Blob([value],{type:type||'text/plain;charset=utf-8'}));const link=el('a');link.href=url;link.download=name;document.body.append(link);link.click();link.remove();URL.revokeObjectURL(url);};
    function setView(view){
      for(const section of document.querySelectorAll('[data-view-section]'))section.hidden=!section.dataset.viewSection.split(' ').includes(view);
      for(const control of document.querySelectorAll('[data-view]')){const selected=control.dataset.view===view;control.classList.toggle('selected',selected);control.classList.toggle('active',selected);control.setAttribute('aria-pressed',String(selected));}
      const title={overview:'结论与 AI 解读',details:'图表分析',evidence:'原始证据'}[view];
      document.querySelector('.topbar>span').textContent='性能观察室 / '+title;
    }
    function coverage(){
      const m=model(),counts={pass:0,regression:0,inconclusive:0,error:0,measured:0};
      for(const s of m.scenarios){const failed=m.runs.some(r=>r.scenarioId===s.id&&!r.success);const status=failed?'error':s.verdict;counts[status in counts?status:'inconclusive']++;}
      const colors={pass:'#78986a',regression:'#bb725b',inconclusive:'#c0a36e',error:'#b34e43',measured:'#9eac91'};
      const labels={pass:'未检出退化',regression:'退化',inconclusive:'不确定',error:'失败',measured:'已测量'};
      const box=$('coverage-visual');box.replaceChildren();const bar=el('div','coverage-bar'),legend=el('div','coverage-labels');
      for(const [status,n]of Object.entries(counts))if(n){const segment=el('span','coverage-segment');segment.style.width=n/Math.max(m.scenarios.length,1)*100+'%';segment.style.background=colors[status];segment.title=labels[status]+' '+n+' 个场景';bar.append(segment);const label=el('span'),dot=el('i');dot.style.background=colors[status];label.append(dot,document.createTextNode(n+' '+labels[status]));legend.append(label);}
      box.append(bar,legend);
      const failures=m.runs.filter(r=>!r.success);
      const readiness=D.get(m.source,'DecisionReadiness');
      $('decision-guidance').className='decision-guidance'+(failures.length?' error-text':'');
      $('decision-guidance').textContent=failures.length?'失败定位：'+failures.slice(0,3).map(r=>D.scenarioLabel(r.scenarioId)+' 第'+r.round+'轮 '+r.side).join('；')
        :m.demo?'示例用于体验界面，不能据此判断 SonicRoute 性能。'
        :D.get(m.source,'IsAaControl')===true?'这是同一版本的 A/A 对照，只说明重复性，不能得出版本改善结论。'
        :readiness&&!D.get(readiness,'RecommendedProtocolMet')?'尚未满足推荐决策条件。查看协议、轮次与测试限制后再下结论。'
        :m.kind==='run'?'仅测量了一个目标；需要可比的基线才能判断改善或退化。':'先查看 AI 解读，再点击下面的场景定位差异。';
    }
    function landscape(){
      const box=$('landscape-chart');box.replaceChildren();const kind=$('landscape-metric').value;
      const value=s=>{if(!s)return null;if(kind==='cpu'){const d=D.num(D.get(s,'DurationSeconds')),v=D.num(D.get(s,'TotalCpuTimeMs'));return d>0&&v!==null?v/d:null;}const v=D.metric(s,kind==='memory'?'PrivateBytes':'WorkingSetBytes');return v===null?null:v/1048576;};
      const unit=kind==='cpu'?'ms/s':'MB',rows=model().scenarios.map(s=>({s,a:value(s.baseline),b:value(s.candidate)}));
      const max=Math.max(0.001,...rows.flatMap(r=>[r.a,r.b]).filter(v=>v!==null));
      for(const {s,a,b}of rows){const button=el('button','landscape-row');button.type='button';button.setAttribute('aria-label',D.scenarioLabel(s.id)+'，查看图表分析');
        const name=el('span','landscape-name',D.scenarioLabel(s.id)),status=el('span','badge '+s.verdict,({pass:'未检出退化',regression:'退化',inconclusive:'不确定',measured:'单目标测量'})[s.verdict]||'待核对');name.append(status);
        const bars=el('span','landscape-bars');for(const [side,v]of [['A',a],['B',b]]){const line=el('span','landscape-line'),track=el('span','landscape-track'),fill=el('span','landscape-fill'+(side==='A'?' a':''));fill.style.width=v===null?'0%':Math.max(0,Math.min(100,v/max*100))+'%';track.append(fill);line.append(el('span','',side),track,el('span','landscape-number',v===null?'未测量':fmt(v,2)+' '+unit));bars.append(line);}
        const delta=a>0&&b!==null?(b/a-1)*100:null,change=el('span','landscape-delta'+(delta>0?' worse':''),delta===null?'—':(delta>0?'+':'')+fmt(delta)+'%');change.append(el('small','',a===0?'零基线，比例未定义':a===null?'缺少可比基线':'B 相对 A 的均值变化'));
        button.append(name,bars,change);button.onclick=()=>options.selectScenario(s.id);box.append(button);
      }
    }
    function renderAI(){
      const m=model(),analysis=m.analysis,box=$('ai-content');box.replaceChildren();
      $('ai-state').textContent=analysis?'已附加 AI 解读':'等待 AI 解读';
      if(!analysis){box.append(el('p','ai-empty-title','这里还没有 AI 总结'),el('p','','把当前报告交给 AI，得到中文结论、关键发现与下一步建议。'));
        const steps=el('div','ai-steps');for(const label of ['1 整理分析材料','2 交给 AI','3 将总结放回这里'])steps.append(el('span','ai-step',label));box.append(steps);return;}
      box.append(el('p','ai-summary-text',analysis.Summary));
      if(analysis.Findings.length){box.append(el('h3','','关键发现'));const ul=el('ul');for(const f of analysis.Findings){const li=el('li');li.append(el('strong','',f.Title+'：'),document.createTextNode(f.Detail));if(f.ScenarioId){const link=el('button','text-button','查看 '+D.scenarioLabel(f.ScenarioId)+' ↗');link.onclick=()=>options.selectScenario(f.ScenarioId);li.append(link);}ul.append(li);}box.append(ul);}
      for(const [title,items]of [['测试限制',analysis.Limitations],['下一步',analysis.Recommendations]])if(items.length){box.append(el('h3','',title));const ul=el('ul');for(const item of items)ul.append(el('li','',item));box.append(ul);}
      box.append(el('p','ai-origin','用户提供的 AI 解读 · '+analysis.Author.Model+' · 报告 '+analysis.ReportSha256.slice(0,12)+'…'+(m.demo?' · 合成示例':'')+'。用于辅助阅读，不替代原始判定。'));
    }
    async function attach(raw,m=model()){
      const sha=await fingerprint(m),checked=A.validate(raw,sha,m.scenarios.map(s=>s.id));m.analysis=checked;
      if(model()===m){renderAI();$('ai-message').textContent='已附加到匹配的报告，原始数据和判定保留。';}
      return checked;
    }
    async function openRequest(){
      const owner=model();$('ai-message').textContent='正在整理分析材料…';
      try{const request=await A.request(owner);if(model()!==owner)return;dialogOwner=owner;dialogMode='request';
        $('ai-dialog-title').textContent='让 AI 分析当前报告';$('ai-dialog-help').textContent='复制或下载下方材料，交给当前对话中的 AI。收到 JSON 总结后，使用“粘贴 AI 总结”放回页面。';
        $('ai-text-label').textContent='已整理的分析材料（含报告指纹与逐轮证据）';$('ai-text').value=request;$('ai-text').readOnly=true;
        $('ai-model-label').hidden=true;$('ai-copy').hidden=false;$('ai-download').hidden=false;$('ai-apply').hidden=true;$('ai-dialog-message').textContent='';$('ai-dialog').showModal();$('ai-message').textContent='';
      }catch(e){$('ai-message').textContent=e.message;}
    }
    function openPaste(){dialogOwner=model();dialogMode='paste';$('ai-dialog-title').textContent='将 AI 总结放到报告中';$('ai-dialog-help').textContent='可粘贴符合模板的 JSON，也可粘贴纯文本总结。纯文本会注明由你提供，并绑定当前报告。';$('ai-text-label').textContent='AI 总结内容';$('ai-text').readOnly=false;$('ai-text').value='';$('ai-model').value='';$('ai-model-label').hidden=false;$('ai-copy').hidden=true;$('ai-download').hidden=true;$('ai-apply').hidden=false;$('ai-dialog-message').textContent='';$('ai-dialog').showModal();}
    async function applyPaste(){
      try{let value=$('ai-text').value.trim();if(!value)throw Error('请先粘贴总结内容。');if(value.length>60000)throw Error('总结内容过长。');
        value=value.replace(/^```(?:json)?\s*/i,'').replace(/\s*```$/,'');let raw;
        if(value.startsWith('{'))raw=JSON.parse(value);else raw={Schema:A.schema,ReportSha256:await fingerprint(dialogOwner),Author:{Kind:'ai',Model:$('ai-model').value.trim()||'模型未注明（用户粘贴）'},Summary:value,Findings:[],Limitations:[],Recommendations:[],ProvidedBy:'user-paste'};
        await attach(raw,dialogOwner);$('ai-dialog').close();
      }catch(e){$('ai-dialog-message').textContent='无法保存：'+e.message;}
    }
    $('ai-request').onclick=openRequest;$('ai-paste').onclick=openPaste;$('ai-dialog-close').onclick=()=>$('ai-dialog').close();$('ai-apply').onclick=applyPaste;
    $('ai-copy').onclick=async()=>{try{await navigator.clipboard.writeText($('ai-text').value);$('ai-dialog-message').textContent='已复制，可粘贴到 AI 对话。';}catch{$('ai-text').focus();$('ai-text').select();$('ai-dialog-message').textContent='浏览器未开放剪贴板，请按 Ctrl+C 复制已选中的内容，或下载材料。';}};
    $('ai-download').onclick=()=>download('benchmark-ai-request.txt',$('ai-text').value);
    $('ai-import').onclick=()=>$('ai-file-input').click();$('ai-file-input').onchange=async event=>{const owner=model(),file=event.target.files[0];try{if(!file)return;if(file.size>100000)throw Error('总结文件不能超过 100 KB。');await attach(JSON.parse(await file.text()),owner);}catch(e){if(model()===owner)$('ai-message').textContent='无法导入：'+e.message;}finally{$('ai-file-input').value='';}};
    $('landscape-metric').onchange=landscape;
    for(const control of document.querySelectorAll('[data-view]'))control.onclick=event=>{event.preventDefault();options.setView(control.dataset.view);};
    for(const id of ['trends','evidence'])$(id).dataset.viewSection=id==='trends'?'details':'evidence';
    document.querySelector('.filters').dataset.viewSection='details evidence';$('metric-grid').dataset.viewSection='details';
    for(const box of document.querySelectorAll('.content>.two-column'))box.dataset.viewSection='details';
    const I=window.BenchmarkIcons;
    I.set(document.querySelector('.brand-mark'),'wave',26);I.set(document.querySelector('.file-symbol'),'evidence',24);
    for(const item of document.querySelectorAll('.nav-item'))I.set(item.querySelector('span'),({overview:'overview',details:'chart',evidence:'evidence'})[item.dataset.view],20);
    for(const item of document.querySelectorAll('.view-tabs [data-view]'))item.prepend(I.make(({overview:'overview',details:'chart',evidence:'evidence'})[item.dataset.view],18));
    for(const [id,name]of Object.entries({'import-button':'upload','import-side':'plus','export-button':'download','ai-request':'ai','ai-paste':'clipboard','ai-import':'folder','ai-copy':'copy','ai-download':'download','ai-apply':'check'}))I.button(id,name);
    I.set('ai-dialog-close','close',20);I.set(document.querySelector('.quality-leaf'),'shield',25);I.set(document.querySelector('.footer-mark'),'wave',19);
    for(const [selector,name]of [['.ai-panel h2','ai'],['.landscape-panel h2','compare'],['.overview-panel h2','shield'],['#evidence h2','evidence'],['#trends h2','chart']]){const node=document.querySelector(selector);node.classList.add('icon-heading');node.prepend(I.make(name,22));}
    return {render(view){setView(view);coverage();landscape();renderAI();},setView,attach,fingerprint,openRequest,renderAI,download};
  }};
})();
