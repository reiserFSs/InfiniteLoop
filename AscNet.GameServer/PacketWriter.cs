using System.Net.Sockets;
using System.Threading.Channels;

namespace AscNet.GameServer;

internal sealed class PacketWriter
{
    // Account for packet, queue entry and optional completion fence even for tiny payloads.
    private const int PacketOverhead = 256;
    private const int QueueBudget = PacketCodec.MaxFrameLength + PacketOverhead;
    private readonly TcpClient client;
    private readonly Action<Exception?> onClosed;
    private readonly bool encrypted;
    private readonly object gate = new();
    private readonly Channel<(Packet Packet, TaskCompletionSource? Written)> outgoing =
        Channel.CreateBounded<(Packet, TaskCompletionSource?)>(new BoundedChannelOptions(PacketCodec.MaxFrameLength / PacketOverhead)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource closed = new();
    private Task? worker;
    private int queuedBytes;
    private bool completing;

    internal PacketWriter(TcpClient client, Action<Exception?> onClosed, bool encrypted = true)
    {
        this.client = client;
        this.onClosed = onClosed;
        this.encrypted = encrypted;
    }

    internal Task? Enqueue(Packet packet, bool trackCompletion = false)
    {
        TaskCompletionSource? written = trackCompletion ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        lock (gate)
        {
            if (completing)
            {
                written?.TrySetCanceled();
                return written?.Task;
            }
            worker ??= Task.Run(WriteLoopAsync);
            if (packet.Content.Length > QueueBudget - queuedBytes - PacketOverhead
                || !outgoing.Writer.TryWrite((packet, written)))
            {
                written?.TrySetCanceled();
                Close();
            }
            else queuedBytes += packet.Content.Length + PacketOverhead;
        }
        return written?.Task;
    }

    internal Task CompleteAsync()
    {
        lock (gate)
        {
            completing = true;
            outgoing.Writer.TryComplete();
            if (worker is null) client.Close();
            return worker ?? Task.CompletedTask;
        }
    }

    internal void Close()
    {
        lock (gate)
        {
            completing = true;
            outgoing.Writer.TryComplete();
            closed.Cancel();
            client.Close();
        }
    }

    private async Task WriteLoopAsync()
    {
        Exception? failure = null;
        TaskCompletionSource? current = null;
        try
        {
            NetworkStream stream = client.GetStream();
            await foreach (var entry in outgoing.Reader.ReadAllAsync(closed.Token))
            {
                current = entry.Written;
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await stream.WriteAsync(PacketCodec.Encode(entry.Packet, encrypted), timeout.Token);
                lock (gate) queuedBytes -= entry.Packet.Content.Length + PacketOverhead;
                current?.TrySetResult();
                current = null;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            current?.TrySetCanceled();
        }
        finally
        {
            Close();
            while (outgoing.Reader.TryRead(out var entry)) entry.Written?.TrySetCanceled();
            onClosed(failure);
        }
    }
}
