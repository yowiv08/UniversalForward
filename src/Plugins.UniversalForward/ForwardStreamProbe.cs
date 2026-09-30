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
    private bool _firstLine = true;

    internal bool Observe(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (_afterCr)
            {
                _afterCr = false;
                if (bytes[0] == (byte)'\n') { bytes = bytes[1..]; continue; }
            }
            var end = bytes.IndexOfAny((byte)'\r', (byte)'\n');
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
                if (_analysis.HasOutput && !_analysis.HasError) return true;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (_data.Length != 0) _data.Append('\n');
                _data.Append(line.AsSpan(5).TrimStart(' '));
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                _eventType = line[6..].Trim();
        }
        return false;
    }

    public void Dispose() => _line.Dispose();
}
