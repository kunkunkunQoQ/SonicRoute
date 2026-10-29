const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { spawn, execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../../..');
const viewer = path.join(root, 'tools/SonicRoute.BenchmarkLab/Viewer');
const D = require(path.join(viewer, 'data.js'));
if (!process.argv[2] || !process.argv[3]) throw new Error('Pass a dedicated temporary work directory and an evidence directory.');
const output = path.resolve(process.argv[2]);
const evidenceDir = path.resolve(process.argv[3]);
fs.mkdirSync(evidenceDir, { recursive: true });
fs.mkdirSync(output, { recursive: true });
const profile = fs.mkdtempSync(path.join(output, 'edge-profile-'));
const profileOwner = require('node:crypto').randomUUID();
fs.writeFileSync(path.join(profile, '.benchmarklab-profile-owner'), profileOwner);
const child = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', ['--headless=new', '--disable-gpu', '--disable-background-networking', '--disable-extensions', '--disable-component-update', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const exceptions = [], checks = [], pending = new Map();
let socket, nextId = 0;
async function waitFor(action, label) {
  for (let i = 0; i < 80; i++) { try { if (await action()) return; } catch {} await sleep(100); }
  throw new Error('Timed out: ' + label);
}
function call(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++nextId, timer = setTimeout(() => { pending.delete(id); reject(new Error(method + ' timed out')); }, 15000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const r = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text + ': ' + JSON.stringify(r.exceptionDetails));
  return r.result.value;
}
async function check(label, expression) { assert.equal(await evaluate(expression), true, label); checks.push(label); }
async function screenshot(name) {
  const layout = await call('Page.getLayoutMetrics');
  const r = await call('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true,
    clip: { x: 0, y: 0, width: layout.cssContentSize.width, height: layout.cssContentSize.height, scale: 1 } });
  fs.writeFileSync(path.join(evidenceDir, name), Buffer.from(r.data, 'base64'));
}
const tool=process.argv[5]||null;
function localPage(file,data){
  let html=fs.readFileSync(path.join(viewer,'index.html'),'utf8');
  html=html.replace('<link rel="stylesheet" href="style.css">',()=>'<style>'+fs.readFileSync(path.join(viewer,'style.css'),'utf8')+'</style>');
  html=html.replace('<script id="report-data" type="application/json">null</script>',()=>'<script id="report-data" type="application/json">'+JSON.stringify(data).replace(/</g,'\\u003c')+'</script>');
  for(const asset of ['data.js','icons.js','workspace.js','app.js'])html=html.replace('<script src="'+asset+'"></script>',()=>'<script>'+fs.readFileSync(path.join(viewer,asset),'utf8')+'</script>');
  fs.writeFileSync(file,html);return file;
}
function fixturePage(name,raw,reviewed=true){
  const dir=path.join(output,name);fs.mkdirSync(dir);
  fs.writeFileSync(path.join(dir,'report.json'),JSON.stringify(raw));
  if(tool){
    const command=extra=>JSON.parse(execFileSync(tool,['report','--input',dir,...extra,'--json'],{encoding:'utf8',windowsHide:true}));
    const prepared=command([]);
    if(!reviewed)return prepared.ReportHtml;
    const file=path.join(dir,'ai-review.json'),review=JSON.parse(fs.readFileSync(file,'utf8'));
    Object.assign(review,{Status:'complete',Author:{Kind:'ai',Model:'QA fixture model'},GeneratedUtc:new Date().toISOString(),Summary:'这是一份报告呈现测试：仅验证图表和 AI 总结的交付流程，不用于判断产品性能。',Findings:[{Title:'图表与结论一起交付',Detail:'打开报告就能阅读已写入的总结，并查看各场景的对比。',ScenarioId:raw.Scenarios[0].ScenarioId}],Limitations:['此报告使用 QA 夹具；合成数据不能代表产品提速。'],Recommendations:['实际测量按预先约定的轮数、基线和协议执行。']});
    fs.writeFileSync(file,JSON.stringify(review));const ready=command(['--finalize']);assert.equal(ready.ReviewStatus,'complete');return ready.ReportHtml;
  }
  const hash='a'.repeat(64),review=reviewed?{Schema:'BenchmarkLab.AIReview.v1',Status:'complete',ReportSha256:hash,Author:{Kind:'ai',Model:'QA fixture model'},GeneratedUtc:new Date().toISOString(),Summary:'浏览器 QA 夹具，仅验证呈现。',Findings:[],Limitations:['合成数据'],Recommendations:[]}:null;
  return localPage(path.join(dir,'report.html'),{ViewerSchemaVersion:1,Source:raw,ReportSha256:hash,Review:review,ReviewHistory:review?[{Number:1,Review:review}]:[]});
}
async function navigate(file){
  const url=pathToFileURL(path.resolve(file)).href;
  await call('Page.navigate',{url});
  await waitFor(()=>evaluate('location.href==='+JSON.stringify(url)+" && document.getElementById('ai-content')?.children.length>0"),'reader report');
}
(async () => {
  try {
    await waitFor(() => fs.existsSync(path.join(profile, 'DevToolsActivePort')), 'headless browser');
    const port = Number(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]);
    const targets = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
    socket = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (message.method === 'Runtime.exceptionThrown') exceptions.push(message.params.exceptionDetails);
      if (pending.has(message.id)) { const p = pending.get(message.id); clearTimeout(p.timer); pending.delete(message.id); message.error ? p.reject(new Error(JSON.stringify(message.error))) : p.resolve(message.result); }
    });
    await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
    await call('Runtime.enable'); await call('Page.enable');
    await call('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1080, deviceScaleFactor: 1, mobile: false });
    const readyPage=fixturePage('ready',D.demo().source);
    await navigate(readyPage);
    await check('synthetic identity is prominent even with a completed AI review',"document.getElementById('verdict-pill').textContent==='SYNTHETIC DEMO'");
    await check('reader immediately sees AI summary without manual steps',"document.getElementById('ai-state').textContent==='已完成' && document.getElementById('ai-content').textContent.includes('QA fixture model')");
    await check('reader has no upload, paste, model-entry or editing workflow',"document.querySelectorAll('input,textarea,dialog').length===0 && !document.getElementById('ai-request') && !document.querySelector('.sidebar')");
    await check('report works offline with every script and stylesheet embedded',"document.querySelectorAll('script[src],link[rel=stylesheet]').length===0");
    await check('desktop stays within viewport','document.documentElement.scrollWidth<=innerWidth');
    await check('overview has paired bars for all scenarios and hides raw tables',"document.querySelectorAll('.landscape-row').length===3 && document.getElementById('evidence').hidden && document.getElementById('review-history').hidden");
    await check('overview avoids building hidden detailed charts',"document.querySelectorAll('#chart svg,#round-chart svg,#interval-chart svg,#runs-body tr').length===0");
    await check('paired bars visibly encode different measured lengths',"(()=>{const bars=document.querySelectorAll('.landscape-fill');return bars[0].getBoundingClientRect().width>bars[1].getBoundingClientRect().width && bars[1].getBoundingClientRect().width>0})()");
    await check('AI summary is visible in the first screen',"document.getElementById('ai-content').getBoundingClientRect().top<innerHeight");
    await check('navigation and summary use recognizable SVG icons',"document.querySelectorAll('.view-tabs .ui-icon').length===3 && !!document.querySelector('.ai-panel h2 svg')");
    await screenshot('desktop.png');
    await call('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
    await check('mobile overview stays within viewport','document.documentElement.scrollWidth<=innerWidth');
    await screenshot('mobile.png');
    await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1080,deviceScaleFactor:1,mobile:false});
    await evaluate("document.getElementById('landscape-metric').value='memory';document.getElementById('landscape-metric').dispatchEvent(new Event('change'))");
    await check('metric selector changes overview units',"document.querySelector('.landscape-number').textContent.includes('MB')");
    await evaluate("document.querySelectorAll('.landscape-row')[1].click()");
    await check('clicking a scenario opens its detailed charts',"document.getElementById('scenario-select').value==='rules_scaling_10' && !document.getElementById('trends').hidden && document.querySelector('.decision-grid').hidden");
    await check('same-scenario round distribution and six curves retain evidence',"document.querySelectorAll('#round-chart circle').length===6 && document.querySelectorAll('#chart polyline').length===6");
    await check('structured intervals appear in detailed view',"document.querySelectorAll('#interval-chart circle').length===4");
    await screenshot('desktop-details.png');
    await evaluate("document.getElementById('round-select').value='2';document.getElementById('round-select').dispatchEvent(new Event('change'))");
    await check('one round does not fabricate a confidence interval',"document.querySelectorAll('#interval-chart circle').length===0 && document.getElementById('interval-chart').textContent.includes('不根据单轮')");
    await evaluate("document.querySelector('[data-metric=memory]').click()");
    await check('resource control selects memory accessibly',"document.querySelector('[data-metric=memory]').getAttribute('aria-pressed')==='true' && document.getElementById('chart-subtitle').textContent.includes('MB')");
    await evaluate("document.querySelector('[data-view=evidence]').click()");
    await check('evidence view contains selected round records and AI log',"!document.getElementById('evidence').hidden && !document.getElementById('review-history').hidden && document.querySelectorAll('#runs-body tr').length===2 && document.getElementById('review-log').textContent.includes('QA fixture model')");
    await call('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
    await check('mobile evidence scrolls tables without overflowing page','document.documentElement.scrollWidth<=innerWidth');
    await screenshot('mobile-evidence.png');
    await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1080,deviceScaleFactor:1,mobile:false});
    const failed=D.demo().source;delete failed.SyntheticExample;failed.Runs[1].Result.Success=false;failed.Runs[1].Result.ErrorMessage='<img src=x onerror="window.PWNED=1">';
    await navigate(fixturePage('failed',failed));
    await check('completed AI summary cannot hide a failed raw measurement',"document.getElementById('ai-state').textContent==='已完成' && document.getElementById('verdict-pill').textContent==='EXECUTION ERROR'");
    await evaluate("document.querySelector('[data-view=details]').click();document.getElementById('round-select').value='3';document.getElementById('round-select').dispatchEvent(new Event('change'))");
    await check('successful-round filter still retains report failure',"document.getElementById('verdict-pill').textContent==='EXECUTION ERROR' && document.getElementById('quality-note').textContent.includes('onerror') && !window.PWNED && !document.querySelector('img[src=x]')");
    const attacked={ViewerSchemaVersion:1,Source:D.demo().source,ReportSha256:'a'.repeat(64),Review:{Schema:'BenchmarkLab.AIReview.v1',ReportSha256:'a'.repeat(64),Status:'complete',Author:{Kind:'ai',Model:'QA injection test'},GeneratedUtc:new Date().toISOString(),Summary:'<img src=x onerror="window.PWNED=1">',Findings:[],Limitations:[],Recommendations:[]}};
    const attackFile=localPage(path.join(output,'hostile-review.html'),attacked);await navigate(attackFile);
    await check('AI-authored markup is displayed as text',"document.getElementById('ai-content').textContent.includes('onerror') && !window.PWNED && !document.querySelector('img[src=x]')");
    await navigate(fixturePage('pending',D.demo().source,false));
    await check('pending review is explicit and offers no user editing task',"document.getElementById('ai-state').textContent==='未完成' && document.getElementById('ai-content').textContent.includes('实际测试报告由 AI') && document.querySelectorAll('input,textarea,dialog').length===0");
    const invalid=localPage(path.join(output,'invalid.html'),{ViewerSchemaVersion:1,Source:{invalid:true}});
    await call('Page.navigate',{url:pathToFileURL(invalid).href});
    await waitFor(()=>evaluate("document.getElementById('report-error') && !document.getElementById('report-error').hidden"),'invalid report notice');
    await check('invalid report fails visibly without silently showing a demo',"document.getElementById('report-error').textContent.includes('报告读取失败') && document.getElementById('verdict-pill').textContent!== 'SYNTHETIC DEMO'");
    if(process.argv[4]){await navigate(process.argv[4]);await check('generated tool fixture loads original embedded data',"document.getElementById('verdict-pill').textContent!=='SYNTHETIC DEMO' && document.getElementById('report-error').hidden");await screenshot('embedded-report.png');}
    assert.deepEqual(exceptions, []); checks.push('no browser JavaScript exceptions');
  } finally {
    if (socket?.readyState === 1) { try { await call('Browser.close'); } catch {} socket.close(); }
    await sleep(300); if (child.exitCode === null) child.kill();
    await waitFor(() => child.exitCode !== null || child.signalCode !== null, 'owned browser exit');
    // Delete only this freshly created profile; never a user's Edge profile.
    const resolvedProfile = path.resolve(profile);
    assert.equal(path.dirname(resolvedProfile), path.resolve(output));
    assert.equal(fs.readFileSync(path.join(resolvedProfile, '.benchmarklab-profile-owner'), 'utf8'), profileOwner);
    const directories = [resolvedProfile];
    while (directories.length) for (const entry of fs.readdirSync(directories.pop(), { withFileTypes: true })) {
      assert.equal(entry.isSymbolicLink(), false, 'linked profile child prevents cleanup');
      if (entry.isDirectory()) directories.push(path.join(entry.parentPath || entry.path, entry.name));
    }
    fs.rmSync(resolvedProfile, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
    for (const p of pending.values()) { clearTimeout(p.timer); p.reject(new Error('Browser closed')); }
  }
  fs.writeFileSync(path.join(evidenceDir, 'checks.json'), JSON.stringify({ passed: checks.length, checks, exceptions, BrowserExited: true, ProfileRemoved: !fs.existsSync(profile) }, null, 2));
  console.log(JSON.stringify({ passed: checks.length, screenshots: ['desktop.png', 'mobile.png'], output: evidenceDir, BrowserExited: true, ProfileRemoved: !fs.existsSync(profile) }));
})().catch(error => { console.error(error.stack); process.exitCode = 1; });
