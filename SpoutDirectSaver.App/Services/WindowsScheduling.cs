using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpoutDirectSaver.App.Services;

internal static class WindowsScheduling
{
    public static void TryPromoteCurrentProcess(ProcessPriorityClass minimumPriorityClass = ProcessPriorityClass.High)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            if (process.PriorityClass < minimumPriorityClass)
            {
                process.PriorityClass = minimumPriorityClass;
            }

            process.PriorityBoostEnabled = true;
        }
        catch
        {
            // Ignore scheduling hints on unsupported environments.
        }
    }

    public static void TryPromoteProcess(Process process, ProcessPriorityClass minimumPriorityClass = ProcessPriorityClass.AboveNormal)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (process.PriorityClass < minimumPriorityClass)
            {
                process.PriorityClass = minimumPriorityClass;
            }

            process.PriorityBoostEnabled = true;
        }
        catch
        {
            // Ignore scheduling hints on unsupported environments.
        }
    }

    public static IDisposable EnterRecordingPriorityScope(ProcessPriorityClass minimumPriorityClass = ProcessPriorityClass.High)
    {
        return WindowsProcessSchedulingScope.TryEnter(minimumPriorityClass);
    }

    public static IDisposable EnterCaptureProfile()
    {
        return WindowsThreadSchedulingScope.TryEnter("Capture", ThreadPriority.Highest, AvrtPriority.High);
    }

    public static IDisposable EnterGameProfile()
    {
        return WindowsThreadSchedulingScope.TryEnter("Games", ThreadPriority.Highest, AvrtPriority.High);
    }

    public static IDisposable EnterRealtimeWriterProfile()
    {
        return WindowsThreadSchedulingScope.TryEnter("Capture", ThreadPriority.Highest, AvrtPriority.High);
    }

    public static IDisposable EnterWriterProfile()
    {
        return WindowsThreadSchedulingScope.TryEnter("Distribution", ThreadPriority.AboveNormal, AvrtPriority.Normal);
    }

    private sealed class WindowsProcessSchedulingScope : IDisposable
    {
        private readonly ProcessPriorityClass? _previousPriorityClass;
        private readonly bool? _previousPriorityBoostEnabled;

        private WindowsProcessSchedulingScope(ProcessPriorityClass? previousPriorityClass, bool? previousPriorityBoostEnabled)
        {
            _previousPriorityClass = previousPriorityClass;
            _previousPriorityBoostEnabled = previousPriorityBoostEnabled;
        }

        public static IDisposable TryEnter(ProcessPriorityClass minimumPriorityClass)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var previousPriorityClass = process.PriorityClass;
                var previousPriorityBoostEnabled = process.PriorityBoostEnabled;

                if (process.PriorityClass < minimumPriorityClass)
                {
                    process.PriorityClass = minimumPriorityClass;
                }

                process.PriorityBoostEnabled = true;
                return new WindowsProcessSchedulingScope(previousPriorityClass, previousPriorityBoostEnabled);
            }
            catch
            {
                return new WindowsProcessSchedulingScope(null, null);
            }
        }

        public void Dispose()
        {
            if (_previousPriorityClass is null || _previousPriorityBoostEnabled is null)
            {
                return;
            }

            try
            {
                using var process = Process.GetCurrentProcess();
                if (!process.HasExited)
                {
                    process.PriorityClass = _previousPriorityClass.Value;
                    process.PriorityBoostEnabled = _previousPriorityBoostEnabled.Value;
                }
            }
            catch
            {
                // Ignore restore failures.
            }
        }
    }

    private sealed class WindowsThreadSchedulingScope : IDisposable
    {
        private readonly IntPtr _mmcssHandle;
        private readonly ThreadPriority _previousPriority;

        private WindowsThreadSchedulingScope(IntPtr mmcssHandle, ThreadPriority previousPriority)
        {
            _mmcssHandle = mmcssHandle;
            _previousPriority = previousPriority;
        }

        public static IDisposable TryEnter(string taskName, ThreadPriority threadPriority, AvrtPriority avrtPriority)
        {
            var currentThread = Thread.CurrentThread;
            var previousPriority = currentThread.Priority;

            try
            {
                currentThread.Priority = threadPriority;
            }
            catch
            {
                // Ignore thread priority failures and keep going.
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                uint taskIndex;
                handle = AvSetMmThreadCharacteristics(taskName, out taskIndex);
                if (handle != IntPtr.Zero)
                {
                    AvSetMmThreadPriority(handle, avrtPriority);
                }
            }
            catch
            {
                handle = IntPtr.Zero;
            }

            return new WindowsThreadSchedulingScope(handle, previousPriority);
        }

        public void Dispose()
        {
            try
            {
                Thread.CurrentThread.Priority = _previousPriority;
            }
            catch
            {
                // Ignore restore failures.
            }

            if (_mmcssHandle != IntPtr.Zero)
            {
                try
                {
                    AvRevertMmThreadCharacteristics(_mmcssHandle);
                }
                catch
                {
                    // Ignore teardown failures.
                }
            }
        }

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, AvrtPriority priority);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
    }

    internal enum AvrtPriority
    {
        Low = -1,
        Normal = 0,
        High = 1,
        Critical = 2
    }
}
