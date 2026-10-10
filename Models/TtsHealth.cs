namespace ClipVoice.Models;

public enum TtsProviderState
{
    Unknown,
    Checking,
    Ready,
    Preparing,
    Installed,
    Missing,
    Failed
}

public sealed record TtsProviderSnapshot(
    string Key,
    string Name,
    TtsProviderState State,
    string Detail,
    DateTime? LastCheckedUtc = null);

public sealed record TtsHealthSnapshot(
    TtsProviderSnapshot Edge,
    TtsProviderSnapshot Google,
    TtsProviderSnapshot Piper,
    string ActiveProvider,
    long AppMemoryMb,
    long? PiperMemoryMb,
    long TotalMemoryMb,
    int MemoryLimitMb);
