(function () {
  'use strict';
  const D = window.BenchmarkData, $ = id => document.getElementById(id), MB = 1024 * 1024;
  const state = { reports: [], active: 0, scenario: '', round: 'all', metric: 'cpu', view:'overview' };
  let renderedChartWidth = 0;
  const fmt = (v, digits = 1) => D.num(v) === null ? '—' : v.toLocaleString('zh-CN', { minimumFractionDigits: digits, maximumFractionDigits: digits });
  const text = (id, value) => { $(id).textContent = value; };
  function el(tag, className, value) { const e = document.createElement(tag); if (className) e.className = className; if (value !== undefined) e.textContent = value; return e; }
  function svgEl(tag, attrs, value) { const e = document.createElementNS('http://www.w3.org/2000/svg', tag); for (const [k, v] of Object.entries(attrs || {})) e.setAttribute(k, String(v)); if (value !== undefined) e.textContent = value; return e; }
  function badge(status, override) { const names = { stable: '稳定', pass: '未检出退化', improved: '改善', regression: '退化', inconclusive: '不确定', error: '失败', measured: '已测量', unmeasured: '未测量' }; return el('span', 'badge ' + status, override || names[status] || '不确定'); }
  function active() { return state.reports[state.active]; }
  function scenario() { return active().scenarios.find(s => s.id === state.scenario) || active().scenarios[0]; }
  function filteredRuns() { return active().runs.filter(r => r.scenarioId === scenario().id && (state.round === 'all' || String(r.round) === state.round)); }
  function option(value, label) { const o = document.createElement('option'); o.value = value; o.textContent = label; return o; }
  function resetSelection() { state.scenario = active().scenarios[0]?.id || ''; state.round = 'all';state.view='overview'; }
  function summaries() {
    if (state.round === 'all') return { a: scenario().baseline, b: scenario().candidate };
    const runs = filteredRuns();
    return { a: D.combineMetrics(runs.filter(r => r.side === 'Baseline')), b: D.combineMetrics(runs.filter(r => r.side !== 'Baseline')) };
  }
  function cpuRate(s) { const duration = D.num(D.get(s, 'DurationSeconds')), cpu = D.num(D.get(s, 'TotalCpuTimeMs')); return duration > 0 && cpu !== null ? cpu / duration : null; }
  function difference(a, b) { return a > 0 && b !== null ? (b / a - 1) * 100 : null; }
  function renderBanner() {
    const model = active(), status = model.status;
    const messages = {
      pass: ['未检出超过门限的退化', '仅适用于本次测试条件；仍需结合逐轮波动、测试范围和协议完成度。', 'PASS'],
      regression: ['检测到性能退化', '报告检出了有重复性证据的退化。点击场景查看具体指标和逐轮数据。', 'REGRESSION'],
      inconclusive: ['证据不足，暂时无法下结论', '样本、波动或协议可比性存在限制。不确定不等于通过。', 'INCONCLUSIVE'],
      error: ['测试存在失败', '负载、测量或来源验证失败。已有数据可供排查，不能作为性能通过的证据。', 'EXECUTION ERROR'],
      measured: ['测量完成，尚无版本对比', '单目标测量可以反映资源消耗，不能判断版本改善或退化。', 'MEASURED']
    };
    const message = messages[status] || messages.inconclusive;
    $('verdict-banner').className = 'verdict-banner' + (status === 'error' || status === 'regression' ? ' error' : status === 'inconclusive' || status === 'measured' ? ' warn' : '');
    text('verdict-title', model.demo ? '合成示例，仅用于展示报告' : D.get(model.source,'IsAaControl')&&status!=='error' ? 'A/A 对照已记录，不能作版本优化结论' : message[0]);
    text('verdict-description', model.demo ? '以下均为合成数据，仅用于体验图表与筛选，不代表真实测量或优化效果。' : message[1]);
    text('verdict-pill', model.demo ? 'SYNTHETIC DEMO' : message[2]);
    window.BenchmarkIcons.set('verdict-icon',status==='error'||status==='regression'?'warning':status==='pass'?'check':'help',28);
  }
  function renderFilters() {
    const select = $('scenario-select'); select.replaceChildren();
    for (const s of active().scenarios) select.append(option(s.id, D.scenarioLabel(s.id)));
    select.value = state.scenario;
    const rounds = [...new Set(active().runs.filter(r => r.scenarioId === scenario().id).map(r => r.round))].sort((a, b) => a - b);
    $('round-select').replaceChildren(option('all', '全部轮次'));
    for (const r of rounds) $('round-select').append(option(String(r), '第 ' + r + ' 轮'));
    if (!rounds.some(r => String(r) === state.round)) state.round = 'all';
    $('round-select').value = state.round;
  }
  function renderOverview() {
    const model = active(), container = $('scenario-overview'); container.replaceChildren();
    for (const s of model.scenarios) {
      const card = el('button', 'scene-card' + (s.id === scenario().id ? ' selected' : ''));
      card.setAttribute('aria-pressed', String(s.id === scenario().id));
      const runs = model.runs.filter(r => r.scenarioId === s.id), failed = runs.some(r => !r.success);
      card.append(el('strong','',D.scenarioLabel(s.id)),badge(failed ? 'error' : s.verdict));
      const cpu = s.comparisons.find(m => m.name === 'Total CPU Time');
      card.append(el('small','',cpu?.percent !== null && cpu?.percent !== undefined ? 'CPU ' + (cpu.percent > 0 ? '+' : '') + fmt(cpu.percent) + '%' : runs.length + ' 次测量'));
      card.onclick = () => { state.scenario=s.id;state.round='all';state.view='details';render(); }; container.append(card);
    }
    text('coverage-count', model.scenarios.length + ' 个场景 · ' + model.runs.length + ' 次测量');
    const protocol = model.protocol, scenarios = Object.values(D.get(protocol,'Scenarios',{}));
    const windows = scenarios.map(s => D.num(D.get(s,'DurationSeconds'))).filter(v=>v!==null);
    const warmups = scenarios.map(s => D.num(D.get(s,'WarmupSeconds'))).filter(v=>v!==null);
    const roundCounts = model.scenarios.map(s => new Set(model.runs.filter(r=>r.scenarioId===s.id).map(r=>r.round)).size);
    const source = model.source, readiness = D.get(source,'DecisionReadiness');
    const chips = [
      ['每场景轮次 ≥ ' + (roundCounts.length ? Math.min(...roundCounts) : 0), roundCounts.some(n=>n<5)],
      ['测量窗口 ' + (windows.length ? fmt(Math.min(...windows),0)+' s 起' : '未记录'),!windows.length || windows.some(n=>n<30)],
      ['预热 ' + (warmups.length ? fmt(Math.min(...warmups),0)+' s 起' : '未记录'),!warmups.length || warmups.some(n=>n<5)],
      ['采样 ' + fmt(D.get(protocol,'SampleIntervalMs'),0)+' ms',false],
      [model.demo ? '合成示例 · 不作结论' : readiness ? D.get(readiness,'RecommendedProtocolMet') ? '推荐协议已满足' : '推荐协议尚未满足' : '决策条件见原报告', !!readiness && !D.get(readiness,'RecommendedProtocolMet')]
    ];
    const strip=$('protocol-strip');strip.replaceChildren();
    for(const [label,warn] of chips) strip.append(el('span','protocol-chip'+(warn?' warn':''),label));
    const saved=D.get(source,'BaselineSavedUtc');
    if(saved) strip.append(el('span','protocol-chip','基线保存于 '+new Date(saved).toLocaleDateString('zh-CN')));
    if(readiness) strip.title=D.get(readiness,'Reasons',[]).join('\n');
  }
  function renderRoundChart() {
    const box=$('round-chart');box.replaceChildren();
    const runs=active().runs.filter(r=>r.scenarioId===scenario().id);
    const historical=D.get(D.get(active().source,'BaselineRounds'),scenario().id,[]);
    const points=runs.map(r=>({round:r.round,side:r.side,value:cpuRate(r.metrics),success:r.success}));
    if(!runs.some(r=>r.side==='Baseline') && D.get(active().source,'UsesStoredBaseline')===true)
      historical.forEach((m,i)=>points.push({round:i+1,side:'Baseline',value:cpuRate(m),success:D.get(m,'MeasurementValid')===true,historical:true}));
    const valid=points.filter(p=>p.value!==null);
    if(!valid.length){box.append(el('div','chart-empty','缺少逐轮 CPU 数据，不能从汇总虚构波动。'));text('round-statistics','');return;}
    const W=Math.max(300,box.clientWidth),H=210,L=43,R=15,T=18,B=34,w=W-L-R,h=H-T-B;
    const max=Math.max(1,...valid.map(p=>p.value))*1.15;
    const rounds=[...new Set(valid.map(p=>p.round))].sort((a,b)=>a-b);
    const svg=svgEl('svg',{viewBox:`0 0 ${W} ${H}`,role:'img','aria-label':'每轮 CPU 毫秒每秒，基线浅绿、待测深绿'});
    for(let i=0;i<4;i++){const y=T+i*h/3;svg.append(svgEl('line',{x1:L,x2:W-R,y1:y,y2:y,stroke:'#e7ede0','stroke-dasharray':'3 5'}),svgEl('text',{x:L-8,y:y+3,'font-size':9,'text-anchor':'end',fill:'#78866c'},fmt(max*(1-i/3),1)));}
    const positions=rounds.map((r,i)=>L+(i+0.5)*w/rounds.length);
    for(const [i,r] of rounds.entries()) svg.append(svgEl('text',{x:positions[i],y:H-8,'text-anchor':'middle','font-size':10,fill:'#78866c'},'第'+r+'轮'));
    for(const side of ['Baseline','Candidate']){
      const current=valid.filter(p=>p.side===side),color=side==='Baseline'?'#b5c0a5':'#527b46';
      const line=current.map(p=>`${positions[rounds.indexOf(p.round)]+(side==='Baseline'?-4:4)},${T+h-p.value/max*h}`).join(' ');
      svg.append(svgEl('polyline',{points:line,fill:'none',stroke:color,'stroke-width':1.3,opacity:0.6}));
      for(const p of current){const circle=svgEl('circle',{cx:positions[rounds.indexOf(p.round)]+(side==='Baseline'?-4:4),cy:T+h-p.value/max*h,r:4,fill:p.success?color:'#ad6552',opacity:state.round==='all'||String(p.round)===state.round?1:0.25});circle.append(svgEl('title',{},(p.historical?'历史基线 ':side==='Baseline'?'基线 A ':'待测 B ')+'第'+p.round+'轮：'+fmt(p.value,3)+' ms/s'));svg.append(circle);}
    }
    box.append(svg);
    const lines=[];for(const side of ['Baseline','Candidate']){const stats=D.distribution(valid.filter(p=>p.side===side).map(p=>p.value));if(stats.count) lines.push((side==='Baseline'?'A':'B')+' 中位数 '+fmt(stats.median,2)+' · 范围 '+fmt(stats.min,2)+'–'+fmt(stats.max,2)+' · CV '+(stats.cv===null?'—':fmt(stats.cv*100)+'%'));}
    text('round-statistics',lines.join('；')+'。失败轮次以红点显示；历史样本轮次编号不代表配对执行。CV 仅描述波动，不单独决定通过。');
  }
  function renderIntervals() {
    const box=$('interval-chart');box.replaceChildren();
    const rows=scenario().comparisons.filter(m=>m.interval && D.num(D.get(m.interval,'DecisionLimit'))>0);
    if(state.round!=='all'||!rows.length){box.append(el('div','chart-empty',state.round!=='all'?'当前选择单轮：不根据单轮生成统计区间。':'报告没有结构化置信区间。旧数据仍可阅读，不能补造置信结论。'));return;}
    const W=Math.max(300,box.clientWidth),H=Math.max(180,rows.length*40+45),L=105,R=14,w=W-L-R;
    box.style.height=H+'px';const svg=svgEl('svg',{viewBox:`0 0 ${W} ${H}`,role:'img','aria-label':'差值除以原判定门限，中心0，阴影区负1到正1'});
    const x=v=>L+(Math.max(-3,Math.min(3,v))+3)/6*w;
    svg.append(svgEl('rect',{x:x(-1),y:12,width:x(1)-x(-1),height:H-40,fill:'#eef4e7'}),svgEl('line',{x1:x(0),x2:x(0),y1:10,y2:H-28,stroke:'#a4b593','stroke-dasharray':'3 3'}));
    rows.forEach((m,i)=>{const e=m.interval,limit=D.get(e,'DecisionLimit'),lo=D.get(e,'Lower95'),hi=D.get(e,'Upper95'),mean=D.get(e,'MeanDifference');if([lo,hi,mean].some(v=>D.num(v)===null))return;
      const y=29+i*40,color=m.status==='regression'?'#af6654':m.status==='improved'?'#527b46':'#aa945e';
      const names={'Total CPU Time':'CPU 时间','Private Memory':'私有内存','Peak Working Set':'工作集峰值','Handle Count':'句柄数量','Thread Count':'线程数量','Input-idle Proxy Latency':'输入空闲代理'};
      svg.append(svgEl('text',{x:0,y:y+3,class:'interval-row-title'},names[m.name]||m.name.slice(0,15)));
      const group=svgEl('g',{});group.append(svgEl('line',{x1:x(lo/limit),x2:x(hi/limit),y1:y,y2:y,stroke:color,'stroke-width':3,'stroke-linecap':'round'}),svgEl('circle',{cx:x(mean/limit),cy:y,r:4,fill:color}),svgEl('title',{},`${m.name}: [${fmt(lo,3)}, ${fmt(hi,3)}] ${m.unit}; 门限 ±${fmt(limit,3)} ${m.unit}; ${D.get(e,'Method')}`));svg.append(group);
      if(lo/limit < -3 || hi/limit >3) svg.append(svgEl('text',{x:hi/limit>3?W-10:L-8,y:y+3,fill:color,'font-size':12},hi/limit>3?'›':'‹'));
    });
    for(const v of [-3,-1,0,1,3]) svg.append(svgEl('text',{x:x(v),y:H-7,'text-anchor':'middle',fill:'#78866c','font-size':9},(v>0?'+':'')+v));box.append(svg);
  }
  function renderCards() {
    const { a, b } = summaries(), box = $('metric-grid'); box.replaceChildren();
    const descriptors = [
      { label: 'CPU 时间 / 秒', value: cpuRate, unit: 'ms/s', icon: 'cpu', key: 'InstantCpuPercentPerCore' },
      { label: '私有内存', value: s => { const v = D.metric(s, 'PrivateBytes'); return v === null ? null : v / MB; }, unit: 'MB', icon: 'memory', key: 'PrivateBytes' },
      { label: '工作集均值', value: s => { const v = D.metric(s, 'WorkingSetBytes'); return v === null ? null : v / MB; }, unit: 'MB', icon: 'layers', key: 'WorkingSetBytes' },
      { label: '句柄数量', value: s => D.metric(s, 'HandleCount'), unit: '个', icon: 'handles', key: 'HandleCount' }
    ];
    for (const descriptor of descriptors) {
      const va = descriptor.value(a), vb = descriptor.value(b), delta = difference(va, vb);
      const card = el('article', 'metric-card'), title = el('div', 'metric-title', descriptor.label); title.append(window.BenchmarkIcons.make(descriptor.icon,21));
      const value = el('div', 'metric-value', fmt(vb, descriptor.unit === '个' ? 0 : 1)); value.append(el('small', '', descriptor.unit));
      const detail = el('div', 'metric-detail');
      if (delta !== null) detail.append(el('span', 'metric-delta' + (delta > 0 ? ' worse' : ''), (delta > 0 ? '+' : '') + fmt(delta) + '%'));
      detail.append(document.createTextNode(a ? '基线 ' + fmt(va, descriptor.unit === '个' ? 0 : 1) : '单目标测量'));
      card.append(title, value, detail);
      const sampleRun = filteredRuns().find(r => r.side === 'Candidate' && r.samples.length);
      if (sampleRun) card.append(sparkline(sampleRun.samples, descriptor.key));
      box.append(card);
    }
  }
  function sparkline(samples, key) {
    const values = samples.map(s => D.num(D.get(s, key))).filter(v => v !== null); if (!values.length) return el('span');
    const svg = svgEl('svg', { viewBox: '0 0 80 30', class: 'sparkline', 'aria-hidden': 'true' });
    let low = Infinity, high = -Infinity;
    for (const value of values) { low = Math.min(low, value); high = Math.max(high, value); }
    const span = high - low || 1;
    const points = values.filter((_, i) => i % Math.max(1, Math.ceil(values.length / 60)) === 0).map((v, i, a) => `${i / Math.max(a.length - 1, 1) * 80},${26 - (v - low) / span * 20}`).join(' ');
    svg.append(svgEl('polyline', { points, fill: 'none', stroke: '#91a57e', 'stroke-width': 1.2 })); return svg;
  }
  function chartSeries() {
    const key = { cpu: 'InstantCpuPercentPerCore', memory: 'PrivateBytes', working: 'WorkingSetBytes' }[state.metric];
    return filteredRuns().map(r => ({ run: r, points: r.samples.slice(state.metric === 'cpu' ? 1 : 0).map(s => ({ x: D.num(D.get(s, 'ElapsedMs')), y: D.num(D.get(s, key)) })).filter(p => p.x !== null && p.y !== null).map(p => ({ x: p.x / 1000, y: state.metric === 'cpu' ? p.y : p.y / MB })) })).filter(s => s.points.length > 1);
  }
  // Preserve local extrema when reducing long curves; raw samples remain in the export.
  function reducePoints(points, limit = 600) {
    if (points.length <= limit) return points;
    const out = [points[0]], size = Math.ceil(points.length / (limit / 2));
    for (let i = 1; i < points.length - 1; i += size) {
      const bucket = points.slice(i, Math.min(i + size, points.length - 1));
      let lo = bucket[0], hi = bucket[0];
      for (const p of bucket) { if (p.y < lo.y) lo = p; if (p.y > hi.y) hi = p; }
      out.push(...(lo.x <= hi.x ? [lo, hi] : [hi, lo]));
    }
    out.push(points[points.length - 1]); return out;
  }
  function renderChart() {
    const container = $('chart'); container.replaceChildren(); const series = chartSeries();
    renderedChartWidth = container.clientWidth;
    const unit = state.metric === 'cpu' ? '% · 单核口径' : 'MB';
    text('chart-subtitle', D.scenarioLabel(scenario().id) + ' · ' + (state.round === 'all' ? '所有轮次，独立曲线' : '第 ' + state.round + ' 轮') + ' · ' + unit);
    text('chart-note', state.metric === 'cpu' ? 'CPU 曲线为区间采样值（单核 100%）；卡片按累计 CPU / 实测时长计算。曲线按各轮开始时间对齐，非同时运行。' : '资源曲线按各轮开始时间对齐。卡片展示完整轮次汇总；采样间隙中的峰值可能未被捕获。');
    if (!series.length) { container.append(el('div', 'chart-empty', '原始报告未提供时间序列，无法绘制曲线。')); return; }
    let maxX = 1, maxY = 0;
    for (const s of series) for (const p of s.points) { maxX = Math.max(maxX, p.x); maxY = Math.max(maxY, p.y); }
    maxY = Math.max(maxY * 1.16, state.metric === 'cpu' ? 1 : 10);
    const W = Math.max(300, container.clientWidth), H = 220, left = 42, right = 15, top = 14, bottom = 27, width = W - left - right, height = H - top - bottom;
    const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: 'none', role: 'img', 'aria-label': '资源曲线：横轴为实测秒数，纵轴为' + unit });
    for (let i = 0; i <= 4; i++) {
      const y = top + i / 4 * height, value = maxY * (1 - i / 4);
      svg.append(svgEl('line', { x1: left, x2: W - right, y1: y, y2: y, stroke: '#edf0e8', 'stroke-dasharray': '3 5' }));
      svg.append(svgEl('text', { x: left - 10, y: y + 3, fill: '#a7b19a', 'font-size': 9, 'text-anchor': 'end' }, fmt(value, maxY < 10 ? 1 : 0)));
    }
    const ticks = W < 500 ? 4 : 6;
    for (let i = 0; i <= ticks; i++) svg.append(svgEl('text', { x: left + width * i / ticks, y: H - 5, fill: '#78866c', 'font-size': 10, 'text-anchor': 'middle' }, fmt(maxX * i / ticks, maxX < 4 ? 1 : 0) + 's'));
    const candidates = series.filter(s => s.run.side === 'Candidate');
    if (candidates.length === 1) {
      const points = reducePoints(candidates[0].points).map(p => `${left + p.x / maxX * width},${top + height - p.y / maxY * height}`);
      svg.append(svgEl('polygon', { points: `${left},${top + height} ` + points.join(' ') + ` ${left + candidates[0].points.at(-1).x / maxX * width},${top + height}`, fill: '#edf2e6', opacity: 0.55 }));
    }
    for (const s of series) {
      const points = reducePoints(s.points).map(p => `${left + p.x / maxX * width},${top + height - p.y / maxY * height}`).join(' ');
      const path = svgEl('polyline', { points, fill: 'none', stroke: s.run.side === 'Baseline' ? '#bdc7b0' : '#688661', 'stroke-width': s.run.side === 'Baseline' ? 1.4 : 1.9, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', opacity: series.length > 2 ? 0.65 : 1, 'vector-effect': 'non-scaling-stroke' });
      path.append(svgEl('title', {}, (s.run.side === 'Baseline' ? '基线 A' : '待测 B') + ' · 第 ' + s.run.round + ' 轮')); svg.append(path);
    }
    const cursor = svgEl('line', { y1: top, y2: top + height, stroke: '#c4ceb8', 'stroke-dasharray': '3 3', visibility: 'hidden' }); svg.append(cursor);
    const tooltip = el('div', 'tooltip'); tooltip.hidden = true; container.append(svg, tooltip);
    svg.addEventListener('pointermove', event => {
      const bounds = svg.getBoundingClientRect(), px = (event.clientX - bounds.left) / bounds.width * W;
      const t = Math.max(0, Math.min(maxX, (px - left) / width * maxX)); cursor.setAttribute('x1', left + t / maxX * width); cursor.setAttribute('x2', left + t / maxX * width); cursor.setAttribute('visibility', 'visible');
      const lines = [fmt(t, 2) + ' s'];
      for (const s of series.slice(0, 12)) {
        // Binary search avoids scanning a long imported timeline on each pointer event.
        let lo = 0, hi = s.points.length - 1;
        while (lo < hi) { const mid = (lo + hi) >> 1; if (s.points[mid].x < t) lo = mid + 1; else hi = mid; }
        const p = lo > 0 && Math.abs(s.points[lo - 1].x - t) < Math.abs(s.points[lo].x - t) ? s.points[lo - 1] : s.points[lo];
        lines.push((s.run.side === 'Baseline' ? 'A' : 'B') + ' · 第' + s.run.round + '轮  ' + fmt(p.y, 2) + (state.metric === 'cpu' ? '%' : ' MB'));
      }
      tooltip.textContent = lines.join('\n'); tooltip.hidden = false;
      tooltip.style.left = Math.max(0, Math.min(bounds.width - 200, event.clientX - bounds.left + 15)) + 'px'; tooltip.style.top = '10px';
    });
    svg.addEventListener('pointerleave', () => { tooltip.hidden = true; cursor.setAttribute('visibility', 'hidden'); });
  }
  function renderComparison() {
    const body = $('comparison-body'); body.replaceChildren(); const s = scenario();
    let comparisons = s.comparisons;
    if (state.round !== 'all' || !comparisons.length) {
      const { a, b } = summaries();
      comparisons = [{ name: 'CPU / 秒', unit: 'ms/s', a: cpuRate(a), b: cpuRate(b) }, { name: '私有内存', unit: 'MB', a: D.metric(a, 'PrivateBytes') === null ? null : D.metric(a, 'PrivateBytes') / MB, b: D.metric(b, 'PrivateBytes') === null ? null : D.metric(b, 'PrivateBytes') / MB }, { name: '句柄数量', unit: '个', a: D.metric(a, 'HandleCount'), b: D.metric(b, 'HandleCount') }, { name: '线程数量', unit: '个', a: D.metric(a, 'ThreadCount'), b: D.metric(b, 'ThreadCount') }].map(m => ({ ...m, status: 'measured', percent: difference(m.a, m.b) }));
    }
    const names = { 'Total CPU Time': 'CPU 时间', 'Private Memory': '私有内存', 'Peak Working Set': '生命周期峰值工作集', 'Handle Count': '句柄数量', 'Thread Count': '线程数量', 'Input-idle Proxy Latency': '输入空闲延迟代理' };
    for (const m of comparisons) {
      const row = el('tr'), name = el('td', '', names[m.name] || m.name); name.title = m.details || '';
      row.append(name, el('td', '', fmt(m.a) + ' ' + m.unit), el('td', '', fmt(m.b) + ' ' + m.unit));
      const last = el('td'), delta = m.percent === null || m.percent === undefined ? '—' : (m.percent > 0 ? '+' : '') + fmt(m.percent) + '%';
      last.append(el('span', '', delta + '  '), badge(m.status, state.round !== 'all' ? '单轮观察' : undefined));
      if (m.interval) last.append(el('span', 'confidence', '95% 区间 [' + fmt(D.get(m.interval, 'Lower95'), 2) + ', ' + fmt(D.get(m.interval, 'Upper95'), 2) + '] ' + m.unit));
      row.append(last); body.append(row);
    }
  }
  function renderQuality() {
    const runs = filteredRuns(), quality = runs.map(r => r.quality).filter(Boolean), container = $('quality-list'); container.replaceChildren();
    const rounds = new Set(runs.map(r => r.round)).size, allRounds = new Set(active().runs.filter(r => r.scenarioId === scenario().id).map(r => r.round)).size;
    const maxGaps = quality.map(q => D.num(D.get(D.get(q, 'IntervalsMs'), 'Max'))).filter(v => v !== null);
    const fraction = quality.map(q => D.num(D.get(q, 'DelayedTimeFraction'))).filter(v => v !== null);
    const records = [
      ['当前显示 / 总轮次', rounds + ' / ' + allRounds + ' 轮'],
      ['负载与测量', !runs.length ? '缺少逐轮证据' : runs.every(r => r.success) ? '全部完成' : '存在失败'],
      ['最大采样间隔', maxGaps.length ? fmt(Math.max(...maxGaps), 1) + ' ms' : '旧报告未记录'],
      ['延迟间隔覆盖时长', fraction.length ? fmt(Math.max(...fraction) * 100) + '%（最差轮）' : '未记录'],
      ['文件身份复核', active().demo ? '合成示例' : active().kind === 'run' ? '见原始测量记录' : D.get(active().source, 'SourceFilesUnchanged') === true ? '通过' : '失败或未记录']
    ];
    for (const [label, value] of records) { const line = el('div', 'quality-item'); line.append(el('span', '', label), el('strong', '', value)); container.append(line); }
    const failures = active().runs.filter(r => !r.success).map(r => D.scenarioLabel(r.scenarioId) + ' 第' + r.round + '轮 ' + r.side + '：' + (r.error || '测量失败'));
    const warnings = [...failures, ...active().warnings, ...quality.flatMap(q => D.get(q, 'Warnings', [])), ...(scenario().notes ? [scenario().notes] : [])];
    text('quality-note', [...new Set(warnings)].slice(0, 4).join(' ') || '每个指标的区间为描述性估计。系统负载、缓存与调度仍可能影响结果，不据此推断代码因果关系。');
  }
  function renderRuns() {
    const runs = filteredRuns(), body = $('runs-body'); body.replaceChildren(); text('run-count', runs.length + ' 次测量');
    for (const run of runs) {
      const row = el('tr'), side = el('td'); side.append(el('span', 'side-dot' + (run.side === 'Baseline' ? ' a' : '')), document.createTextNode(run.side === 'Baseline' ? '基线 A' : '待测 B'));
      row.append(el('td', '', '#' + run.order + ' · 第 ' + run.round + ' 轮'), side,
        el('td', '', fmt(D.get(run.metrics, 'DurationSeconds'), 3) + ' s'), el('td', '', fmt(D.get(run.metrics, 'TotalCpuTimeMs'), 3) + ' ms'),
        el('td', '', D.metric(run.metrics, 'PrivateBytes') === null ? '—' : fmt(D.metric(run.metrics, 'PrivateBytes') / MB) + ' MB'), el('td', '', fmt(D.get(run.metrics, 'SampleCount'), 0)));
      const status = el('td'); status.append(badge(run.status)); status.title = run.error; row.append(status); body.append(row);
    }
    if (!runs.length) { const row = el('tr'), cell = el('td', '', '原始报告未提供逐轮证据。'); cell.colSpan = 7; row.append(cell); body.append(row); }
    // Render only on explicit expansion; large observations need not occupy the DOM while closed.
    $('raw-evidence').textContent = '';
    $('details-panel').ontoggle = () => {
      if ($('details-panel').open) text('raw-evidence', JSON.stringify({ Protocol: active().protocol, Environment: active().environment, Limitations: active().warnings,
        ScenarioNotes: scenario().notes, FailedRunsAcrossReport: active().runs.filter(r => !r.success).map(r => ({ Scenario: r.scenarioId, Round: r.round, Side: r.side, Error: r.error })), Runs: filteredRuns().map(r => ({ Order: r.order, Round: r.round, Side: r.side, Error: r.error, SamplingQuality: r.quality, Observations: r.observations })) }, null, 2));
    };
    if ($('details-panel').open) $('details-panel').ontoggle();
  }
  function render() {
    const model = active();
    text('dataset-title', model.title); text('data-tag', model.demo ? '合成示例' : model.kind === 'comparison' ? '版本对比' : '单目标测量'); $('data-tag').className = 'dataset-tag' + (!model.demo ? ' real' : '');
    const date = new Date(model.timestamp); text('dataset-date', !model.timestamp || Number.isNaN(date.getTime()) ? model.sourceName : date.toLocaleString('zh-CN', { hour12: false }));
    workspace.render(state.view);renderBanner();
    if(state.view==='overview')renderOverview();
    else {
      renderFilters();
      if(state.view==='details'){renderCards();renderRoundChart();renderIntervals();renderChart();renderComparison();renderQuality();}
      else renderRuns();
    }
    document.querySelectorAll('[data-metric]').forEach(b => { const selected = b.dataset.metric === state.metric; b.classList.toggle('selected', selected); b.setAttribute('aria-pressed', String(selected)); });
  }
  const workspace=window.BenchmarkWorkspace.create({getModel:active,format:fmt,setView:view=>{state.view=view;render();},selectScenario:id=>{state.scenario=id;state.round='all';state.view='details';render();window.scrollTo({top:0,behavior:'auto'});}});
  $('scenario-select').onchange = event => { state.scenario = event.target.value; state.round = 'all'; render(); };
  $('round-select').onchange = event => { state.round = event.target.value; render(); };
  document.querySelectorAll('[data-metric]').forEach(button => { button.onclick = () => { state.metric = button.dataset.metric; render(); }; });
  try {
    const embedded = JSON.parse($('report-data').textContent);
    state.reports.push(embedded ? D.normalize(embedded, 'report.json') : D.demo());
  } catch (error) { $('report-error').hidden=false;text('report-error','报告读取失败：'+error.message+'。请由执行 AI 重新生成；不使用示例替代。');return; }
  resetSelection(); render();
  if (typeof ResizeObserver !== 'undefined') new ResizeObserver(() => {
    if (state.view==='details' && $('chart').clientWidth>0 && Math.abs($('chart').clientWidth - renderedChartWidth) > 2) { renderChart();renderRoundChart();renderIntervals(); }
  }).observe($('chart'));
})();
