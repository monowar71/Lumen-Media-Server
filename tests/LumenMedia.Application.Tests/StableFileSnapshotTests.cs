using FluentAssertions;
using LumenMedia.Infrastructure.Transcoding;

namespace LumenMedia.Application.Tests;

public class StableFileSnapshotTests
{
    [Fact]
    public async Task ReadAsync_returns_exact_bytes_when_file_is_stable()
    {
        var dir = Directory.CreateTempSubdirectory("lumen-stable-");
        try
        {
            var path = Path.Combine(dir.FullName, "segment0.m4s");
            var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
            await File.WriteAllBytesAsync(path, payload);

            var got = await StableFileSnapshot.ReadAsync(path, TimeSpan.FromSeconds(2), CancellationToken.None);

            got.Should().NotBeNull();
            got.Should().Equal(payload);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WaitUntilStableAsync_false_while_file_keeps_growing()
    {
        var dir = Directory.CreateTempSubdirectory("lumen-stable-grow-");
        try
        {
            var path = Path.Combine(dir.FullName, "segment1.m4s");
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            await fs.WriteAsync(new byte[100]);
            await fs.FlushAsync();

            var grow = Task.Run(async () =>
            {
                for (var i = 0; i < 8; i++)
                {
                    await Task.Delay(40);
                    await fs.WriteAsync(new byte[50]);
                    await fs.FlushAsync();
                }
            });

            var ok = await StableFileSnapshot.WaitUntilStableAsync(
                path,
                TimeSpan.FromMilliseconds(250),
                CancellationToken.None,
                stableSamples: 3,
                pollMs: 40);
            await grow;

            ok.Should().BeFalse();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WaitUntilStableAsync_returns_false_on_timeout_for_missing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.m4s");
        var ok = await StableFileSnapshot.WaitUntilStableAsync(path, TimeSpan.FromMilliseconds(120), CancellationToken.None);
        ok.Should().BeFalse();
    }
}
