# Changelog

What changed in each release of AudioMixer. Versions follow [semantic versioning](https://semver.org/); while it's 0.x, a minor
version can change how things work.

## [0.1.0] - 2026-10-19

The first release: everything I use day to day in place of the GG and Sonar apps, on my Arctis Nova Pro Wireless.

### Added
- **Channels:** Game, Chat, Media, Aux and Mic on Sonar's virtual devices, each with a level bar and mute, and each able to play on its
  own device or follow the priority list. Media and Aux can be added to or removed from the mixer.
- **ChatMix:** the base station dial balances Game against Chat, as in Sonar.
- **EQ and effects per channel**, the same as Sonar's, with Sonar's preset library and my own saved presets.
- **Device priority and blocklist:** the headset while it's on, then the next device in the list; blocked devices (like a controller's
  speaker and mic) stay disabled.
- **Mic bridge:** the headset mic goes to "Sonar - Microphone" for Discord and games.
- **Headset settings:** base station EQ, sidetone, mic volume, line out and stream mix, brightness, mute light, Bluetooth, sleep and
  dim timers, battery and firmware. Every change is read back from the base station before it's saved there.
- **Base station screen:** the normal display, off, a battery and mic widget, PC stats (CPU and GPU load and temperature, RAM, VRAM),
  the weather for my town with animated snow, rain, fog, lightning or stars, all three taking turns, or my own pictures and GIFs. It can
  sleep after a while with no keyboard, mouse or dial use.
- **Optional CPU temperature**, with the free PawnIO driver and one admin prompt (see the README).
- **Works without the GG app running**, and falls back to the Sonar engine in the driver store.
- A command-line test tool (`AudioMixer.Cli`).

[0.1.0]: https://github.com/Marleybop/AudioMixer/releases/tag/v0.1.0
