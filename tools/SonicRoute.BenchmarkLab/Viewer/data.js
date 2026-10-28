(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.BenchmarkData = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  const MAX_SAMPLES = 500000;
  const own = (o, k) => !!o && Object.prototype.hasOwnProperty.call(o, k);
  const get = (o, k, fallback = null) => own(o, k) ? o[k] : own(o, k[0].toLowerCase() + k.slice(1)) ? o[k[0].toLowerCase() + k.slice(1)] : fallback;
  const num = v => typeof v === 'number' && Number.isFinite(v) ? v : null;
  const list = v => Array.isArray(v) ? v : [];
  const filename = v => String(v || '').split(/[\\/]/).pop() || '未命名目标';
  const avg = values => { const v = values.filter(x => num(x) !== null); return v.length ? v.reduce((a, b) => a + b, 0) / v.length : null; };
  const metric = (s, key, stat = 'Mean') => num(get(get(s, key), stat));
  const labels = { idle_empty: '后台空闲 · 0 条规则', rules_scaling_1: '规则负载 · 1 条', rules_scaling_10: '规则负载 · 10 条', rules_scaling_100: '规则负载 · 100 条', target_absent: '目标应用不存在', app_lifecycle_churn: '应用启动 / 退出', fast_foreground_switch: '快速切换前台', long_delay_step: '长延时步骤', rule_save_hotkeys: '规则与快捷键保存', schedule_rule_churn: '定时规则更新', window_tray_transitions: '托盘 → 主窗口', endurance_leak_trend: '长时资源趋势', generic_process: '通用进程' };
  const scenarioLabel = id => labels[id] || id || '未标记场景';
  function verdict(value, kind = 'overall') {
    const numeric = kind === 'metric' ? ['stable', 'improved', 'regression', 'inconclusive', 'unmeasured'] : ['pass', 'regression', 'inconclusive'];
    if (typeof value === 'number') return numeric[value] || 'inconclusive';
    const key = String(value || '').replace(/[_\s-]/g, '').toLowerCase();
    return ({ pass: 'pass', stable: 'stable', improved: 'improved', regressed: 'regression', regression: 'regression', regressiondetected: 'regression', inconclusive: 'inconclusive', unmeasured: 'unmeasured', executionerror: 'error' })[key] || 'inconclusive';
  }
  function normalizeRun(entry, index, side = 'Candidate') {
    const result = get(entry, 'Result', entry), metrics = get(result, 'Metrics', {});
    const samples = list(get(result, 'Timeseries'));
    if (samples.length > MAX_SAMPLES) throw new Error('单轮样本过多，请分段导入。');
    const duration = num(get(metrics, 'DurationSeconds'));
    const valid = get(result, 'Success') === true && get(metrics, 'MeasurementValid') === true
      && get(metrics, 'ProcessExitedDuringMeasurement') !== true && duration >= 0.5 && num(get(metrics, 'SampleCount')) >= 3;
    const quality = get(metrics, 'SamplingQuality');
    return { order: num(get(entry, 'Order')) ?? index + 1, round: num(get(entry, 'Round')) ?? null,
      side: get(entry, 'Side', side) === 'Baseline' ? 'Baseline' : 'Candidate',
      scenarioId: String(get(result, 'ScenarioId', 'unknown')), metrics, samples,
      success: valid, error: String(get(result, 'ErrorMessage', get(metrics, 'MeasurementError', '')) || ''),
      observations: get(result, 'Observations', {}), quality,
      status: !valid ? 'error' : get(quality, 'SuitableForComparison') === false ? 'inconclusive' : 'measured' };
  }
  function inferRounds(runs) {
    const counts = new Map();
    for (const run of runs) {
      const key = run.scenarioId + ':' + run.side;
      const next = (counts.get(key) || 0) + 1;
      counts.set(key, next);
      if (run.round === null) run.round = next;
    }
    return runs;
  }
  function normalizedScenario(raw, id) {
    return { id: String(get(raw, 'ScenarioId', id)), name: String(get(raw, 'ScenarioName', scenarioLabel(id))),
      baseline: get(raw, 'BaselineSummary'), candidate: get(raw, 'CandidateSummary'),
      verdict: verdict(get(raw, 'Verdict')), notes: String(get(raw, 'Notes', '')),
      comparisons: list(get(raw, 'MetricComparisons')).map(m => ({ name: String(get(m, 'MetricName', '未命名指标')),
        unit: String(get(m, 'Unit', '')), a: num(get(m, 'BaselineValue')), b: num(get(m, 'CandidateValue')),
        delta: num(get(m, 'DeltaAbsolute')), percent: get(m, 'DeltaPercentDefined') === false ? null : num(get(m, 'DeltaPercent')),
        status: verdict(get(m, 'Verdict'), 'metric'), details: String(get(m, 'Details', '')), interval: get(m, 'RoundEvidence') })) };
  }
  function combineMetrics(runs) {
    if (!runs.length) return null;
    const summaries = runs.map(r => r.metrics), result = {};
    for (const key of ['DurationSeconds', 'TotalCpuTimeMs', 'SampleCount', 'PeakWorkingSetBytes', 'MonitorSelfCpuTimeMs']) result[key] = avg(summaries.map(s => num(get(s, key))));
    for (const key of ['WorkingSetBytes', 'PrivateBytes', 'ThreadCount', 'HandleCount']) result[key] = { Mean: avg(summaries.map(s => metric(s, key))) };
    return result;
  }
  function normalize(raw, sourceName = 'report.json', metadata = null, summary = null) {
    if (!raw || typeof raw !== 'object') throw new Error('报告不是有效的数据对象。');
    if (raw.ViewerSchemaVersion === 1 && raw.Source) {
      const restored = normalize(raw.Source, raw.SourceName || sourceName, raw.Metadata, raw.Summary);
      restored.demo = restored.demo || raw.SyntheticExample === true;
      return restored;
    }
    let model = { sourceName, source: raw, metadata, summary, demo: get(raw, 'SyntheticExample') === true, kind: 'comparison', scenarios: [], runs: [], warnings: [], protocol: get(raw, 'Protocol'),
      timestamp: get(raw, 'TimestampUtc', get(metadata, 'TimestampUtc', '')), baseline: get(raw, 'BaselineTarget', ''), candidate: get(raw, 'CandidateTarget', get(metadata, 'Target', '')),
      status: 'inconclusive', environment: get(raw, 'Environment', get(metadata, 'Environment', {})), schema: get(raw, 'SchemaVersion', get(metadata, 'SchemaVersion')) };
    if (Array.isArray(get(raw, 'Scenarios'))) {
      model.scenarios = raw.Scenarios ? raw.Scenarios.map(s => normalizedScenario(s, get(s, 'ScenarioId', 'unknown'))) : raw.scenarios.map(s => normalizedScenario(s, get(s, 'ScenarioId', 'unknown')));
      model.runs = inferRounds(list(get(raw, 'Runs')).map((r, i) => normalizeRun(r, i)));
      model.status = verdict(get(raw, 'OverallVerdict'));
      model.warnings = list(get(raw, 'Limitations')).map(String);
      if (get(raw, 'SourceFilesUnchanged') === false || model.runs.some(r => !r.success)) model.status = 'error';
      else if (model.status !== 'regression' && (!model.runs.length || !model.scenarios.length || model.runs.some(r => r.status === 'inconclusive'))) model.status = 'inconclusive';
      if (model.status === 'pass') {
        const incomplete = model.scenarios.some(s => {
          const a = model.runs.filter(r => r.scenarioId === s.id && r.side === 'Baseline');
          const b = model.runs.filter(r => r.scenarioId === s.id && r.side === 'Candidate');
          const historicalA = list(get(get(raw, 'BaselineRounds'), s.id));
          return s.verdict !== 'pass' || b.length < 3 || (get(raw, 'UsesStoredBaseline') === true ? historicalA.length < 3 : a.length < 3 || a.length !== b.length);
        });
        if (incomplete) { model.status = 'inconclusive'; model.warnings.push('整体判定与逐轮 / 场景证据不完整或不一致，仅供查看。'); }
      }
      if (get(raw, 'IsAaControl') === true) { model.warnings.unshift('A/A 对照：差异反映测量重复性，不能作为产品版本优化结论。'); if (model.status === 'regression') model.status = 'inconclusive'; }
    } else {
      model.kind = 'run'; model.status = 'measured'; model.protocol = get(metadata, 'Protocol');
      const evidence = Array.isArray(raw) ? raw : get(raw, 'Metrics') ? [raw] : null;
      const summaries = evidence ? summary : raw;
      if (evidence) model.runs = inferRounds(evidence.map((r, i) => normalizeRun(r, i)));
      const ids = evidence ? [...new Set(model.runs.map(r => r.scenarioId))] : Object.keys(summaries).filter(id => get(summaries[id], 'TotalCpuTimeMs') !== null && get(summaries[id], 'DurationSeconds') !== null);
      if (!ids.length) throw new Error('未找到基准数据。请导入完整 report.json、run_summary.json 或 run_evidence.json。');
      model.scenarios = ids.map(id => ({ id, name: scenarioLabel(id), baseline: null, candidate: get(summaries, id) || combineMetrics(model.runs.filter(r => r.scenarioId === id)), verdict: 'measured', comparisons: [], notes: '' }));
      if (model.runs.some(r => !r.success)) model.status = 'error';
      else if (model.runs.some(r => r.status === 'inconclusive')) model.status = 'inconclusive';
      if (get(metadata, 'SourceFilesUnchanged') === false) { model.status = 'error'; model.warnings.push(String(get(metadata, 'SourceError', '目标文件在测量期间发生变化。'))); }
      if (!model.runs.length) { model.status = 'inconclusive'; model.warnings.push('仅有汇总，缺少逐轮样本与负载证据。不能用于性能门禁。'); }
      model.warnings.push('单目标测量不产生版本改善 / 退化结论。');
    }
    let sampleCount = 0;
    for (const run of model.runs) sampleCount += run.samples.length;
    if (sampleCount > MAX_SAMPLES) throw new Error('报告总样本量过大，请拆分为多个报告。');
    model.title = filename(model.candidate || model.runs[0]?.observations?.TargetPath || sourceName);
    if (!model.protocol) model.warnings.push('旧报告或缺少完整协议：可查看，不据此建立跨协议比较。');
    return model;
  }
  function importDocuments(documents) {
    const comparisons = documents.filter(d => Array.isArray(get(d.data, 'Scenarios')) || d.data?.ViewerSchemaVersion === 1);
    const rest = documents.filter(d => !comparisons.includes(d));
    const models = comparisons.map(d => normalize(d.data, d.name));
    const metadata = rest.find(d => get(d.data, 'TargetFiles') && get(d.data, 'Tier'));
    const evidence = rest.find(d => Array.isArray(d.data) || get(d.data, 'Metrics'));
    const summaries = rest.filter(d => !Array.isArray(d.data) && !get(d.data, 'Metrics') && !get(d.data, 'TargetFiles'));
    if (metadata && !evidence && !summaries.length) throw new Error('元数据需要和 run_evidence.json 或 run_summary.json 一起导入。');
    if (evidence) models.push(normalize(evidence.data, evidence.name, metadata?.data, summaries[0]?.data));
    else for (const document of summaries) models.push(normalize(document.data, document.name, metadata?.data));
    if (!models.length) throw new Error('未找到可读取的基准报告。');
    return models;
  }
  function demo() {
    const MB = 1024 * 1024, runs = [], scenarios = [];
    for (const [si, id] of ['idle_empty', 'rules_scaling_10', 'target_absent'].entries()) {
      const summaries = { Baseline: [], Candidate: [] };
      for (let r = 1; r <= 3; r++) for (const side of r % 2 ? ['Baseline', 'Candidate'] : ['Candidate', 'Baseline']) {
        const candidate = side === 'Candidate', samples = [], duration = 30;
        const rate = (candidate ? 5.1 : 7.4) + si * 0.7 + (r - 2) * 0.16;
        for (let i = 0; i <= 150; i++) {
          const elapsed = i * 200, wave = Math.sin(i * 0.21 + r) * 0.12 + Math.sin(i * 0.077) * 0.09;
          const spike = (i > 29 && i < 39 ? Math.sin((i - 29) * Math.PI / 10) * 1.5 : 0) + (i > 92 && i < 100 ? Math.sin((i - 92) * Math.PI / 8) * 0.9 : 0);
          samples.push({ ElapsedMs: elapsed, TotalCpuTimeMs: rate * elapsed / 1000, InstantCpuPercentPerCore: Math.max(0, rate / 10 + wave + spike), PrivateBytes: (candidate ? 40.8 : 46.5) * MB + si * MB + Math.sin(i * 0.07) * MB * 0.35, WorkingSetBytes: (candidate ? 52 : 58.1) * MB + Math.sin(i * 0.03) * MB, ThreadCount: 14, HandleCount: 276, CollectionDurationMs: 0.3 });
        }
        const metrics = { DurationSeconds: duration, SampleCount: samples.length, TotalCpuTimeMs: rate * duration, PrivateBytes: { Mean: (candidate ? 40.8 : 46.5) * MB + si * MB }, WorkingSetBytes: { Mean: (candidate ? 52 : 58.1) * MB }, PeakWorkingSetBytes: (candidate ? 54 : 60) * MB, ThreadCount: { Mean: 14 }, HandleCount: { Mean: 276 }, MonitorSelfCpuTimeMs: 82,
          MeasurementValid: true, ProcessExitedDuringMeasurement: false, SamplingQuality: { SuitableForComparison: true, RequestedIntervalMs: 200, IntervalsMs: { Max: 201.5 }, DelayedIntervalCount: 0, DelayedTimeFraction: 0, Warnings: [] } };
        summaries[side].push(metrics);
        runs.push({ Round: r, Side: side, Order: runs.length + 1, Result: { ScenarioId: id, Success: true, Metrics: metrics, Timeseries: samples, Observations: { SyntheticExample: true } } });
      }
      const a = combineMetrics(summaries.Baseline.map(metrics => ({ metrics }))), b = combineMetrics(summaries.Candidate.map(metrics => ({ metrics })));
      scenarios.push({ ScenarioId: id, ScenarioName: scenarioLabel(id), BaselineSummary: a, CandidateSummary: b, Verdict: 0, MetricComparisons: [
        { MetricName: 'Total CPU Time', Unit: 'ms', BaselineValue: a.TotalCpuTimeMs, CandidateValue: b.TotalCpuTimeMs, DeltaAbsolute: b.TotalCpuTimeMs - a.TotalCpuTimeMs, DeltaPercent: (b.TotalCpuTimeMs / a.TotalCpuTimeMs - 1) * 100, DeltaPercentDefined: true, Verdict: 1 },
        { MetricName: 'Private Memory', Unit: 'MB', BaselineValue: a.PrivateBytes.Mean / MB, CandidateValue: b.PrivateBytes.Mean / MB, DeltaAbsolute: (b.PrivateBytes.Mean - a.PrivateBytes.Mean) / MB, DeltaPercent: (b.PrivateBytes.Mean / a.PrivateBytes.Mean - 1) * 100, DeltaPercentDefined: true, Verdict: 1 },
        { MetricName: 'Thread Count', Unit: 'threads', BaselineValue: 14, CandidateValue: 14, DeltaAbsolute: 0, DeltaPercent: 0, DeltaPercentDefined: true, Verdict: 0 },
        { MetricName: 'Handle Count', Unit: 'handles', BaselineValue: 276, CandidateValue: 276, DeltaAbsolute: 0, DeltaPercent: 0, DeltaPercentDefined: true, Verdict: 0 }
      ] });
    }
    const raw = { SchemaVersion: 3, OverallVerdict: 0, BaselineTarget: '示例 · v1.1.exe', CandidateTarget: '示例 · v1.2.exe', TimestampUtc: '2026-10-28T09:00:00Z', Protocol: { Revision: 'SYNTHETIC DEMO — NOT A MEASUREMENT', SampleIntervalMs: 200 }, SourceFilesUnchanged: true, Rounds: 3, Runs: runs, Scenarios: scenarios, Limitations: ['所有数字均为合成示例，用于展示界面，不代表 SonicRoute 实测性能或优化收益。'] };
    for (const s of scenarios) for (const m of s.MetricComparisons) {
      const limit=Math.max(m.MetricName==='Total CPU Time'?30:m.Unit==='MB'?2:8,Math.abs(m.BaselineValue)*(m.MetricName==='Total CPU Time'?0.05:0.1));
      const half=m.MetricName==='Total CPU Time'?3:m.Unit==='MB'?0.3:0.2;
      m.RoundEvidence={ Method:'synthetic-example',MeanDifference:m.DeltaAbsolute,Lower95:m.DeltaAbsolute-half,Upper95:m.DeltaAbsolute+half,DecisionLimit:limit };
    }
    raw.SyntheticExample = true;
    const model = normalize(raw, '合成示例.json'); model.demo = true; model.title = '一次更轻盈的变化 · 合成示例'; return model;
  }
  function distribution(values) {
    const valid = values.filter(v => num(v) !== null).sort((a,b) => a-b);
    if (!valid.length) return { count: 0, mean: null, median: null, min: null, max: null, sd: null, cv: null };
    const mean = avg(valid), n = valid.length, sd = n > 1 ? Math.sqrt(valid.reduce((s,v) => s + (v-mean)**2,0)/(n-1)) : null;
    return { count:n, mean, median: n % 2 ? valid[(n-1)/2] : (valid[n/2-1]+valid[n/2])/2, min:valid[0],max:valid.at(-1), sd, cv:sd !== null && mean > 0 ? sd/mean : null };
  }
  return { normalize, importDocuments, demo, get, num, metric, avg, filename, scenarioLabel, verdict, combineMetrics, distribution };
});
