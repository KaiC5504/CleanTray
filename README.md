# CleanTray

Hide chosen apps' tray icons on Windows 11 without closing the apps.

Windows 11 can only move a tray icon between the taskbar and the `^` overflow; it can't remove one app's icon. CleanTray takes the icons you pick out of the tray entirely and keeps them in a single CleanTray icon. The apps keep running and never notice: left-click, double-click and right-click on an icon in CleanTray's panel reach the real app exactly as they would from the normal tray.

## Features

- Per-app choice in Settings: **Show** or **Inside CleanTray**, with per-icon overrides for apps that show several icons.
- Lists every app Windows has ever shown in the tray, including ones that were already running before CleanTray started. A search box finds apps by name or exe file name.
- Clicks and right-click menus are forwarded to the real app.
- Hold an icon in the panel, then drag it to reorder; the order is remembered. Releasing outside the panel or pressing Esc cancels.
- Per-app pop-ups: a hidden app's balloon notifications can still be shown (as a Windows toast) or blocked.
- Rules survive app updates (version folders such as Squirrel `app-1.2.3` and MSIX package versions are ignored).
- Optional: hide CleanTray's own icon too and open the panel with a shortcut (default `Ctrl+Alt+T`).
- Optional "hide new apps automatically", behind a warning.
- If CleanTray exits or crashes, every hidden icon returns to the normal tray.
- Starts at logon through a per-user scheduled task, ahead of the usual startup apps.

## How it works

CleanTray has two parts:

- `CleanTrayHook.dll` (C++) is loaded into Explorer's taskbar thread with a thread-targeted `SetWindowsHookEx`. It subclasses `Shell_TrayWnd` and inspects the `WM_COPYDATA` messages that `Shell_NotifyIcon` sends, passing shown icons through and keeping hidden ones. Clicks on hidden icons are replayed from inside Explorer, so apps receive exactly what the real tray would send.
- `CleanTray.exe` (C#, WPF) is the master icon, panel and Settings. It talks to the DLL over a per-session named pipe and publishes the rules through shared memory, so the taskbar thread never waits on it.

Some antivirus tools may be suspicious of a DLL loaded into Explorer. Windows' own icons (volume, network, battery) don't use the normal tray API and can't be hidden.

## Requirements

- Windows 11, x64
- To build: .NET SDK 10, Visual Studio 2022+ Build Tools with the C++ workload, CMake 3.20+, PowerShell 7

## Build

```powershell
./build.ps1
```

Builds the hook DLL and tests with CMake, then the .NET solution.

## Test

```powershell
dotnet test tests/CleanTray.Core.Tests
ctest --test-dir build -C Release --output-on-failure
```

`tools/TrayProbe` is a small tray app for manual testing: it adds one icon and logs every callback it receives.

## Install

```powershell
pwsh scripts/install.ps1
```

Builds, publishes and installs to `%LOCALAPPDATA%\Programs\CleanTray` (change with `-InstallDir`), registers the logon task, adds a Start menu shortcut, and starts CleanTray. No admin rights needed.

## License

[MIT](LICENSE)
