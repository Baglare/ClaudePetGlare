using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Petglare;

internal sealed class HookPipe
{
    internal static string Channel { get; } = GetChannel();
    private const int MaxFrame = 2048;
    private const int MaxHookJson = 8 * 1024 * 1024;

    private static string GetChannel()
    {
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return $"Petglare-v1-{user.User?.Value}-{process.SessionId}";
    }

    internal async Task<bool> ForwardAsync(Stream input)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Channel, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // Connect before reading stdin so a closed pet never needs to parse the payload.
            await pipe.ConnectAsync(120).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(450));
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaxHookJson) return false;
                buffer.Write(chunk, 0, count);
            }

            using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            if (HookNotice.FromClaude(json.RootElement) is not { } notice) return false;
            var body = JsonSerializer.SerializeToUtf8Bytes(notice);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
            await pipe.WriteAsync(length, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(body, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsTransportFailure(ex) || ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal async Task ListenAsync(Action<HookNotice> onNotice, CancellationToken stop)
    {
        var length = new byte[4];
        while (!stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(Channel, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(650);
                await pipe.ReadExactlyAsync(length, timeout.Token).ConfigureAwait(false);
                int count = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (count is <= 0 or > MaxFrame) continue;
                var body = new byte[count];
                await pipe.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                if (JsonSerializer.Deserialize<HookNotice>(body) is { IsValid: true } notice)
                    onNotice(notice);
            }
            catch (Exception ex) when (IsTransportFailure(ex) || ex is JsonException)
            {
                // A truncated or malformed notice has no effect on the current turn.
            }
        }
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException;
}
