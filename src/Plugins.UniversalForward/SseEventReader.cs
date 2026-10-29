using System.Text;

namespace Plugins.UniversalForward;

/// <summary>增量读取 SSE data 事件，只扫描新解码的字符，避免反复复制未完成的长行。</summary>
internal sealed class SseEventReader(int maxChars, bool strictUtf8 = true, bool skipOversizeEvents = false) : IDisposable
{
    private readonly Decoder _decoder = new UTF8Encoding(false, strictUtf8).GetDecoder();
    private readonly char[] _buffer = new char[4096];
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private bool _afterCr;
    private bool _firstLine = true;
    private bool _skippingEvent;
    private bool _lineHasChars;

    internal bool Observe(ReadOnlySpan<byte> bytes, Func<string, bool> onEvent)
    {
        while (!bytes.IsEmpty)
        {
            // 回调可能在结束事件处停止，不解码其后的无关数据。
            var end = bytes.IndexOfAny((byte)'\r', (byte)'\n');
            var length = end < 0 ? bytes.Length : end + 1;
            var segment = bytes[..length];
            do
            {
                _decoder.Convert(segment, _buffer, false, out var bytesUsed, out var charsUsed, out _);
                segment = segment[bytesUsed..];
                if (ReadChars(_buffer.AsSpan(0, charsUsed), onEvent)) return true;
            } while (!segment.IsEmpty);
            bytes = bytes[length..];
        }
        return false;
    }

    private bool ReadChars(ReadOnlySpan<char> chars, Func<string, bool> onEvent)
    {
        while (!chars.IsEmpty)
        {
            if (_afterCr)
            {
                _afterCr = false;
                if (chars[0] == '\n')
                {
                    chars = chars[1..];
                    continue;
                }
            }
            var end = chars.IndexOfAny('\r', '\n');
            var length = end < 0 ? chars.Length : end;
            _lineHasChars |= length > 0;
            if (!_skippingEvent)
            {
                if (length > maxChars - _line.Length)
                {
                    if (!skipOversizeEvents) throw new InvalidDataException("上游 SSE 行超过解析上限");
                    SkipEvent();
                }
                else _line.Append(chars[..length]);
            }
            if (end < 0) break;
            _afterCr = chars[end] == '\r';
            chars = chars[(end + 1)..];
            if (_skippingEvent)
            {
                // 长行被分片读取时，最后一片可能只含换行，不能把它误当作事件空行。
                if (!_lineHasChars) _skippingEvent = false;
                _firstLine = false;
            }
            else if (ReadLine(onEvent)) return true;
            _lineHasChars = false;
        }
        return false;
    }

    private bool ReadLine(Func<string, bool> onEvent)
    {
        var line = _line.ToString();
        _line.Clear();
        if (_firstLine)
        {
            line = line.TrimStart('\uFEFF');
            _firstLine = false;
        }
        if (line.Length == 0)
        {
            if (_data.Length == 0) return false;
            var data = _data.ToString();
            _data.Clear();
            return onEvent(data);
        }
        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            var value = line.AsSpan(5).TrimStart(' ');
            var separator = _data.Length == 0 ? 0 : 1;
            if (value.Length > maxChars - _data.Length - separator)
            {
                if (!skipOversizeEvents) throw new InvalidDataException("上游 SSE 事件超过解析上限");
                SkipEvent();
                return false;
            }
            if (separator != 0) _data.Append('\n');
            _data.Append(value);
        }
        return false;
    }

    private void SkipEvent()
    {
        _skippingEvent = true;
        _line.Clear();
        _data.Clear();
    }

    // EOF 只校验 UTF-8 尾部；缺少空行的事件不作为完整事件交付。
    internal void Complete() => _decoder.GetChars([], _buffer, true);

    public void Dispose()
    {
        _decoder.Reset();
        _line.Clear();
        _data.Clear();
        _skippingEvent = false;
        _lineHasChars = false;
    }
}
