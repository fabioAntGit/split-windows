using Split.Windows;

namespace Split.App;

/// <summary>Soft water drops drawn in code: one when a job is done, two lower ones on an error, a faint short one as the pointer moves across the wheel.</summary>
public static class Sounds
{
    public static readonly float[] DoneDrop = Drop(520, 1250, 0.11, 0.35);
    public static readonly float[] ErrorDrops = [.. Drop(330, 600, 0.09, 0.32), .. Drop(260, 470, 0.12, 0.3)];
    public static readonly float[] WheelDrop = Drop(900, 1700, 0.045, 0.12);

    public static void Success() { if (Settings.PlaySounds) SoundOut.Play(DoneDrop); }

    public static void Failure() { if (Settings.PlaySounds) SoundOut.Play(ErrorDrops); }

    public static void Hover() { if (Settings.PlayWheelSounds) SoundOut.Play(WheelDrop); }

    /// <summary>A sine whose pitch rises quickly while it fades, which is what makes a drop sound like a drop.</summary>
    private static float[] Drop(double from, double to, double seconds, double volume)
    {
        const int rate = SoundOut.Rate;
        var count = (int)(seconds * rate);
        var samples = new float[count];
        var phase = 0.0;
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / count;
            phase += 2 * Math.PI * from * Math.Pow(to / from, t) / rate;
            // A 2 ms rise and a 5 ms fall keep the edges from clicking.
            var edges = Math.Min(1, Math.Min(i / (0.002 * rate), (count - i) / (0.005 * rate)));
            samples[i] = (float)(Math.Sin(phase) * Math.Exp(-4.5 * t) * edges * volume);
        }
        return samples;
    }
}
