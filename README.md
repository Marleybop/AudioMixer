# AudioMixer

My lightweight replacement for the SteelSeries GG / Sonar apps, for the **Arctis Nova Pro Wireless** on Windows.

GG and Sonar use over a gigabyte of RAM between them and kept falling over on me, so I replaced the apps. AudioMixer keeps the parts that already work well: the Sonar virtual audio driver and the Sonar audio engine that GG installs.

> Work in progress. Routing, ChatMix, the mic, EQ and effects, and the headset settings all work on my setup. Per-app routing (an Apps page) is next.

## What it does

- **Channels:** Game, Chat, Media, Aux and Mic, using Sonar's virtual devices. Apps play to a channel, and every channel goes to your real output.
- **EQ and effects per channel**, the same as Sonar's: a 10-band parametric EQ you drag on a graph, bass/voice/treble boosts,
  spatial audio, volume boost and smart volume on Game, Media and Aux; ClearCast AI noise cancelling, echo cancelling, noise reduction,
  noise gate and compressor on Chat and Mic. Sonar's preset library (and anything you saved in Sonar) shows up, and you can save your own.
- **Each channel can play on its own device**, or follow your priority list.
- **Add or remove channels:** Media and Aux can be added to or removed from the mixer, which also turns them on or off in Windows. Sonar's driver has four playback channels plus the mic, so that's the limit.
- **ChatMix:** the dial on the base station balances Game against Chat, the same as in Sonar. The switch in the app turns ChatMix on or off on the base station.
- **Device priority:** the headset is used while it's switched on. When you turn it off, audio moves to the next device in your list in about 150 ms.
- **Blocklist:** keeps devices like a controller's speaker and mic disabled, so they can't take over as the default.
- **Mic:** your headset mic goes to the virtual "Sonar - Microphone" that Discord and games record from, with its own volume and mute.
- **Base station screen:** show AudioMixer's widget (headset and spare battery, a big mic live/muted, volume), your own pictures and animated GIFs (drop them in `%APPDATA%\AudioMixer\Screens`), or nothing, with a live preview in the app. The screen can also turn itself off after a while with no keyboard, mouse or dial use, handy if the PC stays on overnight.
- **Headset settings:** base station EQ, sidetone, mic volume, line out and stream mix, screen and mute light brightness, Bluetooth, sleep timer, battery, firmware and status, all talking straight to the base station.
- Runs from the tray, no admin rights needed.

## Requirements

- Windows 10/11 x64, .NET 10.
- SteelSeries GG **installed**. AudioMixer uses its signed Sonar driver and audio engine, so GG has to stay installed, but none of its apps need to run. AudioMixer can stop GG and turn off its autostart, and you can undo that in Settings.
- An Arctis Nova Pro Wireless (base station USB `1038:12E0`). Routing and channels also work without the headset settings.

## Build

```
dotnet build src/AudioMixer.slnx -c Release
```

- `src/AudioMixer.Core`: headset protocol, Sonar engine control, routing, mic bridge, config.
- `src/AudioMixer.App`: WPF tray app (`AudioMixer.exe`).
- `src/AudioMixer.Cli`: test host (`list`, `status`, `run`, `watch`, …).
- `notes/FINDINGS.md`: my notes on the base station protocol and the Sonar engine, with how sure I am of each.
- `tools/make_icon.py`: draws the app icon (needs Pillow).

## Safety

The app only sends the base station the setting commands on an allowlist (`NovaProProtocol.AllowedCommands`). Firmware update, bootloader and reset commands are refused in code. Use GG for firmware updates.

## Disclaimer

This is an independent project. It isn't affiliated with, endorsed by, or supported by SteelSeries. SteelSeries, Arctis, GG and Sonar are trademarks of their owners. The repository contains no SteelSeries code or files. It uses SteelSeries software that is already installed on your PC.

## License

[MIT](LICENSE)
