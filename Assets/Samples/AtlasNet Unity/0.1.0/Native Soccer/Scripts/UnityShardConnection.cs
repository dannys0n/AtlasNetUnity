using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// The socket reader owns framing only. Unity lifecycle code drains messages on the main thread.
internal sealed class UnityShardConnection : IDisposable
{
    private const int MaxFrame = 1024 * 1024;
    private const int MaxQueuedFrames = 128;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly Socket socket;
    private readonly NetworkStream stream;
    private readonly Thread reader;
    private readonly ConcurrentQueue<JObject> inbox = new();
    private Exception failure;
    private int waiting;
    private int disposed;

    public UnityShardConnection(string path)
    {
        socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            stream = new NetworkStream(socket, ownsSocket: true) { WriteTimeout = 750 };
            reader = new Thread(ReadFrames) { IsBackground = true, Name = "AtlasNet shard IPC" };
            reader.Start();
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Send(JObject message)
    {
        byte[] bytes = Utf8.GetBytes(message.ToString(Formatting.None) + "\n");
        if (bytes.Length > MaxFrame) throw new InvalidDataException("AtlasNet shard frame exceeds 1 MiB.");
        stream.Write(bytes, 0, bytes.Length);
    }

    public bool TryReceive(out JObject message)
    {
        var error = Volatile.Read(ref failure);
        if (error != null) throw new IOException("AtlasNet shard IPC failed.", error);
        if (!inbox.TryDequeue(out message)) return false;
        Interlocked.Decrement(ref waiting);
        return true;
    }

    private void ReadFrames()
    {
        try
        {
            var buffer = new byte[8192];
            using var frame = new MemoryStream();
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                int start = 0;
                while (start < count)
                {
                    int end = Array.IndexOf(buffer, (byte)'\n', start, count - start);
                    int length = (end < 0 ? count : end) - start;
                    if (frame.Length + length >= MaxFrame)
                        throw new InvalidDataException("AtlasNet shard frame exceeds 1 MiB.");
                    frame.Write(buffer, start, length);
                    if (end < 0) break;
                    var message = JObject.Parse(Utf8.GetString(frame.GetBuffer(), 0, (int)frame.Length));
                    if (Interlocked.Increment(ref waiting) > MaxQueuedFrames)
                        throw new InvalidDataException("AtlasNet shard IPC inbox is full.");
                    inbox.Enqueue(message);
                    frame.SetLength(0);
                    start = end + 1;
                }
            }

            throw new EndOfStreamException("AtlasNet worker closed the shard socket.");
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref disposed) == 0) Volatile.Write(ref failure, error);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stream.Dispose();
    }
}
