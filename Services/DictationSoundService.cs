using System.Media;
using System.Text;

namespace ClipVoice.Services;

internal static class DictationSoundService
{
    private static readonly byte[] StartSound = CreateStartSound();

    // A quiet, rounded ascending chime with smooth edges instead of a system alert.
    public static Task PlayStartAsync() => Task.Run(() =>
    {
        try
        {
            using var stream = new MemoryStream(StartSound, writable: false);
            using var player = new SoundPlayer(stream);
            player.PlaySync();
        }
        catch { /* Sound output must never prevent dictation. */ }
    });

    private static byte[] CreateStartSound()
    {
        const int rate = 22050;
        const double duration = 0.24;
        var count = (int)(rate * duration);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + count * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(count * 2);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / rate;
            var sample = Tone(t, 0, 0.14, 523.25) + Tone(t, 0.09, 0.15, 659.25);
            writer.Write((short)(sample * 0.11 * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static double Tone(double time, double start, double length, double frequency)
    {
        var t = time - start;
        if (t < 0 || t >= length) return 0;
        var envelope = Math.Pow(Math.Sin(Math.PI * t / length), 2);
        return Math.Sin(2 * Math.PI * frequency * t) * envelope;
    }
}
