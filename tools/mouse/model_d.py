"""Minimal driver for the Glorious Model D Wireless (SinoWealth 258A:2012 wired, 258A:2023 dongle).

Read from the vendor app Glorious CORE (Electron; protocol in
src/electron-process/renderer-interop/protocol/device/mouse/ModelOSeries.js inside app.asar).
See docs/mouse/PROTOCOL.md.

    python tools/mouse/model_d.py info
    python tools/mouse/model_d.py color 0 255 0 [--zone wheel|left|right] [--brightness 100]
    python tools/mouse/model_d.py effect 1 [--zone ...] [--speed 60]
    python tools/mouse/model_d.py off
"""
import argparse
import ctypes as C
import ctypes.wintypes as W
import time

VID = 0x258A
PIDS = {0x2012: "wired", 0x2023: "dongle"}
REPORT_LEN = 65

# Status byte at the start of every reply.
STATUS = {0xA1: "ok", 0xA4: "mouse not reachable", 0xA2: "failed", 0xA0: "busy", 0xA3: "unsupported"}

# Firmware effect ids (Glorious CORE stores the UI index; firmware id = UI index + 1, UI 8 -> 0).
EFFECTS = {0: "off", 1: "glorious", 2: "seamless breathing", 3: "breathing (up to 6 colours)",
           4: "single colour", 5: "breathing single colour", 6: "tail", 7: "rave (2 colours)", 8: "wave"}

# Zone byte of the effect command (Glorious CORE only ever sends ALL_ZONES).
ZONES = {"all": 0xFF, "wheel": 0, "left": 1, "right": 2}

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
hid.HidD_GetFeature.argtypes = [W.HANDLE, C.c_void_p, W.ULONG]
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


class ModelD:
    """Opens the vendor collection: usage page 0xFFFF, usage 0x00, 65-byte feature report."""

    def __init__(self):
        self.h = None
        self.link = None
        for p in _paths():
            low = p.lower()
            pid = next((pid for pid in PIDS if f"vid_{VID:04x}&pid_{pid:04x}" in low), None)
            if pid is None:
                continue
            h = k32.CreateFileW(p, 0xC0000000, 3, None, 3, 0, None)
            if h in (None, _INVALID):
                continue
            pp = C.c_void_p(); caps = _CAPS()
            if hid.HidD_GetPreparsedData(h, C.byref(pp)):
                hid.HidP_GetCaps(pp, C.byref(caps)); hid.HidD_FreePreparsedData(pp)
            if caps.UsagePage == 0xFFFF and caps.Usage == 0x00 and caps.Feature == REPORT_LEN:
                self.h, self.link = h, PIDS[pid]
                break
            k32.CloseHandle(h)
        if not self.h:
            raise RuntimeError("Glorious Model D Wireless control interface not found")

    def close(self):
        if self.h:
            k32.CloseHandle(self.h); self.h = None

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()

    # --- transport ---
    def _set(self, cmd: bytes):
        """cmd = bytes after the report ID: [target, size, group, op, args...]."""
        pkt = (C.c_ubyte * REPORT_LEN)(0x00, 0x00, 0x00, *cmd)
        if not hid.HidD_SetFeature(self.h, pkt, REPORT_LEN):
            raise OSError(C.get_last_error(), "HidD_SetFeature failed")

    def _get(self) -> bytes:
        pkt = (C.c_ubyte * REPORT_LEN)()
        if not hid.HidD_GetFeature(self.h, pkt, REPORT_LEN):
            raise OSError(C.get_last_error(), "HidD_GetFeature failed")
        return bytes(pkt)[1:]  # drop report ID; [0] = status

    def query(self, target, size, op, delay=0.05):
        self._set(bytes([target, size, 0x00, op]))
        time.sleep(delay)
        return self._get()

    def command(self, cmd: bytes, delay=0.03):
        self._set(cmd)
        time.sleep(delay)

    # --- read-only queries ---
    def version(self, target):
        """0x81. target 0x00 = dongle, 0x02 = mouse."""
        r = self.query(target, 0x03, 0x81, 0.1)
        return STATUS.get(r[0], hex(r[0])), ".".join(str(b) for b in r[6:10])

    def battery(self):
        """0x83 -> (status, percent, charging)."""
        r = self.query(0x02, 0x02, 0x83)
        return STATUS.get(r[0], hex(r[0])), r[7], bool(r[6])

    def profile(self):
        """0x85 -> active hardware profile (1..3)."""
        return self.query(0x02, 0x01, 0x85)[6]

    # --- lighting ---
    def set_effect(self, effect, colors=(), speed=60, profile=None, zone=ZONES["all"]):
        """02 00: effect for one zone (or all) of a profile. speed is Glorious CORE's 0..100 slider."""
        if profile is None:
            profile = self.profile()
        raw_speed = int((105 - speed) * 2 if effect in (7, 8) else (105 - speed) / 5)
        rgb = bytearray()
        for r, g, b in colors:
            rgb += bytes((1, 0, 0) if (r, g, b) == (0, 0, 0) else (r, g, b))
        self.command(bytes([0x02, len(colors) * 3 + 5, 0x02, 0x00, profile, zone, effect, 0x00, raw_speed]) + rgb)

    def set_brightness(self, percent, wireless_percent=None):
        """02 02: brightness 0..100 % for wired (1) and wireless (0) mode."""
        if wireless_percent is None:
            wireless_percent = percent
        for mode, pct in ((1, percent), (0, wireless_percent)):
            self.command(bytes([0x02, 0x02, 0x02, 0x02, mode, int(pct / 100 * 255)]))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("info")
    c = sub.add_parser("color"); c.add_argument("rgb", type=int, nargs=3)
    c.add_argument("--brightness", type=int, default=None)
    c.add_argument("--zone", choices=ZONES, default="all")
    e = sub.add_parser("effect"); e.add_argument("id", type=int, choices=sorted(EFFECTS))
    e.add_argument("--speed", type=int, default=60)
    e.add_argument("--zone", choices=ZONES, default="all")
    sub.add_parser("off")
    a = ap.parse_args()

    with ModelD() as m:
        if a.cmd == "info":
            print("link:           ", m.link)
            print("dongle version: ", *m.version(0x00))
            print("mouse version:  ", *m.version(0x02))
            print("battery:        ", *m.battery())
            print("active profile: ", m.profile())
        elif a.cmd == "color":
            m.set_effect(4, [tuple(a.rgb)], zone=ZONES[a.zone])
            if a.brightness is not None:
                m.set_brightness(a.brightness)
        elif a.cmd == "effect":
            m.set_effect(a.id, speed=a.speed, zone=ZONES[a.zone])
        elif a.cmd == "off":
            m.set_effect(0)


if __name__ == "__main__":
    main()
