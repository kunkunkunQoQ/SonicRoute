using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute.BenchmarkLab.Core
{
    public sealed class ProcessMonitor : IDisposable
    {
        private readonly Process _process;
        private readonly bool _ownsProcess;
        private readonly ProcessJobObject? _jobObject;
        private readonly int _sampleIntervalMs;
        private readonly List<ProcessSample> _samples = new();
        private readonly object _lock = new();

        private CancellationTokenSource? _cts;
        private Task? _monitorTask;
        private bool _disposed;

        public int ProcessId => _process.Id;
        public Process Process => _process;
        public IReadOnlyList<ProcessSample> Samples { get { lock (_lock) return _samples.ToArray(); } }
        public double? StartupLatencyMs { get; private set; }
        public double? ResponseLatencyMs { get; set; }

        private double _selfCpuStartMs;
        private double _selfCpuEndMs;

        private ProcessMonitor(Process process, bool ownsProcess, ProcessJobObject? jobObject, int sampleIntervalMs = 100)
        {
            _process = process;
            _ownsProcess = ownsProcess;
            _jobObject = jobObject;
            _sampleIntervalMs = Math.Max(20, sampleIntervalMs);
        }

        /// <summary>
        /// Launches a target executable inside a Job Object with specified environment and arguments.
        /// </summary>
        public static ProcessMonitor Launch(
            string executablePath,
            string arguments = "",
            string? workingDirectory = null,
            IDictionary<string, string>? environmentVariables = null,
            int sampleIntervalMs = 100,
            bool measureStartupLatency = true)
        {
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException($"Target executable not found: {executablePath}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            if (environmentVariables != null)
            {
                foreach (var kvp in environmentVariables)
                {
                    startInfo.Environment[kvp.Key] = kvp.Value;
                }
            }

            var jobObject = new ProcessJobObject();
            var sw = Stopwatch.StartNew();
            var process = new Process { StartInfo = startInfo };

            if (!process.Start())
            {
                jobObject.Dispose();
                throw new InvalidOperationException($"Failed to launch process: {executablePath}");
            }

            jobObject.AssignProcess(process);

            double? startupLatency = null;
            if (measureStartupLatency)
            {
                try
                {
                    // WaitForInputIdle waits for process to finish initialization and be ready for input
                    if (NativeMethods.WaitForInputIdle(process.Handle, 5000) == 0)
                        startupLatency = sw.Elapsed.TotalMilliseconds;
                }
                catch
                {
                    // Timeout/unsupported input queue is not a measured successful startup.
                    startupLatency = null;
                }
            }

            var monitor = new ProcessMonitor(process, ownsProcess: true, jobObject, sampleIntervalMs)
            {
                StartupLatencyMs = startupLatency
            };

            return monitor;
        }

        /// <summary>
        /// Attaches to an already running Windows process by PID.
        /// </summary>
        public static ProcessMonitor Attach(int processId, int sampleIntervalMs = 100)
        {
            var process = Process.GetProcessById(processId);
            return new ProcessMonitor(process, ownsProcess: false, jobObject: null, sampleIntervalMs);
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_monitorTask != null) return;

            _selfCpuStartMs = GetCurrentProcessCpuMs();
            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitoringLoop(_cts.Token));
        }

        public async Task<ProcessMetricSummary> StopAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            if (_monitorTask != null)
            {
                try { await _monitorTask.ConfigureAwait(false); } catch { }
            }

            _selfCpuEndMs = GetCurrentProcessCpuMs();
            double monitorSelfCpu = Math.Max(0, _selfCpuEndMs - _selfCpuStartMs);

            lock (_lock)
            {
                double duration = _samples.Count > 1
                    ? (_samples[_samples.Count - 1].ElapsedMs - _samples[0].ElapsedMs) / 1000.0
                    : 0;

                var summary = ProcessMetricSummary.Aggregate(_samples, duration, monitorSelfCpu);
                summary.StartupLatencyMs = StartupLatencyMs;
                summary.ResponseLatencyMs = ResponseLatencyMs;
                return summary;
            }
        }

        private void MonitoringLoop(CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            IntPtr hProcess;
            try
            {
                hProcess = _process.Handle;
            }
            catch
            {
                return;
            }

            double prevCpuTime = 0;
            double prevWallMs = 0;
            int processorCount = Environment.ProcessorCount;

            // Initial sample
            Sample(hProcess, stopwatch.Elapsed.TotalMilliseconds, ref prevCpuTime, ref prevWallMs, processorCount);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_process.HasExited)
                    {
                        // Process terminated
                        break;
                    }

                    Thread.Sleep(_sampleIntervalMs);
                    Sample(hProcess, stopwatch.Elapsed.TotalMilliseconds, ref prevCpuTime, ref prevWallMs, processorCount);
                }
                catch (Exception)
                {
                    break;
                }
            }

            // Final sample
            if (!_process.HasExited)
            {
                try
                {
                    Sample(hProcess, stopwatch.Elapsed.TotalMilliseconds, ref prevCpuTime, ref prevWallMs, processorCount);
                }
                catch { }
            }
        }

        private void Sample(IntPtr hProcess, double currentWallMs, ref double prevCpuTime, ref double prevWallMs, int processorCount)
        {
            if (!NativeMethods.GetProcessTimes(hProcess, out _, out _, out long kernelTime, out long userTime))
            {
                return;
            }

            // 100-nanosecond units to milliseconds
            double userMs = userTime / 10000.0;
            double kernelMs = kernelTime / 10000.0;
            double totalCpuMs = userMs + kernelMs;

            double deltaCpu = totalCpuMs - prevCpuTime;
            double deltaWall = currentWallMs - prevWallMs;

            double cpuPercentPerCore = 0;
            double cpuPercentMachine = 0;

            if (deltaWall > 0 && prevWallMs > 0)
            {
                cpuPercentPerCore = Math.Max(0, (deltaCpu / deltaWall) * 100.0);
                cpuPercentMachine = cpuPercentPerCore / processorCount;
            }

            prevCpuTime = totalCpuMs;
            prevWallMs = currentWallMs;

            long workingSet = 0;
            long peakWorkingSet = 0;
            long privateBytes = 0;

            if (NativeMethods.GetProcessMemoryInfo(
                hProcess,
                out NativeMethods.PROCESS_MEMORY_COUNTERS_EX mem,
                (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS_EX>()))
            {
                workingSet = (long)mem.WorkingSetSize.ToUInt64();
                peakWorkingSet = (long)mem.PeakWorkingSetSize.ToUInt64();
                privateBytes = (long)mem.PrivateUsage.ToUInt64();
            }

            uint handleCount = 0;
            NativeMethods.GetProcessHandleCount(hProcess, out handleCount);

            ulong readBytes = 0, writeBytes = 0, readOps = 0, writeOps = 0;
            if (NativeMethods.GetProcessIoCounters(hProcess, out NativeMethods.IO_COUNTERS io))
            {
                readBytes = io.ReadTransferCount;
                writeBytes = io.WriteTransferCount;
                readOps = io.ReadOperationCount;
                writeOps = io.WriteOperationCount;
            }

            int threadCount = 0;
            try
            {
                threadCount = _process.Threads.Count;
            }
            catch { }

            var sample = new ProcessSample
            {
                TimestampUtc = DateTime.UtcNow,
                ElapsedMs = currentWallMs,
                TotalCpuTimeMs = totalCpuMs,
                UserCpuTimeMs = userMs,
                KernelCpuTimeMs = kernelMs,
                InstantCpuPercentMachine = cpuPercentMachine,
                InstantCpuPercentPerCore = cpuPercentPerCore,
                WorkingSetBytes = workingSet,
                PeakWorkingSetBytes = peakWorkingSet,
                PrivateBytes = privateBytes,
                ThreadCount = threadCount,
                HandleCount = (int)handleCount,
                IoReadBytes = readBytes,
                IoWriteBytes = writeBytes,
                IoReadOps = readOps,
                IoWriteOps = writeOps,
                ManagedMemoryBytes = null
            };

            lock (_lock)
            {
                _samples.Add(sample);
            }
        }

        private static double GetCurrentProcessCpuMs()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                return p.TotalProcessorTime.TotalMilliseconds;
            }
            catch
            {
                return 0;
            }
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }

        public bool WaitForExit(int timeoutMilliseconds)
        {
            try
            {
                return _process.WaitForExit(timeoutMilliseconds);
            }
            catch
            {
                return true;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                }

                if (_ownsProcess)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            _process.Kill(entireProcessTree: true);
                            _process.WaitForExit(3000);
                        }
                    }
                    catch { }
                    _process.Dispose();
                }

                _jobObject?.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
