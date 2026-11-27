using System.Security.Cryptography;
using System.Text.Json;
using PhoneRemote.Core;
using PhoneRemote.Files;
using Xunit;

namespace PhoneRemote.Tests;

public class FileTests
{
    [Theory]
    [InlineData("rapport.pdf", "rapport.pdf")]
    [InlineData("..\\..\\windows\\system32\\x.dll", "x.dll")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("  photo: été?.jpg ", "photo été.jpg")]
    [InlineData("", "fichier")]
    [InlineData("...", "fichier")]
    public void Names_are_made_safe(string raw, string expected) => Assert.Equal(expected, PathSanitizer.SafeName(raw));

    [Fact]
    public void Long_names_keep_their_extension()
    {
        var s = PathSanitizer.SafeName(new string('a', 300) + ".jpeg");
        Assert.EndsWith(".jpeg", s);
        Assert.True(s.Length <= 150);
    }

    [Fact]
    public void Risky_extensions_are_flagged()
    {
        Assert.True(PathSanitizer.IsRisky("setup.exe"));
        Assert.True(PathSanitizer.IsRisky("script.PS1"));
        Assert.False(PathSanitizer.IsRisky("notes.txt"));
    }

    [Fact]
    public void Unique_path_numbers_duplicates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vision-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(Path.Combine(dir, "a.txt"), PathSanitizer.UniquePath(dir, "a.txt"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
            Assert.Equal(Path.Combine(dir, "a (2).txt"), PathSanitizer.UniquePath(dir, "a.txt"));
            File.WriteAllText(Path.Combine(dir, "a (2).txt.part"), "x"); // a transfer in progress counts too
            Assert.Equal(Path.Combine(dir, "a (3).txt"), PathSanitizer.UniquePath(dir, "a.txt"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Window_and_ack_rules()
    {
        Assert.Equal(0u, FileChunker.ChunkCount(0));
        Assert.Equal(1u, FileChunker.ChunkCount(1));
        Assert.Equal(2u, FileChunker.ChunkCount(FileChunker.ChunkSize + 1));
        Assert.True(FileChunker.CanSend(0, 0));
        Assert.False(FileChunker.CanSend((uint)FileChunker.Window, 0));
        Assert.True(FileChunker.CanSend((uint)FileChunker.Window, 1));
        Assert.True(FileChunker.ShouldAck(7, 100));
        Assert.False(FileChunker.ShouldAck(8, 100));
        Assert.True(FileChunker.ShouldAck(99, 100));
        Assert.Equal("3,5 Mo", FileChunker.Human(3_670_016).Replace('.', ','));
    }

    [Fact]
    public async Task A_file_goes_from_sender_to_receiver_through_the_framing_and_comes_out_identical()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vision-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "source.bin");
            var data = new byte[FileChunker.ChunkSize * 70 + 1234]; // several windows, a ragged tail
            new Random(7).NextBytes(data);
            File.WriteAllBytes(src, data);
            var receiver = new FileReceiver(Path.Combine(dir, "in"), 1L << 30);
            FileEvent? done = null;
            receiver.Event += e => { if (e.Kind == "done") done = e; };
            FileSender? sender = null;
            int acks = 0;
            Task SendText(string json)
            {
                using var doc = JsonDocument.Parse(json);
                var m = doc.RootElement;
                uint id = (uint)Json.Num(m, "id");
                switch (Json.Str(m, "t"))
                {
                    case "fopen":
                        Assert.Null(receiver.Open(id, Json.Str(m, "name"), (long)Json.Num(m, "size")));
                        sender!.Ok();
                        break;
                    case "fdone":
                        var path = receiver.Done(id, Json.Str(m, "sha"));
                        sender!.Done(path != null);
                        break;
                }
                return Task.CompletedTask;
            }
            Task SendChunk(byte[] msg)
            {
                Assert.True(Protocol.TryParseFileChunk(msg, out var id, out var seq, out var payload));
                long ack = receiver.Chunk(id, seq, payload);
                Assert.NotEqual(-1, ack);
                if (ack > 0) { acks++; sender!.Ack((uint)ack); }
                return Task.CompletedTask;
            }
            sender = new FileSender(42, src, SendText, SendChunk);
            Assert.Null(await sender.RunAsync(CancellationToken.None));
            Assert.NotNull(done);
            Assert.Equal(data.Length, done!.Size);
            Assert.Equal("source.bin", done.Name);
            Assert.Equal(SHA256.HashData(data), SHA256.HashData(File.ReadAllBytes(done.Path!)));
            Assert.True(acks >= 8);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "in"), "*.part"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_wrong_hash_or_missing_chunk_leaves_no_file_behind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vision-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new FileReceiver(dir, 1L << 30);
            Assert.Null(r.Open(1, "x.bin", 100));
            Assert.Equal(-1, r.Chunk(1, 5, new byte[10])); // out of order
            Assert.Null(r.Open(2, "y.bin", 10));
            Assert.Equal(1, r.Chunk(2, 0, new byte[10]));
            Assert.Null(r.Done(2, new string('0', 64)));
            Assert.Empty(Directory.GetFiles(dir));
            Assert.Equal("fichier trop gros (max 1 Ko)", new FileReceiver(dir, 1024).Open(3, "big", 4096));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
