using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && args[1] == "diag")
            return RunDiagnostic();
        if (args.Length > 2 && args[1] == "voltest")
            return RunVolumeTest(int.Parse(args[2]));
        if (args.Length > 3 && args[1] == "route")
            return RunRoute(int.Parse(args[2]), args[3]);
        Console.WriteLine("===== 声道快切 v1.0 自检 =====");

        var outputs = Check("播放设备枚举", () => AudioService.GetDevices(EDataFlow.eRender)) ?? new List<AudioDeviceInfo>();
        var inputs = Check("录音设备枚举", () => AudioService.GetDevices(EDataFlow.eCapture)) ?? new List<AudioDeviceInfo>();
        var apps = Check("应用枚举", () => AudioService.GetApps()) ?? new List<AudioAppInfo>();

        Console.WriteLine();
        Console.WriteLine("-- 播放设备（含原始 ID） --");
        foreach (var d in outputs) Console.WriteLine($"  {d.DisplayLabel}\n      id={d.Id}");
        Console.WriteLine("-- 录音设备 --");
        foreach (var d in inputs) Console.WriteLine("  " + d.DisplayLabel);
        Console.WriteLine("-- 应用 --");
        foreach (var a in apps) Console.WriteLine($"  {a.Label}  [PID {a.ProcessId}]");

        Console.WriteLine();
        Console.WriteLine("-- 应用输出切换（真实应用，测后立即恢复） --");
        if (outputs.Count > 0 && apps.Count > 0)
        {
            var target = outputs[0];
            var app = apps[0];
            string? defaultOutId = AudioService.GetDefaultDeviceId(EDataFlow.eRender);
            string? original = AudioService.GetPersistedEndpoint((int)app.ProcessId, EDataFlow.eRender);

            var r = AudioService.ApplyEndpoint((int)app.ProcessId, EDataFlow.eRender, target.Id);
            Console.WriteLine($"  应用 {app.Label}(PID {app.ProcessId}) Set → {target.DisplayName}: {(r.Success ? "成功 ✓" : $"失败 {r.Message}")}");
            if (!r.Success) _failures++;

            string? readback = AudioService.GetPersistedEndpoint((int)app.ProcessId, EDataFlow.eRender);
            string? readbackShort = readback == null ? null : AudioPolicyConfig.UnpackDeviceId(readback);
            bool match = readbackShort != null && string.Equals(readbackShort, target.Id, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  回读验证: {(match ? "一致 ✓" : $"不一致 ✗ (readback={readback})")}");
            if (!match) _failures++;

            // 立即恢复（有原设置则恢复原设置，否则恢复为系统默认）
            string? restoreId = original ?? defaultOutId;
            if (restoreId != null)
            {
                var rr = AudioService.ApplyEndpoint((int)app.ProcessId, EDataFlow.eRender, restoreId);
                Console.WriteLine($"  已恢复为{(original != null ? "原设置" : "系统默认")}: {(rr.Success ? "✓" : $"✗ {rr.Message}")}");
            }
        }
        else
        {
            Console.WriteLine("  无应用或无播放设备，跳过");
            _failures++;
        }

        Console.WriteLine();
        Console.WriteLine("-- 应用输入切换（真实应用，测后立即恢复） --");
        if (inputs.Count > 0 && apps.Count > 0)
        {
            var target = inputs[0];
            var app = apps[0];
            string? defaultInId = AudioService.GetDefaultDeviceId(EDataFlow.eCapture);
            string? original = AudioService.GetPersistedEndpoint((int)app.ProcessId, EDataFlow.eCapture);

            var r = AudioService.ApplyEndpoint((int)app.ProcessId, EDataFlow.eCapture, target.Id);
            Console.WriteLine($"  应用 {app.Label}(PID {app.ProcessId}) Set → {target.DisplayName}: {(r.Success ? "成功 ✓" : $"失败 {r.Message}")}");
            if (!r.Success) _failures++;

            string? readback = AudioService.GetPersistedEndpoint((int)app.ProcessId, EDataFlow.eCapture);
            string? readbackShort = readback == null ? null : AudioPolicyConfig.UnpackDeviceId(readback);
            bool match = readbackShort != null && string.Equals(readbackShort, target.Id, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  回读验证: {(match ? "一致 ✓" : $"不一致 ✗ (readback={readback})")}");
            if (!match) _failures++;

            string? restoreId = original ?? defaultInId;
            if (restoreId != null)
            {
                var rr = AudioService.ApplyEndpoint((int)app.ProcessId, EDataFlow.eCapture, restoreId);
                Console.WriteLine($"  已恢复为{(original != null ? "原设置" : "系统默认")}: {(rr.Success ? "✓" : $"✗ {rr.Message}")}");
            }
        }
        else
        {
            Console.WriteLine("  无应用或无录音设备，跳过");
            _failures++;
        }

        Console.WriteLine();
        Console.WriteLine("-- 前台进程检测 --");
        int fgPid = ForegroundAppService.GetForegroundProcessId();
        string? fgName = ForegroundAppService.GetProcessNameSafe(fgPid);
        bool fgOk = fgPid > 0 && !string.IsNullOrWhiteSpace(fgName);
        Console.WriteLine($"  前台窗口 PID={fgPid} 进程={fgName ?? "?"} → {(fgOk ? "✓" : "✗")}");
        if (!fgOk) _failures++;

        Console.WriteLine();
        Console.WriteLine("-- 会话音量（真实改值+回读+恢复） --");
        if (apps.Count > 0)
        {
            var app = apps[0];
            int pid = (int)app.ProcessId;
            SessionVolumeService.Refresh();
            int v = SessionVolumeService.GetVolumePercent(pid);
            bool hasSession = v >= 0;
            if (hasSession)
            {
                bool muted = SessionVolumeService.IsMuted(pid);

                // 1) 真实改值：设为 (原值±15 取整到 5 的倍数，范围 5..95)
                int newVal = Math.Clamp((v / 5 * 5 + 15) % 95 + 5, 5, 95);
                bool setOk = SessionVolumeService.SetVolumePercent(pid, newVal);
                int vAfter = SessionVolumeService.GetVolumePercent(pid);
                bool setMatch = setOk && Math.Abs(vAfter - newVal) <= 2;
                Console.WriteLine($"  应用 {app.Label}(PID {pid}) 原音量={v}% → 设 {newVal}% → 回读 {vAfter}% → {(setMatch ? "✓" : "✗")}");

                // 2) 静音切换：设为 开 → 回读 → 恢复
                bool muteSet = SessionVolumeService.SetMute(pid, true);
                bool mutedAfter = SessionVolumeService.IsMuted(pid);
                bool muteMatch = muteSet && mutedAfter;
                Console.WriteLine($"  静音设为开 → 回读 {mutedAfter} → {(muteMatch ? "✓" : "✗")}");

                // 3) 立即恢复原音量与原静音状态
                bool restoreV = SessionVolumeService.SetVolumePercent(pid, v);
                bool restoreM = SessionVolumeService.SetMute(pid, muted);
                int vFinal = SessionVolumeService.GetVolumePercent(pid);
                bool mutedFinal = SessionVolumeService.IsMuted(pid);
                bool restoreMatch = restoreV && restoreM && Math.Abs(vFinal - v) <= 2 && mutedFinal == muted;
                Console.WriteLine($"  恢复原值 {v}%/{muted} → 回读 {vFinal}%/{mutedFinal} → {(restoreMatch ? "✓" : "✗")}");

                if (!(setMatch && muteMatch && restoreMatch)) _failures++;
            }
            else
            {
                Console.WriteLine($"  应用 {app.Label}(PID {pid}) 无会话，跳过（合理） ✓");
            }
        }
        else
        {
            Console.WriteLine("  无应用，跳过");
            _failures++;
        }

        Console.WriteLine();
        Console.WriteLine($"===== 结果：{(_failures == 0 ? "全部通过 ✓" : $"{_failures} 项失败 ✗")} =====");
        return _failures == 0 ? 0 : 1;
    }

    private static T? Check<T>(string name, Func<T> f)
    {
        try
        {
            var result = f();
            Console.WriteLine($"[✓] {name}");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[✗] {name}: {ex.Message}");
            _failures++;
            return default;
        }
    }

    // ---- 诊断：每个 PID 的所有会话细节（验证"一个进程多个 Session"） ----
    private static int RunDiagnostic()
    {
        Console.WriteLine("===== 会话诊断：按 PID 列出所有 Session =====");
        var iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        var rows = new List<string>();
        foreach (var flow in new[] { EDataFlow.eRender, EDataFlow.eCapture })
        {
            var devices = AudioService.GetDevices(flow);
            foreach (var dev in devices)
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                try
                {
                    if (enumerator.GetDevice(dev.Id, out var device) < 0 || device == null) continue;
                    try
                    {
                        if (device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object mgrObj) < 0) continue;
                        var mgr = (IAudioSessionManager2)mgrObj;
                        try
                        {
                            if (mgr.GetSessionEnumerator(out var se) < 0) continue;
                            try
                            {
                                se.GetCount(out int n);
                                for (int j = 0; j < n; j++)
                                {
                                    if (se.GetSession(j, out var sess) < 0) continue;
                                    try
                                    {
                                        uint pid = 0;
                                        try { sess.GetProcessId(out pid); } catch { }
                                        if (pid == 0) continue;
                                        sess.GetState(out var st);
                                        string? name = null;
                                        try { sess.GetDisplayName(out name); } catch { }
                                        float vol = -1;
                                        try { var sv = (ISimpleAudioVolume)sess; sv.GetMasterVolume(out vol); } catch { }
                                        int m = -1;
                                        try { var sv = (ISimpleAudioVolume)sess; sv.GetMute(out m); } catch { }
                                        rows.Add($"PID {pid,-8} flow={flow,-8} state={st,-11} vol={vol*100,4:0}% mute={m,-2} dev={dev.DisplayName} name={name ?? "-"}");
                                    }
                                    finally { Marshal.ReleaseComObject(sess); }
                                }
                            }
                            finally { Marshal.ReleaseComObject(se); }
                        }
                        finally { Marshal.ReleaseComObject(mgr); }
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
                finally { Marshal.ReleaseComObject(enumerator); }
            }
        }
        foreach (var r in rows.OrderBy(r => r)) Console.WriteLine("  " + r);
        Console.WriteLine($"共 {rows.Count} 个会话");
        return 0;
    }

    // ---- 定向验证：改一次音量，确认该 PID 的全部会话都跟着变（EarTrumpet 组语义） ----
    private static int RunVolumeTest(int targetPid)
    {
        Console.WriteLine($"===== 音量组语义验证：PID {targetPid} =====");
        var iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        var snapshots = new List<(string dev, string flow, float vol, int mute)>();

        void Snap(string tag)
        {
            foreach (var flow in new[] { EDataFlow.eRender, EDataFlow.eCapture })
            {
                foreach (var dev in AudioService.GetDevices(flow))
                {
                    var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    try
                    {
                        if (enumerator.GetDevice(dev.Id, out var device) < 0) continue;
                        try
                        {
                            if (device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object o) < 0) continue;
                            var mgr = (IAudioSessionManager2)o;
                            try
                            {
                                if (mgr.GetSessionEnumerator(out var se) < 0) continue;
                                try
                                {
                                    se.GetCount(out int n);
                                    for (int j = 0; j < n; j++)
                                    {
                                        if (se.GetSession(j, out var s) < 0) continue;
                                        try
                                        {
                                            s.GetProcessId(out uint pid);
                                            if ((int)pid != targetPid) continue;
                                            float vol = -1; int m = -1;
                                            try { var sv = (ISimpleAudioVolume)s; sv.GetMasterVolume(out vol); sv.GetMute(out m); } catch { }
                                            snapshots.Add(($"{tag} {dev.DisplayName}", flow.ToString(), vol, m));
                                        }
                                        finally { Marshal.ReleaseComObject(s); }
                                    }
                                }
                                finally { Marshal.ReleaseComObject(se); }
                            }
                            finally { Marshal.ReleaseComObject(mgr); }
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                    finally { Marshal.ReleaseComObject(enumerator); }
                }
            }
        }

        Console.WriteLine("-- 改音量前 --");
        Snap("BEFORE");
        foreach (var s in snapshots) Console.WriteLine($"  {s.dev,-48} vol={s.vol*100,4:0}% mute={s.mute}");

        SessionVolumeService.Refresh();
        int before = SessionVolumeService.GetVolumePercent(targetPid);
        bool setOk = SessionVolumeService.SetVolumePercent(targetPid, 30);
        int after = SessionVolumeService.GetVolumePercent(targetPid);

        snapshots.Clear();
        Console.WriteLine("-- 设 30% 后（应全部会话=30%） --");
        Snap("AFTER");
        foreach (var s in snapshots) Console.WriteLine($"  {s.dev,-48} vol={s.vol*100,4:0}% mute={s.mute}");
        bool allChanged = snapshots.All(s => Math.Abs(s.vol * 100 - 30) <= 2);
        Console.WriteLine($"全部会话=30%: {allChanged} ✓");
        if (!allChanged) return 1;

        bool restoreOk = SessionVolumeService.SetVolumePercent(targetPid, before);
        int restored = SessionVolumeService.GetVolumePercent(targetPid);
        Console.WriteLine($"SetVolumePercent(30): {setOk}, 回读={after}%, 恢复 {before}% → {restored}% (ok={restoreOk})");
        return (setOk && restoreOk && Math.Abs(restored - before) <= 2) ? 0 : 1;
    }

    // ---- 路由指定 PID 的输出设备到某设备（用于 UI 幽灵项验证） ----
    private static int RunRoute(int pid, string shortId)
    {
        var ok = AudioService.ApplyEndpoint(pid, EDataFlow.eRender, shortId);
        var id = AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender);
        Console.WriteLine($"route pid={pid} -> {shortId}: ok={ok.Success} persisted={id}");
        return ok.Success ? 0 : 1;
    }
}
