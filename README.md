# Keyboard Overlay

[![Build Windows app](https://github.com/Ismail79-byte/keyboard-overlay/actions/workflows/build.yml/badge.svg)](https://github.com/Ismail79-byte/keyboard-overlay/actions/workflows/build.yml)

A small, transparent keyboard and mouse overlay for Windows game recordings, speedruns, and tutorials. It displays live key presses and mouse clicks without recording video or audio.

## Download

Download **[KeyboardOverlay.exe from the latest release](https://github.com/Ismail79-byte/keyboard-overlay/releases/latest)**. Run it to show the overlay at the bottom-left of your screen. The app stays available from the Windows taskbar and notification area.

## Features

- Live keyboard display for movement and common game keys: Esc, 1–5, Tab, QWERT, Caps, ASDFG, Shift, ZXCVB, Ctrl, backtick, and Space.
- Mouse display for left, right, and middle click, scroll wheel, and side buttons.
- Transparent, click-through, always-on-top overlay that stays in the bottom-left corner.
- Custom outline and pressed-key colors, adjustable opacity, and size.
- Settings saved locally in `%AppData%\KeyboardOverlay\settings.ini`.
- Smiley app icon, taskbar button, and notification-area menu.
- Event-driven updates; it does not poll for input or capture/encode recordings.

## Controls

- **Ctrl+Shift+F11** — hide or show the overlay.
- **Ctrl+Shift+F12** — exit.
- **Right-click the notification-area icon** — open Settings, hide/show, or exit.

## Build from source

Requirements: Windows, PowerShell, and the .NET Framework 4 C# compiler.

```powershell
git clone https://github.com/Ismail79-byte/keyboard-overlay.git
cd keyboard-overlay
.\build.ps1
```

The executable is written to `dist\KeyboardOverlay.exe`. No NuGet packages or third-party runtime are needed.

## License

MIT. See [LICENSE](LICENSE).
