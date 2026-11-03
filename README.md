# ClipVoice

<img src="Assets/ClipVoice.png" alt="ClipVoice — dictation, clipboard, translation and text-to-speech" width="220" />

ClipVoice is a Windows app for voice dictation, clipboard history, reusable text templates, translation, and text-to-speech.

Speak a phrase and ClipVoice transcribes it with Groq Whisper large-v3, inserts the punctuated text into the selected field, and keeps it on the clipboard. Press **Caps Lock** to start recording and again to submit. You can change the shortcut in settings.

## Download

Download the standalone Windows x64 app from [GitHub Releases](https://github.com/merlinmedeo-ai/ClipVoice/releases/latest). No separate .NET installation is required.

**Current version: 1.1.1.** [Download for Windows x64](https://github.com/merlinmedeo-ai/ClipVoice/releases/download/v1.1.1/ClipVoice-1.1.1-win-x64.zip). Extract the archive and run `ClipVoice.exe` after closing the previous version. Your settings and history are preserved.

### What's new in 1.1.1

- Project page and release now use the current application icon
- Updated feature descriptions, setup instructions, and privacy information
- Application manifest version synchronized with the release

### What's new in 1.1.0

- Online dictation with Groq Whisper large-v3 and automatic punctuation
- Personal API key encrypted with Windows DPAPI; setup instructions under ★
- Caps Lock to start / finish dictation, or a custom shortcut
- Automatic insertion into the original field, with the result retained on the clipboard
- Soft recording chime and a compact microphone level indicator

### What's new in 1.0.1

- Updated application icon and a dedicated system tray icon
- Translation results in a popup near the pointer
- Clipboard history available from the template popup menu
- Improved template popup behavior and clipboard duplicate detection

## Features

- Groq Whisper large-v3 dictation with punctuation and a personal API key
- Caps Lock or a configurable shortcut to start / finish recording
- Soft start chime and a compact panel with stationary bars reacting to microphone volume
- Dictation result inserted into the original field and retained in clipboard history
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
- Internet access for dictation, online translation, online speech, and the initial Piper download
- Personal Groq API key and microphone access for dictation (instructions under ★ in settings)

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

The executable is created at `dist\ClipVoice-1.1.1-win-x64\ClipVoice.exe`. The `dist` directory is excluded from Git; publish binary builds through GitHub Releases.

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

Dictation uses the official Groq API. Microphone audio is sent to Groq only when dictation is used; the temporary local WAV is deleted after the request. The personal API key is encrypted with Windows DPAPI in settings. Groq's free plan has usage limits. Review [Groq data controls](https://console.groq.com/docs/your-data) and [current limits](https://console.groq.com/docs/rate-limits).

## Project structure

- `Views/` — WPF windows and layouts
- `Services/` — clipboard, Windows integration, translation, and speech
- `Models/` — settings and data models
- `Native/` — Win32 declarations
- `Assets/` — application icons
- `tools/` — local SDK setup scripts

## License

No license has been selected yet. Until a `LICENSE` file is added, standard copyright remains with the project owner. Publicly visible source code does not, by itself, grant permission to copy, modify, or distribute it.

## Dictation with Groq

In settings, open **SPEECH & TRANSLATION**, enter your personal Groq API key,
select a language, and save. Click **★** for instructions on obtaining a key
from [Groq API Keys](https://console.groq.com/keys). The key is encrypted with
Windows DPAPI and is accessible only to the current Windows user. Enter it again
if you move your settings to another computer or Windows account.

Place the cursor in a text field and press **Caps Lock**. After the chime, speak
your phrase and press the shortcut again. Recording stops automatically after
60 seconds. ClipVoice sends the WAV recording to Groq Whisper large-v3, inserts
the punctuated result into the original field, and keeps it on the clipboard
and in history. If automatic insertion is unavailable, press **Ctrl+V**.
Keep the destination field unchanged until the operation finishes.

To assign Caps Lock or a custom combination, click the dictation shortcut field,
press your preferred shortcut, and save. Existing shortcuts are preserved when
updating; assign Caps Lock in this field if you want to switch to it.

Dictation requires internet access, a default microphone, and Windows microphone
permission for desktop apps. The temporary WAV is deleted after the request.
Groq's free plan has limits; additional keys within the same organization do not
increase its quota. Manage retention in [Groq Data Controls](https://console.groq.com/settings/data-controls).
Word and punctuation accuracy depend on recording quality. Failed requests are
not retried automatically. End-to-end verification requires a valid key and a microphone recording.
