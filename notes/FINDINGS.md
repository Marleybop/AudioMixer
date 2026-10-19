# Findings — AudioMixer (SteelSeries Sonar / GG + Arctis Nova Pro Wireless)

Target(s): see original/SHA256SUMS.txt (GG install: C:\Program Files\SteelSeries\GG)
Started: 2026-10-18

Confidence: CONFIRMED (seen at runtime / proven by cited xrefs) · LIKELY (strong static evidence) · GUESS
Cite addresses for every claim.

## Architecture
- `apps\sonar\SteelSeriesSonar.exe` — self-contained .NET, unobfuscated. SteelSeries code = SoundStage.*, Sonar.*.
  Decompiled to work/decomp (ilspycmd 11.1). CONFIRMED.
- Sonar audio path: kernel VAD `SteelSeries-Sonar-VAD.sys` (virtual Game/Chat/Media/Aux/Mic endpoints)
  + `Sonar.APO.dll` (APO: EQ, spatial, ONNX/DirectML noise-cancel) controlled via `Sonar.APOAPI.dll`. LIKELY (file layout + interop names).
- `apps\engine\SteelSeriesEngine.exe` — Go 1.15+ binary embedding a Lisp interpreter ("golisp"); owns HID.
  Device behaviour in `deviceSpecifications\*.edevice` = base64 encrypted "-----BEGIN DESCRIPTOR-----" blobs. CONFIRMED.
- Local APIs (addresses in C:\ProgramData\SteelSeries\GG\coreProps.json):
  GG https://127.0.0.1:6327 (`/subApps`), Engine https://127.0.0.1:35780 (`/devices`, `/device/{id}/deviceFunction/{feature}`). CONFIRMED (curl).
  Sonar → Engine: `/device/{id}/deviceFunction/<feature>` (SoundStage.Services.Engine/SidetoneService.cs). CONFIRMED (decomp).

## Nova Pro Wireless HID
- Base station VID 1038 PID 12E0. Vendor interface MI_04 Col01, usage page 0xFFC0. CONFIRMED (hidapi enumerate).
- Engine device id 245 `arctis_nova_pro_wireless_tx`; status props `wirelessConnection`, `batteryLevels`. CONFIRMED (/devices).
- Status query: write `06 B0` (64-byte report), reply `06 B0 ...`. Works alongside GG (shared access). CONFIRMED (scripts/novaprobe.py).
  Engine's decoded fields for this reply (Engine heap): bt_power_default, bt_call_default, bt_connection_mode, bt_connection_status,
  headset_batt_level, charger_batt_level, headset_batt_status, mic_muted, muted_mic_brightness, power_inactivity_timer,
  radio_connection_status, transparency_mode, transparent_level, wireless_mode. CONFIRMED (scripts/memgrep.py → live/mem_radio.bin).
  Byte layout is authoritative from the decrypted spec (`wireless_settings` struct in base_arctis_nova_pro_wireless)
  and matches the live replies:
  `[2]bt_power_default [3]bt_call_default [4]bt_connection_mode [5]bt_connection_status [6]headset_batt_level(0-8)
   [7]charger_batt_level [8]transparent_level [9]mic_muted [10]transparency_mode(0 off/1 transp/2 ANC)
   [11]muted_mic_brightness [12]power_inactivity_timer [13]wireless_mode [14]radio_connection_status [15]headset_batt_status`
  - radio_connection_status (byte 14): 8 PAIRED_CONNECTED → 4 PAIRED_NOT_CONNECTED when headset switched off — CONFIRMED (spec + on/off test 15:04:26/15:04:40, live/novawatch.log)
  - headset_batt_status (byte 15): 8 discharging → 1 disconnected — CONFIRMED (same)
  - (My pre-spec guess had bytes 14/15 swapped — corrected.)
- Input reports reach every open handle, so replies to the Engine's own queries appear in our reads (`06 20`, `06 10`). CONFIRMED.

