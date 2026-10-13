using Lively.Core.Wallpapers;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Lively.Tests;

public sealed class MpvJsonIpcClientTests
{
    [Fact]
    public async Task MatchesReplyToRequestAndIgnoresUnrelatedEvents()
    {
        var pipeName = "lively-ipc-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var response = MpvJsonIpcClient.TryRequestAsync(pipeName, ["get_property", "percent-pos"]);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
        Assert.Equal("percent-pos", request.RootElement.GetProperty("command")[1].GetString());
        var id = request.RootElement.GetProperty("request_id").GetInt32();
        await writer.WriteLineAsync("{\"event\":\"playback-restart\"}");
        await writer.WriteLineAsync("{\"request_id\":-1,\"error\":\"success\",\"data\":99}");
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { request_id = id, error = "success", data = 42.5 }));

        var reply = await response;
        Assert.True(reply?.Success);
        Assert.Equal(42.5, reply!.Data.GetDouble());
    }

    [Fact]
    public async Task MissingPipeHasABoundedTimeout()
    {
        var reply = await MpvJsonIpcClient.TryRequestAsync("lively-missing-" + Guid.NewGuid().ToString("N"),
            ["get_property", "percent-pos"], TimeSpan.FromMilliseconds(100));
        Assert.Null(reply);
    }

    [Fact]
    public async Task ConnectedPlayerThatNeverRepliesHasABoundedTimeout()
    {
        var pipeName = "lively-ipc-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var response = MpvJsonIpcClient.TryRequestAsync(pipeName, ["get_property", "percent-pos"], TimeSpan.FromMilliseconds(300));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.WaitForConnectionAsync(timeout.Token);
        Assert.Null(await response.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
