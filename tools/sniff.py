"""Log every HID feature report the official CZC app sends/receives.

Usage: python tools/sniff.py            (attaches to running DeviceDriver.exe,
                                          or spawns it if not running)
Output goes to stdout and tools/captures/<timestamp>.log
Each line: direction, length, hex bytes. Only observes; never alters data.
"""
import datetime
import os
import sys
import frida

APP = r"C:\Users\matya\.t3\projects\czcreaper2software\source\CZC.Gaming Reaper\DeviceDriver.exe"

JS = r"""
function attachAll() {
  for (const [n, d] of [['HidD_SetFeature','SET'], ['HidD_GetFeature','GET'],
                        ['HidD_SetOutputReport','OUT'], ['HidD_GetInputReport','IN']]) {
    const p = Process.getModuleByName('hid.dll').getExportByName(n);
    Interceptor.attach(p, {
      onEnter(a) {
        this.buf = a[1]; this.len = a[2].toInt32();
        if (d === 'SET' || d === 'OUT') send({d, b: Array.from(new Uint8Array(this.buf.readByteArray(this.len)))});
      },
      onLeave(r) {
        if (d === 'GET' || d === 'IN') send({d, ok: r.toInt32(), b: Array.from(new Uint8Array(this.buf.readByteArray(this.len)))});
      }
    });
  }
}
attachAll();
"""


def main():
    os.makedirs(os.path.join(os.path.dirname(__file__), "captures"), exist_ok=True)
    log_path = os.path.join(os.path.dirname(__file__), "captures",
                            datetime.datetime.now().strftime("%Y%m%d-%H%M%S") + ".log")
    log = open(log_path, "w", buffering=1)
    dev = frida.get_local_device()
    try:
        session = frida.attach("DeviceDriver.exe")
        pid = None
    except frida.ProcessNotFoundError:
        pid = dev.spawn([APP])
        session = dev.attach(pid)

    def on_msg(msg, _):
        if msg["type"] != "send":
            print(msg, file=sys.stderr); return
        p = msg["payload"]
        b = bytes(p["b"])
        # trim trailing zero padding for readability
        t = b.rstrip(b"\x00")
        line = f"{datetime.datetime.now():%H:%M:%S.%f}"[:-3] + f" {p['d']:3} len={len(b):3} " + t.hex(" ")
        print(line); log.write(line + "\n")

    script = session.create_script(JS)
    script.on("message", on_msg)
    script.load()
    if pid:
        dev.resume(pid)
    print(f"Sniffing... log -> {log_path}  (Ctrl+C to stop)")
    import time
    while True:
        time.sleep(1)


if __name__ == "__main__":
    main()
