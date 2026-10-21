using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ForgePad.Core;

/// <summary>
/// Переключение устройства ввода по умолчанию через недокументированный IPolicyConfig.
/// Тот же механизм использует панель «Звук» Windows, поэтому поведение совпадает с ручным выбором.
/// </summary>
public static class AudioDevicePolicy
{
    private const int EConsole = 0;
    private const int EMultimedia = 1;
    private const int ECommunications = 2;

    /// <summary>ID устройства ввода, которое сейчас стоит по умолчанию для связи.</summary>
    public static string? GetDefaultCaptureId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ID устройства ввода по части имени — чтобы не зависеть от локали.</summary>
    public static string? FindCaptureId(string namePart)
    {
        using var enumerator = new MMDeviceEnumerator();

        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (device.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase))
                return device.ID;
        }

        return null;
    }

    /// <summary>Ставит устройство ввода по умолчанию во все три роли: система, мультимедиа, связь.</summary>
    public static void SetDefaultCapture(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();

        foreach (int role in new[] { EConsole, EMultimedia, ECommunications })
        {
            int hr = policy.SetDefaultEndpoint(deviceId, role);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceName, out IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceName, bool isDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceName);
        [PreserveSig] int SetDeviceFormat(string deviceName, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceName, bool isDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceName, ref long period);
        [PreserveSig] int GetShareMode(string deviceName, out IntPtr mode);
        [PreserveSig] int SetShareMode(string deviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string deviceName, IntPtr key, out IntPtr value);
        [PreserveSig] int SetPropertyValue(string deviceName, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint(string deviceName, int role);
        [PreserveSig] int SetEndpointVisibility(string deviceName, int visible);
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }
}
