using System.Runtime.InteropServices;

namespace ClipVoice.Services;

// Reads the peak of the default capture endpoint without opening another recorder.
internal sealed class MicrophoneMeter : IDisposable
{
    private object? _enumerator;
    private IMMDevice? _device;
    private IAudioMeterInformation? _meter;

    public MicrophoneMeter()
    {
        try
        {
            _enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!);
            Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)_enumerator!).GetDefaultAudioEndpoint(1, 0, out var device));
            _device = device;
            var iid = typeof(IAudioMeterInformation).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out var meter));
            _meter = (IAudioMeterInformation)meter;
        }
        catch { Dispose(); }
    }

    public float ReadPeak()
    {
        try { return _meter is not null && _meter.GetPeakValue(out var peak) >= 0 ? Math.Clamp(peak, 0, 1) : 0; }
        catch { return 0; }
    }

    public void Dispose()
    {
        if (_meter is not null) Marshal.ReleaseComObject(_meter);
        if (_device is not null) Marshal.ReleaseComObject(_device);
        if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator);
        _meter = null; _device = null; _enumerator = null;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
