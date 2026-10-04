using System.Text;

namespace SDRSharp.PlutoSDR.Iio;

/// <summary>
/// Client for the IIOD text protocol (the wire protocol shared by libiio's USB and network
/// backends). Every command is an ASCII line terminated with CRLF; responses are either a bare
/// integer on its own line, or an integer byte-count followed by that many payload bytes plus a
/// trailing newline.
/// </summary>
internal sealed class IiodClient : IDisposable
{
    private const int BufferSize = 1 << 16;

    private readonly IIioTransport _transport;
    private readonly byte[] _rx = new byte[BufferSize];
    private int _rxStart;
    private int _rxEnd;
    private readonly object _sync = new object();
    private bool _disposed;

    public string TransportDescription => _transport.Description;
    public int ChannelMaskWords { get; set; } = 1;

    /// <summary>Protocol statistics, for throughput diagnostics.</summary>
    public long TotalChunks { get; private set; }
    public int LastChunkCount { get; private set; }
    public int LastChunkMax { get; private set; }
    public long TransportReadCalls { get; private set; }

    public IiodClient(IIioTransport transport)
    {
        _transport = transport;
    }

    // ---------------------------------------------------------------- buffered byte stream

    private void Compact()
    {
        if (_rxStart == 0) return;
        int n = _rxEnd - _rxStart;
        if (n > 0) Buffer.BlockCopy(_rx, _rxStart, _rx, 0, n);
        _rxStart = 0;
        _rxEnd = n;
    }

    private bool FillOnce()
    {
        if (_rxEnd == _rx.Length) Compact();
        if (_rxEnd == _rx.Length)
            throw new IioException("IIOD receive buffer overflow while looking for a line terminator.");
        TransportReadCalls++;
        int got = _transport.Read(_rx, _rxEnd, _rx.Length - _rxEnd);
        if (got <= 0) return false;
        _rxEnd += got;
        return true;
    }

    private int Buffered => _rxEnd - _rxStart;

    private void ReadExact(byte[] dst, int offset, int count)
    {
        int done = 0;
        while (done < count)
        {
            if (Buffered > 0)
            {
                int take = Math.Min(Buffered, count - done);
                Buffer.BlockCopy(_rx, _rxStart, dst, offset + done, take);
                _rxStart += take;
                done += take;
                continue;
            }

            // Nothing buffered: for a large read, go straight to the transport.
            if (count - done >= BufferSize)
            {
                TransportReadCalls++;
                int got = _transport.Read(dst, offset + done, count - done);
                if (got <= 0) throw new IioException("IIOD stream closed while reading payload.");
                done += got;
            }
            else if (!FillOnce())
            {
                throw new IioException("IIOD stream closed while reading payload.");
            }
        }
    }

    private string ReadLine()
    {
        while (true)
        {
            for (int i = _rxStart; i < _rxEnd; i++)
            {
                if (_rx[i] != (byte)'\n') continue;
                string line = Encoding.ASCII.GetString(_rx, _rxStart, i - _rxStart);
                _rxStart = i + 1;
                return line;
            }
            if (!FillOnce())
            {
                if (Buffered > 0)
                {
                    string tail = Encoding.ASCII.GetString(_rx, _rxStart, Buffered);
                    _rxStart = _rxEnd;
                    return tail;
                }
                throw new IioException("IIOD stream closed while reading a response line.");
            }
        }
    }

