using System.Text;
using System.Text.Json;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ConnectionTestResultsTests
{
    [TestMethod]
    public void BodyEvictionPreservesSummaryAndCannotExpandJobProgress()
    {
        using var store = new ConnectionTestResults(1024);
        store.Begin("job", Enumerable.Range(0, 100).Select(i => (object)new { model = "中文", keyId = i.ToString(System.Globalization.CultureInfo.InvariantCulture), keyName = "Key " + i }));
        for (var i = 0; i < 100; i++)
        {
            store.Set("job", i, new { success = i % 2 == 0, originalStatus = i % 2 == 0 ? 200 : 403 }, new byte[800]);
            Assert.IsLessThan(1024, Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(store.Progress("job"))));
        }
        store.Finish("job");
        var first = JsonSerializer.SerializeToElement(store.Page("job", 0, 20));
        Assert.AreEqual(100, first.GetProperty("total").GetInt32());
        Assert.AreEqual(20, first.GetProperty("rows").GetArrayLength());
        Assert.IsTrue(first.GetProperty("rows")[0].GetProperty("responseEvicted").GetBoolean());
        Assert.IsTrue(JsonSerializer.SerializeToElement(store.Detail("job", 0, 0)).GetProperty("evicted").GetBoolean());
        Assert.IsFalse(JsonSerializer.SerializeToElement(store.Detail("job", 99, 0)).GetProperty("evicted").GetBoolean());
    }

    [TestMethod]
    public void DetailChunksPreserveUtf8AndCancelledRowsKeepNames()
    {
        using var store = new ConnectionTestResults();
        store.Begin("job", Enumerable.Range(0, 2).Select(i => (object)new { model = "m", keyId = i.ToString(System.Globalization.CultureInfo.InvariantCulture), keyName = "Name " + i }));
        var original = new string('a', 65535) + "中文\n\"转义\"😀";
        store.Set("job", 0, new { success = true }, Encoding.UTF8.GetBytes(original));
        var first = JsonSerializer.SerializeToElement(store.Detail("job", 0, 0));
        var second = JsonSerializer.SerializeToElement(store.Detail("job", 0, first.GetProperty("nextOffset").GetInt32()));
        Assert.AreEqual(original, first.GetProperty("text").GetString() + second.GetProperty("text").GetString());
        store.Finish("job", cancelled: true);
        var rows = JsonSerializer.SerializeToElement(store.Page("job", 0, 20)).GetProperty("rows");
        Assert.AreEqual("succeeded", rows[0].GetProperty("state").GetString());
        Assert.AreEqual("cancelled", rows[1].GetProperty("state").GetString());
        Assert.AreEqual("Name 1", rows[1].GetProperty("keyName").GetString());
    }

    [TestMethod]
    public void CompletedRetentionIsBoundedAndDisposeClearsBodies()
    {
        using var store = new ConnectionTestResults();
        for (var i = 0; i < 130; i++) { store.Begin(i.ToString(System.Globalization.CultureInfo.InvariantCulture), []); store.Finish(i.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        Assert.IsNull(store.Page("0", 0, 20)); Assert.IsNotNull(store.Page("129", 0, 20));
        store.Dispose(); Assert.IsNull(store.Page("129", 0, 20));
    }

    [TestMethod]
    public void InvalidUtf8StillAdvancesBodyCursor()
    {
        using var store = new ConnectionTestResults();
        store.Begin("job", Enumerable.Repeat((object)new { model = "m" }, 1));
        store.Set("job", 0, new { success = false }, Enumerable.Repeat((byte)0x80, 70000).ToArray());
        var detail = JsonSerializer.SerializeToElement(store.Detail("job", 0, 0));
        Assert.AreEqual(65536, detail.GetProperty("nextOffset").GetInt32());
    }
}
