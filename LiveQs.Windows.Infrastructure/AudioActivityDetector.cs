using System.Runtime.InteropServices;

namespace LiveQs.Windows.Infrastructure;

internal sealed class AudioActivityDetector
{
    private const float ActivePeakThreshold = 0.005f;

    internal bool IsAudioPlaying()
    {
        object? enumeratorObject = null;
        object? deviceObject = null;
        object? meterObject = null;
        try
        {
            enumeratorObject = new MmDeviceEnumerator();
            var enumerator = (IMmDeviceEnumerator)enumeratorObject;
            if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) != 0 || device is null)
                return false;
            deviceObject = device;
            var meterId = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref meterId, ClassContext.All, nint.Zero, out meterObject) != 0 || meterObject is not IAudioMeterInformation meter)
                return false;
            return meter.GetPeakValue(out var peak) == 0 && peak >= ActivePeakThreshold;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            Release(meterObject);
            Release(deviceObject);
            Release(enumeratorObject);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private enum DataFlow { Render }
    private enum Role { Console, Multimedia }
    [Flags] private enum ClassContext : uint { All = 0x17 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MmDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMmDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMmDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMmDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, ClassContext context, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? value);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out uint channelCount);
        [PreserveSig] int GetChannelsPeakValues(uint channelCount, [Out] float[] peakValues);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
    }
}
