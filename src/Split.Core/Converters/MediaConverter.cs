namespace Split;

/// <summary>Audio, video and subtitles, through ffmpeg.</summary>
public sealed class MediaConverter : IConversionFamily
{
    public static readonly Format[] AudioTargets = [Format.Mp3, Format.M4a, Format.Wav, Format.Flac, Format.Ogg, Format.Opus, Format.Aiff, Format.Wma];
    public static readonly Format[] VideoTargets = [Format.Mp4, Format.Mov, Format.Mkv, Format.Webm, Format.Avi, Format.Wmv, Format.Gif];

    public IReadOnlyList<Format> Targets(Format source) => source.Group() switch
    {
        FormatGroup.Audio => AudioTargets,
        FormatGroup.Video => [.. VideoTargets, .. AudioTargets],
        FormatGroup.Subtitle => [Format.Srt, Format.Vtt],
        _ => [],
    };

    public IReadOnlyList<string> Convert(string path, Format source, Format target) => throw Pending.Error();
}
