"""Measure how often the keyboard stalls (≈5 s feature-report write) for different packet pacing.

Usage: python pacetest.py SECONDS_PER_PHASE DELAY_MS [DELAY_MS ...]
"""
import colorsys
import ctypes as C
import sys
import time

import rkkb


def frame_bytes(t):
    body = bytearray()
    for i in range(126):
        body += bytes(int(c * 255) for c in colorsys.hsv_to_rgb((i / 40 + t / 3) % 1, 1, 1))
    return bytes([6, 0]) + body


def phase(kb, seconds, delay_ms):
    end = time.time() + seconds
    frames, stalls, stall_time, packets = 0, 0, 0.0, 0
    started = time.time()
    while time.time() < end:
        t = time.perf_counter()
        msg = frame_bytes(t)
        chunks = [msg[i:i + 62] for i in range(0, len(msg), 62)]
        for n, ch in enumerate(chunks, 1):
            pkt = (C.c_ubyte * 65)(0x0A, len(chunks), n, *ch)
            s = time.perf_counter()
            rkkb.hid.HidD_SetFeature(kb.h, pkt, 65)
            d = time.perf_counter() - s
            packets += 1
            if d > 0.5:
                stalls += 1
                stall_time += d
            if delay_ms:
                time.sleep(delay_ms / 1000)
        frames += 1
    wall = time.time() - started
    good = wall - stall_time
    print(f"delay={delay_ms:>2} ms: {frames} frames, {packets} packets, {stalls} stalls "
          f"({stalls / packets * 1000:.2f} per 1000 packets), fps excl. stalls={frames / good:.1f}", flush=True)


with rkkb.Keyboard() as kb:
    secs = float(sys.argv[1])
    for d in sys.argv[2:]:
        phase(kb, secs, float(d))
