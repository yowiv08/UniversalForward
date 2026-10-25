using System.Text;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ForwardUsageTests
{
    [TestMethod]
    [DataRow("{\"input_tokens\":2,\"cache_read_input_tokens\":3,\"cache_creation_input_tokens\":4,\"output_tokens\":4}", 9, 4, 13)]
    [DataRow("{\"input_tokens\":2,\"output_tokens\":4,\"total_tokens\":99}", 2, 4, 99)]
    [DataRow("{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0}", 0, 0, 0)]
    [DataRow("{\"input_tokens\":9,\"output_tokens\":4,\"input_tokens_details\":{\"cached_tokens\":3},\"output_tokens_details\":{\"reasoning_tokens\":2}}", 9, 4, 13)]
    [DataRow("{\"prompt_tokens\":9,\"completion_tokens\":4,\"prompt_tokens_details\":{\"cached_tokens\":3}}", 9, 4, 13)]
    public void CompleteUsageKeepsCachesZerosAndExplicitTotal(string json, int input, int output, int total)
    {
        var result = ForwardUsage.Read(Encoding.UTF8.GetBytes("{\"usage\":" + json + "}"));
        Assert.IsNotNull(result);
        Assert.AreEqual(input, result.PromptTokens);
        Assert.AreEqual(output, result.CompletionTokens);
        Assert.AreEqual(total, result.TotalTokens);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"usage\":{}}")]
    [DataRow("{\"usage\":{\"total_tokens\":99}}")]
    [DataRow("{\"usage\":{\"input_tokens\":2}}")]
    [DataRow("{\"usage\":{\"input_tokens\":-1,\"output_tokens\":4}}")]
    [DataRow("{\"usage\":{\"input_tokens\":\"2\",\"output_tokens\":4}}")]
    [DataRow("{\"usage\":{\"input_tokens\":2,\"output_tokens\":4,\"cache_read_input_tokens\":null}}")]
    [DataRow("{\"usage\":{\"input_tokens\":2,\"output_tokens\":4,\"total_tokens\":-1}}")]
    [DataRow("{\"usage\":{\"input_tokens\":2147483647,\"output_tokens\":4}}")]
    [DataRow("{\"usage\":null}")]
    [DataRow("{\"usage\":[]}")]
    [DataRow("[]")]
    [DataRow("not json")]
    public void MissingAndInvalidUsageFallsBackWithoutThrowing(string json)
        => Assert.IsNull(ForwardUsage.Read(Encoding.UTF8.GetBytes(json)));
}
