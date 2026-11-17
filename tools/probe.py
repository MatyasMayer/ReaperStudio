"""Read-only probe for the CZC Gaming Reaper (SinoWealth 258A:00C1).

Enumerates the keyboard's HID collections and, on the vendor collection with a
520-byte feature report, issues the same GetLED read (cmd 0x84) that the
official DeviceDriver.exe performs on startup. Nothing is written to flash.
Uses only ctypes + Windows hid.dll / setupapi.dll.
"""
import ctypes as C
import ctypes.wintypes as W
import sys

VID, PID = 0x258A, 0x00C1

hid = C.WinDLL("hid.dll")
setupapi = C.WinDLL("setupapi.dll")
k32 = C.WinDLL("kernel32.dll", use_last_error=True)


class GUID(C.Structure):
    _fields_ = [("D1", W.DWORD), ("D2", W.WORD), ("D3", W.WORD), ("D4", C.c_ubyte * 8)]


class SP_DEVICE_INTERFACE_DATA(C.Structure):
    _fields_ = [("cbSize", W.DWORD), ("Guid", GUID), ("Flags", W.DWORD), ("Reserved", C.c_void_p)]


class HIDD_ATTRIBUTES(C.Structure):
    _fields_ = [("Size", W.ULONG), ("VendorID", W.USHORT), ("ProductID", W.USHORT), ("Version", W.USHORT)]


class HIDP_CAPS(C.Structure):
    _fields_ = [("Usage", W.USHORT), ("UsagePage", W.USHORT),
                ("InputReportByteLength", W.USHORT), ("OutputReportByteLength", W.USHORT),
                ("FeatureReportByteLength", W.USHORT), ("Reserved", W.USHORT * 17),
                ("rest", W.USHORT * 10)]


k32.CreateFileW.restype = W.HANDLE
setupapi.SetupDiGetClassDevsW.restype = C.c_void_p
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [C.c_void_p, C.c_void_p, C.POINTER(GUID), W.DWORD, C.POINTER(SP_DEVICE_INTERFACE_DATA)]
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [C.c_void_p, C.POINTER(SP_DEVICE_INTERFACE_DATA), C.c_void_p, W.DWORD, C.POINTER(W.DWORD), C.c_void_p]
hid.HidD_SetFeature.argtypes = [W.HANDLE, C.c_void_p, W.ULONG]
hid.HidD_GetFeature.argtypes = [W.HANDLE, C.c_void_p, W.ULONG]
INVALID = W.HANDLE(-1).value


def enum_paths():
    g = GUID()
    hid.HidD_GetHidGuid(C.byref(g))
    h = setupapi.SetupDiGetClassDevsW(C.byref(g), None, None, 0x12)  # PRESENT|INTERFACEDEVICE
    i = 0
    while True:
        d = SP_DEVICE_INTERFACE_DATA(); d.cbSize = C.sizeof(d)
        if not setupapi.SetupDiEnumDeviceInterfaces(h, None, C.byref(g), i, C.byref(d)):
            break
        i += 1
        need = W.DWORD()
        setupapi.SetupDiGetDeviceInterfaceDetailW(h, C.byref(d), None, 0, C.byref(need), None)
        buf = C.create_string_buffer(need.value)
        C.cast(buf, C.POINTER(W.DWORD))[0] = 8 if C.sizeof(C.c_void_p) == 8 else 6
        if setupapi.SetupDiGetDeviceInterfaceDetailW(h, C.byref(d), buf, need, None, None):
            yield C.wstring_at(C.addressof(buf) + 4)
    setupapi.SetupDiDestroyDeviceInfoList(h)


def open_path(p):
    return k32.CreateFileW(p, 0xC0000000, 3, None, 3, 0, None)  # RW, share RW, OPEN_EXISTING


def caps(h):
    pp = C.c_void_p()
    if not hid.HidD_GetPreparsedData(h, C.byref(pp)):
        return None
    c = HIDP_CAPS()
    hid.HidP_GetCaps(pp, C.byref(c))
    hid.HidD_FreePreparsedData(pp)
    return c


def g5_read(h, cmd, length, arg=0):
    """CDevG5KB::AccessData read path: SetFeature(header) then GetFeature."""
    out = bytearray()
    chunks = (length + 511) // 512
    for idx in range(chunks):
        n = min(512, length - idx * 512)
        pkt = (C.c_ubyte * 0x208)()
        pkt[0], pkt[1], pkt[2], pkt[3], pkt[4], pkt[5] = 6, cmd, arg, 0, chunks, idx
        pkt[6], pkt[7] = n & 0xFF, n >> 8
        if not hid.HidD_SetFeature(h, pkt, 0x208):
            raise OSError(C.get_last_error(), "SetFeature failed")
        if not hid.HidD_GetFeature(h, pkt, 0x208):
            raise OSError(C.get_last_error(), "GetFeature failed")
        out += bytes(pkt[8:8 + n])
    return bytes(out)


def hexdump(b):
    for o in range(0, len(b), 16):
        print(f"  {o:04x}: " + " ".join(f"{x:02x}" for x in b[o:o + 16]))


def main():
    target = None
    for p in enum_paths():
        if f"vid_{VID:04x}&pid_{PID:04x}" not in p.lower():
            continue
        h = open_path(p)
        if h in (None, INVALID):
            h = k32.CreateFileW(p, 0, 3, None, 3, 0, None)  # attributes-only open
        c = caps(h) if h not in (None, INVALID) else None
        if c:
            print(f"{p}\n  usage_page=0x{c.UsagePage:04x} usage=0x{c.Usage:02x} in={c.InputReportByteLength} "
                  f"out={c.OutputReportByteLength} feature={c.FeatureReportByteLength}")
            if c.FeatureReportByteLength == 0x208 and target is None:
                target = p
        if h not in (None, INVALID):
            k32.CloseHandle(h)
    if not target:
        print("No 520-byte feature collection found"); return 1
    print("\nG5KB control collection:", target)
    h = open_path(target)
    led = g5_read(h, 0x84, 0x80)
    print("\nGetLED (cmd 0x84, 128 bytes):"); hexdump(led)
    if "--all" in sys.argv:
        print("\nGetMacroBufferSize (cmd 0x82 arg 2):"); hexdump(g5_read(h, 0x82, 4, arg=2))
    k32.CloseHandle(h)


if __name__ == "__main__":
    sys.exit(main())
