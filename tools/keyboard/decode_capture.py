"""Reassemble report-0x0A messages from a sniff log and print a compact timeline."""
import sys

LOG = sys.argv[1]
SHOW_RT = "--rt" in sys.argv


def messages(path):
    cur = None
    for line in open(path):
        p = line.split()
        t, b = p[0], bytes.fromhex("".join(p[4:]))
        if not b or b[0] != 0x0A:
            continue
        tot, idx = b[1], b[2]
        if idx == 1:
            cur = [t, tot, bytearray()]
        if cur is None:
            continue
        cur[2] += b[3:65].ljust(62, b"\0")
        if idx == tot:
            yield cur[0], cur[1], bytes(cur[2])
            cur = None


last_rt = None
for t, tot, d in messages(LOG):
    cmd = d[:2].hex()
    if d[0] == 0x06:  # realtime frame: 06 00 + 126*RGB
        rgb = d[2:2 + 378]
        lit = {i: rgb[i * 3:i * 3 + 3].hex() for i in range(126) if any(rgb[i * 3:i * 3 + 3])}
        sig = tuple(sorted(lit.items()))
        if sig != last_rt or SHOW_RT:
            print(f"{t} RT  pk={tot} lit={len(lit)} {dict(list(lit.items())[:10])}")
        last_rt = sig
    elif d[0] == 0x03:
        rgb = d[3:3 + 378]
        lit = {i: rgb[i * 3:i * 3 + 3].hex() for i in range(126) if any(rgb[i * 3:i * 3 + 3])}
        print(f"{t} KEYCOLOR pk={tot} hdr={d[:3].hex(' ')} lit={len(lit)} {dict(list(lit.items())[:10])}")
    elif d[0] == 0x02:
        print(f"{t} LED {d[:12].hex(' ')}")
    else:
        print(f"{t} {cmd} pk={tot} {d[:24].hex(' ')} ...")
