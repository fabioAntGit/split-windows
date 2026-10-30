using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using WinRT;

namespace Split.Windows;

/// <summary>
/// Short sounds through one low-latency audio graph that mixes them: a sound that starts while another plays joins it
/// instead of cutting it off, so quick moves across the wheel are all heard. The graph runs only while there is something
/// to hear, since an open audio stream keeps the PC from going to sleep.
/// </summary>
public static class SoundOut
{
    /// <summary>The rate of the samples given to <see cref="Play"/>; the graph converts them to the device's.</summary>
    public const int Rate = 48000;

    private const int MostAtOnce = 4;
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(1.5);

    // gate: the voices, taken on the audio thread. power: opening, starting and stopping, never taken on the audio thread,
    // since Stop waits for the audio thread.
    private static readonly object gate = new();
    private static readonly object power = new();
    private static readonly List<(float[] Samples, int At)> voices = [];
    private static DateTime lastHeard;
    private static bool resting;
    private static AudioGraph? graph;
    private static Task? opening;
    private static bool running;

    /// <summary>Whether a device took the graph: null until it has been tried.</summary>
    public static bool? Available { get; private set; }

    /// <summary>Samples handed to the device so far.</summary>
    public static long Mixed { get { lock (gate) return mixed; } }
    private static long mixed;

    public static bool IsRunning { get { lock (power) return running; } }

    /// <summary>Opens the graph ahead of the first sound, so that one is on time too. It stays silent until then.</summary>
    public static Task Prepare() { lock (power) return opening ??= Open(); }

    public static void Play(float[] samples)
    {
        lock (gate)
        {
            if (voices.Count >= MostAtOnce) voices.RemoveAt(0);
            voices.Add((samples, 0));
            lastHeard = DateTime.UtcNow;
        }
        Wake();
    }

    private static void Wake()
    {
        lock (power)
        {
            opening ??= Open();
            if (graph is null || running) return;
            graph.Start();
            running = true;
        }
    }

    private static async Task Open()
    {
        try
        {
            var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.SoundEffects) { QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency });
            if (created.Status != AudioGraphCreationStatus.Success) { Available = false; return; }
            var made = created.Graph;
            var output = await made.CreateDeviceOutputNodeAsync();
            if (output.Status != AudioDeviceNodeCreationStatus.Success) { made.Dispose(); Available = false; return; }
            var format = AudioEncodingProperties.CreatePcm(Rate, 1, 32);
            format.Subtype = "Float";
            var input = made.CreateFrameInputNode(format);
            input.AddOutgoingConnection(output.DeviceOutputNode);
            input.QuantumStarted += Fill;
            // The device went away or changed: the next sound opens a new graph on the current one.
            made.UnrecoverableErrorOccurred += (_, _) =>
            {
                lock (power) { graph = null; opening = null; running = false; }
                made.Dispose();
            };
            lock (power) graph = made;
            Available = true;
        }
        catch (Exception)
        {
            // shortcut: with no audio at the first sound, Split stays silent until it restarts; retry on a device change if users ask.
            Available = false;
            return;
        }
        bool waiting;
        lock (gate) waiting = voices.Count > 0;
        if (waiting) Wake();
    }

    /// <summary>On the audio thread, every few milliseconds while the graph runs.</summary>
    private static void Fill(AudioFrameInputNode node, FrameInputNodeQuantumStartedEventArgs args)
    {
        var count = args.RequiredSamples;
        if (count <= 0) return;
        float[]? mix = null;
        lock (gate)
        {
            if (voices.Count > 0)
            {
                mix = Mix(voices, count);
                mixed += count;
            }
            else if (!resting && DateTime.UtcNow - lastHeard > Linger)
            {
                resting = true;
                Task.Run(Rest);
            }
        }
        if (mix is null) return;
        using var frame = new AudioFrame((uint)(count * sizeof(float)));
        using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
        using (var reference = buffer.CreateReference())
        {
            reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out _);
            Marshal.Copy(mix, 0, data, count);
        }
        node.AddFrame(frame);
    }

    /// <summary>The next <paramref name="count"/> samples of every voice, added up; the voices that end are taken out.</summary>
    public static float[] Mix(List<(float[] Samples, int At)> playing, int count)
    {
        var mix = new float[count];
        for (var v = playing.Count - 1; v >= 0; v--)
        {
            var (samples, at) = playing[v];
            for (var i = 0; i < count && at < samples.Length; i++, at++) mix[i] += samples[at];
            if (at >= samples.Length) playing.RemoveAt(v);
            else playing[v] = (samples, at);
        }
        for (var i = 0; i < count; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
        return mix;
    }

    private static void Rest()
    {
        lock (power)
        {
            lock (gate)
            {
                resting = false;
                if (voices.Count > 0) return;
            }
            if (!running) return;
            graph?.Stop();
            running = false;
        }
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
