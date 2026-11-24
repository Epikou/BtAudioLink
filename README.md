# BtAudioLink

Send the sound of one Windows PC to another over Bluetooth.

Windows can receive standard Bluetooth audio (A2DP) from a phone, but not from another Windows PC.
BtAudioLink works around that: it captures the system sound on one PC, compresses it with Opus and sends it
over a Bluetooth serial link (RFCOMM) to the other PC, which plays it. No driver, no extra hardware.

## Use

1. Pair the two PCs in Windows Bluetooth settings.
2. Run `BtAudioLink.exe` on both.
3. On the PC that should play the sound, choose **Receiver** and press **Start**.
4. On the PC whose sound you want, choose **Sender**, pick the other PC in **Settings**, and press **Start**.

The sender reconnects by itself. The window hides next to the clock; right-click the icon to quit.

## Features

- Sender / receiver switch, start with Windows, single instance, tray icon coloured by link state
- Option to mute the sending PC's speakers while it sends
- Low latency or stable connection profile, playback volume
- English, French and Spanish
- Gamepad navigation (D-pad or left stick, A to select, B to hide)
- Updates from the published releases of this repository, with a forced reinstall button

## Build

Requires the .NET 9 SDK on Windows.

```powershell
dotnet publish -c Release -o dist
```

## Credits

Interface font: Ubuntu Light, under the Ubuntu Font Licence (see `Assets/Ubuntu-UFL.txt`).
Libraries: NAudio, Concentus (Opus), 32feet.NET (InTheHand.Net.Bluetooth).
