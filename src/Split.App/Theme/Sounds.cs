using System.Media;

namespace Split.App;

/// <summary>Soft water drops drawn in code: one when a job is done, two lower ones on an error, a faint short one as the pointer moves across the wheel.</summary>
public static class Sounds
{
    private const int Rate = 44100;

    public static readonly byte[] SuccessWave = Wave(Drop(520, 1250, 0.11, 0.35));
    public static readonly byte[] FailureWave = Wave([.. Drop(330, 600, 0.09, 0.32), .. Drop(260, 470, 0.12, 0.3)]);
    public static readonly byte[] HoverWave = Wave(Drop(900, 1700, 0.045, 0.12));

    private static readonly Lazy<SoundPlayer> success = new(() => Player(SuccessWave));
    private static readonly Lazy<SoundPlayer> failure = new(() => Player(FailureWave));
    private static readonly Lazy<SoundPlayer> hover = new(() => Player(HoverWave));

    public static void Success() { if (Settings.PlaySounds) success.Value.Play(); }

    public static void Failure() { if (Settings.PlaySounds) failure.Value.Play(); }

    public static void Hover() { if (Settings.PlayWheelSounds) hover.Value.Play(); }

    /// <summary>A sine whose pitch rises quickly while it fades, which is what makes a drop sound like a drop.</summary>
    private static short[] Drop(double from, double to, double seconds, double volume)
    {
        var count = (int)(seconds * Rate);
        var samples = new short[count];
        var phase = 0.0;
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / count;
            phase += 2 * Math.PI * from * Math.Pow(to / from, t) / Rate;
            // A 2 ms rise and a 5 ms fall keep the edges from clicking.
            var edges = Math.Min(1, Math.Min(i / (0.002 * Rate), (count - i) / (0.005 * Rate)));
            samples[i] = (short)(Math.Sin(phase) * Math.Exp(-4.5 * t) * edges * volume * short.MaxValue);
        }
        return samples;
    }

    private static byte[] Wave(short[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(Rate); writer.Write(Rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write(sample);
        return stream.ToArray();
    }

    private static SoundPlayer Player(byte[] wave)
    {
        var player = new SoundPlayer(new MemoryStream(wave));
        player.Load();
        return player;
    }
}
