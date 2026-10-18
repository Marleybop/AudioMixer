# AudioMixer

A lightweight replacement for the SteelSeries GG / Sonar apps for the **Arctis Nova Pro Wireless** on Windows.

GG and Sonar are heavy (over a gigabyte of RAM across their processes) and can be unstable. AudioMixer replaces the apps and keeps the parts that work: the Sonar virtual audio driver and the Sonar audio engine that GG already installed.

> Work in progress. The engine works end to end on the author's machine. The UI is being redesigned.

## What it does

- **Channels:** Game, Chat, Media, Aux and Mic, using the Sonar virtual devices. Apps play to a channel; AudioMixer routes every channel to your real output.
- **ChatMix:** the base station's dial balances Chat against Game, plus Media and Aux if you choose.
- **Device priority:** the headset is used while it's switched on. When it's off, audio falls back to the next device in your list (in about 150 ms).
- **Blocklist:** keeps devices such as a controller's speaker and mic disabled, so they can't take over as the default.
- **Mic bridge:** headset mic → the virtual "Sonar - Microphone" that Discord and games use.
- **Headset settings:** base station EQ, sidetone, mic volume, screen brightness, battery and status, over the base station's own HID protocol.
- Runs from the tray without admin rights.

## Requirements

- Windows 10/11 x64, .NET 10.
- SteelSeries GG **installed**. AudioMixer uses its signed Sonar driver and audio engine, so GG has to stay installed, but none of its apps need to run. AudioMixer can stop GG and turn off its autostart, and you can undo this in Settings.
- An Arctis Nova Pro Wireless (base station USB `1038:12E0`). Routing and channels also work without the headset settings.

## Build

```
dotnet build src/AudioMixer.slnx -c Release
```

- `src/AudioMixer.Core`: headset protocol, Sonar engine control, routing, mic bridge, config.
- `src/AudioMixer.App`: WPF tray app (`AudioMixer.exe`).
- `src/AudioMixer.Cli`: test host (`list`, `status`, `run`, `watch`, …).
- `notes/FINDINGS.md`: protocol and engine notes, with a confidence level for each claim.
- `tools/make_icon.py`: draws the app icon (needs Pillow).

## Safety

The app talks to the base station only through an allowlist of setting commands (`NovaProProtocol.AllowedCommands`). Firmware update, bootloader and reset commands are refused in code. Use GG for firmware updates.

## Disclaimer

AudioMixer is an independent project. It is not affiliated with, endorsed by, or supported by SteelSeries. SteelSeries, Arctis, GG and Sonar are trademarks of their owners. The repository contains no SteelSeries code or files. It uses SteelSeries software that is already installed on your PC, and it reads your own local Sonar presets at run time.

## License

[MIT](LICENSE)
