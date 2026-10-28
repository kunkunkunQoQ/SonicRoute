const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { spawn } = require('node:child_process');
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
async function importFile(file) {
  const { root: document } = await call('DOM.getDocument');
  const { nodeId } = await call('DOM.querySelector', { nodeId: document.nodeId, selector: '#file-input' });
  await call('DOM.setFileInputFiles', { nodeId, files: [file] });
  await evaluate("document.getElementById('file-input').dispatchEvent(new Event('change',{bubbles:true}))");
  await waitFor(() => evaluate("document.getElementById('import-message').textContent.startsWith('已载入') || document.getElementById('import-message').textContent.startsWith('无法导入')"), 'file import');
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
    await call('Page.navigate', { url: pathToFileURL(path.join(viewer, 'index.html')).href });
    await waitFor(() => evaluate("document.getElementById('metric-grid').children.length===4"), 'viewer rendering');
    await check('synthetic example is prominently marked', "document.getElementById('verdict-pill').textContent==='SYNTHETIC DEMO'");
    await check('desktop has no page overflow', 'document.documentElement.scrollWidth<=innerWidth');
    await check('overview exposes AI workspace and all-scene paired bars', "!document.getElementById('ai-panel').closest('[hidden]') && document.querySelectorAll('.landscape-row').length===3 && document.getElementById('evidence').hidden");
    await check('paired bars visibly encode different measured lengths', "(()=>{const bars=document.querySelectorAll('.landscape-fill');return bars[0].getBoundingClientRect().width>bars[1].getBoundingClientRect().width && bars[1].getBoundingClientRect().width>0})()");
    await check('recognizable local SVG icons accompany action labels', "document.querySelector('#ai-request svg')!==null && document.querySelector('#import-button svg')!==null && document.querySelector('#ai-request').textContent.includes('AI')");
    await screenshot('desktop.png');
    await evaluate("document.getElementById('ai-request').click()");
    await waitFor(() => evaluate("document.getElementById('ai-dialog').open"), 'AI analysis material');
    await check('AI material contains bound report and real round evidence', "document.getElementById('ai-text').value.includes('ReportSha256') && document.getElementById('ai-text').value.includes('SamplingQuality')");
    await evaluate("document.getElementById('ai-dialog-close').click();document.getElementById('ai-paste').click();document.getElementById('ai-model').value='Browser QA model';document.getElementById('ai-text').value='合成示例的 AI 阅读测试 <img src=x onerror=window.PWNED=1>';document.getElementById('ai-apply').click()");
    await waitFor(() => evaluate("!document.getElementById('ai-dialog').open"), 'AI paste');
    await check('AI summary displays model provenance and renders hostile text safely', "document.getElementById('ai-content').textContent.includes('Browser QA model') && document.getElementById('ai-state').textContent==='已附加 AI 解读' && !window.PWNED && !document.querySelector('img[src=x]')");
    await evaluate("document.getElementById('ai-paste').click();document.getElementById('ai-text').value=JSON.stringify({Schema:'BenchmarkLab.AIAnalysis.v1',ReportSha256:'0'.repeat(64)});document.getElementById('ai-apply').click()");
    await waitFor(() => evaluate("document.getElementById('ai-dialog-message').textContent.includes('另一份报告')"), 'mismatched AI identity');
    await check('mismatched AI summary preserves previous interpretation', "document.getElementById('ai-content').textContent.includes('Browser QA model') && document.getElementById('ai-dialog').open");
    await evaluate("document.getElementById('ai-dialog-close').click();document.getElementById('landscape-metric').value='memory';document.getElementById('landscape-metric').dispatchEvent(new Event('change'))");
    await evaluate("document.getElementById('ai-paste').click();document.getElementById('ai-model').value='QA 示例';document.getElementById('ai-text').value='这是合成数据，仅用于展示 AI 解读区域。真实结论应结合轮次、采样质量与置信区间。';document.getElementById('ai-apply').click()");
    await waitFor(()=>evaluate("!document.getElementById('ai-dialog').open"),'clean summary preview');
    await check('overview metric selector updates paired-bar units', "document.querySelector('.landscape-number').textContent.includes('MB')");
    await call('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
    await check('mobile overview and AI panel remain within viewport', 'document.documentElement.scrollWidth<=innerWidth');
    await screenshot('mobile-overview.png');
    await call('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1080, deviceScaleFactor: 1, mobile: false });
    await check('all six same-scenario runs and resource chart render', "document.querySelectorAll('#runs-body tr').length===6 && document.querySelectorAll('#chart polyline').length===6");
    await check('scenario overview and round distribution display real report scope', "document.querySelectorAll('.scene-card').length===3 && document.querySelectorAll('#round-chart circle').length===6");
    await check('confidence chart renders structured example intervals', "document.querySelectorAll('#interval-chart circle').length===4");
    await evaluate("document.querySelectorAll('.scene-card')[1].click()");
    await check('clicking a scenario card updates the shared filter', "document.getElementById('scenario-select').value==='rules_scaling_10'");
    await evaluate("document.querySelectorAll('.scene-card')[0].click()");
    await check('scene opens focused charts view', "!document.getElementById('trends').hidden && document.querySelector('.decision-grid').hidden");
    await screenshot('desktop-details.png');
    await evaluate("document.getElementById('round-select').value='2';document.getElementById('round-select').dispatchEvent(new Event('change'))");
    await check('round filter displays only its two runs', "document.querySelectorAll('#runs-body tr').length===2 && document.getElementById('comparison-body').textContent.includes('单轮观察')");
    await check('single round does not fabricate confidence intervals', "document.querySelectorAll('#interval-chart circle').length===0 && document.getElementById('interval-chart').textContent.includes('不根据单轮')");
    await evaluate("document.querySelector('[data-metric=memory]').click()");
    await check('memory control changes chart metric and accessible pressed state', "document.querySelector('[data-metric=memory]').getAttribute('aria-pressed')==='true' && document.getElementById('chart-subtitle').textContent.includes('MB')");
    await evaluate("document.getElementById('scenario-select').value='target_absent';document.getElementById('scenario-select').dispatchEvent(new Event('change'))");
    await check('scene filter resets round and refreshes evidence', "document.getElementById('round-select').value==='all' && document.getElementById('chart-subtitle').textContent.includes('目标应用不存在')");
    await call('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
    await check('mobile remains within viewport', 'document.documentElement.scrollWidth<=innerWidth');
    await screenshot('mobile.png');
    await call('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1080, deviceScaleFactor: 1, mobile: false });
    const fixture = D.demo().source; delete fixture.SyntheticExample; fixture.CandidateTarget = 'QA FIXTURE - NOT PRODUCT DATA.exe'; fixture.Runs[1].Result.Success = false; fixture.Runs[1].Result.ErrorMessage = '<img src=x onerror="window.PWNED=1">';
    const file = path.join(output, 'failed-fixture.json'); fs.writeFileSync(file, JSON.stringify(fixture));
    await importFile(file);
    await check('new report cannot inherit previous AI interpretation', "document.getElementById('ai-state').textContent==='等待 AI 解读'");
    await check('failed imported report does not display PASS', "document.getElementById('verdict-pill').textContent==='EXECUTION ERROR'");
    await check('hostile observation text creates no DOM or script execution', "!window.PWNED && !document.querySelector('img[src=x]')");
    await evaluate("document.getElementById('round-select').value='3';document.getElementById('round-select').dispatchEvent(new Event('change'))");
    await check('filtering successful rounds retains failure notice', "document.getElementById('verdict-pill').textContent==='EXECUTION ERROR' && document.getElementById('quality-note').textContent.includes('测量失败')===false && document.getElementById('quality-note').textContent.includes('onerror')");
    const invalid = path.join(output, 'invalid.json'); fs.writeFileSync(invalid, '{invalid');
    await importFile(invalid);
    await check('invalid JSON preserves current dataset and explains the error', "document.getElementById('import-message').textContent.startsWith('无法导入') && document.getElementById('verdict-pill').textContent==='EXECUTION ERROR'");
    const wrap = path.join(output, 'exported-example.json'); fs.writeFileSync(wrap, JSON.stringify({ ViewerSchemaVersion: 1, SyntheticExample: true, Source: D.demo().source }));
    await importFile(wrap);
    await check('reimported example retains prominent synthetic label', "document.getElementById('verdict-pill').textContent==='SYNTHETIC DEMO'");
    const annotated=await evaluate("(async()=>{const m=BenchmarkData.demo();return {ViewerSchemaVersion:1,Source:m.source,Analysis:{Schema:BenchmarkAnalysis.schema,ReportSha256:await BenchmarkAnalysis.fingerprint(m),Author:{Kind:'ai',Model:'Import QA model'},Summary:'合成示例，仅验证 AI 解读往返。',Findings:[],Limitations:['不是产品测量'],Recommendations:[]}}})()");
    const annotatedFile=path.join(output,'annotated-example.json');fs.writeFileSync(annotatedFile,JSON.stringify(annotated));
    await importFile(annotatedFile);
    await waitFor(()=>evaluate("document.getElementById('ai-content').textContent.includes('Import QA model')"),'bound AI summary reimport');
    await check('reimported AI interpretation retains synthetic identity and limitations',"document.getElementById('verdict-pill').textContent==='SYNTHETIC DEMO' && document.getElementById('ai-content').textContent.includes('不是产品测量')");
    if (process.argv[4]) {
      await call('Page.navigate', { url: pathToFileURL(path.resolve(process.argv[4])).href });
      await waitFor(() => evaluate("document.getElementById('metric-grid').children.length===4"), 'standalone embedded report');
      await check('generated standalone report loads its own data without example fallback', "document.getElementById('verdict-pill').textContent!=='SYNTHETIC DEMO' && !document.getElementById('import-message').textContent.includes('内嵌报告读取失败')");
      await check('generated standalone report uses no external scripts or style files', "document.querySelectorAll('script[src],link[rel=stylesheet]').length===0");
      await screenshot('embedded-report.png');
    }
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
