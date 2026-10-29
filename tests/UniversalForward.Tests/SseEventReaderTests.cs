using System.Text;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class SseEventReaderTests
{
    private static readonly string[] FragmentedEvents = ["{\n\"text\":\"你好😀\"}", "second", "third"];
    private static readonly string[] FirstEvent = ["first"];
    private static readonly string[] ReplacementEvent = ["\uFFFD"];
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
}
