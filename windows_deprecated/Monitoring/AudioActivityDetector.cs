using System.Runtime.InteropServices;

namespace AiLife.WindowsAgent.Monitoring;

public sealed class AudioActivityDetector
{
    private const float ActivePeakThreshold = 0.005f;

    public bool IsAudioPlaying()
    {
        object? enumeratorObject = null;
        object? deviceObject = null;
        object? meterObject = null;

        try
        {
            enumeratorObject = new MMDeviceEnumerator();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);
            if (hr != 0 || device is null) return false;
            deviceObject = device;

            var meterId = typeof(IAudioMeterInformation).GUID;
            hr = device.Activate(ref meterId, ClsCtx.All, IntPtr.Zero, out meterObject);
            if (hr != 0 || meterObject is not IAudioMeterInformation meter) return false;

            hr = meter.GetPeakValue(out var peakValue);
            return hr == 0 && peakValue >= ActivePeakThreshold;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(meterObject);
            ReleaseComObject(deviceObject);
            ReleaseComObject(enumeratorObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private enum EDataFlow
    {
        Render = 0,
    }

    private enum ERole
    {
        Multimedia = 1,
    }

    [Flags]
    private enum ClsCtx : uint
    {
        All = 0x17,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, ClsCtx clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? interfaceObject);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);

        [PreserveSig]
        int GetMeteringChannelCount(out uint channelCount);

        [PreserveSig]
        int GetChannelsPeakValues(uint channelCount, [Out] float[] peakValues);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);
    }
}

