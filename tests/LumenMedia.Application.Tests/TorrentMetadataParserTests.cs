using System.Text;
using FluentAssertions;
using LumenMedia.Infrastructure.Torrents;

namespace LumenMedia.Application.Tests;

public class TorrentMetadataParserTests
{
    [Fact]
    public void Parse_single_file_torrent_extracts_infohash_and_file()
    {
        var pieces = new byte[20];
        var info = new List<byte>();
        Append(info, "d6:lengthi1024e4:name9:movie.mkv12:piece lengthi16384e6:pieces20:");
        info.AddRange(pieces);
        Append(info, "e");

        var root = new List<byte>();
        Append(root, "d4:info");
        root.AddRange(info);
        Append(root, "e");

        var parser = new TorrentMetadataParser();
        using var ms = new MemoryStream(root.ToArray());
        var meta = parser.Parse(ms);

        meta.InfoHash.Should().HaveLength(40);
        meta.Name.Should().Be("movie.mkv");
        meta.Files.Should().ContainSingle();
        meta.Files[0].Index.Should().Be(1);
        meta.Files[0].Path.Should().Be("movie.mkv");
        meta.Files[0].Length.Should().Be(1024);
    }

    [Fact]
    public void Parse_multi_file_torrent_uses_1_based_indexes()
    {
        var pieces = new byte[20];
        var info = new List<byte>();
        Append(info, "d4:name4:Show5:filesl");
        Append(info, "d6:lengthi100e4:pathl10:S01E01.mkvee");
        Append(info, "d6:lengthi200e4:pathl10:S01E02.mkvee");
        Append(info, "e12:piece lengthi16384e6:pieces20:");
        info.AddRange(pieces);
        Append(info, "e");

        var root = new List<byte>();
        Append(root, "d4:info");
        root.AddRange(info);
        Append(root, "e");

        var parser = new TorrentMetadataParser();
        using var ms = new MemoryStream(root.ToArray());
        var meta = parser.Parse(ms);

        meta.Files.Should().HaveCount(2);
        meta.Files[0].Index.Should().Be(1);
        meta.Files[0].Path.Should().Be("S01E01.mkv");
        meta.Files[1].Index.Should().Be(2);
        meta.Files[1].Path.Should().Be("S01E02.mkv");
        meta.Files[1].Length.Should().Be(200);
    }

    private static void Append(List<byte> dest, string ascii) =>
        dest.AddRange(Encoding.ASCII.GetBytes(ascii));
}
