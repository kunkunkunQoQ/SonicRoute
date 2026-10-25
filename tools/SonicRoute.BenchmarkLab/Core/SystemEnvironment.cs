using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SonicRoute.BenchmarkLab.Core
{
    public sealed class SystemEnvironmentInfo
    {
        public string MachineName { get; set; } = Environment.MachineName;
        public string OperatingSystem { get; set; } = RuntimeInformation.OSDescription;
        public string OsVersion { get; set; } = Environment.OSVersion.Version.ToString();
        public string OsArchitecture { get; set; } = RuntimeInformation.OSArchitecture.ToString();
        public string ProcessArchitecture { get; set; } = RuntimeInformation.ProcessArchitecture.ToString();
        public string DotNetRuntime { get; set; } = RuntimeInformation.FrameworkDescription;
        public int LogicalProcessorCount { get; set; } = Environment.ProcessorCount;
        public string CpuModel { get; set; } = GetCpuModelName();
        public long TotalPhysicalMemoryMb { get; set; } = GetTotalMemoryMb();

        public static SystemEnvironmentInfo CaptureCurrent()
        {
            return new SystemEnvironmentInfo();
        }

        private static string GetCpuModelName()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key != null)
                {
                    var val = key.GetValue("ProcessorNameString") as string;
                    if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                }
            }
            catch
            {
                // Ignore fallback to generic
            }
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown Windows CPU";
        }

        private static long GetTotalMemoryMb()
        {
            try
            {
                var mem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (mem > 0) return mem / (1024 * 1024);
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Validates whether a historical baseline environment is compatible with the current candidate environment.
        /// Returns warning message if incompatible or significantly skewed.
        /// </summary>
        public bool IsCompatibleWith(SystemEnvironmentInfo other, out string reason)
        {
            if (!string.Equals(OsVersion, other.OsVersion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ProcessArchitecture, other.ProcessArchitecture, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(MachineName, other.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Machine, OS version or measurement-process architecture differs from the historical baseline.";
                return false;
            }
            if (LogicalProcessorCount != other.LogicalProcessorCount)
            {
                reason = $"CPU logical core mismatch: current={LogicalProcessorCount}, baseline={other.LogicalProcessorCount}. CPU % normalization will skew.";
                return false;
            }

            if (!string.Equals(OsArchitecture, other.OsArchitecture, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"OS architecture mismatch: current={OsArchitecture}, baseline={other.OsArchitecture}.";
                return false;
            }

            // If CPU model differs
            if (!string.Equals(CpuModel, other.CpuModel, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"CPU model differs: current='{CpuModel}', baseline='{other.CpuModel}'. Absolute timings not directly comparable.";
                return false;
            }

            reason = "Environment is fully compatible.";
            return true;
        }
    }
}
