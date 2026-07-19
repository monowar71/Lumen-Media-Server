using LumenMedia.Domain.Enums;

namespace LumenMedia.Domain.Media;

public class MediaStream
{
    private MediaStream() { }

    public MediaStream(StreamKind kind, int streamIndex)
    {
        Id = Guid.CreateVersion7();
        Kind = kind;
        StreamIndex = streamIndex;
    }

    public Guid Id { get; private set; }
    public Guid MediaSourceId { get; internal set; }
    public StreamKind Kind { get; private set; }
    public int StreamIndex { get; private set; }
    public string? Codec { get; set; }
    public string? Profile { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsExternal { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FrameRate { get; set; }
    public int? BitrateKbps { get; set; }
    public string? Hdr { get; set; }
    public int? Channels { get; set; }
    public int? SampleRate { get; set; }
    public string? SubtitleFormat { get; set; }
    public string? ExternalPath { get; set; }
}
