# Which other keyboards could Reaper Studio support?

Research note, 2026-11-17. Repository state: `main` at `1f688b9`. No code was changed.

**Evidence tags.** Every claim below carries one of these, so facts and guesses stay apart.

| Tag | Meaning |
|---|---|
| **[V]** | Verified here: read from this repo, run on the connected keyboard, or computed by a script over the cited data. |
| **[3P]** | Reported by a third party (a repo, issue or capture) that we read but did not reproduce. |
| **[I]** | Inference from [V] and [3P] facts. Probably right, not proven. |
| **[G]** | Guess. Do not build on it without a test. |

**Confidence for candidate keyboards**

| Level | Rule used |
|---|---|
| **confirmed** | Someone has put the exact wire format on that model's hardware and reported that it works (or it is the Reaper itself). |
| **likely** | The vendor software routes the model to the same driver class and the model has a profile in the same vendor data, but no hardware report exists. |
| **speculative** | Same OEM, chip or software name only. |

---

## 1. Summary

1. **The Reaper is a rebadged Royal Kludge RK98.** USB `258A:00C1` is "RK98-100KRGB N Keyboard" in Royal Kludge's own software. The CZC build ships one profile, `Dev/00C1`, and its `[KEY]` table matches RK's 00C1 profile key for key (type, VK and LED index, 100 of 100, only the pixel rectangles differ). **[V]**
2. **The OEM is "BY Tech" / Beiying, on a Sinowealth (VID `0x258A`) USB controller.** Evidence in §3. The exact MCU part of the 00C1 board is not identified anywhere. **[I]**
3. **The 65-byte `0x0A` protocol that Reaper Studio uses is the "RKKB" driver class of the RK software.** In RK software 4.06, 125 of 188 model profiles route to it, 93 of those are RGB. **[3P]**, counts re-derived by script **[V]**.
4. **Best candidates, same protocol, mostly a layout file away:**