## Prior work
- My earlier hardware research on this base station (Aug 2026) is kept privately and is not part of this repository.
- My original base station (now in storage) had a patched bootloader. The unit I use now is a **replacement with stock firmware/bootloader**.
- Still, the app never needs `06 EB` (reboot to bootloader), `06 A1` (flash), `06 BB` (handover), `06 01` (reset_mcu) or any fw_update,
  and it ships to other people, so the allowlist keeps blocking them (firmware updates stay GG's job).

## Nova Pro Wireless command set (from spec; report id 0x06, 64-byte HIDIO on MI_04 Col01 / usage 0xFFC0)
Reads: `10` fw versions (4×12 ASCII) · `12` unique id · `20` audio_settings · `80` ux_settings · `B0` wireless_settings · `B7` battery
Writes: `09` save_to_flash · `27` high_gain (dev value 1 low/2 high) · `2E` eq preset (0 Flat,1 Bass,2 Focus,3 Smiley,4 Custom,5-18 games)
  · `33` custom EQ 10 bands, byte = 2*(dB+10), 0..40 (0x14 = 0 dB), ±10 dB in 0.5 dB · `37` mic volume 1-10 · `39` sidetone 0-3
  · `43` line-out mode (1 ChatMix, 2 StreamMix) · `47` stream mix · `49` software chatmix on/off · `8D` sonar_present on/off
  · `83` dim timer (takes the STEP 0–6, not minutes) · `85` OLED brightness · `89` home screen · `B2` BT on startup · `B3` BT call behaviour · `BF` mute LED · `C1` headset sleep timer · `C3` speed/range
  · `93` draw OLED bitmap (feature report) · `95` reload display
Push events on sync interface (MI_04 Col02, usage page 0xFF00): `B5` connection (status in byte 4), `B7` battery, `45` ChatMix game/chat,
  plus echo of setting changes made on the dial (27/2E/31/37/39/43/47/83/85/89/B2/B3/BF/C1/C3).
Live (2026-10-18 15:10): ux_settings `06 80 01 01 01 01 00 01` → sonar_present=1 (GG enabled it); battery `06 b7 06 08 08`. CONFIRMED.

## Sonar VAD as a free, already-signed channel backend (2026-10-18 15:3x)
- Watchdog: `SteelSeriesGGEZ.exe` respawns GG/Engine/Sonar within ~3 s; stopping it first keeps everything down. CONFIRMED.
- With ALL SteelSeries processes stopped, the VAD endpoints stay present (Gaming/Chat/Media/Microphone; Aux/Stream disabled in registry). CONFIRMED.
- Tone rendered into Gaming / Chat / Microphone(render) is captured back by WASAPI loopback at the sent level (rms 0.035, 1 kHz). CONFIRMED (scripts/vadtest.py).
- Virtual mic path: render into "Sonar - Microphone" (render endpoint) → appears on "Sonar - Microphone" capture endpoint. CONFIRMED.
- ⇒ Our app can own routing: loopback-capture each virtual channel → mix/ChatMix/EQ → render to active physical output; and headset mic → process → virtual mic.

## Sonar APO control API (the actual routing/DSP engine)
- With all GG processes stopped, music on the Media channel was STILL heard in the headset → the APO (Sonar.APO.dll in audiodg)
  does routing itself using persisted settings; the Sonar app only configures it. CONFIRMED (heard it, 2026-10-18).
- Control = in-proc COM server `Sonar.APOAPI.dll`: DllGetClassObject(CLSID DEA05346-7BE3-4DB0-AE9F-14423648EA7B) → IClassFactory
  → IBasicControl {40076C40-0E9C-4D14-9CF6-34839A766B8A}; `Initialize(repo)` with repo ∈ Game, ChatRender, Media, Aux, ChatCapture;
  `OpenStore` → IStore {1191FCBC-…} → `OpenSetting(ESetting)` → ISetting {A108D082-…} Get/Set Bool/Long/Float/String/arrays.
  Interfaces from Interop.SonarAPOSettingsControl.dll (decomp/Interop.SonarAPOSettingsControl). CONFIRMED (decomp ControlInteropService.cs).
- ESetting catalog (~280): redirection device/state/gain/mute ×2 outputs, capture redirection, 10-band parametric EQ (freq/Q/gain/type),
  compressor, limiter, bass/treble/mid boost, virtual surround, ChatMixGainLin, noise gate, AI noise cancelling, AEC.
- Our own process can READ (work/probes/ApoProbe): Game/Media/Aux → {92425ba9…} (Nova Pro headphones); ChatRender via CaptureStreamRedirection;
  Game ChatMixGainLin 0.709. CONFIRMED.
- Our own process can WRITE: Media kSet_StreamRedirectionGainLin 1.0→0.2→1.0, takes effect live. CONFIRMED (`ApoProbe dip-media`, 2026-10-18).

## Sync interface pushes (MI_04 Col02, usage page 0xFF00) use REPORT ID 0x07 (not 0x06) — CONFIRMED 2026-10-18 16:13
- `07 25 <vol>` base station volume dial, 0..56.
- `07 45 <game> <chat>` ChatMix dial, each 0..100, one side pinned at 100 (0x64). Full game = `64 00`, full chat = `00 64`.
- Seen with sonar_present=1 and software ChatMix (0x49) on. (scripts/rawall.py)

## How Sonar wires channels (decomp SteelSeriesSonar/…/IServiceCollectionExtensions.cs:38-56)
- Game, Media, Aux → `ApoRenderRedirection` → settings kSet_StreamRedirection{State,DeviceId,GainLin,Mute}[2] on repo Game/Media/Aux.
- Chat → `ApoCaptureRedirection` → kSet_CaptureStreamRedirection{State,DeviceId,GainLin,Mute}[2] on repo ChatRender.
- Mic / MicPlayer / MicCopy → `LibraryRedirection` = in-process capture→render inside SteelSeriesSonar.exe
  (Sonar.AudioStreamProcessing.Interop). DIES with the app → our app must bridge headset mic → "Sonar - Microphone" render endpoint.
  Mic DSP (noise cancel, gate, AEC) lives on repo ChatCapture, applied by the APO on the virtual mic.
- ChatMix (ChatMixService.cs): device gives (gameGain, chatGain) in 0..1 with one of them = 1;
  balance = game<1 ? 1-game : chat-1 (∈[-1,1]); s = sign(b)*2b/(b+sign(b)); Game.kSet_ChatMixGainLin = clamp(1-s,0,1);
  ChatRender.kSet_CaptureChatMixGainLin = clamp(1+s,0,1). Only active when Game and Chat route to the same device.

## End-to-end engine test (2026-10-18 16:07–16:37, GG fully stopped, src/AudioMixer.Cli run)
- Routing all channels → headset, mic bridge → virtual mic (live signal), headset off/on reroute in ~150 ms. CONFIRMED.
- ChatMix dial → APO gains, verified by reading back Game kSet_ChatMixGainLin = 0.835 at dial 91/100. CONFIRMED.
- kSet_ChatMixGainLin also works on Media and Aux (Sonar never uses it there) → the dial could balance Chat vs Game+Media+Aux. CONFIRMED (YouTube Music on Media faded at full chat). Not used: AudioMixer keeps Sonar's Game vs Chat.
- Per-app outputs are Windows' own per-app assignments (HKCU\…\Audio\PolicyConfig\PropertyStore); Sonar had pinned chrome.exe to Sonar Media.
- Must reset ChatMixGainLin to 1 on exit — Sonar never resets Media/Aux, so a stale 0 silences them. (Router.Dispose does this.)

## Sonar's per-channel sound → APO settings (decomp: RenderConfigSettings, CaptureConfigSettings, AudioRepository, AudioSettingExtensions)
- Game/Media/Aux use the playback set; **Chat (ChatRender repo) and Mic (ChatCapture) use the voice set**, including the *Capture*ParametricEq settings.
- Playback: RenderState · BassBoost{State,GainDB} (80 Hz) · voice = MidBoost{State,GainDB} (2 kHz) · TrebleBoost{State,GainDB} (8 kHz) ·
  Smart volume = CompressorState + CompressorLevel (0–1) + loudness as Compressor1/2ThresholdDB (−9 loud, −18 balanced, −27 quiet) ·
  Volume boost = GeneralGainDB (UI 0–12) · Spatial = VirtualSurroundState + AdvancedSurroundState, FormFactor (long, 0 headphones / 1 speakers),
  ReverbGainDB (−12 performance … 0 immersion), 8× ChannelPosition*71 / GainDB* (distance = gain on all but the sub, 0–12).
- Voice: CaptureState · CaptureCompressor{State,Level} ("Compressor") · NoiseGate{State,ThresholdDB −60…−10,AutoThreshold}, NoiseGateOffsetDB = 3 ·
  CaptureAmbientNoiseReduction{State,Rate} (background) · ImpactNoiseReduction{State,Agressiveness} — both forced off while ClearCast is on ·
  ClearCast = NoiseCanceling{State,MixFactor} · AECState (needs AEC{Game,ChatRender,Media,Aux}DeviceId on ChatCapture = each channel's output, set on routing).
- EQ filter Type is a long: 0 bypass, 1 low pass, 2 high pass, 3 band pass Q, 4 band pass 0 dB, 5 notch, 6 all pass, 7 peaking, 8 low shelf, 9 high shelf.
  APO ranges: freq 20–20000, Q 0.5–10, gain ±36 (Sonar's UI shows ±12).
- Verified: reading all five channels and writing the same values back changes nothing (`AudioMixer.Cli sound <channel>`); a bass boost set from the
  app's UI reads back from the APO. CONFIRMED (2026-10-18).
- Sonar's presets live in `C:\ProgramData\SteelSeries\GG\apps\sonar\db\database.db`, table `configs` (`vad` 1 Game, 2 Chat, 3 Mic, 4 Media, 5 Aux;
  `data` = the config JSON above, camelCase) and `selected_config`. 399 rows here (346 game presets). Read at run time from a temp copy with Windows' winsqlite3.
- Not in Sonar's UI but in the APO: ForceMonoMixState, a master limiter, compressor bands, noise gate attack/release, reverb filters. Not used yet.

## Open questions
- Does the VAD driver survive a GG uninstall?
- ~~Which byte = radio_connection_status~~ resolved (byte 14). ~~Decrypt .edevice~~ resolved via prior work.
- With sonar_present=0 and software chatmix off, does the base expose a separate Chat render endpoint for hardware ChatMix?
  Only one "Headphones (Arctis Nova Pro Wireless)" endpoint is currently active — check disabled/hidden endpoints.
- Confirm push events on Col02 (`B5`) live, so switching is event-driven instead of polled.
- How Sonar configures its APO (Sonar.APOAPI) — only matters if we want parametric EQ beyond the DSP's 10 bands.

## Findings
| Date | Address / item | Claim | Confidence | Evidence |
|---|---|---|---|---|

## What the base station can set vs only report (from base_arctis_nova_pro_wireless.lsp, 2026-10-18)
- **Writable:** EQ preset 0x2E (0 Flat, 1 Bass boost, 2 Focus, 3 Smiley, 4 Custom, 5–18 game presets), custom EQ 0x33, high mic gain 0x27, mic volume 0x37, sidetone 0x39 (0–3),
  line out 0x43 (1 ChatMix / 2 Stream mix), stream mix 0x47 (main/aux/mic 0–100), dim timer 0x83, OLED brightness 0x85 (1–10), home screen 0x89 (Detailed/Simple),
  BT on at power-up 0xB2, BT call behaviour 0xB3 (nothing / −12 dB / mute), mute-light brightness 0xBF (1–10), headset sleep 0xC1, Speed/Range 0xC3. CONFIRMED (spec).
- **Timers (0x83 dim, 0xC1 sleep) take the step 0–6** ([0,1,5,10,15,30,60] min), not minutes: GG's engine converts with
  `transform_minutes_to_dim_timer` before writing. My first version sent minutes (a bug). CONFIRMED (spec + live: wrote 5 min → read back step 2).
- Stream mix 0x47 = `[main, unused, aux, mic]`, each 0–100; in the 0x20 report at r[22], r[23] unused, r[24], r[25]. CONFIRMED (spec + live read `64 00 64 64`).
- Firmware 0x10 = four 12-char ASCII fields (base DSP, base MCU, headset MCU, headset BT), e.g. `0000.003.082`. Mine: base 0.3.82 / 1.29.27, headset 1.22.11 / 1.15.4. CONFIRMED (live).
- The base station echoes setting changes made on its own menu on the sync interface (report 0x07, same command byte; `31 band gain` for one custom EQ band). LIKELY (spec `translate_sync_data`).
- The mic-muted light is drawn by the headset firmware using muted_mic_brightness; nothing in Sonar or GG drives it otherwise. CONFIRMED (spec + decomp search).
- **Read-only** (headset buttons only; no write command in the spec): ANC/transparency mode and level, mic mute, Bluetooth power/link state, battery, volume, audio input (USB1/USB2/Xbox).
  Read: ux_settings also has idle_brightness and home_screen_mic_mute, but the spec has no separate write for them. CONFIRMED (spec).
- **No RGB** on this headset/base. The only "lights" are OLED brightness/idle/dim and the mic-muted light brightness.
- Battery is in 8 steps (12.5 %), not 1 %.
- Software mic mute is possible on our side (MicBridge / Sonar virtual mic), separate from the headset's hardware mute.
