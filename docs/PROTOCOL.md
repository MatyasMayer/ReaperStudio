# CZC Gaming Reaper – USB lighting protocol

Reverse-engineered from the vendor app `DeviceDriver.exe` (sha256 `2cc34bca…4799`,
driver class `CDevRKKB`, vtable `0x5b1a0c`) with REA/Ghidra, then confirmed by
hooking `hid.dll` in the running vendor app (`tools/sniff.py`) and by driving the
keyboard directly (`tools/rkkb.py`).

## Device

| | |
|---|---|
| USB | `VID 0x258A` (SinoWealth) `PID 0x00C1` |
| Control interface | `MI_01`, HID top-level collection with **usage page `0xFF00`, usage `0x01`**, feature report length **65** (`COL05` on Windows) |
| Other collections | boot keyboard (MI_00), NKRO keyboard, consumer control, system control, a 6-byte vendor feature collection (unused by the lighting code) |

Open the collection with `CreateFile` (read/write, share read/write) and use
`HidD_SetFeature`. The vendor app never reads anything back from this collection.

## Transport (`FUN_0040fe80`)

A logical message of any length is split into 62-byte chunks, each sent as one
65-byte feature report:

```
byte 0     0x0A          report ID
byte 1     N             total number of packets in this message
byte 2     i             packet number, 1-based
byte 3..64 payload       62 bytes (last packet zero-padded)
```

**Pace the packets.** The firmware needs a short pause after each packet. Sent back-to-back
(~4 ms apart), it occasionally fails to acknowledge one, and `HidD_SetFeature` then blocks for the
5 s USB timeout. The write cannot be cancelled: HID completes it synchronously even on an overlapped
handle, and `CancelIoEx` has no effect. Measured stall rates (`tools/pacetest.py`):

| gap after each packet | stalls | max real-time fps |
|---|---|---|
| 0 ms | 0.32 per 1000 packets (5 in 90 s) | ~35 |
| 1 ms | 0 of 16 506 | ~26 |
| 2 ms | 0 of 13 979 | ~22 |
| 8 ms (vendor app) | 0 of 7 175 | ~11 |

Reaper Studio uses 2 ms, timed with a high-resolution waitable timer because `Sleep` has ~15.6 ms
granularity.

## Messages

### `02 29` – built-in effect (60 bytes)

```
[0] 0x02  [1] 0x29
[2] mode        1..21 (table below)
[3] 0x00
[4] speed       1..5
[5] brightness  0..5   (0 = off)
[6] R  [7] G  [8] B
[9] colourful   0 = use RGB above, 1 = rainbow/random colours
[10] direction  effect-specific (observed 1, 2, 5)
rest 0
```

| id | effect | id | effect | id | effect |
|---|---|---|---|---|---|
| 0x01 | Neon Stream | 0x08 | Rich And Honored | 0x0F | Ambilight |
| 0x02 | Ripples Shining | 0x09 | Marquee | 0x10 | Streamer |
| 0x03 | Rotating Windmill | 0x0A | Rotating Storm | 0x11 | Steady |
| 0x04 | Sine Wave | 0x0B | Serpentine Horse Race | 0x12 | Breathing |
| 0x05 | Rainbow Roulette | 0x0C | Retro Snake | 0x13 | Neon |
| 0x06 | Stars Twinkle | 0x0D | Diagonal Transformation | 0x14 | Shadow Disappear |
| 0x07 | Layer Upon Layer | 0x0E | **Customize** (per-key) | 0x15 | Flash Away |

Per-mode capabilities (from `Dev/00C1/KB.ini` `LedOptN = anim,speed,light,direction,random,colornum`):
modes 3, 5, 16, 19 have no direction/random option; mode 17 (Steady) has no speed.

### `03 7E` – store per-key colours for Customize (381 bytes)

```
[0] 0x03  [1] 0x7E  [2] slot (1-based)
[3 + led*3 + 0..2] R, G, B     for led = 0..125
```

Written to the keyboard's flash, so it persists without any software. Shown when
the effect mode is `0x0E`.

### `06 00` – real-time frame (380 bytes)

```
[0] 0x06  [1] 0x00
[2 + led*3 + 0..2] R, G, B     for led = 0..125
```

Volatile: it overrides whatever effect is running while frames keep arriving, and
the keyboard returns to its stored effect shortly after the stream stops. The vendor
app streams it at about 11 fps for music and Ambilight. This is what makes
software-driven effects possible.

### Other messages (not used by the new app yet)

| header | size | purpose |
|---|---|---|
| `01 F8` | 505 | key matrix / remapping, 4 layers × 0x84 × u32 HID codes |
| `04 00` | 0x782 | macro buffer |

## LED indices

Each key's LED index is the last field of its `Kn=` line in `Dev/00C1/KB.ini`
(converted to `src/layout.json`). Examples: Esc 0, `` ` `` 1, Tab 2, Caps 3, LShift 4,
LCtrl 5, F1 6, 1 7, Q 8, A 9 … Numpad Enter 112. The array has 126 slots; 100 are wired.
