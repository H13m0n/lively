#nullable enable
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lively.Core.Wallpapers
{
    /// <summary>Bounded request/reply IPC for infrequent position capture and restoration.</summary>
    internal static class MpvJsonIpcClient
    {
        private static int nextRequestId;

        internal sealed record Reply(bool Success, JsonElement Data);

        public static async Task<Reply?> TryRequestAsync(string pipeName, object[] command,
            TimeSpan? timeout = null)
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(1));
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(cts.Token);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                var requestId = Interlocked.Increment(ref nextRequestId);
                var request = JsonSerializer.Serialize(new { command, request_id = requestId });
                await writer.WriteLineAsync(request.AsMemory(), cts.Token);
                await writer.FlushAsync(cts.Token);

                while (await reader.ReadLineAsync(cts.Token) is string line)
                {
                    using var response = JsonDocument.Parse(line);
                    var root = response.RootElement;
                    if (!root.TryGetProperty("request_id", out var id) || !id.TryGetInt32(out var value) || value != requestId)
                        continue;

                    var success = root.TryGetProperty("error", out var error) && error.GetString() == "success";
                    return new Reply(success, root.TryGetProperty("data", out var data) ? data.Clone() : default);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                // A missing, stopped or unresponsive player must never block policy polling indefinitely.
            }
            return null;
        }
    }
}
