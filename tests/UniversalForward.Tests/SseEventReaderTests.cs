using System.Text;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class SseEventReaderTests
{
    private static readonly string[] FragmentedEvents = ["{\n\"text\":\"你好😀\"}", "second", "third"];
    private static readonly string[] FirstEvent = ["first"];
    private static readonly string[] RecoveredUtf8Events = ["first", "你好😀"];
    private static readonly string[] RecoveredEvents = ["first", "second"];
    private static readonly string[] ReplacementEvent = ["\uFFFD"];
    private static readonly string[] BufferNames = ["_line", "_data"];
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(4096)]
    public void FragmentedEventsKeepUtf8BomMultilineAndLineEndings(int chunkSize)
    {
        var bytes = Encoding.UTF8.GetBytes("\uFEFF: ping\r\n\r\nevent: first\r\ndata:   {\r\ndata: \"text\":\"你好😀\"}\r\n\r\n: next\ndata: second\n\ndata: third\r\r");
        using var reader = new SseEventReader(1024);
        var events = new List<string>();
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            Assert.IsFalse(reader.Observe(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)), data =>
            {
                events.Add(data);
                return false;
            }));
        reader.Complete();
        CollectionAssert.AreEqual(FragmentedEvents, events);
    }

    [TestMethod]
    public void CallbackCanStopBeforeFollowingEvents()
    {
        using var reader = new SseEventReader(1024);
        var events = new List<string>();
        Assert.IsTrue(reader.Observe("data: first\n\ndata: second\n\n"u8, data =>
        {
            events.Add(data);
            return true;
        }));
        CollectionAssert.AreEqual(FirstEvent, events);
    }

    [TestMethod]
    public void CallbackStopsBeforeInvalidUtf8TrailerInTheSameChunk()
    {
        using var reader = new SseEventReader(1024);
        var bytes = "data: first\n\n"u8.ToArray().Concat(new byte[] { 0xff }).ToArray();
        var events = new List<string>();
        Assert.IsTrue(reader.Observe(bytes, data => { events.Add(data); return true; }));
        CollectionAssert.AreEqual(FirstEvent, events);
    }

    [TestMethod]
    public void UnterminatedEventsAreNotDispatchedAtEof()
    {
        using var reader = new SseEventReader(1024);
        var count = 0;
        Assert.IsFalse(reader.Observe("data: partial\n"u8, _ => { count++; return false; }));
        reader.Complete();
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    [DataRow("data: 123456789\n\n")]
    [DataRow("data: 123456\ndata: 123456\ndata: 123456\n\n")]
    public void LimitsApplyToFragmentedLinesAndMultilineEvents(string frame)
    {
        using var reader = new SseEventReader(12);
        var bytes = Encoding.UTF8.GetBytes(frame);
        Assert.ThrowsExactly<InvalidDataException>(() =>
        {
            for (var i = 0; i < bytes.Length; i++) reader.Observe(bytes.AsSpan(i, 1), _ => false);
        });
    }

    [TestMethod]
    public void StrictUtf8RejectsTruncatedCodePointAtEof()
    {
        using var reader = new SseEventReader(1024);
        reader.Observe(new byte[] { 0xf0, 0x9f }, _ => false);
        Assert.ThrowsExactly<DecoderFallbackException>(reader.Complete);
    }

    [TestMethod]
    public void TolerantUtf8MetadataDoesNotInterruptRawForwarding()
    {
        using var reader = new SseEventReader(1024, strictUtf8: false);
        var events = new List<string>();
        reader.Observe(new byte[] { 100, 97, 116, 97, 58, 32, 0xff, 10, 10 }, data =>
        {
            events.Add(data);
            return false;
        });
        reader.Complete();
        CollectionAssert.AreEqual(ReplacementEvent, events);
    }

    [TestMethod]
    [DataRow("\n", 1)]
    [DataRow("\r\n", 1)]
    [DataRow("\r", 1)]
    [DataRow("\n", 7)]
    [DataRow("\r\n", 7)]
    [DataRow("\r", 7)]
    [DataRow("\r\n", 4096)]
    public void OversizeLineSkipsWholeEventAndRecoversAtBlankLine(string newline, int chunkSize)
    {
        var text = "\uFEFF: start" + newline + "data: first" + newline + newline
            + "event: skipped" + newline + "data: " + new string('x', 256) + newline
            + "data: poison" + newline + ": comment" + newline + "event: still-skipped" + newline + newline
            + "event: next" + newline + "data: 你好😀" + newline + newline;
        var events = ReadSkippedEvents(text, 32, chunkSize);
        CollectionAssert.AreEqual(RecoveredUtf8Events, events);
    }

    [TestMethod]
    [DataRow("\n", 1)]
    [DataRow("\r\n", 1)]
    [DataRow("\r", 7)]
    [DataRow("\r\n", 4096)]
    public void OversizeMultilineDataSkipsPartialPayloadAndFollowingFields(string newline, int chunkSize)
    {
        const string line = "data: 123456789";
        var text = "data: first" + newline + newline + line + newline + line + newline + line + newline
            + line + newline + "data: poison" + newline + "event: no-dispatch" + newline + ": ping" + newline + newline
            + "data: second" + newline + newline;
        var events = ReadSkippedEvents(text, 24, chunkSize);
        CollectionAssert.AreEqual(RecoveredEvents, events);
    }

    [TestMethod]
    public void LongCommentsAndEventNamesDiscardCurrentEventUntilAnActualBlankLine()
    {
        var text = "data: poison\n: " + new string('x', 256) + "\ndata: still-poison\n\n"
            + "data: more-poison\nevent: " + new string('y', 256) + "\ndata: still-poison\n\n"
            + "event: valid\ndata: first\n\n";
        var events = ReadSkippedEvents(text, 32, 7);
        CollectionAssert.AreEqual(FirstEvent, events);
    }

    [TestMethod]
    public void RecoveryDoesNotDispatchAfterOnlyTheOversizeLineEnding()
    {
        using var reader = new SseEventReader(16, skipOversizeEvents: true);
        var events = new List<string>();
        reader.Observe(Encoding.UTF8.GetBytes("data: " + new string('x', 64)), data => { events.Add(data); return false; });
        reader.Observe("\r"u8, data => { events.Add(data); return false; });
        reader.Observe("\ndata: poison\r\n"u8, data => { events.Add(data); return false; });
        Assert.AreEqual(0, events.Count);
        reader.Observe("\r"u8, data => { events.Add(data); return false; });
        reader.Observe("\ndata: first\r\n\r\n"u8, data => { events.Add(data); return false; });
        reader.Complete();
        CollectionAssert.AreEqual(FirstEvent, events);
    }

    [TestMethod]
    public void CallbackAfterRecoveryStopsBeforeInvalidUtf8Trailer()
    {
        using var reader = new SseEventReader(32, skipOversizeEvents: true);
        var bytes = Encoding.UTF8.GetBytes("data: " + new string('x', 256) + "\n\ndata: first\n\n")
            .Concat(new byte[] { 0xff }).ToArray();
        var events = new List<string>();
        Assert.IsTrue(reader.Observe(bytes, data => { events.Add(data); return true; }));
        CollectionAssert.AreEqual(FirstEvent, events);
    }

    [TestMethod]
    public void OversizeAndRecoveredTrailingPartialEventsAreNotDispatchedAtEof()
    {
        var text = "data: first\n\ndata: " + new string('x', 256) + "\n\ndata: partial\n";
        CollectionAssert.AreEqual(FirstEvent, ReadSkippedEvents(text, 32, 7));
        var unfinished = "data: first\n\ndata: " + new string('x', 256) + "\ndata: poison\n";
        CollectionAssert.AreEqual(FirstEvent, ReadSkippedEvents(unfinished, 32, 7));
    }

    [TestMethod]
    public void ManyOversizeEventsDoNotLeakTheirPartialDataIntoRecoveredEvents()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 128; i++)
            text.Append("data: ").Append('x', 128).Append("\n\ndata: ").Append(i).Append("\n\n");
        var events = ReadSkippedEvents(text.ToString(), 32, 7);
        CollectionAssert.AreEqual(Enumerable.Range(0, 128).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), events);
    }

    [TestMethod]
    public void OversizeSingleLineKeepsBuffersBoundedAcrossSmallReads()
    {
        using var reader = new SseEventReader(128, skipOversizeEvents: true);
        var events = new List<string>();
        reader.Observe("data: "u8, data => { events.Add(data); return false; });
        var chunk = Encoding.UTF8.GetBytes(new string('x', 1024));
        for (var i = 0; i < 2048; i++) reader.Observe(chunk, data => { events.Add(data); return false; });
        reader.Observe("\n\ndata: first\n\n"u8, data => { events.Add(data); return false; });
        reader.Complete();
        foreach (var name in BufferNames)
        {
            var buffer = (StringBuilder)typeof(SseEventReader)
                .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
            Assert.IsTrue(buffer.Capacity <= 4096, "Discarded event must not grow the text buffer: " + name);
        }
        CollectionAssert.AreEqual(FirstEvent, events);
    }

    private static List<string> ReadSkippedEvents(string text, int limit, int chunkSize)
    {
        using var reader = new SseEventReader(limit, skipOversizeEvents: true);
        var bytes = Encoding.UTF8.GetBytes(text);
        var events = new List<string>();
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            Assert.IsFalse(reader.Observe(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)), data =>
            {
                events.Add(data); return false;
            }));
        reader.Complete();
        return events;
    }
}
