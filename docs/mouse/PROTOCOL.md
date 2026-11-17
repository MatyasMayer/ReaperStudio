# Glorious Model D Wireless – USB lighting protocol

Read from the vendor app **Glorious CORE 17.4.11** (Electron). Its `resources/app.asar` ships the
protocol code as plain JavaScript, so no disassembly was needed:

| File inside `app.asar` | What |
|---|---|
| `src/electron-process/renderer-interop/protocol/device/mouse/ModelOSeries.js` | all commands below (`SetLEDEffect`, `SetLEDBright`, `GetDeviceBatteryStats`, …) |
| `src/electron-process/hiddevice/src/hiddevice.cc` | native addon around hidapi (`hid_send_feature_report` / `hid_get_feature_report`) |
| `resources/extraResources/database/SupportDevice.DB` | device table: VID/PID, HID collection, `routerID: "ModelOSeries"` |
| `src/renderer-process/components/MouseComponent/mouse.component.ts` | effect list as shown in the UI |

Checked against the real mouse with `tools/mouse/model_d.py`, both over the dongle and over the
cable: version, battery and profile queries answer, effect writes are acknowledged with `0xA1` and
show on the mouse, including per-zone writes (see [Zones](#zones)), which Glorious CORE never sends.

## Device

| | |
|---|---|
| USB | `VID 0x258A` (SinoWealth), `PID 0x2012` over the cable, `PID 0x2023` for the 2.4 GHz dongle |
| Control interface | HID collection with **usage page `0xFFFF`, usage `0x00`**, feature report length **65** (`MI_02` on the dongle) |
| Firmware (tested) | `0x81` reports 0.3.8.0 for both targets over the dongle and 0.3.8.1 for both over the cable, so the reachable end seems to answer for both; treat the per-target split as unreliable |

Over the cable the mouse uses the same collection, the same commands and target `0x02`; only the
PID differs. Wired, the battery query reports charging = 1.

Glorious CORE must not be running at the same time; it polls the same collection.

## Transport

Every command is one 65-byte feature report with **report ID `0x00`**, sent with `HidD_SetFeature`.
The reply is fetched with `HidD_GetFeature` on the same collection. Glorious CORE waits 20–100 ms
between the two and 30 ms after writes that it does not read back.

Request (byte 0 is the report ID):

```
[0] 0x00       report ID
[1] 0x00  [2] 0x00
[3] target     0x02 = mouse, 0x00 = dongle
[4] size       number of argument bytes that follow [6]
[5] group      0x00 = system/query, 0x02 = lighting
[6] op
[7..] args
```

Reply (`HidD_GetFeature`, report ID stripped, so offsets are one lower than in the request):

```
[0] status     0xA1 ok, 0xA4 mouse not reachable (asleep / out of range), 0xA2 failed, 0xA0 busy, 0xA3 unsupported
[5] op         echo of the request's op
[6..] data
```

On `0xA2` Glorious CORE resends after 40 ms; on `0xA0` and `0xA4` it polls again after 40 ms.

## Queries (group `0x00`)

| op | size | target | reply data |
|---|---|---|---|
| `0x81` firmware version | 3 | `0x00` dongle / `0x02` mouse | `[6..9]` = major, minor, patch, revision |
| `0x83` battery | 2 | `0x02` | `[6]` charging flag, `[7]` percent (0 means 1) |
| `0x85` active profile | 1 | `0x02` | `[6]` profile 1..3 |

## Commands

### `02 00` – lighting effect

```
[3] 0x02  [4] colours*3 + 5  [5] 0x02  [6] 0x00
[7]  profile 1..3
[8]  zone        0xFF = all (the only value Glorious CORE sends), or 0..2, see Zones
[9]  effect      table below
[10] direction   always 0
[11] speed       raw value, see below
[12..] R,G,B per colour (Glorious CORE sends 0,0,0 as 1,0,0; a colour slot it does not use is 0,0,0)
```

The effect is stored in the given profile: it survives switching the mouse off and on (verified).
There is no volatile real-time frame command like the keyboard's `06 00`, neither wired nor
wireless; do not stream this at frame rate (wireless latency and likely flash wear).

#### Zones

Verified by writing single colour (`4`) with zone 0 red, 1 blue, 2 yellow:

| zone | LEDs |
|---|---|
| 0 | scroll wheel |
| 1 | left side stripe |
| 2 | right side stripe |

Each zone keeps its own effect. Observed combinations:

| wheel (0) | left (1) | right (2) | result |
|---|---|---|---|
| breathing single colour red | single colour blue | single colour yellow | all three as set |
| breathing single colour red | Glorious (`1`) | single colour yellow | wheel **and** left show the rainbow, right stays yellow |

So Glorious mode on the left stripe also takes over the wheel (or overrides animated zones); not
yet tested which. The other animated effects (3, 6, 7, 8) are untested per zone.

While the cable is connected, the firmware shows its own charging animation on the wheel instead of
the wheel's effect. Whether a wheel write sent while charging is kept for after unplugging is not
yet tested (the one attempt was masked by the Glorious-mode takeover above).

| id | effect | colours |
|---|---|---|
| 0 | off | – |
| 1 | Glorious (rainbow) | – |
| 2 | seamless breathing | – (app sends `FF 00 00`) |
| 3 | breathing | up to 6 |
| 4 | single colour | 1 |
| 5 | breathing single colour | 1 |
| 6 | tail | – |
| 7 | rave | 2 |
| 8 | wave | – |

The Glorious CORE UI and its profile JSON number effects 0..8 in a different order: firmware id =
UI index + 1, and UI index 8 ("LED off") is firmware id 0.

Speed: the UI slider `RateValue` (0..100) becomes `(105 - RateValue) / 5` for most effects and
`(105 - RateValue) * 2` for rave and wave. Lower is faster.

### `02 02` – brightness

```
[3] 0x02  [4] 0x02  [5] 0x02  [6] 0x02
[7] mode        1 = wired, 0 = wireless
[8] brightness  0..255
```

Glorious CORE always sends both modes, wired first. Unless "separate brightness" is ticked, both
get the wired value.

### `00 07` – sleep timer

```
[3] 0x02  [4] 0x02  [5] 0x00  [6] 0x07
[7] seconds / 0xFF   [8] seconds & 0xFF   (FF FF = never)
```

Note the high byte is computed with `/ 0xFF` (not `>> 8`) in Glorious CORE; this is copied as-is.

### `00 05` – switch profile

```
[3] 0x02  [4] 0x01  [5] 0x00  [6] 0x05  [7] profile 1..3
```

Glorious CORE follows it with a brightness write and a full profile upload.
