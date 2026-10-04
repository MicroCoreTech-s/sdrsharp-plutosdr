namespace SDRSharp.PlutoSDR.Iio;

/// <summary>Byte-stream transport carrying the IIOD protocol.</summary>
internal interface IIioTransport : IDisposable
{
    string Description { get; }
    /// <summary>Reads up to <paramref name="count"/> bytes. Returns the number read, 0 on peer close.</summary>
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
    void Flush();
}

internal sealed class IioException : Exception
{
    public IioException(string message, Exception inner = null) : base(message, inner) { }
}
