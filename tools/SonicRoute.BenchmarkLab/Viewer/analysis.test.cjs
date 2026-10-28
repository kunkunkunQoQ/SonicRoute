const { test } = require('node:test');
const assert = require('node:assert/strict');
const A = require('./analysis.js');
const D = require('./data.js');
const draft = hash => ({ Schema:A.schema, ReportSha256:hash, Author:{Kind:'ai',Model:'QA model'}, Summary:'合成示例，不能推断产品性能。', Findings:[{Title:'示例',Detail:'仅验证呈现。'}], Limitations:['合成数据'], Recommendations:[] });

test('report fingerprint ignores property order but binds source and metadata', async () => {
  assert.equal(A.canonical({b:2,a:1}), A.canonical({a:1,b:2}));
  const m=D.demo(), hash=await A.fingerprint(m);
  assert.match(hash,/^[a-f0-9]{64}$/);
  assert.equal(hash,await A.fingerprint({...m,source:JSON.parse(JSON.stringify(m.source))}));
  assert.notEqual(hash,await A.fingerprint({...m,metadata:{Changed:true}}));
});
test('AI summary survives export and normalized reimport without changing verdict', async () => {
  const m=D.demo(),hash=await A.fingerprint(m),status=m.status;
  const checked=A.validate(draft(hash),hash,m.scenarios.map(s=>s.id));
  const restored=D.normalize({ViewerSchemaVersion:1,Source:m.source,Analysis:checked});
  assert.equal(await A.fingerprint(restored),hash);
  assert.deepEqual(A.validate(restored.pendingAnalysis,hash,restored.scenarios.map(s=>s.id)),checked);
  assert.equal(restored.status,status);
});
test('rejects unrelated report, rule formatter output and missing model', async () => {
  const hash=await A.fingerprint(D.demo());
  assert.throws(()=>A.validate(draft('0'.repeat(64)),hash),/另一份报告/);
  assert.throws(()=>A.validate({Summary:'tool formatter'},hash),/规则摘要/);
  const raw=draft(hash);raw.Author.Model='';assert.throws(()=>A.validate(raw,hash),/模型来源/);
});
test('rejects unknown scene and excessive input; text remains text', async () => {
  const hash=await A.fingerprint(D.demo()),raw=draft(hash);
  raw.Findings[0].ScenarioId='missing';assert.throws(()=>A.validate(raw,hash,[]),/不存在/);
  raw.Findings=[];raw.Summary='x'.repeat(4001);assert.throws(()=>A.validate(raw,hash),/过长/);
  raw.Summary='<img src=x onerror=alert(1)>';assert.equal(A.validate(raw,hash).Summary,raw.Summary);
});
test('analysis request carries report identity and constraints without time-series bloat', async () => {
  const m=D.demo(),prompt=await A.request(m);
  assert.ok(prompt.includes(await A.fingerprint(m)));
  assert.ok(prompt.includes('SyntheticExample'));
  assert.ok(prompt.includes('不得把 A/A'));
  const packet=JSON.parse(prompt.split('原始汇总和逐轮证据：\n')[1]);
  assert.equal(packet.Runs.length,18);
  assert.equal(packet.Runs[0].Samples,undefined);
  assert.equal(packet.Runs[0].Observations,undefined);
});
