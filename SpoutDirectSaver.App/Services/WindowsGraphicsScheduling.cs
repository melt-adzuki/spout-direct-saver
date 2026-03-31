using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SpoutDirectSaver.App.Services;

internal static class WindowsGraphicsScheduling
{
    private const int DefaultGpuThreadPriority = unchecked((int)0x4000001E);
    private const uint KmtQueryAdapterInfoTypeWddm27Caps = 70;

    public static void TryApplyGraphicsDeviceHints(ID3D11Device device, string owner)
    {
        TryIncreaseMaximumFrameLatency(device, owner);
        TrySetGpuPriority(device, owner);
    }

    private static void TryIncreaseMaximumFrameLatency(ID3D11Device device, string owner)
    {
        try
        {
            using var dxgiDevice = device.QueryInterfaceOrNull<IDXGIDevice1>();
            if (dxgiDevice is null)
            {
                DebugTrace.WriteLine("WindowsGraphicsScheduling", $"SetMaximumFrameLatency skipped owner={owner} reason=no-idxgidevice1");
                return;
            }

            var result = dxgiDevice.SetMaximumFrameLatency(16);
            if (result.Failure)
            {
                DebugTrace.WriteLine(
                    "WindowsGraphicsScheduling",
                    $"SetMaximumFrameLatency failed owner={owner} hresult=0x{result.Code:X8}");
                return;
            }

            DebugTrace.WriteLine("WindowsGraphicsScheduling", $"SetMaximumFrameLatency success owner={owner} latency=16");
        }
        catch (Exception ex)
        {
            DebugTrace.WriteLine(
                "WindowsGraphicsScheduling",
                $"SetMaximumFrameLatency threw owner={owner} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TrySetGpuPriority(ID3D11Device device, string owner)
    {
        try
        {
            using var dxgiDevice = device.QueryInterfaceOrNull<IDXGIDevice>();
            if (dxgiDevice is null)
            {
                DebugTrace.WriteLine("WindowsGraphicsScheduling", $"GPU priority skipped owner={owner} reason=no-idxgidevice");
                return;
            }

            var schedulingClass = ResolveGpuSchedulingPriorityClass(dxgiDevice, owner);
            var status = D3DKMTSetProcessSchedulingPriorityClass(GetCurrentProcess(), schedulingClass);
            if (status != 0)
            {
                DebugTrace.WriteLine(
                    "WindowsGraphicsScheduling",
                    $"D3DKMTSetProcessSchedulingPriorityClass failed owner={owner} class={schedulingClass} status=0x{status:X8}");
                return;
            }

            var priority = ResolveGpuThreadPriority();
            var result = dxgiDevice.SetGPUThreadPriority(priority);
            if (result.Failure)
            {
                DebugTrace.WriteLine(
                    "WindowsGraphicsScheduling",
                    $"SetGPUThreadPriority failed owner={owner} priority={priority} hresult=0x{result.Code:X8}");
                return;
            }

            DebugTrace.WriteLine(
                "WindowsGraphicsScheduling",
                $"GPU priority success owner={owner} class={schedulingClass} threadPriority={priority}");
        }
        catch (Exception ex)
        {
            DebugTrace.WriteLine(
                "WindowsGraphicsScheduling",
                $"GPU priority threw owner={owner} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private static D3DKMTSchedulingPriorityClass ResolveGpuSchedulingPriorityClass(IDXGIDevice dxgiDevice, string owner)
    {
        var overrideValue = Environment.GetEnvironmentVariable("SPOUT_DIRECT_SAVER_GPU_SCHEDULING_CLASS");
        var overridden = overrideValue?.Trim().ToLowerInvariant() switch
        {
            "realtime" => D3DKMTSchedulingPriorityClass.Realtime,
            "high" => D3DKMTSchedulingPriorityClass.High,
            "abovenormal" => D3DKMTSchedulingPriorityClass.AboveNormal,
            _ => (D3DKMTSchedulingPriorityClass?)null
        };

        if (overridden is not null)
        {
            return overridden.Value;
        }

        var hagsEnabled = TryGetHagsEnabled(dxgiDevice, owner);
        return hagsEnabled switch
        {
            true => D3DKMTSchedulingPriorityClass.High,
            false => D3DKMTSchedulingPriorityClass.Realtime,
            null => D3DKMTSchedulingPriorityClass.High
        };
    }

    private static int ResolveGpuThreadPriority()
    {
        var overrideValue = Environment.GetEnvironmentVariable("SPOUT_DIRECT_SAVER_GPU_THREAD_PRIORITY");
        if (!string.IsNullOrWhiteSpace(overrideValue) && TryParsePriority(overrideValue, out var parsed))
        {
            return parsed;
        }

        // OBS injects this at build time; default to the documented absolute hard-realtime priority and allow override.
        return DefaultGpuThreadPriority;
    }

    private static bool? TryGetHagsEnabled(IDXGIDevice dxgiDevice, string owner)
    {
        try
        {
            dxgiDevice.GetAdapter(out var adapter).CheckError();
            using (adapter)
            {
                using var adapter1 = adapter.QueryInterfaceOrNull<IDXGIAdapter1>();
                if (adapter1 is null)
                {
                    DebugTrace.WriteLine("WindowsGraphicsScheduling", $"HAGS query skipped owner={owner} reason=no-idxgiadapter1");
                    return null;
                }

                var description = adapter1.Description1;
                var openArgs = new D3DKMTOpenAdapterFromLuid
                {
                    AdapterLuid = new NativeLuid
                    {
                        LowPart = description.Luid.LowPart,
                        HighPart = description.Luid.HighPart
                    }
                };

                var status = D3DKMTOpenAdapterFromLuidNative(ref openArgs);
                if (status != 0)
                {
                    DebugTrace.WriteLine(
                        "WindowsGraphicsScheduling",
                        $"HAGS open adapter failed owner={owner} status=0x{status:X8}");
                    return null;
                }

                try
                {
                    var caps = new D3DKMTWddm27Caps();
                    var query = new D3DKMTQueryAdapterInfo
                    {
                        AdapterHandle = openArgs.AdapterHandle,
                        Type = KmtQueryAdapterInfoTypeWddm27Caps,
                        PrivateDriverData = Marshal.AllocHGlobal(Marshal.SizeOf<D3DKMTWddm27Caps>()),
                        PrivateDriverDataSize = (uint)Marshal.SizeOf<D3DKMTWddm27Caps>()
                    };

                    try
                    {
                        Marshal.StructureToPtr(caps, query.PrivateDriverData, false);
                        status = D3DKMTQueryAdapterInfoNative(ref query);
                        if (status != 0)
                        {
                            DebugTrace.WriteLine(
                                "WindowsGraphicsScheduling",
                                $"HAGS query failed owner={owner} status=0x{status:X8}");
                            return null;
                        }

                        caps = Marshal.PtrToStructure<D3DKMTWddm27Caps>(query.PrivateDriverData);
                        var enabled = (caps.Value & (1u << 1)) != 0;
                        var supported = (caps.Value & 1u) != 0;
                        DebugTrace.WriteLine(
                            "WindowsGraphicsScheduling",
                            $"HAGS query success owner={owner} supported={supported} enabled={enabled} raw=0x{caps.Value:X8}");
                        return enabled;
                    }
                    finally
                    {
                        if (query.PrivateDriverData != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(query.PrivateDriverData);
                        }
                    }
                }
                finally
                {
                    var closeArgs = new D3DKMTCloseAdapter
                    {
                        AdapterHandle = openArgs.AdapterHandle
                    };
                    var closeStatus = D3DKMTCloseAdapterNative(ref closeArgs);
                    if (closeStatus != 0)
                    {
                        DebugTrace.WriteLine(
                            "WindowsGraphicsScheduling",
                            $"HAGS close adapter failed owner={owner} status=0x{closeStatus:X8}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugTrace.WriteLine(
                "WindowsGraphicsScheduling",
                $"HAGS query threw owner={owner} error={ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool TryParsePriority(string text, out int value)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTSetProcessSchedulingPriorityClass")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(
        IntPtr processHandle,
        D3DKMTSchedulingPriorityClass priorityClass);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTOpenAdapterFromLuid")]
    private static extern int D3DKMTOpenAdapterFromLuidNative(ref D3DKMTOpenAdapterFromLuid openAdapterFromLuid);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTQueryAdapterInfo")]
    private static extern int D3DKMTQueryAdapterInfoNative(ref D3DKMTQueryAdapterInfo queryAdapterInfo);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTCloseAdapter")]
    private static extern int D3DKMTCloseAdapterNative(ref D3DKMTCloseAdapter closeAdapter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMTOpenAdapterFromLuid
    {
        public NativeLuid AdapterLuid;
        public uint AdapterHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMTQueryAdapterInfo
    {
        public uint AdapterHandle;
        public uint Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMTCloseAdapter
    {
        public uint AdapterHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMTWddm27Caps
    {
        public uint Value;
    }

    private enum D3DKMTSchedulingPriorityClass
    {
        Idle = 0,
        BelowNormal = 1,
        Normal = 2,
        AboveNormal = 3,
        High = 4,
        Realtime = 5
    }
}