| Candidate | PID | Confidence | Work needed |
|---|---|---|---|
| RK98Wired-100K N RGB | `00D8` | likely | none beyond a PID entry: the key→LED map is identical to the Reaper's **[V]** |
| RK98RGB ISO | `0173` | likely | one extra key (ISO `\`, LED 76), `\` moves to LED 75 **[V]** |
| RK100 family (`0056` `0067` `00E0` `004B` `00AA` `00B3`) | see §4 | likely | four bottom-right keys sit on different LEDs **[V]** |
| RK96 RGB (`0089`, ISO `01C4`) | | likely | subset layout, no Home/PgUp/Ins/PgDn |
| RK71RGB (`005A` `00EA` `0069`) | | **confirmed** protocol (Rangoli author tested on hardware) | new 71-key layout |
| 87/104-key RK, 84-key RK, 61/68-key RK | | likely | new layouts; some need a colour-order swap (§3.3) |

5. **Other brands.** No non-RK keyboard has evidence for the *65-byte RKKB* protocol. Other brands that share the OEM and MCU use the **second protocol generation** ("G5", 520-byte report `0x06`, USB `258A:010C`): AULA F75/F99/F87 Pro, LEOBOG Hi75C Pro, Redragon K686 Eisa Pro, Kreo Swarm75, Surmen GS98. OpenRGB drives them. They use the **same 126-slot, `column*6+row` LED numbering**, so layouts transfer, but Reaper Studio would need a second transport (§5, step 9).
6. **PID `258A:0049` is a trap.** RK-G68, MAD Dog GK850W ("BY Tech GK850"), Machenike K500, Deltaco PK95, FL Esports F11 and many Redragon boards share it with *different* protocols. A USB capture of the GK850W shows `05 83 B6…` and a 1032-byte `06 08 B8…` report, not `0A`/65 bytes. **[V]** (§4.4) Detection must fingerprint the HID collections, never trust VID:PID alone.
7. **Brick risk is real.** OpenRGB disabled several Sinowealth keyboard controllers because writes meant for one board bricked another, and a user reported an RK84 (`258A:0059`) bricked after using Rangoli. Support must be allow-list based and opt-in (§6). **[3P]**
8. **Main code change:** replace the hardcoded `ReaperKeyboard` + `KeyboardLayout.Reaper` + `LedFrame.LedCount` trio with a data-driven `DeviceProfile` (PID list, dialect, layout, LED count, colour order, effect table). Core refactor is small (about 1 to 2 days); layouts can be generated from vendor `KB.ini` by a script (§5).

---

## 2. What Reaper Studio does today (all [V], from the repo)

### 2.1 Device identification

| Item | Value | Where |
|---|---|---|
| USB VID / PID | `0x258A` / `0x00C1` | `src/ReaperStudio.Core/Hid/ReaperKeyboard.cs:21` |
| Match method | HID interface path must contain `vid_258a&pid_00c1` (case-insensitive) | `ReaperKeyboard.cs:45-48` |
| Collection filter | `HidP_GetCaps`: `UsagePage 0xFF00`, `Usage 1`, `FeatureReportByteLength 65`, probed with a zero-access handle | `ReaperKeyboard.cs:49-55` |
| Interface | `MI_01`, Windows collection `COL05` (confirmed live on this PC, below) | `docs/PROTOCOL.md:13` |
| Open mode | read/write, share read+write, write only (no read-back ever) | `ReaperKeyboard.cs:56-58`, `PROTOCOL.md:16-17` |
| Report ID / size | `0x0A`, 65 bytes via `HidD_SetFeature` | `ReaperKeyboard.cs:22, 74, 81` |

Live check on this machine (Windows PnP, read-only): the Reaper enumerates as `USB\VID_258A&PID_00C1` revision `2004`, with `MI_00` (boot keyboard) and `MI_01&COL01…COL05` (system control, consumer control, a 6-byte vendor collection `COL03`, an NKRO keyboard `COL04`, and the 65-byte vendor collection `COL05`). Windows reports its USB product string as **"Bluetooth Keyboard"**, even though it is plugged in by cable. **[V]**

A second `258A` device is present on the same PC: PID `2023`, product string "Model D Wireless". OpenRGB identifies `2023` as a Glorious Model D wireless dongle. **[V]** + **[3P]** (`SinowealthControllerDetect.cpp`). Any VID-only match would open that mouse. The existing PID + collection filter is what prevents that, and must be kept.

### 2.2 Packet format

A logical message is cut into 62-byte chunks, each sent as one 65-byte feature report. **[V]** `ReaperKeyboard.cs:66-87`

```
byte 0     0x0A         report ID
byte 1     N            total packets in this message
byte 2     i            packet number, 1-based
byte 3..64 payload      62 bytes, last packet zero-padded
```

Pacing: 2 ms gap after every packet (`ReaperKeyboard.cs:23`, high-resolution timer in `PreciseDelay.cs`), because a back-to-back write stalls for 5 s about once per 3000 packets (`PROTOCOL.md:31-44`).

### 2.3 Opcodes

| Message | Header | Size | Use | Code |
|---|---|---:|---|---|
| Built-in effect | `02 29` | 60 | mode 1..21, speed 1..5, brightness 0..5, RGB, colourful flag, byte 10 | `Messages.cs:57-69` |
| Stored per-key colours | `03 7E <slot>` | 381 | 126 × RGB, written to flash, shown by mode `0x0E` "Customize" | `Messages.cs:80-87` |
| Real-time frame | `06 00` | 380 | 126 × RGB, volatile, keyboard reverts to the stored effect when frames stop | `Messages.cs:72-78` |
| Key matrix / remap | `01 F8` | 505 | documented, not used by the app | `PROTOCOL.md:101` |
| Macro buffer | `04 00` | 0x782 | documented, not used by the app | `PROTOCOL.md:102` |

Wire captures behind the tests: `ProtocolTests.cs:13-34` (effect), `:45-54` (real-time frame, 380 bytes, "as on the wire"), `:57-64` (custom colours).

### 2.4 LED addressing and layout model

* A frame is `LedFrame.LedCount = 126` slots of RGB (`Lighting/LedFrame.cs:6`). 100 are wired to keys on the Reaper. **[V]**
* The index is `column * 6 + row`: Esc 0, `` ` `` 1, Tab 2, Caps 3, LShift 4, LCtrl 5, F1 6, `1` 7, Q 8, A 9 … numpad Enter 112. 126 slots = 21 columns × 6 rows. **[V]** (`layout.json`, `PROTOCOL.md:104-108`). The same scheme appears in OpenRGB's AULA F99 map and in the RK R87 PRO plugin (`index = column * 6 + row`, "Esc=0, `1`=7"). **[3P]**
* `layout.json` (embedded, `ReaperStudio.Core.csproj:12`) has the header `{model, vid, pid, ledCount}` and 100 keys `{id, name, x, y, w, h, vk, led}` in the vendor's ≈850 × 300 pixel space, converted from `Dev/00C1/KB.ini` `Kn=` lines. **[V]**
* Windows VK codes are used to light keys on typing. The layout stores numpad Enter as the synthetic VK `0xFD` and Fn as `0xFA` (`KeyboardHook.cs:17, 70`; `KeyboardLayout.cs:38`). **[V]**
* The effect renderer works in layout pixel space: ripple speed `520 px/s` (`EffectRenderer.cs:17`), wave phase divided by `_layout.Width` (`:62`). **[V]**

### 2.5 Effects and options

* 21 firmware effects, ids 1..21, in the vendor order of `Dev/en/led.xml` (`tc_led_mode1…21`). **[V]**
* Per-effect controls (speed, direction, colour) come from the vendor `LedOptN = anim,speed,light,direction,random,colornum` table of `Dev/00C1/KB.ini` and are hardcoded as three `Supports*` methods (`Messages.cs:44-51`). **[V]**
* Brightness 0..5 and speed 1..5 are fixed ranges (`Messages.cs:63-64`, `MainWindow.xaml:173-177`).

### 2.6 Everything that is hardcoded to the Reaper

| # | What | Where |
|---|---|---|
| 1 | Class name, `VendorId`, `ProductId` | `ReaperKeyboard.cs:19-21` |
| 2 | Path match string | `ReaperKeyboard.cs:45` |
| 3 | Collection fingerprint (`0xFF00`, 1, 65) | `ReaperKeyboard.cs:53` |
| 4 | Report ID `0x0A`, report and payload sizes, 2 ms gap | `ReaperKeyboard.cs:22-23, 74` |
| 5 | `EffectMode` enum (21 ids) and the three `Supports*` tables | `Messages.cs:6-51` |
| 6 | `02 29` size 60 and field offsets, `06 00`, `03 7E` | `Messages.cs:57-87` |
| 7 | `LedCount = 126`, also sizes the star-field arrays and clamps painted keys | `LedFrame.cs:6`, `EffectRenderer.cs:14-15, 45`, `Messages.cs:55` |
| 8 | `KeyboardLayout.Reaper` singleton and embedded `layout.json` | `KeyboardLayout.cs:24-30`, `ReaperStudio.Core.csproj:12` |
| 9 | Engine owns `new EffectRenderer(KeyboardLayout.Reaper)` and a `ReaperKeyboard?` | `LightingEngine.cs:21, 31, 107` |
| 10 | "Save to keyboard" = `03 7E` slot 1 + effect `Customize` | `LightingEngine.cs:85-86` |
| 11 | Hardware preview approximation uses the Reaper layout | `LightingEngine.cs:195-197` |
| 12 | Painted keys are stored **by LED index** | `Profile.cs:45`; default FPS profile hardcodes LEDs 14, 9, 15, 21 at `ConfigStore.cs:41` |
| 13 | Config has no model field: one global profile list | `ConfigStore.cs:10-18` |
| 14 | Keyboard-preview control and the paint-all button use the Reaper layout | `KeyboardView.cs:24, 99, 150`, `MainWindow.xaml.cs:331` |
| 15 | Hardware effect list (names) | `MainWindow.xaml.cs:28-37, 68` |
| 16 | UI strings: "CZC Gaming Reaper", "USB 258A:00C1", "original CZC app (DeviceDriver.exe)" | `MainWindow.xaml:20, 42, 318` |
| 17 | Vendor-app process name `DeviceDriver` | `App.xaml.cs:122` (RK's own app is also `DeviceDriver.exe`, so this check already works for RK boards **[3P]**) |
| 18 | Tests: 100 keys, unique LEDs, Esc = 0, W = 14; 380 and 381 byte messages | `ProtocolTests.cs:45-75` |
| 19 | README and protocol doc requirements text | `README.md:165, 203`, `PROTOCOL.md:8-17` |

---

## 3. Who made it

### 3.1 Evidence chain

| Fact | Evidence | Tag |
|---|---|---|
| VID `0x258A` belongs to **SINO WEALTH Electronic Ltd.**, a Chinese microcontroller maker. It is a chip vendor, not a brand: dozens of brands reuse it. | Linux `hid-ids.h` defines `USB_VENDOR_ID_SINOWEALTH 0x258a` ([source](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-ids.h)); the USB ID repository discussion says "vendor 258a is just the chip producer" ([source](https://usb-ids.gowdy.us/read/UD/258a)); OpenRGB uses `SINOWEALTH_VID 0x258A` ([source](https://gitlab.com/CalcProgrammer1/OpenRGB/-/raw/master/Controllers/SinowealthController/SinowealthControllerDetect.cpp)) | [3P] |
| The RK98 updater's firmware blob carries a `5A A5 "SINO" 25 8A` header | issue [wushuai0802/myfinder-device-battery#316](https://github.com/wushuai0802/myfinder-device-battery/issues/316) | [3P] |
| The vendor app was built by an OEM called **Beiying**: PDB path `d:\OEM\Beiying\Src\RK\Debug\DeviceDriver.pdb`; driver classes `CDevRKKB`, `CDevG5KB`, `CDev3632`; `Cfg.ini` has `appdir=RKKB` | strings in `source/CZC.Gaming Reaper/DeviceDriver.exe` (offsets `0x1bd110`, `0x1e3b2c`, `0x1e3b94`, `0x1e3bac`) | [V] |
| The same OEM tag is used elsewhere: RK's catalog labels the protocol `BeiYing`; boards report the USB product string **"BY Tech"** (`258A:0102`, `258A:0049`, `258A:019F`); the MCU flash headers read `BYK916`, `BYK901`, `BYK816`… | RK drive catalog ([rk-drive-keyboard-catalog.csv](https://github.com/shubh-3011/rk-keyboard-software-research/blob/main/docs/rk-drive-keyboard-catalog.csv)), Rangoli issue [#17](https://github.com/rnayabed/rangoli/issues/17), [MAD Dog #5118](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/5118), [openrgb-rk-r87pro](https://github.com/myxk1792/openrgb-rk-r87pro), [sinowisp table](https://github.com/carlossless/sinowealth-kb-tool) | [3P] |
| Royal Kludge's own keyboard software, "RK Keyboard Software 4.06", contains the profile `Dev/00C1` named **"RK98-100KRGB N Keyboard"** (family 98, ANSI, 100 keys). The CZC `Dev/00C1/KB.ini` is the same table: **all 100 `K` rows have identical type/VK/LED**, only the screen rectangles differ (CZC re-skinned the artwork). | Script diff of the local `KB.ini` against `models.json` ([RK research repo](https://github.com/shubh-3011/rk-keyboard-software-research)) | [V] |
| The Reaper reports the product string "Bluetooth Keyboard" over USB, like the RK98 does in [issue #316](https://github.com/wushuai0802/myfinder-device-battery/issues/316). The Reaper is tri-mode (BT / 2.4 GHz / USB-C), as the RK98 is. | live PnP read **[V]**; [arecenze.cz review](https://www.arecenze.cz/?p=204581) **[3P]** | |
| Rangoli's per-PID config for `0xc1` (name "RK98-100KRGB N") lists 100 keys whose `bIndex` values are **exactly** the 100 LED indices of `layout.json`, key for key | [`c1.json`](https://github.com/rnayabed/rangoli/blob/master/keyboards/258a/configs/c1.json), compared by script | [V] |
| CZC sells it as `CZCGK880K` / `CZCGK880W`, 100 keys, TTC Red switches | [arecenze.cz](https://www.arecenze.cz/?p=204581) | [3P] |

### 3.2 Conclusions

* **Brand/design:** Royal Kludge RK98 family (OEM: BY Tech / Beiying). CZC.Gaming is a retailer label. **[I]**, strong.
* **Controller:** a Sinowealth USB MCU (VID `0x258A`). RK's other boards use Sinowealth 8051-class parts (`SH68F90`, `SH68F90A`, `SH68F90AS` on the RKG68) with `BYK916`/`BYK901` flash labels ([sinowisp](https://github.com/carlossless/sinowealth-kb-tool)); the 00C1 board is not in that list, so its exact part is **unknown**. It is **not** SONiX, Holtek, Weltrend or Evision: RK's SONiX boards (`0C45:xxxx`) use a different protocol ([Rangoli #13](https://github.com/rnayabed/rangoli/issues/13)). **[I]**
* **Vendor software lineage:** "RK Keyboard Software" (Beiying `DeviceDriver.exe` + `Dev/<PID>/KB.ini`) is white-labelled per customer. The CZC package is one such build (single `DevName1=00C1`); an OpenRGB code comment says Kreo's Swarm75 layout was taken from "the manufacturer's `Dev/kb/KB.ini`" ([source](https://gitlab.com/CalcProgrammer1/OpenRGB/-/raw/master/Controllers/SinowealthController/SinowealthKeyboard10cController/SinowealthKeyboard10cDevices.cpp)), and a KZZI app with a same-named `DeviceDriver.exe` exists ([listing](https://www.advanceduninstaller.com/KZZI-RGB-Keyboard-7d80d9c06c8464cc8ecbfd14f64233f4-application.htm), 2.19 MB). **[3P]**; KZZI is **[G]** only.

### 3.3 The protocol family (what is shared, what varies)

RK software 4.06 selects a driver class per profile from the `[OPT] Fw` value plus HID descriptor checks ([coverage doc](https://github.com/shubh-3011/rk-keyboard-software-research/blob/main/docs/rk-profile-family-protocol-coverage.md), recomputed from `models.json` **[V]**):

| Class | `Fw` | Transport | Profiles in RK 4.06 | Notes |
|---|---|---|---:|---|
| **`CDevRKKB`** | 0 / absent | 65-byte feature report `0x0A`, 62-byte chunks, no ACK or checksum, `HidD_SetFeature` | **125** (93 RGB, 32 single-colour) | **the Reaper and Reaper Studio**. Independent static analysis agrees on the framing, and says the music path sends `[6, 0]` + data, i.e. our real-time frame ([reverse-engineering.md](https://github.com/shubh-3011/rk-keyboard-software-research/blob/main/docs/reverse-engineering.md), lines 1049, 797) |
| **`CDevG5KB`** | 1, 2 | 520-byte feature reports (report ID 6), commands + paging, optional ACK | 63 | RK R87 PRO `019F`, S98, F75…; same chip family as OpenRGB's `258A:010C` AULA/Leobog/Kreo boards **[I]** |
| `CDev3632` | wireless receivers | 20-byte reports | — | 2.4 GHz / BT receivers (`3554:FA09`, `258A:01BB`…). Out of scope. |

**What varies inside the RKKB class** (all from per-profile `[OPT]` data, **[V]** by script unless noted):

| Knob | Values among the 125 RKKB profiles | Consequence |
|---|---|---|
| `RGBKb` | 93 × RGB, 32 × single-colour backlight | single-colour boards have no per-key colour |
| `RGBIndex` (colour channel order) | default `0,1,2` for 118 (26 set explicitly, the rest absent); **`1,0,2` for 6** (`0055 0065 008C 00C7 004A 00A9`); `2,0,1` for 1 (`0214`) | R/G (or R/B) must be swapped on the wire. Rangoli issue [#12](https://github.com/rnayabed/rangoli/issues/12) reports exactly "R and G swapped" on `0065`. **[I]** |
| LED grid | 14, 16, 17, 19 or 21 columns × 6 rows; the 93 RGB profiles have 24 distinct (columns, key count) combinations | per-model `layout.json` |
| Effect table `LedOpt1..21` | 78 of 93 RGB profiles have the same 21-row table as 00C1; 15 differ in details | per-model capability flags |
| Effect id → name | 4 RKKB profiles ship their own lists (e.g. `014E` has 3 modes, `023D` has 13 differently named) | per-model effect table; ids are not universal |
| `ShowSleepTime`, `KbLayout`, `LayoutKeyNum`, `KeyNotToSetColor`, `UnRectKey`, `DefLedIndex` | various | UI and layout details |
| Windows collection | Rangoli uses `&Col05` by default and `Col06` for `0057` ([`hid-col.json`](https://github.com/rnayabed/rangoli/blob/master/keyboards/hid-col.json)) | **select by caps, not by index**, as `TryOpen` already does |
| Wired vs wireless | tri-mode boards expose RKKB only on USB; the 2.4 GHz dongle is another device | Reaper Studio works over cable only |

**Real-time `06 00` frames are only evidenced on the Reaper.** Rangoli (hardware-tested on RK71/RK96) implements `02 29`, `03 7E` and `01 F8` but lists music mode as "planned" ([README](https://github.com/rnayabed/rangoli)). RK's own analysis shows the vendor app *can* send `[6,0]` music frames over the RKKB path, and only one profile has `ShowMusic=0` / `ShowRealTime=0`, so it very likely works elsewhere, but nobody reported it. **[I]**

---

## 4. Candidate keyboards

Legend: PID is the wired USB PID. "Layout Δ" is measured against the Reaper's key → LED table (§2.4), by script. The complete RKKB list (125 profiles) is in **Appendix A**.

### 4.1 Same protocol, Royal Kludge brand (RKKB class)

| Brand / model | PID | Evidence it shares the protocol | Layout Δ vs Reaper | Confidence | Sources |
|---|---|---|---|---|---|
| **CZC.Gaming Reaper** (CZCGK880) = RK98-100KRGB N | `00C1` | this repo; byte-for-byte captures in `ProtocolTests.cs` | none | **confirmed** | §2, §3 |
| RK98Wired-100K N RGB | `00D8` | RKKB profile, identical `[OPT]` (`KbImgUse`, `ShowSleepTime`, `KbLayout 7`) and identical VK→LED map (100/100) | **none** | likely | [models.json](https://github.com/shubh-3011/rk-keyboard-software-research/blob/main/models.json); Rangoli lists it ✔ "should work" |
| RK98RGB ISO | `0173` | RKKB profile | +1 key (ISO `\` = LED 76), `\` → LED 75 | likely | models.json |
| RK98Wired-100KRGB | `00B3` | RKKB profile | RShift 70, RAlt 53, Fn 59, RCtrl 65 (4 keys moved) | likely | models.json; Rangoli ✔ |
| RK100RGB / RK100RGB N / RK100PRO / RK100RGB Wireless / RK750-100RGB | `0056` `00E0` `0067` `004B` `00AA` | RKKB profiles, same 4-key delta as `00B3` | same 4-key delta | likely | models.json; Rangoli ✔ |
| RK98 RGB | `0070` | RKKB profile | F-row and nav cluster use other LEDs (F1 = 12 …), no Home/Insert | likely | models.json; Rangoli ✔ |
| RK96 RGB, RK96 ISO Return | `0089`, `01C4` | RKKB profiles | no Home/PgUp/Ins/PgDn | likely | models.json |
| RK96 (single-colour) | `00A4` | **Rangoli author reports it fully working** (`🚀`) | single-colour board | **confirmed** (protocol; no per-key colour) | [keyboards-list.md](https://github.com/rnayabed/rangoli/blob/master/keyboards-list.md) |
| RK71RGB, RK71RGB N Wired, RK71RGB Wireless (cable) | `005A`, `00EA`, `0069` | **Rangoli author reports all three fully working** (`🚀`); Rangoli was reverse-engineered "based on the RK71RGB N Keyboard" | 71 keys, 16 × 6 grid, different layout | **confirmed** (protocol), layout new | [Rangoli README](https://github.com/rnayabed/rangoli), keyboards-list.md |
| RK71RGB variants | `00A5` `00B5` `004D` | same family and grid as the confirmed three | as `005A` | likely | models.json |
| RK87/RK987/RK104-class, 17 columns × 87 keys: `0078` `004C` `00C4` `00DE` `005B` `01FF` `00F9` `0068` `004F` `007D` `00EF` `00C3` `00F6` `0129` `0106` `010F` | | RKKB profiles | new 87-key layout (one layout, 16 PIDs) | likely | models.json; Rangoli ✔ for most |
| RK84-class: `00C8` `00F3` `010D` `005D`, ISO `00F4` | | RKKB profiles | new 84-key layout | likely | models.json; Rangoli ✔ |
| **RK84RGB `0059`** | | RKKB profile, **but** Rangoli [#19](https://github.com/rnayabed/rangoli/issues/19): a user reports the board **bricked** ("Device Descriptor Request Failed") after using Rangoli, and a second user reports the same on an RK84; the maintainer then disabled it | — | **exclude** | Rangoli #19 [3P]; cause unproven |
| RK61 / RK68 / RK G68 families (`0055 00C9 00ED 007A 00E1 00C7 004A 00F8 00C6`, `00A2 006F 0079 005E 00FF 010A 0064 00DA 007C 0049 013A 00E7`) | | RKKB profiles | new 61/68-key layouts; **`0055 004A 00C7` (and ISO `0065 008C 00A9`) need colour order `1,0,2`**; `0049` is the PID collision from §4.4 | likely | models.json; Rangoli ✔ (`0065` has the R/G swap report, #12) |
| 32 single-colour RKKB boards | see appendix | RKKB profiles, `RGBKb=0` | no RGB, per-key brightness only | speculative for Reaper Studio's feature set | models.json |

### 4.2 Same OEM and MCU, second protocol generation ("G5", `258A:010C` and RK `Fw=1/2`)

Evidence: OpenRGB's [Sinowealth keyboard 010C controller](https://gitlab.com/CalcProgrammer1/OpenRGB/-/tree/master/Controllers/SinowealthController/SinowealthKeyboard10cController) detects `258A:010C`, usage page `0xFF00`, interface 1, sends a 520-byte report `06 82 01 00 01 00 06…` and reads the **model id from byte 13** of the reply. It then draws frames as `06 08 .. 01 .. 7A 01 | RGB × 126` (`0x017A` = 378 = 3 × 126) in a 520-byte report. The RK R87 PRO plugin documents the same format and says RK Keyboard Software V4.6's `Dev/019F/KB.ini` matches its LED map entry by entry ([source](https://github.com/myxk1792/openrgb-rk-r87pro)). **[3P]**

| Brand / model | USB | Model-id byte | Evidence | Confidence for "same OEM, new transport" | Sources |
|---|---|---|---|---|---|
| AULA F75 | `258A:010C` | `0xCD` | OpenRGB device table + issue [#4232](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/4232) | confirmed (OpenRGB) | [Devices.cpp](https://gitlab.com/CalcProgrammer1/OpenRGB/-/raw/master/Controllers/SinowealthController/SinowealthKeyboard10cController/SinowealthKeyboard10cDevices.cpp), MR [!3062](https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/3062) |
| AULA F99 | `258A:010C` | `0xA4` | same; issue [#5166](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/5166) | confirmed (OpenRGB) | same |
| AULA F87 Pro | `258A:010C` | `0x0B` | MR [!3087](https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/3087) | confirmed (OpenRGB) | same |
| LEOBOG Hi75C Pro | `258A:010C` | `0xA3` | OpenRGB device table | confirmed (OpenRGB) | Devices.cpp |
| Redragon K686 Eisa Pro | `258A:010C` | `0x20`, `0x05` | OpenRGB device table | confirmed (OpenRGB) | Devices.cpp |
| Kreo Swarm75 | `258A:010C` | `0xBE` | table; code comment: indices from "the manufacturer's `Dev/kb/KB.ini`" | confirmed (OpenRGB), explicit vendor-KB.ini lineage | Devices.cpp |
| Surmen GS98 | `258A:010C` | n/a | issue [#3972](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/3972) has captures, not in the table | likely | issue |
| RK R87 PRO | `258A:019F` | `0x56` | [openrgb-rk-r87pro](https://github.com/myxk1792/openrgb-rk-r87pro), RK profile `019F`, `Fw=1` | confirmed (plugin author) | same |
| 63 RK `Fw=1/2` profiles (S98, F75, R98 Pro, N99…) | various | | RK 4.06 routes them to `CDevG5KB` | likely, same transport | coverage doc |

Reaper Studio would need a new transport for these (§5 step 9). They are *not* "a layout file away". Notably, the LED numbering is the same `column*6+row` and the same 126 slots, so renderer, layouts and the rest of the app carry over unchanged. **[I]**

### 4.3 Same name, other brands: evidence missing

| Brand / model | Why it came up | Status |
|---|---|---|
| KZZI RGB Keyboard software | a Windows app whose main file is a `DeviceDriver.exe` of 2 294 784 bytes ([listing](https://www.advanceduninstaller.com/KZZI-RGB-Keyboard-7d80d9c06c8464cc8ecbfd14f64233f4-application.htm); the CZC one is 2 190 848 bytes, so a different build) | **speculative** [G]: same file name and size class only, no USB IDs found |
| Other CZC.Gaming keyboards | the CZC build's `DeviceDriver.exe` contains all three driver classes (`CDevRKKB`, `CDevG5KB`, `CDev3632`) **[V]** | **speculative** [G]: depends on whether other CZC models ship the same installer. Check the `DevNameN` lines of their `Cfg.ini` |
| Other Kreo boards | Kreo Swarm75 is in OpenRGB (§4.2). The Kreo Hive is a different chip (`320f:5055`, Evision, [OpenRGB #4831](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/4831)) | Swarm75 only |

### 4.4 Do not treat as compatible

| Device | Why | Evidence |
|---|---|---|
| `258A:0049`: **RK-G68**, **MAD Dog GK850W** ("BY Tech GK850"), Machenike K500, Deltaco Gaming PK95, AULA F3261, tecware phantom+ elite, FL Esports F11, 14 Redragon models | One VID:PID, several firmwares. OpenRGB lists them and says the existing controller **bricked** Redragon K618 and K621 ([RFC #5951](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5951), `SinowealthControllerDetect.cpp` comment). I parsed the GK850W capture ([issue #5118](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/5118), `Set_whole_keyboard_to_red.pcapng.gz`, 11 604 bytes): its `SET_REPORT` writes are `05 83 B6 00 00 00` (6 bytes), `05 88 B8 …` and `06 08 B8 00 40 00 00 00 …` / `06 03 B6 …` at **1032 bytes**, with **no `0A` report** | [V] for the capture. RK's software routes its own `0049` profile (RK-G68) to RKKB, so the same PID appears to carry two protocols **[I]** |
| `258A:0016` (Zuoya X51, Havit, Redragon K568, Marvo…) | different protocol | RFC #5951 [3P] |
| RK boards on SONiX `0C45:xxxx` or `05AC:xxxx` | other chips and protocols | Rangoli [#13](https://github.com/rnayabed/rangoli/issues/13), [#14](https://github.com/rnayabed/rangoli/issues/14) |
| 2.4 GHz / BT receivers (`3554:FA09`, `258A:01BB`, `258A:0150`…) | 19/20-byte reports, `CDev3632` | RK coverage doc, openrgb-rk-r87pro |
| Glorious mice (`258A:0036`, `0033`, `2022/2023`) | Sinowealth mouse protocol | OpenRGB |

The safe discriminator between the two `0049` families (and between RKKB and G5) is the HID descriptor. RKKB boards expose a vendor collection with **one 65-byte feature report, ID `0x0A`**, next to a **6-byte** one (report `0x05`) **[3P]** (issue #316 lists exactly this for `00C1`; Windows shows `COL03` and `COL05` live **[V]**); G5 boards expose a 520-byte report ID 6 collection. `TryOpen` already checks this, and must keep checking it.

---

## 5. Edits Reaper Studio would need

### 5.1 Minimal architecture: data-driven device profiles

```
Core/Devices/
  DeviceProfile            immutable record, loaded from an embedded devices.json
                           { Id, Name, Brand, UsbIds[], Dialect, LedCount, LayoutId,
                             ColorOrder, Effects[], Capabilities{Rgb, PerKey, Realtime, StoredCustom},
                             Verification (Confirmed|Likely|Untested), SafeWrites }
  DeviceCatalog            Match(IEnumerable<HidInterface>) -> (DeviceProfile, path)
  IKeyboardDevice          SetEffect, SendFrame, StoreCustomColors, Stalls, Dispose
  RkkbDevice               today's ReaperKeyboard, parameterised by DeviceProfile (dialect "rkkb-65")
  G5Device (later)         dialect "g5-520": model-id query, direct frames
Core/Layout/
  KeyboardLayout.Load(layoutId)   layouts/<id>.json, generated from vendor KB.ini
  KeyId                           model-neutral key identity (name or HID usage) for painting
tools/kbini2layout.py     Dev/<PID>/KB.ini -> layouts/<id>.json + devices.json entry
```

The engine takes a `DeviceCatalog`, owns one `EffectRenderer` per attached layout, and exposes the capability flags to the UI. The renderer already iterates `layout.Keys` and writes `frame[key.Led]`, so it needs no algorithm change.

### 5.2 Edit list, ordered by effort

| Step | Effort | Edit | Files / lines | Tests |
|---|---|---|---|---|
| **1. Profile record for the Reaper only** (pure refactor, no behaviour change) | S (0.5-1 d) | Add `DeviceProfile`; move VID/PID, `LedCount`, layout name, effect table and `Supports*` rules into a `00C1` profile; rename `ReaperKeyboard` → `RkkbKeyboard(profile)`; pass the profile into `LightingEngine` instead of `KeyboardLayout.Reaper` | `ReaperKeyboard.cs:19-23, 45, 53`; `LightingEngine.cs:21, 31, 107`; `KeyboardLayout.cs:24-30`; `Messages.cs:6-51` | existing tests keep passing; add a `devices.json` parse test |
| **2. Multi-PID detection with a fingerprint** | S (0.5 d) | `TryOpen(IEnumerable<DeviceProfile>)`: for every HID path, parse `vid/pid`, look up the profile, then require the profile's collection fingerprint (`0xFF00`, 1, 65, report ID `0x0A`) before opening for write. Report every match, not just the first. Never match on VID alone | `ReaperKeyboard.cs:43-62`, `HidNative.cs:146-180` | introduce an `IHidEnumerator` seam so detection is testable with fake descriptors, including a fake `0049` G5/1032-byte device that must be rejected |
| **3. Layout generator + first extra layouts** | M (1-2 d) | `tools/kbini2layout.py` converts `[KEY]` `Kn=x1,y1,x2,y2,type,vk,0,led` rows (the format as seen in `Dev/00C1/KB.ini`; `PROTOCOL.md:104-108` documents the LED field) and `[OPT]` (`RGBKb`, `LayoutKeyNum`, `RGBIndex`, `LedOpt*`) into JSON. Ship `00D8` (identical map, free), `0173`, the RK100 family; later the 87/84/71/68/61 layouts. Derive from KB.ini you can legally obtain; **do not copy Rangoli's configs (GPL-3.0)**, and note the repo has no `LICENSE` file at this commit | new `layouts/*.json`, `ReaperStudio.Core.csproj:12` (embed a folder) | parameterised invariant test over every layout: unique LEDs, `0 <= led < LedCount`, every key has a non-zero VK, snapshot of Esc/W/Space LEDs |
| **4. Per-model capabilities and effect tables** | M (1 d) | Replace the three `Supports*` statics with `profile.Effects[id] = {name, speed, direction, colour, random, colornum}` (the `LedOptN` rows). Allow a profile to ship its own id→name list (4 RKKB profiles do). Make speed/brightness ranges profile data | `Messages.cs:6-51`, `MainWindow.xaml.cs:28-37, 68, 234-237`, `MainWindow.xaml:173-177` | table-driven test against `LedOpt` rows |
| **5. Colour order** | S (0.5 d) | Apply `ColorOrder` in `WriteLeds` and in the `02 29` colour bytes (`m[6..8]`): `0,1,2` default, `1,0,2` for six profiles, `2,0,1` for one | `Messages.cs:66, 89-97` | golden vectors: same frame, three orders |
| **6. Config: model awareness and portable paint** | M (1 d) | Add `DeviceId` to `AppConfig` (and optionally to `Profile`); store painted keys by `KeyId` (key name) instead of LED index so a profile paints "WASD" on any board; migrate old `Painted` (LED→key via the 00C1 layout). The default FPS profile's hardcoded LEDs 14/9/15/21 become key names | `Profile.cs:45`, `ConfigStore.cs:10-18, 41`, `EffectRenderer.cs:44-45`, `MainWindow.xaml.cs:309-332` | migration test; round-trip test |
| **7. UI: model picker and capability-driven controls** | M (1-2 d) | Auto-detected model shown in the header (replace the "CZC Gaming Reaper" label and the "USB 258A:00C1" text); a manual override list showing each model's verification badge; hide per-key painting and colour pickers when `Rgb == false`; hide "Direction" when the profile has no direction column; swap `KeyboardView` to the active layout; status text names the model | `MainWindow.xaml:20, 42, 318`, `MainWindow.xaml.cs:68, 141, 331`, `KeyboardView.cs:24, 99` | UI screenshot mode already exists (`--screenshot`) |
| **8. Renderer scaling** | S (0.5 d) | The effect renderer uses fixed pixel constants for an ≈850 px board (`RippleSpeed = 520`, `d < 45` ring width at `EffectRenderer.cs:17, 102`). Normalise by `layout.Width` so ripples look right on a 61-key board | `EffectRenderer.cs:17, 62, 98-102` | none (visual) |
| **9. G5 dialect (second transport)** | L (1-2 wk, needs captures or hardware) | New `G5Device`: 520-byte report ID 6, model-id query `06 82 01 00 01 00 06`, direct frame `06 08 …` (+ resend about once a second, the keyboard drops back to its effect otherwise, as for RKKB frames), model-id→layout table. Hardware *effects* on G5 need captures; OpenRGB and the R87 PRO plugin only implement direct mode. Use OpenRGB's `010C` code as the protocol reference, RK repo's `rebuild/windows/G5*.cs` as a second | new files; `LedFrame` stays 126 | golden vectors from OpenRGB's code and captures; fake-HID tests |
| **10. Single-colour (`RGBKb=0`) boards** | M-L | Brightness-only capability set | profile + UI | — |
| **11. Wireless receivers (`CDev3632`)** | XL, not recommended | 20-byte transport, different handshake | — | — |

Cheapest useful result: steps 1 + 2 + 3 (profile record, multi-PID detection, generator) give support for `00D8`, `0173` and the RK100/RK98 family in about 3-4 days, because they sit on the same 19-column grid.

### 5.3 What does *not* need to change

`HidNative.cs`, `PreciseDelay.cs`, the 62-byte chunking and `0A` framing, the rule engine, tray, keyboard hook (except mapping ISO `\` = VK `0xE2`, which RK's ISO profiles already store), and effect maths.

---

## 6. Safety rules for supporting unverified hardware

1. **Allow-list, never a wildcard.** Only open a device whose PID is in `devices.json` **and** whose HID collection matches the dialect's fingerprint. `258A:0049` shows why.
2. **Three verification levels in the profile** (`Confirmed`, `Likely`, `Untested`) shown in the UI. For anything below `Confirmed`, require an explicit "I understand this writes to my keyboard" opt-in.
3. **Gate the flash-writing messages.** `03 7E` ("Save to keyboard") and `01 F8` (key matrix) write to flash. Reaper Studio uses only `03 7E`. On non-`Confirmed` models, allow only `02 29` (effect select) and `06 00` (volatile frames). There is no evidence either way on which message class bricked the RK84, so this is a precaution, not a diagnosis. **[G]**
4. **No read-back exists**, so the app cannot verify the firmware. The only identity data is the PID, `bcdDevice` (the Reaper reports revision `2004`), the product string, and the descriptor. Log all four at connect time. **[V]**
5. **Capture-first onboarding.** `tools/sniff.py` (Frida hook of the vendor app) already exists. A "contribute a profile" path that asks the user to capture three effects from the vendor app and compare them with `Messages.Effect` would turn "likely" into "confirmed" without a risky first write.
6. **Keep the vendor-app warning** (`App.xaml.cs:122`). RK's app is also `DeviceDriver.exe`.

---

## 7. Open questions and discrepancies

1. **Byte 10 of `02 29`.** `PROTOCOL.md:58` and `HardwareEffect.Direction` call it "direction (observed 1, 2, 5)". Rangoli (`buffer[13]` = the same byte, named `m_sleep`) and RKCU ("sleep timeout (1–5)") both call it **sleep time**, and the Reaper's `KB.ini` has `ShowSleepTime=1`. The three could be the same byte or the app could be mislabelled. A short capture comparing the vendor app's "sleep" and "direction" widgets would settle it; until then, the "Direction / variant" slider may be changing a sleep timer. **Unresolved.**
2. **`06 00` on other models.** Unverified outside the Reaper (§3.3).
3. **Custom-colour slots.** `03 7E <slot>` is always slot 1 in the app; Rangoli also only sends `01`. Whether other slots exist is unknown.
4. **Vendor build differences.** The CZC `DeviceDriver.exe` (2 190 848 bytes, 2022-08-25 per file time, SHA-256 `2cc34bca…4799` per `PROTOCOL.md`) differs from RK 4.06's (SHA-256 `C03F88BC…47A9AB6`, per the RK repo). The issue #316 author calls `DeviceDriver.exe` "Delphi"; the CZC build has an MFC/C++ PDB path and `CMFC*` classes. Possibly a different version of the app; not important for the wire format.
5. **Is 2.4 GHz/BT usable?** The Reaper is tri-mode. In those modes the keyboard is not `258A:00C1`, and RK's data lists no receiver for the RK98 ([issue #316](https://github.com/wushuai0802/myfinder-device-battery/issues/316)). Reaper Studio only works on the USB-C cable.
6. **Exact MCU of the 00C1 board.** Unknown. Opening the keyboard, or reading the ISP bootloader with [sinowisp](https://github.com/carlossless/sinowealth-kb-tool) (read-only, but it touches the bootloader, so not recommended), would tell.
7. **Licensing of reused data.** Rangoli is GPL-3.0; the RK research repo is MIT but its `models.json` is derived from vendor `KB.ini` files. Generate layouts from files the user has, or from fresh captures.

---

## 8. Sources

**Repo and local evidence (all [V])**: `src/ReaperStudio.Core/**`, `src/ReaperStudio/**`, `tests/ReaperStudio.Tests/ProtocolTests.cs`, `docs/PROTOCOL.md`, `README.md`; `source/CZC.Gaming Reaper/{Cfg.ini, Dev/00C1/KB.ini, Dev/en/led.xml, DeviceDriver.exe}` (string table: PDB path, `CDev*` classes); Windows PnP read of the connected keyboard (VID/PID, revision, collections, product string).

**Prior art and data**
* RK software research and catalogs (188-profile `models.json`, driver-class map, static analysis of `DeviceDriver.exe`): <https://github.com/shubh-3011/rk-keyboard-software-research> ([`models.json`](https://github.com/shubh-3011/rk-keyboard-software-research/blob/main/models.json), [profile-to-family coverage](https://github.com/shubh-3011/rk-keyboard-software-research/blob/main/docs/rk-profile-family-protocol-coverage.md), [reverse-engineering.md](https://github.com/shubh-3011/rk-keyboard-software-research/blob/main/docs/reverse-engineering.md), [catalog CSV](https://github.com/shubh-3011/rk-keyboard-software-research/blob/main/docs/rk-drive-keyboard-catalog.csv))
* Rangoli (GPL-3.0, hardware-tested RKKB writer, per-PID configs, support list, issues #11 #12 #13 #14 #16 #17 #19): <https://github.com/rnayabed/rangoli> ([`keyboardconfiguratorcontroller.cpp`](https://github.com/rnayabed/rangoli/blob/master/src/keyboardconfiguratorcontroller.cpp), [`keyboards-list.md`](https://github.com/rnayabed/rangoli/blob/master/keyboards-list.md), [`c1.json`](https://github.com/rnayabed/rangoli/blob/master/keyboards/258a/configs/c1.json))
* RKCU, RK61 reverse-engineering: <https://github.com/oddlyspaced/rkcu>; KludgeKnight, WebHID remapper with vendor `KB.ini` files: <https://github.com/vinc3m1/kludgeknight>; Royal Kludge Configurator: <https://github.com/Ripwords/royal-kludge-configurator>
* RK R87 PRO OpenRGB plugin (G5, `258A:019F`): <https://github.com/myxk1792/openrgb-rk-r87pro>
* Issue with the RK98 `258A:00C1` descriptors and firmware header: <https://github.com/wushuai0802/myfinder-device-battery/issues/316>
* OpenRGB: [Sinowealth detector](https://gitlab.com/CalcProgrammer1/OpenRGB/-/raw/master/Controllers/SinowealthController/SinowealthControllerDetect.cpp), [010C device table](https://gitlab.com/CalcProgrammer1/OpenRGB/-/raw/master/Controllers/SinowealthController/SinowealthKeyboard10cController/SinowealthKeyboard10cDevices.cpp), [010C controller](https://gitlab.com/CalcProgrammer1/OpenRGB/-/raw/master/Controllers/SinowealthController/SinowealthKeyboard10cController/SinowealthKeyboard10cController.cpp), [RFC on protocol conflicts #5951](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5951), issues [#2308](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/2308) (RK84), [#3365](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/3365) (Machenike K500), [#4599](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/4599) (Deltaco PK95), [#5118](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/5118) (MAD Dog GK850W, with captures), [#4232](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/4232) / [#5166](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/5166) (AULA), [#3972](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/3972) (Surmen GS98), MRs [!3062](https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/3062), [!3087](https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/3087)
* Sinowealth MCU table (`SH68F90A`, `BYK916` labels, brands): <https://github.com/carlossless/sinowealth-kb-tool>
* Vendor ID: Linux [`hid-ids.h`](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-ids.h); [USB ID repository, 258a](https://usb-ids.gowdy.us/read/UD/258a)
* CZC Gaming Reaper review (CZCGK880K/W, tri-mode): <https://www.arecenze.cz/?p=204581>
* KZZI app listing: <https://www.advanceduninstaller.com/KZZI-RGB-Keyboard-7d80d9c06c8464cc8ecbfd14f64233f4-application.htm>

**Not found despite searching:** SignalRGB's device list (page truncates; no entry could be confirmed), any public statement naming CZC's OEM, any USB-IDs database entry for `258A:00C1`, and any non-RK brand with evidence for the 65-byte RKKB protocol.

---

## Appendix A. All 125 RKKB-class profiles in RK software 4.06

Generated by script from `models.json` (RK repo) **[V]**. "Keys identical to Reaper" counts keys whose (VK, LED) pair equals the Reaper's. "Colour order" is `[OPT] RGBIndex`. "Rangoli status" is its public support list ([`keyboards-list.md`](https://github.com/rnayabed/rangoli/blob/master/keyboards-list.md)): *confirmed* = author-tested, *should work* = untested, *partial* / *not supported* = reported problems. The protocol is the same for all rows; none of these rows proves that a given PID works with Reaper Studio's `06 00` frames.

| PID | Vendor-app name | Layout | Keys | LED grid cols | Keys identical to Reaper (VK→LED) | RGB | Colour order | Rangoli status |
|---|---|---|---:|---:|---:|:-:|:-:|---|
| `00C1` | RK98-100KRGB N Keyboard | 98/ANSI | 100 | 19 | 100 | yes | 0,1,2 | should work |
| `00D8` | RK98Wired-100K N RGB Keyboard | 98/ANSI | 100 | 19 | 100 | yes | 0,1,2 | should work |
| `0173` | RK98RGB ISO Keyboard | 98/ANSI | 101 | 19 | 99 | yes | 0,1,2 | not listed |
| `004B` | RK100RGB Wireless Keyboard | 100/ANSI | 100 | 19 | 96 | yes | 0,1,2 | should work |
| `0056` | RK100RGB Keyboard | 100/ANSI | 100 | 19 | 96 | yes | 0,1,2 | should work |
| `0067` | RK100PRO Keyboard | 100/ANSI | 100 | 19 | 96 | yes | 0,1,2 | should work |
| `0089` | RK96 RGB Keyboard | 96/ANSI | 96 | 19 | 96 | yes | 0,1,2 | should work |
| `00AA` | RK750-100RGB Keyboard | 750/ANSI | 100 | 19 | 96 | yes | 0,1,2 | should work |
| `00B3` | RK98Wired-100KRGB Keyboard | 98/ANSI | 100 | 19 | 96 | yes | 0,1,2 | should work |
| `00E0` | RK100RGB N Keyboard | 100/ANSI | 100 | 19 | 96 | yes | 0,1,2 | should work |
| `01C4` | RK96 ISO Return Keyboard | 96/ISO | 97 | 19 | 95 | yes | 0,1,2 | not listed |
| `0070` | RK98 RGB Keyboard | 98/ANSI | 98 | 19 | 80 | yes | 0,1,2 | should work |
| `017F` | RK-R75RGBSingle mode | R75/ANSI | 81 | 16 | 75 | yes | 0,1,2 | not listed |
| `0059` | RK84RGB Keyboard | 84/ANSI | 84 | 16 | 74 | yes | 0,1,2 | not supported |
| `005D` | RK857 Keyboard | 857/ANSI | 84 | 16 | 74 | yes | 0,1,2 | should work |
| `00C8` | RK84N RGB Keyboard | 84/ANSI | 84 | 16 | 74 | yes | 0,1,2 | should work |
| `00F3` | RK84Pro N RGB Keyboard | 84/ANSI | 84 | 16 | 74 | yes | 0,1,2 | should work |
| `010D` | RK84RGB N Wired Keyboard | 84/ANSI | 84 | 16 | 74 | yes | 0,1,2 | should work |
| `00F4` | RK84 RGB ISO Return Keyboard | 84/ISO | 85 | 16 | 73 | yes | 0,1,2 | should work |
| `0184` | RK-R65RGBSingle mode | R65/ANSI | 67 | 16 | 61 | yes | 0,1,2 | not listed |
| `004D` | RK71RGB Wired Keyboard | 71/ANSI | 71 | 16 | 60 | yes | 0,1,2 | should work |
| `005A` | RK71RGB Keyboard | 71/ANSI | 71 | 16 | 60 | yes | 0,1,2 | confirmed |
| `0069` | RK71RGB Wireless | 71/ANSI | 71 | 16 | 60 | yes | 0,1,2 | confirmed |
| `006F` | RK68Plus Keyboard | 68/ANSI | 68 | 16 | 60 | yes | 0,1,2 | should work |
| `00A5` | RK71RGB N Keyboard | 71/ANSI | 71 | 16 | 60 | yes | 0,1,2 | should work |
| `00B5` | RK71RGB N BT Keyboard | 71/ANSI | 71 | 16 | 60 | yes | 0,1,2 | should work |
| `00D6` | RK Round RGB Keyboard | RK/ANSI | 73 | 16 | 60 | yes | 0,1,2 | should work |
| `00EA` | RK71RGB N Wired Keyboard | 71/ANSI | 71 | 16 | 60 | yes | 0,1,2 | confirmed |
| `0049` | RK-G68 Keyboard | G68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `005E` | RK68RGB Bluetooth Dual Mode | 68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `0064` | RK68RGB Wired Keyboard | 68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `0079` | RK68Pro Keyboard | 68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `007C` | RK87T RGBWired Keyboard | 87/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `00A2` | 68RGB Wired Keyboard | 68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `00DA` | RK68plus RGB N Keyboard | 68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `00E7` | RKG68RGB N Keyboard | G68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `00FF` | RK68RGB N Bluetooth Dual Mode | 68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `010A` | RK68RGB N Wired Keyboard | 68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | should work |
| `013A` | RKG68 RGB N Wired Keyboard | G68/ANSI | 68 | 16 | 59 | yes | 0,1,2 | not listed |
| `004C` | RK87RGB Keyboard | 87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `004F` | G87RGB Wired Keyboard | G87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `005B` | RK987RGB | 987/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `005C` | RK-G68RGB-UK Keyboard | G68/ANSI | 69 | 16 | 58 | yes | 0,1,2 | should work |
| `0068` | RK987RGB Wired Keyboard | 987/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `0075` | G68RGB ISO Return Keyboard | G68/ISO | 69 | 16 | 58 | yes | 0,1,2 | should work |
| `0078` | RK87 Keyboard | 87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `007B` | RK896 Keyboard | 896/ANSI | 104 | 21 | 58 | yes | 0,1,2 | should work |
| `007D` | G87RGB Wired Keyboard | G87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `0093` | RK104 RGB three-mode | 104/ANSI | 104 | 21 | 58 | yes | 0,1,2 | not listed |
| `0094` | RK104 RGB Wired | 104/ANSI | 104 | 21 | 58 | yes | 0,1,2 | not listed |
| `009C` | RK89RGB Keyboard | 89/ANSI | 89 | 17 | 58 | yes | 0,1,2 | should work |
| `009E` | RK92RGB N Keyboard | 92/ANSI | 92 | 17 | 58 | yes | 0,1,2 | should work |
| `00A1` | RK932RGB Keyboard | 932/ANSI | 108 | 21 | 58 | yes | 0,1,2 | not listed |
| `00A9` | RK68ISO Return Keyboard | 68/ISO | 69 | 16 | 58 | yes | 1,0,2 | not supported |
| `00B4` | RK937RGB Wired Keyboaed | 937/ANSI | 108 | 21 | 58 | yes | 0,1,2 | not listed |
| `00C3` | K87RGB N Keyboard | K87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `00C4` | RK87RGB N Wired Keyboard | 87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `00C5` | RK-S108RGB | S108/ANSI | 108 | 21 | 58 | yes | 0,1,2 | not listed |
| `00DE` | RK883RGB N Wired Keyboard | 883/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `00EF` | RK-H87RGB Keyboard | H87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `00F6` | RK-R87RGB Wired Keyboard | R87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `00F9` | RK987RGB N  Keyboard | 987/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `0103` | RK68 N ISO Return Keyboard Bluetooth | 68/ISO | 69 | 16 | 58 | yes | 0,1,2 | should work |
| `0106` | RK-S87 RGB Keyboard | S87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | not listed |
| `010F` | RK-S87 RGB Wired Keyboard | S87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | not listed |
| `011E` | RK-R104 RGB | R104/ANSI | 104 | 21 | 58 | yes | 0,1,2 | not listed |
| `0129` | RK-R87S | R87/ANSI | 87 | 17 | 58 | yes | 0,1,2 | not listed |
| `014F` | RK932RGB | 932/ANSI | 108 | 21 | 58 | yes | 0,1,2 | not listed |
| `01FF` | RK987RGB 2.4G N Keyboard | 987/ANSI | 87 | 17 | 58 | yes | 0,1,2 | should work |
| `0214` | RK S108 Keyboard | S108/ANSI | 104 | 21 | 58 | yes | 2,0,1 | not listed |
| `008F` | RK920RGB Keyboard | 920/ANSI | 104 | 21 | 57 | yes | 0,1,2 | should work |
| `00F2` | RK87RGB ISO Return Keyboard | 87/ISO | 88 | 17 | 57 | yes | 0,1,2 | not listed |
| `01C5` | RK-R87 ISO Return Keyboard | R87/ISO | 88 | 17 | 57 | yes | 0,1,2 | not listed |
| `01E3` | RK-R87 Wired Keyboard | R87/ANSI | 89 | 17 | 57 | yes | 0,1,2 | not listed |
| `004A` | RK61RGB Wired Keyboard | 61/ANSI | 61 | 14 | 56 | yes | 1,0,2 | should work |
| `0055` | RK-61 Keyboard | 61/ANSI | 61 | 14 | 56 | yes | 1,0,2 | should work |
| `007A` | RK61 RGB Bluetooth Dual Mode | 61/ANSI | 61 | 14 | 56 | yes | 0,1,2 | should work |
| `00C6` | RK858  61   Keyboard | 858/ANSI | 61 | 14 | 56 | yes | 0,1,2 | should work |
| `00C7` | RK61RGB N Wired Keyboard | 61/ANSI | 61 | 14 | 56 | yes | 1,0,2 | not listed |
| `00C9` | RK61 N RGB Bluetooth Dual Mode | 61/ANSI | 61 | 14 | 56 | yes | 0,1,2 | should work |
| `00E1` | RK61RGB N Keyboard | 61/ANSI | 61 | 14 | 56 | yes | 0,1,2 | should work |
| `00ED` | RK61 N RGB Bluetooth Dual Mode | 61/ANSI | 61 | 14 | 56 | yes | 0,1,2 | should work |
| `00F8` | RK61plus RGB Keyboard | 61/ANSI | 61 | 14 | 56 | yes | 0,1,2 | should work |
| `0065` | RK61RGB ISO Return Keyboard | 61/ISO | 62 | 14 | 55 | yes | 1,0,2 | partial |
| `008C` | RK61RGB ISO ReturnWired | 61/ISO | 62 | 14 | 55 | yes | 1,0,2 | should work |
| `00EC` | RK61RGB N ISO Return Keyboard | 61/ISO | 62 | 14 | 55 | yes | 0,1,2 | should work |
| `0183` | RK 61 | 61/ANSI | 63 | 14 | 55 | yes | 0,1,2 | not listed |
| `01AB` | RK61plus ISO Return Keyboard | 61/ISO | 62 | 14 | 55 | yes | 0,1,2 | not listed |
| `00D7` | RK SPLIT70 three-mode | T70/ANSI | 74 | 17 | 2 | yes | 0,1,2 | not listed |
| `014D` | RK868RGB Wired keyboard | 868/ANSI | 74 | 17 | 2 | yes | 0,1,2 | not listed |
| `01D9` | RK-S70 DE Keyboard | S70/DE | 75 | 17 | 2 | yes | 0,1,2 | not listed |
| `0220` | RK-S70 Keyboard | S70/ANSI | 74 | 17 | 2 | yes | 0,1,2 | not listed |
| `0229` | RK-S70 FR Keyboard | S70/FR | 75 | 17 | 2 | yes | 0,1,2 | not listed |
| `0166` | RK-G98 Single three-mode | G98/ANSI | 100 | 19 | 95 | no | 0,1,2 | not listed |
| `023D` | RK G98 Keyboard | G98/ANSI | 100 | 19 | 95 | no | 0,1,2 | not listed |
| `015B` | RK-R75 | R75/ANSI | 81 | 16 | 72 | no | 0,1,2 | not listed |
| `020B` | RK X87 Keyboard | X87/ANSI | 86 | 17 | 72 | no | 0,1,2 | not listed |
| `0057` | RK857 Single | 857/ANSI | 84 | 16 | 71 | no | 0,1,2 | partial |
| `00C0` | RK857 Single | 857/ANSI | 84 | 16 | 71 | no | 0,1,2 | should work |
| `015D` | R98 Single Color Wired | R98/ANSI | 99 | 18 | 69 | no | 0,1,2 | not listed |
| `0167` | R98 Single three-mode | R98/ANSI | 99 | 18 | 69 | no | 0,1,2 | not listed |
| `0048` | RK-68Plus | 68/ANSI | 68 | 16 | 59 | no | 0,1,2 | should work |
| `014E` | RK-F68 | F68/ANSI | 68 | 16 | 59 | no | 0,1,2 | not listed |
| `004E` | RK71 Keyboard | 71/ANSI | 71 | 16 | 58 | no | 0,1,2 | should work |
| `006A` | RK71 Bluetooth Dual Mode | 71/ANSI | 71 | 16 | 58 | no | 0,1,2 | should work |
| `006B` | RK87(882) Keyboard | 87/ANSI | 87 | 17 | 58 | no | 0,1,2 | should work |
| `0074` | RK87(882) Wired Keyboard | 87/ANSI | 87 | 17 | 58 | no | 0,1,2 | should work |
| `0053` | RK960 Keyboard | 960/ANSI | 104 | 21 | 57 | no | 0,1,2 | should work |
| `0054` | RK987 Single Color | 987/ANSI | 87 | 17 | 57 | no | 0,1,2 | should work |
| `0058` | RK SINK104 Keyboard | K104/ANSI | 104 | 21 | 57 | no | 0,1,2 | should work |
| `00A6` | RK932 Keyboard | 932/ANSI | 108 | 21 | 57 | no | 0,1,2 | should work |
| `00F7` | RK-R87 Wired Keyboard | R87/ANSI | 87 | 17 | 57 | no | 0,1,2 | should work |
| `011C` | RK-R104 Single | R104/ANSI | 104 | 21 | 57 | no | 0,1,2 | not listed |
| `0176` | Sink87G | K87/ANSI | 87 | 17 | 57 | no | 0,1,2 | not listed |
| `0177` | RK-R104 | R104/ANSI | 104 | 21 | 57 | no | 0,1,2 | not listed |
| `01DC` | RK-R87 Wired Keyboard | R87/ANSI | 87 | 17 | 57 | no | 0,1,2 | not listed |
| `002A` | RK100 Keyboard | 100/ANSI | 100 | 21 | 56 | no | 0,1,2 | should work |
| `00A4` | RK96 Keyboard | 96/ANSI | 96 | 21 | 56 | no | 0,1,2 | confirmed |
| `00CD` | RK89 Keyboard | 89/ANSI | 89 | 20 | 56 | no | 0,1,2 | should work |
| `0052` | G68 Keyboard | G68/ANSI | 68 | 15 | 55 | no | 0,1,2 | should work |
| `0060` | RK61 Keyboard | 61/ANSI | 61 | 14 | 55 | no | 0,1,2 | should work |
| `008B` | RK68 Bluetooth Dual Mode | 68/ANSI | 68 | 15 | 55 | no | 0,1,2 | should work |
| `008E` | RK68 a switch Keyboard | 68/ANSI | 68 | 15 | 55 | no | 0,1,2 | should work |
| `00B8` | RK61 Bluetooth Dual Mode | 61/ANSI | 61 | 14 | 55 | no | 0,1,2 | should work |
| `0066` | RK61ISO Return Keyboard | 61/ISO | 62 | 14 | 54 | no | 0,1,2 | should work |
