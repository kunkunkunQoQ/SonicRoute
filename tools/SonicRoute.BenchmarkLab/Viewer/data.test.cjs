const { test } = require('node:test');
const assert = require('node:assert/strict');
const D = require('./data.js');
const clone = value => JSON.parse(JSON.stringify(value));
const demoRaw = () => { const raw = clone(D.demo().source); delete raw.SyntheticExample; return raw; };

test('ready report loads AI-authored summary and history without altering the raw verdict', () => {
  const hash='a'.repeat(64),review={Schema:'BenchmarkLab.AIReview.v1',Status:'complete',ReportSha256:hash,Author:{Kind:'ai',Model:'QA'},GeneratedUtc:'2026-10-29T00:00:00Z',Summary:'仅测试报告呈现',Findings:[],Limitations:['合成数据'],Recommendations:[]};
  const model=D.normalize({ViewerSchemaVersion:1,Source:D.demo().source,ReportSha256:hash,Review:review,ReviewHistory:[{Number:1,Review:review}]});
  assert.equal(model.review.Summary,review.Summary);assert.equal(model.reviewHistory.length,1);assert.equal(model.status,'pass');assert.equal(model.demo,true);
  assert.throws(()=>D.normalize({ViewerSchemaVersion:1,Source:D.demo().source,ReportSha256:'b'.repeat(64),Review:review}),/不匹配/);
  review.Findings=[{Title:'错误引用',Detail:'测试',ScenarioId:'missing'}];
  assert.throws(()=>D.normalize({ViewerSchemaVersion:1,Source:D.demo().source,ReportSha256:hash,Review:review}),/不匹配/);
});

