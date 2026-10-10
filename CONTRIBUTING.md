# Contributing

Thank you for your interest in ClipVoice.

## Local development

1. Install the .NET 8 SDK or run `tools\ensure-dotnet.ps1`.
2. Run `dotnet restore`.
3. Build with `dotnet build ClipVoice.csproj -c Release`.
4. Manually verify changed scenarios on Windows 10 or Windows 11.

## Pull requests

- Keep each pull request focused on one logical change.
- Do not add `bin`, `obj`, `dist`, or `.dotnet` contents.
- Do not include clipboard history, templates, logs, or other user data.
- Describe how the change was verified and any behavior changes.
- For Win32 integration changes, check global hotkeys, clipboard behavior, and clean shutdown.
- For TTS changes, check cancellation, offline behavior, and Piper child-process cleanup.

## Style

Preserve nullable annotations, `CancellationToken` support, and the existing separation between `Views`, `Services`, `Models`, and `Native`.
