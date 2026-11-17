"""Minimal driver for the CZC Gaming Reaper (SinoWealth 258A:00C1, CDevRKKB protocol).

Reverse-engineered from DeviceDriver.exe (CDevRKKB @ vtable 0x5b1a0c) and
verified against captures of the official app. See docs/keyboard/PROTOCOL.md.
"""
import ctypes as C
import ctypes.wintypes as W

VID, PID = 0x258A, 0x00C1
NUM_LEDS = 126

hid = C.WinDLL("hid.dll")
setupapi = C.WinDLL("setupapi.dll")
k32 = C.WinDLL("kernel32.dll", use_last_error=True)


class _GUID(C.Structure):
    _fields_ = [("D1", W.DWORD), ("D2", W.WORD), ("D3", W.WORD), ("D4", C.c_ubyte * 8)]


class _IFDATA(C.Structure):
    _fields_ = [("cbSize", W.DWORD), ("Guid", _GUID), ("Flags", W.DWORD), ("Reserved", C.c_void_p)]


class _CAPS(C.Structure):
    _fields_ = [("Usage", W.USHORT), ("UsagePage", W.USHORT), ("In", W.USHORT), ("Out", W.USHORT),
                ("Feature", W.USHORT), ("Reserved", W.USHORT * 27)]


k32.CreateFileW.restype = W.HANDLE
k32.CloseHandle.argtypes = [W.HANDLE]
setupapi.SetupDiGetClassDevsW.restype = C.c_void_p
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [C.c_void_p, C.c_void_p, C.POINTER(_GUID), W.DWORD, C.POINTER(_IFDATA)]
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [C.c_void_p, C.POINTER(_IFDATA), C.c_void_p, W.DWORD, C.POINTER(W.DWORD), C.c_void_p]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [C.c_void_p]
hid.HidD_SetFeature.argtypes = [W.HANDLE, C.c_void_p, W.ULONG]
hid.HidD_GetPreparsedData.argtypes = [W.HANDLE, C.POINTER(C.c_void_p)]
hid.HidD_FreePreparsedData.argtypes = [C.c_void_p]
hid.HidP_GetCaps.argtypes = [C.c_void_p, C.POINTER(_CAPS)]
_INVALID = W.HANDLE(-1).value


def _paths():
    g = _GUID()
    hid.HidD_GetHidGuid(C.byref(g))
    h = setupapi.SetupDiGetClassDevsW(C.byref(g), None, None, 0x12)
    i = 0
    try:
        while True:
            d = _IFDATA(); d.cbSize = C.sizeof(d)
            if not setupapi.SetupDiEnumDeviceInterfaces(h, None, C.byref(g), i, C.byref(d)):
                return
            i += 1
            need = W.DWORD()
            setupapi.SetupDiGetDeviceInterfaceDetailW(h, C.byref(d), None, 0, C.byref(need), None)
            buf = C.create_string_buffer(need.value)
            C.cast(buf, C.POINTER(W.DWORD))[0] = 8 if C.sizeof(C.c_void_p) == 8 else 6
            if setupapi.SetupDiGetDeviceInterfaceDetailW(h, C.byref(d), buf, need, None, None):
                yield C.wstring_at(C.addressof(buf) + 4)
    finally:
        setupapi.SetupDiDestroyDeviceInfoList(h)


class Keyboard:
    """Opens the vendor collection: usage page 0xFF00, usage 1, 65-byte feature report."""

    def __init__(self):
        self.h = None
        for p in _paths():
            if f"vid_{VID:04x}&pid_{PID:04x}" not in p.lower():
                continue
            h = k32.CreateFileW(p, 0xC0000000, 3, None, 3, 0, None)
            if h in (None, _INVALID):
                continue
            pp = C.c_void_p(); caps = _CAPS()
            if hid.HidD_GetPreparsedData(h, C.byref(pp)):
                hid.HidP_GetCaps(pp, C.byref(caps)); hid.HidD_FreePreparsedData(pp)
            if caps.UsagePage == 0xFF00 and caps.Usage == 1 and caps.Feature == 65:
                self.h = h
                break
            k32.CloseHandle(h)
        if not self.h:
            raise RuntimeError("CZC Gaming Reaper control interface not found")

    def close(self):
        if self.h:
            k32.CloseHandle(self.h); self.h = None

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()

    # --- transport (FUN_0040fe80) ---
    def send(self, msg: bytes):
        chunks = [msg[i:i + 62] for i in range(0, len(msg), 62)]
        for n, chunk in enumerate(chunks, 1):
            pkt = (C.c_ubyte * 65)(0x0A, len(chunks), n, *chunk)
            if not hid.HidD_SetFeature(self.h, pkt, 65):
                raise OSError(C.get_last_error(), "HidD_SetFeature failed")

    # --- messages ---
    def set_effect(self, mode, speed=4, brightness=5, rgb=(255, 0, 0), colorful=False, direction=1):
        """02 29: built-in effect. mode 1..21, speed 1..5, brightness 0..5."""
        r, g, b = rgb
        msg = bytes([0x02, 0x29, mode, 0, speed, brightness, r, g, b, int(colorful), direction])
        self.send(msg.ljust(60, b"\0"))

    def set_custom_colors(self, slot, colors):
        """03 7E: store per-key colours for Customize (mode 0x0E) in slot 1..n. colors: LED index -> (r,g,b)."""
        body = bytearray(NUM_LEDS * 3)
        for i, (r, g, b) in colors.items():
            body[i * 3:i * 3 + 3] = bytes((r, g, b))
        self.send(bytes([0x03, 0x7E, slot]) + bytes(body))

    def frame(self, colors):
        """06 00: volatile real-time frame (what music/ambilight mode streams)."""
        body = bytearray(NUM_LEDS * 3)
        for i, (r, g, b) in colors.items():
            body[i * 3:i * 3 + 3] = bytes((r, g, b))
        self.send(bytes([0x06, 0x00]) + bytes(body))
