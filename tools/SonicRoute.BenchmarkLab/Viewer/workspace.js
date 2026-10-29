(function(){
  'use strict';
  window.BenchmarkWorkspace={create(options){
    const D=window.BenchmarkData,$=id=>document.getElementById(id),model=options.getModel,fmt=options.format;
    const el=(tag,cls,value)=>{const n=document.createElement(tag);if(cls)n.className=cls;if(value!==undefined)n.textContent=value;return n;};
    function setView(view){
      for(const section of document.querySelectorAll('[data-view-section]'))section.hidden=!section.dataset.viewSection.split(' ').includes(view);
      for(const control of document.querySelectorAll('[data-view]')){const selected=control.dataset.view===view;control.classList.toggle('selected',selected);control.setAttribute('aria-pressed',String(selected));}
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
    function renderReview(){
      const m=model(),review=m.review,box=$('ai-content');box.replaceChildren();
      $('ai-state').textContent=review?'已完成':'未完成';
      $('report-completion').textContent=m.demo?'合成示例 · 非实测数据':review?'AI 已完成总结':'AI 总结尚未完成';
      $('report-completion').className='completion-label'+(review?' complete':'');
      if(!review){box.append(el('p','ai-summary-text',m.demo?'这里展示报告的阅读布局。实际测试报告由 AI 直接写入总结后交付。':'测试数据已记录，AI 总结尚未完成。'),el('p','review-pending',m.demo?'以下图表均为合成示例，不代表产品优化收益。':'当前报告不作为最终解读；完成后的结论会直接显示在这里。'));return;}
      box.append(el('p','ai-summary-text',review.Summary));
      if(review.Findings.length){const list=el('div','finding-grid');for(const f of review.Findings){const item=el('article','finding');item.append(el('h3','',f.Title),el('p','',f.Detail));if(f.ScenarioId){const link=el('button','text-button','查看相关图表');link.onclick=()=>options.selectScenario(f.ScenarioId);item.append(link);}list.append(item);}box.append(list);}
      const notes=el('div','review-notes');
      for(const [title,items,cls]of [['结论边界',review.Limitations,'limits'],['下一步',review.Recommendations,'next']])if(items.length){const section=el('div',cls);section.append(el('h3','',title));const ul=el('ul');for(const item of items)ul.append(el('li','',item));section.append(ul);notes.append(section);}
      if(notes.children.length)box.append(notes);
      const date=new Date(review.GeneratedUtc);box.append(el('p','ai-origin',review.Author.Model+' · '+(Number.isNaN(date.getTime())?review.GeneratedUtc:date.toLocaleString('zh-CN',{hour12:false}))+' · 已写入测试日志'));
    }
    function facts(){
      const m=model(),box=$('report-facts');box.replaceChildren();const failed=m.runs.filter(r=>!r.success).length;
      const items=[['chart',String(m.scenarios.length),'个测试场景'],['clock',String(m.runs.length),'次测量记录'],[failed?'warning':'shield',failed?failed+' 次失败':m.demo?'合成示例':D.get(m.source,'IsAaControl')?'A/A 对照':m.kind==='run'?'单目标测量':m.status==='pass'?'未检出退化':m.status==='regression'?'检出退化':m.status==='error'?'测量有失败':'证据不足',failed?'保留失败证据':'结论以本次条件为限']];
      for(const [icon,value,label]of items){const item=el('div','report-fact');item.append(window.BenchmarkIcons.make(icon,23));const copy=el('div');copy.append(el('strong','',value),el('span','',label));item.append(copy);box.append(item);}
      $('report-binding').textContent=m.reportHash?'报告 '+m.reportHash.slice(0,12):'本地 · 离线 · 可复查';
    }
    function history(){
      const m=model(),box=$('review-log');box.replaceChildren();const revisions=m.reviewHistory||[];
      $('review-revision-count').textContent=revisions.length+' 次总结记录';
      if(!revisions.length){box.append(el('p','muted','尚无已完成的 AI 总结记录。'));return;}
      for(const entry of [...revisions].reverse()){const details=el('details','review-revision'),summary=el('summary','', '第 '+entry.Number+' 版 · '+entry.Review.Author.Model+' · '+entry.Review.GeneratedUtc);details.append(summary);const content=el('div','revision-content');content.append(el('p','',entry.Review.Summary));for(const f of entry.Review.Findings)content.append(el('h3','',f.Title),el('p','',f.Detail));for(const [title,items]of [['限制',entry.Review.Limitations],['建议',entry.Review.Recommendations]])if(items.length){content.append(el('h3','',title));const ul=el('ul');for(const item of items)ul.append(el('li','',item));content.append(ul);}details.append(content);box.append(details);}
    }
    $('landscape-metric').onchange=landscape;
    for(const control of document.querySelectorAll('[data-view]'))control.onclick=()=>options.setView(control.dataset.view);
    const I=window.BenchmarkIcons;I.set(document.querySelector('.brand-mark'),'wave',24);I.set(document.querySelector('.quality-leaf'),'shield',23);I.set(document.querySelector('.footer-mark'),'wave',18);
    for(const item of document.querySelectorAll('.view-tabs [data-view]'))item.prepend(I.make(({overview:'overview',details:'chart',evidence:'evidence'})[item.dataset.view],18));
    for(const [selector,name]of [['.ai-panel h2','ai'],['.landscape-panel h2','compare'],['#evidence h2','evidence'],['#trends h2','chart'],['#review-history h2','clipboard']]){const node=document.querySelector(selector);node.classList.add('icon-heading');node.prepend(I.make(name,21));}
    return {render(view){setView(view);if(view==='overview'){coverage();landscape();renderReview();facts();}else if(view==='evidence')history();}};
  }};
})();
