# ClipVoice

<img src="Assets/ClipVoice-logo.png" alt="ClipVoice" width="220" />

ClipVoice is a Windows app for clipboard history, reusable text templates, translation, and text-to-speech.

## Download

Download the standalone Windows x64 app from [GitHub Releases](https://github.com/merlinmedeo-ai/ClipVoice/releases/latest). No separate .NET installation is required.

### What's new in 1.0.1

- Updated application icon and a dedicated system tray icon
- Translation results in a popup near the pointer
- Clipboard history available from the template popup menu
- Improved template popup behavior and clipboard duplicate detection

## Features

- Recent clipboard history
- Reusable text templates and global hotkeys
- Template menu with `Alt + Right-click`
- Translation of selected text or clipboard content
- Online speech through Edge with Google TTS as a fallback
- Optional offline Piper speech
- Local settings protected with Windows DPAPI
- System tray support and configurable hotkeys

## Requirements

- Windows 10 or Windows 11, x64
- .NET SDK 8 for development
- Internet access for online translation, online speech, and the initial Piper download

The self-contained release includes the .NET runtime and does not require a separate .NET installation.

## Run from source

```powershell
dotnet restore
dotnet run --project ClipVoice.csproj
```

If the .NET SDK is not installed globally, the project can download a local SDK into `.dotnet` (which Git ignores):

```powershell
.\run-debug.bat
```

## Build a standalone executable

Run:

```powershell
.\build-exe.bat
```

The executable is created at `dist\ClipVoice-1.0.1-win-x64\ClipVoice.exe`. The `dist` directory is excluded from Git; publish binary builds through GitHub Releases.

Equivalent command:

```powershell
dotnet publish ClipVoice.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

## Data and privacy

Settings, clipboard history, templates, logs, and the audio cache are stored in `%LocalAppData%\ClipVoice`, outside the repository. History and templates are protected with Windows DPAPI for the current user.

Text is sent to the relevant external services when you use online translation or speech. Piper works offline after its runtime and voice models have been downloaded. The app uses unofficial consumer interfaces for online services, so they may change without notice.

## Project structure

- `Views/` — WPF windows and layouts
- `Services/` — clipboard, Windows integration, translation, and speech
- `Models/` — settings and data models
- `Native/` — Win32 declarations
- `Assets/` — application icons
- `tools/` — local SDK setup scripts

## Contributing

Before opening a pull request, run a Release build:

```powershell
dotnet build ClipVoice.csproj -c Release
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for details.

## License

No license has been selected yet. Until a `LICENSE` file is added, standard copyright remains with the project owner. Publicly visible source code does not, by itself, grant permission to copy, modify, or distribute it.
