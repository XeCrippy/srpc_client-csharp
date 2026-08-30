using System.Net.Sockets;
using System.Text;

namespace SRPC.Internal;

internal sealed class XbdmConnection : IDisposable
{
    private const int MaximumLineSize = 64 * 1024;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly List<byte> _buffer = new();
    private int _offset;

    internal bool Connected => _client?.Connected == true && _stream is not null;

    internal void Connect(string host, ushort port, TimeSpan connectTimeout, TimeSpan ioTimeout)
    {
        Close();
        var client = new TcpClient();
        try
        {
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(connectTimeout)) throw new SrpcTimeoutException($"Connection to {host} timed out.");
            task.GetAwaiter().GetResult();
            client.ReceiveTimeout = CheckedMilliseconds(ioTimeout);
            client.SendTimeout = CheckedMilliseconds(ioTimeout);
            _client = client;
            _stream = client.GetStream();
        }
        catch (SrpcException) { client.Dispose(); throw; }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            client.Dispose();
            throw new ConnectionException($"Could not connect to {host}.", ex.InnerException);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
            client.Dispose();
            throw new ConnectionException($"Could not connect to {host}.", ex);
        }
    }

    private static int CheckedMilliseconds(TimeSpan value) => (int)Math.Clamp(value.TotalMilliseconds, 1, int.MaxValue);

    internal void SendAll(ReadOnlySpan<byte> data)
    {
        EnsureConnected();
        try { _stream!.Write(data); }
        catch (IOException ex) { Close(); throw IsTimeout(ex) ? new SrpcTimeoutException("Timed out while sending data to the console.", ex) : new ConnectionException("Failed to send data to the console.", ex); }
        catch (SocketException ex) { Close(); throw new ConnectionException("Failed to send data to the console.", ex); }
    }

    internal string ReadLine()
    {
        EnsureConnected();
        while (true)
        {
            for (var i = _offset; i < _buffer.Count; i++)
            {
                if (_buffer[i] != (byte)'\n') continue;
                var end = i > _offset && _buffer[i - 1] == (byte)'\r' ? i - 1 : i;
                var line = Encoding.UTF8.GetString(_buffer.GetRange(_offset, end - _offset).ToArray());
                _offset = i + 1;
                Compact();
                return line;
            }
            if (_buffer.Count - _offset > MaximumLineSize) throw new ProtocolException("XBDM response line exceeded 64 KiB.");
            ReceiveMore();
        }
    }

    internal byte[] ReadExact(int size)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        EnsureConnected();
        var result = new byte[size];
        var copied = 0;
        while (copied < size)
        {
            var available = _buffer.Count - _offset;
            if (available == 0) { ReceiveMore(); continue; }
            var count = Math.Min(available, size - copied);
            _buffer.CopyTo(_offset, result, copied, count);
            _offset += count;
            copied += count;
            Compact();
        }
        return result;
    }

    private void ReceiveMore()
    {
        var chunk = new byte[8192];
        try
        {
            var count = _stream!.Read(chunk, 0, chunk.Length);
            if (count == 0) { Close(); throw new ConnectionException("Console closed the connection."); }
            for (var i = 0; i < count; i++) _buffer.Add(chunk[i]);
        }
        catch (SrpcException) { throw; }
        catch (IOException ex) { Close(); throw IsTimeout(ex) ? new SrpcTimeoutException("Timed out while waiting for the console.", ex) : new ConnectionException("Failed to receive data from the console.", ex); }
        catch (SocketException ex) { Close(); throw new ConnectionException("Failed to receive data from the console.", ex); }
    }

    private static bool IsTimeout(IOException ex) => ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut };
    private void EnsureConnected() { if (!Connected) throw new ConnectionException("Not connected to a console."); }
    private void Compact()
    {
        if (_offset == _buffer.Count) { _buffer.Clear(); _offset = 0; }
        else if (_offset >= 8192 && _offset >= _buffer.Count / 2) { _buffer.RemoveRange(0, _offset); _offset = 0; }
    }
    internal void Close()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null; _client = null; _buffer.Clear(); _offset = 0;
    }
    public void Dispose() => Close();
}