    /// <summary>Reads a response line and parses the first integer on it (may be negative).</summary>
    private int ReadInt()
    {
        string lastLine = null;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            string line = ReadLine().Trim();
            if (line.Length == 0) continue;
            lastLine = line;
            int end = 0;
            if (line[0] == '-' || line[0] == '+') end = 1;
            int digits = end;
            while (digits < line.Length && char.IsDigit(line[digits])) digits++;
            if (digits == end) continue;
            if (long.TryParse(line.Substring(0, digits), out long v)) return (int)v;
        }
        throw new IioException(
            $"IIOD returned an unparsable response (last line {Describe(lastLine)}; " +
            $"next bytes {Peek()}).");
    }

    private static string Describe(string line)
        => line == null ? "<none>" : "\"" + (line.Length > 80 ? line.Substring(0, 80) + "..." : line) + "\"";

    /// <summary>Non-destructive hex/ASCII dump of what is buffered right now, for diagnostics.</summary>
    private string Peek()
    {
        try { if (Buffered < 32) FillOnce(); } catch { }
        int n = Math.Min(Buffered, 48);
        if (n <= 0) return "<empty>";
        var hex = new StringBuilder();
        var ascii = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            byte b = _rx[_rxStart + i];
            hex.Append(b.ToString("x2")).Append(' ');
            ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
        }
        return $"[{hex.ToString().Trim()}] \"{ascii}\"";
    }

    private void Send(string command)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(command);
        _transport.Write(bytes, 0, bytes.Length);
        _transport.Flush();
    }

    private int Exec(string command)
    {
        Send(command);
        return ReadInt();
    }

    private string ReadPayloadString(int length)
    {
        var buf = new byte[length + 1];
        ReadExact(buf, 0, length + 1);
        return Encoding.ASCII.GetString(buf, 0, length);
    }

    // ---------------------------------------------------------------- protocol commands

    public string GetVersion()
    {
        lock (_sync)
        {
            Send("VERSION\r\n");
            return ReadLine().Trim();
        }
    }

    public string GetXml()
    {
        lock (_sync)
        {
            Send("PRINT\r\n");
            int len = ReadInt();
            if (len < 0) throw new IioException($"IIOD PRINT (context XML) failed with code {len}.");
            return ReadPayloadString(len);
        }
    }

    public string ReadDeviceAttr(string device, string attr)
    {
        lock (_sync)
        {
            Send($"READ {device} {attr}\r\n");
            int len = ReadInt();
            if (len < 0) throw new IioException($"READ {device} {attr} failed with code {len}.");
            return ReadPayloadString(len);
        }
    }

    public string ReadChannelAttr(string device, bool output, string channel, string attr)
    {
        lock (_sync)
        {
            Send($"READ {device} {(output ? "OUTPUT" : "INPUT")} {channel} {attr}\r\n");
            int len = ReadInt();
            if (len < 0) throw new IioException($"READ {device} {channel} {attr} failed with code {len}.");
            return ReadPayloadString(len);
        }
    }

    public int WriteDeviceAttr(string device, string attr, string value)
    {
        lock (_sync)
        {
            byte[] payload = Encoding.ASCII.GetBytes(value);
            Send($"WRITE {device} {attr} {payload.Length}\r\n");
            _transport.Write(payload, 0, payload.Length);
            _transport.Flush();
            return ReadInt();
        }
    }

    public int WriteChannelAttr(string device, bool output, string channel, string attr, string value)
    {
        lock (_sync)
        {
            byte[] payload = Encoding.ASCII.GetBytes(value);
            Send($"WRITE {device} {(output ? "OUTPUT" : "INPUT")} {channel} {attr} {payload.Length}\r\n");
            _transport.Write(payload, 0, payload.Length);
            _transport.Flush();
            return ReadInt();
        }
    }

    public int Open(string device, long sampleCount, uint[] mask, bool cyclic = false)
    {
        lock (_sync)
        {
            var sb = new StringBuilder();
            sb.Append("OPEN ").Append(device).Append(' ').Append(sampleCount).Append(' ');
            for (int i = mask.Length; i > 0; i--) sb.Append(mask[i - 1].ToString("x8"));
            if (cyclic) sb.Append(" CYCLIC");
            sb.Append("\r\n");
            return Exec(sb.ToString());
        }
    }

    public int CloseDevice(string device)
    {
        lock (_sync)
        {
            return Exec($"CLOSE {device}\r\n");
        }
    }

    public int SetTimeout(uint milliseconds)
    {
        lock (_sync)
        {
            return Exec($"TIMEOUT {milliseconds}\r\n");
        }
    }

    public int SetBuffersCount(string device, uint count)
    {
        lock (_sync)
        {
            return Exec($"SET {device} BUFFERS_COUNT {count}\r\n");
        }
    }

    /// <summary>
    /// Issues a READBUF and blocks until <paramref name="length"/> bytes are collected (or the
    /// device reports that the block ended). Returns the number of bytes actually read.
    /// Mirrors libiio's iiod_client_read_unlocked: the channel mask always accompanies the first
    /// data chunk (skipping it shifts every following read out of frame), and chunk sizes are
    /// taken verbatim.
    /// </summary>
    public int ReadBuf(string device, byte[] destination, int length, uint[] maskOut)
    {
        lock (_sync)
        {
            Send($"READBUF {device} {length}\r\n");
            int total = 0;
            bool maskRead = false;
            LastChunkCount = 0;
            LastChunkMax = 0;
            var overflow = new byte[4096];
            var maskScratch = new byte[Math.Max(1, ChannelMaskWords) * 8 + 1];

            while (total < length)
            {
                int chunk = ReadInt();
                if (chunk < 0) throw new IioException($"READBUF {device} failed with code {chunk}.");
                if (chunk == 0) break;
                LastChunkCount++;
                TotalChunks++;
                if (chunk > LastChunkMax) LastChunkMax = chunk;

                if (!maskRead)
                {
                    ReadExact(maskScratch, 0, maskScratch.Length);
                    if (maskOut != null)
                    {
                        for (int w = 0; w < maskOut.Length; w++)
                        {
                            string hex = Encoding.ASCII.GetString(
                                maskScratch, (maskOut.Length - 1 - w) * 8, 8);
                            try { maskOut[w] = Convert.ToUInt32(hex, 16); } catch { }
                        }
                    }
                    maskRead = true;
                }

                if (chunk <= length - total)
                {
                    ReadExact(destination, total, chunk);
                    total += chunk;
                }
                else
                {
                    // The device announced more than we asked for: take what fits and drain the
                    // rest so the stream stays framed.
                    int fits = length - total;
                    ReadExact(destination, total, fits);
                    total += fits;
                    int remaining = chunk - fits;
                    while (remaining > 0)
                    {
                        int take = Math.Min(remaining, overflow.Length);
                        ReadExact(overflow, 0, take);
                        remaining -= take;
                    }
                }
            }
            return total;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transport.Dispose(); } catch { }
    }
}

