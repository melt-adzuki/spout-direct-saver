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

    public static IDisposable EnterCaptureProfile()
    {
        return WindowsThreadSchedulingScope.TryEnter("Capture", ThreadPriority.Highest, AvrtPriority.High);
    }

    public static IDisposable EnterGameProfile()
    {
        return WindowsThreadSchedulingScope.TryEnter("Games", ThreadPriority.Highest, AvrtPriority.High);
    }

    public static IDisposable EnterWriterProfile()
    {
        return WindowsThreadSchedulingScope.TryEnter("Distribution", ThreadPriority.AboveNormal, AvrtPriority.Normal);
    }

    public static IDisposable EnterBackgroundWorkProfile()
    {
        return WindowsThreadSchedulingScope.TryEnterWithoutMmcss(ThreadPriority.BelowNormal);
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

        public static IDisposable TryEnterWithoutMmcss(ThreadPriority threadPriority)
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

            return new WindowsThreadSchedulingScope(IntPtr.Zero, previousPriority);
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
