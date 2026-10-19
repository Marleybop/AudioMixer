# Third-party notices

AudioMixer itself is under the [MIT license](LICENSE). The release build includes these libraries, unchanged, under their own licenses.

| Library | License | Source |
|---|---|---|
| .NET runtime and WPF | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| H.NotifyIcon, H.NotifyIcon.Wpf, H.GeneratedIcons.System.Drawing | MIT | https://github.com/HavenDV/H.NotifyIcon |
| NAudio.Core, NAudio.Wasapi | MIT | https://github.com/naudio/NAudio |
| HidSharp | Apache-2.0 | https://software.seekye.com/hidsharp |
| System.IO.Ports, System.Management, System.Numerics.Tensors | MIT | https://github.com/dotnet/runtime |
| Mono.Posix.NETStandard | MIT | https://github.com/mono/mono |
| LibreHardwareMonitorLib | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit, RAMSPDToolkit-NDD, BlackSharp.Core (used by LibreHardwareMonitorLib) | MPL-2.0 | https://github.com/Blacktempel |

The MPL-2.0 libraries are used as published on NuGet. Their source code is at the links above.

**Not included:**
- **The PawnIO driver** (GPL), which the optional CPU temperature needs. Install it yourself from https://pawnio.eu/.
- **SteelSeries software.** AudioMixer uses the Sonar driver and engine that SteelSeries GG has already installed on your PC.

**Weather data** is from [Open-Meteo.com](https://open-meteo.com/), under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
