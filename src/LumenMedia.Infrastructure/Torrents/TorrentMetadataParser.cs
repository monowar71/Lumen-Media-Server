using System.Security.Cryptography;
using System.Text;
using LumenMedia.Application.Abstractions;

namespace LumenMedia.Infrastructure.Torrents;

/// <summary>
/// Minimal bencode reader for <c>.torrent</c> files — extracts infohash and file list.
/// TorrServer file ids are 1-based in the same order as the info.files / single-file layout.
/// </summary>
public sealed class TorrentMetadataParser : ITorrentMetadataParser
{
    public TorrentMetadata ParseFile(string torrentPath)
    {
        using var fs = File.OpenRead(torrentPath);
        return Parse(fs);
    }

    public TorrentMetadata Parse(Stream torrentStream)
    {
        using var ms = new MemoryStream();
        torrentStream.CopyTo(ms);
        var data = ms.ToArray();
        var reader = new BencodeReader(data);
        var root = reader.ReadValue();
        if (root is not Dictionary<string, object> dict)
            throw new InvalidDataException("Torrent root must be a dictionary.");

        if (!dict.TryGetValue("info", out var infoObj) || infoObj is not Dictionary<string, object> info)
            throw new InvalidDataException("Torrent missing info dictionary.");

        var infoStart = reader.InfoStart;
        var infoEnd = reader.InfoEnd;
        if (infoStart < 0 || infoEnd <= infoStart)
            throw new InvalidDataException("Could not locate raw info dictionary for hashing.");

        var infoHash = Convert.ToHexString(SHA1.HashData(data.AsSpan(infoStart, infoEnd - infoStart)))
            .ToLowerInvariant();

        var name = info.TryGetValue("name", out var nameObj) && nameObj is string n
            ? n
            : "unknown";

        var files = new List<TorrentFileEntry>();
        if (info.TryGetValue("files", out var filesObj) && filesObj is List<object> multi)
        {
            var index = 1;
            foreach (var entry in multi)
            {
                if (entry is not Dictionary<string, object> fe)
                    continue;
                var length = ReadLong(fe, "length");
                var pathParts = fe.TryGetValue("path", out var pathObj) && pathObj is List<object> parts
                    ? parts.OfType<string>().ToArray()
                    : [];
                var rel = string.Join('/', pathParts);
                files.Add(new TorrentFileEntry(index++, rel, length));
            }
        }
        else
        {
            var length = ReadLong(info, "length");
            files.Add(new TorrentFileEntry(1, name, length));
        }

        return new TorrentMetadata(infoHash, name, files);
    }

    private static long ReadLong(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var v))
            return 0;
        return v switch
        {
            long l => l,
            int i => i,
            string s when long.TryParse(s, out var p) => p,
            _ => 0,
        };
    }

    /// <summary>Tracks byte ranges of the info dict while parsing.</summary>
    private sealed class BencodeReader(byte[] data)
    {
        private int _pos;
        public int InfoStart { get; private set; } = -1;
        public int InfoEnd { get; private set; } = -1;

        public object ReadValue()
        {
            if (_pos >= data.Length)
                throw new InvalidDataException("Unexpected end of bencode.");

            var b = data[_pos];
            if (b == (byte)'i')
                return ReadInt();
            if (b == (byte)'l')
                return ReadList();
            if (b == (byte)'d')
                return ReadDict();
            if (b is >= (byte)'0' and <= (byte)'9')
                return ReadString();
            throw new InvalidDataException($"Unexpected bencode token at {_pos}.");
        }

        private long ReadInt()
        {
            _pos++; // i
            var start = _pos;
            while (_pos < data.Length && data[_pos] != (byte)'e')
                _pos++;
            if (_pos >= data.Length)
                throw new InvalidDataException("Unterminated integer.");
            var s = Encoding.ASCII.GetString(data, start, _pos - start);
            _pos++; // e
            return long.Parse(s);
        }

        private string ReadString()
        {
            var start = _pos;
            while (_pos < data.Length && data[_pos] != (byte)':')
                _pos++;
            if (_pos >= data.Length)
                throw new InvalidDataException("Unterminated string length.");
            var len = int.Parse(Encoding.ASCII.GetString(data, start, _pos - start));
            _pos++; // :
            if (_pos + len > data.Length)
                throw new InvalidDataException("String overruns buffer.");
            var s = Encoding.UTF8.GetString(data, _pos, len);
            _pos += len;
            return s;
        }

        private List<object> ReadList()
        {
            _pos++; // l
            var list = new List<object>();
            while (_pos < data.Length && data[_pos] != (byte)'e')
                list.Add(ReadValue());
            if (_pos >= data.Length)
                throw new InvalidDataException("Unterminated list.");
            _pos++; // e
            return list;
        }

        private Dictionary<string, object> ReadDict()
        {
            _pos++; // d
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            while (_pos < data.Length && data[_pos] != (byte)'e')
            {
                var key = ReadString();
                if (key == "info" && InfoStart < 0)
                    InfoStart = _pos;
                var value = ReadValue();
                if (key == "info" && InfoEnd < 0)
                    InfoEnd = _pos;
                dict[key] = value;
            }

            if (_pos >= data.Length)
                throw new InvalidDataException("Unterminated dictionary.");
            _pos++; // e
            return dict;
        }
    }
}
