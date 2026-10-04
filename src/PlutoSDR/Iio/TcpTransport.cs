using System.Net.Sockets;

namespace SDRSharp.PlutoSDR.Iio;

/// <summary>
/// IIOD over TCP. The Pluto runs the very same IIOD daemon on port 30431, reachable over its
/// USB-Ethernet (RNDIS) interface or a real network link, so this is a dependency-free fallback
/// when the USB stack cannot be used.
/// </summary>
internal sealed class TcpTransport : IIioTransport
{
    public const int DefaultPort = 30431;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly string _description;

    public string Description => _description;

    public TcpTransport(string host, int port = DefaultPort, int timeoutMs = 5000)
    {
        _client = new TcpClient();
        _client.NoDelay = true;
        var ar = _client.BeginConnect(host, port, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
        {
            _client.Close();
            throw new IioException($"Timed out connecting to the IIOD service at {host}:{port}.");
        }
        _client.EndConnect(ar);
        _client.ReceiveTimeout = timeoutMs;
        _client.SendTimeout = timeoutMs;
        _stream = _client.GetStream();
        _description = $"TCP {host}:{port}";
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _stream.Read(buffer, offset, count);
        }
        catch (IOException ex)
        {
            // A receive timeout surfaces as IOException; model it as "nothing available".
            if (ex.InnerException is SocketException) return 0;
            throw new IioException("IIOD network read failed: " + ex.Message, ex);
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            _stream.Write(buffer, offset, count);
        }
        catch (Exception ex)
        {
            throw new IioException("IIOD network write failed: " + ex.Message, ex);
        }
    }

    public void Flush()
    {
        try { _stream.Flush(); } catch { }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
    }
}
