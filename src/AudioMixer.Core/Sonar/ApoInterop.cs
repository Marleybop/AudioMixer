// COM declarations for the Sonar APO settings API exposed by Sonar.APOAPI.dll.
// The interfaces are dual (IDispatch-based); only vtable order matters, so methods we never call are
// declared as parameterless placeholders to keep their slots.
using System.Runtime.InteropServices;

namespace AudioMixer.Core.Sonar;

internal enum ApoSettingType
{
    Boolean,
    LongInt,
    Float,
    FloatCoupleArray,
    FloatArray,
    String,
    ShortStringArray,
    ByteArray,
}

internal enum ApoSettingDevice
{
    Render,
    RenderCommunication,
    Capture,
    VadService,
    SonicMapperDisplay,
    ApplicationDetection,
    All,
}

[ComImport, Guid("40076C40-0E9C-4D14-9CF6-34839A766B8A"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IApoBasicControl
{
    void GetStreamCount(out int count);
    void GetApplicationCount(out int count);
    void Reserved_GetApplication();
    void Reserved_AddApplication();
    void Reserved_RemoveApplication();
    void Reserved_OpenApplication();
    void Reserved_OpenDefaultApplication();
    void GetProfileCount(out int count);
    void Reserved_GetProfile();
    void Reserved_AddProfile();
    void Reserved_RemoveProfile();
    void Reserved_OpenProfile();
    void Reserved_OpenDefaultProfile();
    void OpenStore([MarshalAs(UnmanagedType.Interface)] out IApoStore store);
    void Reserved_GetSettingSource();
    void GetVUMeterEnergy(ApoSetting audioChannel, ApoSettingDevice device, out float energy);
    void Reserved_GetFormFactorOverride();
    void Reserved_SetFormFactorOverride();
    void Reserved_RegisterNotificationClient();
    void Reserved_UnregisterNotificationClient();
    void Reserved_OpenApplicationById();
    void Reserved_DuplicateProfile();
    void Reserved_OpenEqPresetStore();
    void Reserved_AddEqPreset();
    void Reserved_GetCaptureEqPresetCount();
    void Reserved_GetCaptureEqPresetName();
    void Reserved_SetCurrentCaptureEqPreset();
    void Reserved_GetCurrentCaptureEqPreset();
    void Reserved_SetUseGlobalProfile();
    void Reserved_GetUseGlobalProfile();
    void Reserved_AddProfileFromGUID();
    void Reserved_SetGlobalProfile();
    void Reserved_GetGlobalProfile();
    void Reserved_AddApplicationWithId();
    void Initialize([MarshalAs(UnmanagedType.BStr)] string productId);
}

[ComImport, Guid("1191FCBC-AD6E-4991-992C-CA966DF163BC"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IApoStore
{
    void GetSettingCount(out int count);
    void Reserved_GetSetting();
    void OpenSetting(ApoSetting setting, [MarshalAs(UnmanagedType.Interface)] out IApoSetting value);
}

[ComImport, Guid("A108D082-10D7-11E4-9191-0800200C9A66"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IApoSetting
{
    void GetSetting(out ApoSetting setting);
    void GetSettingType(out ApoSettingType type);
    void GetState([MarshalAs(UnmanagedType.VariantBool)] out bool state);
    void Reserved_GetClass();
    void Reserved_GetDevice();
    void Reserved_GetSource();
    void GetName([MarshalAs(UnmanagedType.BStr)] out string name);
    void GetBoolTraits([MarshalAs(UnmanagedType.VariantBool)] out bool defaultValue);
    void GetLongTraits(out int defaultValue, out int min, out int max);
    void GetFloatTraits(out float defaultValue, out float min, out float max);
    void GetBoolSetting([MarshalAs(UnmanagedType.VariantBool)] out bool value);
    void GetLongSetting(out int value);
    void GetFloatSetting(out float value);
    void GetStringSetting([MarshalAs(UnmanagedType.BStr)] out string value);
    void Reserved_GetFltArraySetting();
    void Reserved_GetFltCoupleArraySetting();
    void Reserved_GetStringArraySetting();
    void Reserved_GetByteArraySetting();
    void SetBoolSetting([MarshalAs(UnmanagedType.VariantBool)] bool value);
    void SetLongSetting(int value);
    void SetFloatSetting(float value);
    void SetStringSetting([MarshalAs(UnmanagedType.BStr)] string value);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    void CreateInstance(IntPtr outer, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    void LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
}
