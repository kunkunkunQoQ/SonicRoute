using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SonicRoute.BenchmarkLab.Core
{
    /// <summary>
    /// Encapsulates a Windows Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
    /// Guarantees that any spawned test processes are automatically terminated by Windows kernel
    /// when the job object handle is closed or if BenchmarkLab exits unexpectedly.
    /// </summary>
    internal sealed class ProcessJobObject : IDisposable
    {
        private IntPtr _jobHandle;
        private bool _disposed;

        public ProcessJobObject(string? name = null)
        {
            _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, name);
            if (_jobHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create Windows Job Object.");
            }

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int length = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!NativeMethods.SetInformationJobObject(
                    _jobHandle,
                    NativeMethods.JobObjectExtendedLimitInformation,
                    ptr,
                    (uint)length))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set Job Object extended limit information.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public bool AssignProcess(Process process)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (process.HasExited) return false;

            try
            {
                return NativeMethods.AssignProcessToJobObject(_jobHandle, process.Handle);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_jobHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                }
            }
            GC.SuppressFinalize(this);
        }

        ~ProcessJobObject()
        {
            Dispose();
        }
    }
}
