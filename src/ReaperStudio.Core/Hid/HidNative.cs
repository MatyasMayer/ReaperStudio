using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReaperStudio.Core.Hid;

internal static partial class HidNative
{
    public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    public const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    public const uint DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out nint data);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(nint data, ref HidpCaps caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    public static extern nint SetupDiGetClassDevsW(ref Guid g, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll")]
    public static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid g, int index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref SpDeviceInterfaceData data, nint detail, int size, out int required, nint devInfo);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint sa, uint disposition, uint flags, nint template);

    public static IEnumerable<string> EnumerateHidPaths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, null, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == -1) yield break;
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref data)) yield break;
                SetupDiGetDeviceInterfaceDetailW(set, ref data, 0, 0, out int size, 0);
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buf, nint.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, size, out _, 0))
                        yield return Marshal.PtrToStringUni(buf + 4)!;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static HidpCaps? GetCaps(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out var pp)) return null;
        try
        {
            var caps = new HidpCaps { Reserved = new ushort[17] };
            return HidP_GetCaps(pp, ref caps) == 0x110000 /* HIDP_STATUS_SUCCESS */ ? caps : null;
        }
        finally { HidD_FreePreparsedData(pp); }
    }
}
