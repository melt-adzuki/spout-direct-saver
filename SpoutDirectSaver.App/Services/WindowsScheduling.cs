using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
namespace SpoutDirectSaver.App.Services;

internal static class WindowsScheduling
{
    private const uint TimePeriodMilliseconds = 1;
    private const string SeDebugPrivilege = "SeDebugPrivilege";
    private const string SeIncreaseBasePriorityPrivilege = "SeIncreaseBasePriorityPrivilege";
    private const int ErrorNotAllAssigned = 1300;
    private const uint SePrivilegeEnabled = 0x00000002;
    private static readonly object InitializationGate = new();
    private static bool _processHintsInitialized;
    private static bool _timerPeriodActive;

    public static void InitializeProcessSchedulingHints()
    {
        lock (InitializationGate)
        {
            if (_processHintsInitialized)
            {
                return;
            }

            _processHintsInitialized = true;
            TryEnablePrivilege(SeDebugPrivilege);
            TryEnablePrivilege(SeIncreaseBasePriorityPrivilege);
            TryBeginTimePeriod(TimePeriodMilliseconds);
        }
    }

    public static void ShutdownProcessSchedulingHints()
    {
        lock (InitializationGate)
        {
            if (!_timerPeriodActive)
            {
                return;
            }

            try
            {
                if (timeEndPeriod(TimePeriodMilliseconds) == 0)
                {
                    Log($"timeEndPeriod success periodMs={TimePeriodMilliseconds}");
                }
            }
            catch
            {
                // Ignore timer resolution teardown failures.
            }
            finally
            {
                _timerPeriodActive = false;
            }
        }
    }

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

    private static void TryEnablePrivilege(string privilegeName)
    {
        IntPtr tokenHandle = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges, out tokenHandle))
            {
                Log($"OpenProcessToken failed privilege={privilegeName} error={Marshal.GetLastWin32Error()}");
                return;
            }

            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                Log($"LookupPrivilegeValue failed privilege={privilegeName} error={Marshal.GetLastWin32Error()}");
                return;
            }

            var tokenPrivileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                }
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref tokenPrivileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                Log($"AdjustTokenPrivileges failed privilege={privilegeName} error={Marshal.GetLastWin32Error()}");
                return;
            }

            var lastError = Marshal.GetLastWin32Error();
            if (lastError == ErrorNotAllAssigned)
            {
                Log($"Privilege not assigned privilege={privilegeName}");
                return;
            }

            Log($"Privilege enabled privilege={privilegeName}");
        }
        catch (Exception ex)
        {
            Log($"Privilege enable threw privilege={privilegeName} error={ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
            {
                CloseHandle(tokenHandle);
            }
        }
    }

    private static void TryBeginTimePeriod(uint milliseconds)
    {
        try
        {
            if (timeBeginPeriod(milliseconds) == 0)
            {
                _timerPeriodActive = true;
                Log($"timeBeginPeriod success periodMs={milliseconds}");
                return;
            }

            Log($"timeBeginPeriod failed periodMs={milliseconds}");
        }
        catch (Exception ex)
        {
            Log($"timeBeginPeriod threw periodMs={milliseconds} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        Trace.WriteLine($"[WindowsScheduling] {message}");
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern uint timeEndPeriod(uint period);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, TokenAccessLevels desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [Flags]
    private enum TokenAccessLevels : uint
    {
        Query = 0x0008,
        AdjustPrivileges = 0x0020
    }

    internal enum AvrtPriority
    {
        Low = -1,
        Normal = 0,
        High = 1,
        Critical = 2
    }
}
