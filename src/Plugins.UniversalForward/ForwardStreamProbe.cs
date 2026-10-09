using System.Text;

namespace Plugins.UniversalForward;

/// <summary>SSE 有效输出检测。</summary>
internal sealed class ForwardStreamProbe : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly MemoryStream _line = new();
    private readonly StringBuilder _data = new();
    private readonly ForwardResponseAnalysis _analysis = new();
    private string _eventType = "";
    private bool _afterCr;
    private bool _crlf;
    private bool _firstLine = true;
    internal ForwardResponseAnalysis Analysis => _analysis;
    // Keep the LF of the final CRLF even when it arrives in a separate read.
    internal bool Completed => _analysis.StreamEnded && !_analysis.HasError && !(_afterCr && _crlf);

    internal bool Observe(ReadOnlySpan<byte> bytes, bool stopOnOutput = true)
    {
        while (!bytes.IsEmpty)
        {
            if (_afterCr)
            {
                _afterCr = false;
                _crlf = bytes[0] == (byte)'\n';
                if (_crlf) { bytes = bytes[1..]; continue; }
            }
            var end = bytes.IndexOfAny((byte)'\r', (byte)'\n');
            if (_line.Length + (end < 0 ? bytes.Length : end) > 32 * 1024 * 1024)
                throw new InvalidDataException("上游 SSE 行超过 32 MiB");
            if (end < 0) { _line.Write(bytes); break; }
            _line.Write(bytes[..end]);
            _afterCr = bytes[end] == (byte)'\r';
            bytes = bytes[(end + 1)..];
            var line = Utf8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
            _line.SetLength(0);
            if (_firstLine) { line = line.TrimStart('\uFEFF'); _firstLine = false; }
            if (line.Length == 0)
            {
                if (_data.Length != 0)
                    _analysis.ReadEvent(_data.ToString(), _eventType);
                _data.Clear();
                _eventType = "";
                if (stopOnOutput && _analysis.HasOutput && !_analysis.HasError) return true;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (_data.Length != 0) _data.Append('\n');
                _data.Append(line.AsSpan(5).TrimStart(' '));
                if (_data.Length > 32 * 1024 * 1024)
                    throw new InvalidDataException("上游 SSE 事件超过 32 MiB");
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                _eventType = line[6..].Trim();
        }
        return false;
    }

    internal void Complete()
    {
        if (_line.Length != 0 || _data.Length != 0)
            throw new IOException("upstream_stream_incomplete: 上游 SSE 事件未完整结束");
        if (!_analysis.Terminal)
            throw new IOException("missing_terminal: 上游流缺少结束事件");
    }

    public void Dispose() => _line.Dispose();
}