test('example survives export/import with its visible synthetic identity', () => {
  const demo = D.demo();
  const [restored] = D.importDocuments([{ name: 'example.json', data: { ViewerSchemaVersion: 1, SyntheticExample: true, Source: demo.source } }]);
  assert.equal(restored.demo, true);
  assert.equal(D.normalize(demo.source).demo, true);
});
test('complete three-round report retains its result and raw sample data', () => {
  const raw = demoRaw(), model = D.normalize(raw);
  assert.equal(model.status, 'pass'); assert.equal(model.demo, false);
  assert.equal(model.runs.length, 18); assert.equal(model.runs[0].samples.length, 151);
  assert.equal(model.source, raw);
});
test('failure and changed source override PASS', () => {
  const raw = demoRaw(); raw.Runs[1].Result.Success = false; raw.Runs[1].Result.ErrorMessage = 'Load missing';
  const model = D.normalize(raw); assert.equal(model.status, 'error'); assert.equal(model.runs[1].error, 'Load missing');
  const changed = demoRaw(); changed.SourceFilesUnchanged = false; assert.equal(D.normalize(changed).status, 'error');
});
test('confirmed regression is preserved when another scenario has sampling uncertainty', () => {
  const raw = demoRaw(); raw.OverallVerdict = 1; raw.Scenarios[0].Verdict = 1;
  raw.Runs.at(-1).Result.Metrics.SamplingQuality.SuitableForComparison = false;
  assert.equal(D.normalize(raw).status, 'regression');
  raw.OverallVerdict = 0; assert.equal(D.normalize(raw).status, 'inconclusive');
});
test('missing rounds and inconsistent scenario result cannot produce a displayed PASS', () => {
  const raw = demoRaw(); raw.Runs = raw.Runs.filter(r => r.Round === 1); assert.equal(D.normalize(raw).status, 'inconclusive');
  const contradiction = demoRaw(); contradiction.Scenarios[0].Verdict = 2; assert.equal(D.normalize(contradiction).status, 'inconclusive');
});
test('historical report reads independent baseline rounds without requiring current baseline runs', () => {
  const raw = demoRaw(); raw.UsesStoredBaseline = true; raw.BaselineRounds = {};
  for (const s of raw.Scenarios) raw.BaselineRounds[s.ScenarioId] = raw.Runs.filter(r => r.Side === 'Baseline' && r.Result.ScenarioId === s.ScenarioId).map(r => r.Result.Metrics);
  raw.Runs = raw.Runs.filter(r => r.Side === 'Candidate');
  assert.equal(D.normalize(raw).status, 'pass');
});
test('A/A control never displays a product regression', () => {
  const raw = demoRaw(); raw.IsAaControl = true; raw.OverallVerdict = 1;
  const model = D.normalize(raw); assert.equal(model.status, 'inconclusive'); assert.match(model.warnings[0], /A\/A/);
});
test('legacy numeric and string enums remain readable without treating unknown values as PASS', () => {
  const raw = demoRaw(); raw.SchemaVersion = 2; delete raw.Protocol;
  assert.equal(D.normalize(raw).status, 'pass'); assert.ok(D.normalize(raw).warnings.length);
  raw.OverallVerdict = 'Inconclusive'; assert.equal(D.normalize(raw).status, 'inconclusive');
  raw.OverallVerdict = 'unexpected'; assert.equal(D.normalize(raw).status, 'inconclusive');
});
test('run evidence, metadata and summary import as one measured dataset', () => {
  const raw = demoRaw(), results = raw.Runs.filter(r => r.Side === 'Candidate').map(r => r.Result);
  const summary = Object.fromEntries(raw.Scenarios.map(s => [s.ScenarioId, s.CandidateSummary]));
  const models = D.importDocuments([{ name: 'run_evidence.json', data: results }, { name: 'run_metadata.json', data: { Target: 'test.exe', TargetFiles: { executable: 'hash' }, Tier: 'Quick', Protocol: { Revision: 'test' } } }, { name: 'run_summary.json', data: summary }]);
  assert.equal(models.length, 1); assert.equal(models[0].status, 'measured'); assert.equal(models[0].title, 'test.exe');
  assert.deepEqual(models[0].runs.filter(r => r.scenarioId === 'idle_empty').map(r => r.round), [1, 2, 3]);
  assert.equal(models[0].scenarios[0].candidate.TotalCpuTimeMs, summary.idle_empty.TotalCpuTimeMs);
});
test('summary only does not fabricate samples or a passing verdict', () => {
  const raw = demoRaw(), summary = { idle_empty: raw.Scenarios[0].CandidateSummary };
  const model = D.normalize(summary, 'run_summary.json');
  assert.equal(model.status, 'inconclusive'); assert.equal(model.runs.length, 0);
});
test('malformed and reduced AI summaries fail import without inventing metrics', () => {
  for (const input of [null, {}, { Status: 'PASS', Rounds: 3 }, []]) assert.throws(() => D.normalize(input));
  assert.throws(() => D.importDocuments([{ name: 'run_metadata.json', data: { TargetFiles: {}, Tier: 'Quick' } }]));
  assert.equal(D.num(null), null); assert.equal(D.num('0'), null); assert.equal(D.num(0), 0);
});
test('HTML-like report text remains data with no evaluation', () => {
  const raw = demoRaw(); raw.CandidateTarget = '</script><script>global.pwned=1</script>.exe';
  raw.Runs[0].Result.Observations = { '<img src=x onerror=alert(1)>': '<script>payload</script>' };
  const model = D.normalize(raw); assert.equal(model.candidate, raw.CandidateTarget); assert.equal(global.pwned, undefined);
});
test('round distribution preserves zeros, ignores missing values and does not invent zero variation for one round', () => {
  const s=D.distribution([0,2,4,null]);assert.equal(s.count,3);assert.equal(s.median,2);assert.equal(s.mean,2);assert.equal(s.sd,2);assert.equal(s.cv,1);
  assert.equal(D.distribution([3]).sd,null);assert.equal(D.distribution([0,0,0]).cv,null);assert.equal(D.distribution([]).mean,null);
});
