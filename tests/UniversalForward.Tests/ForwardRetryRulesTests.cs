using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ForwardRetryRulesTests
{
    [TestMethod]
    public void RetryDefaultBoundaryCodesAreExact()
    {
        var rules = ForwardRetryRules.Parse(ForwardRetryRules.DefaultCodes);
        for (var code = 0; code < 700; code++)
        {
            var expected = code is >= 100 and <= 199 or >= 300 and <= 399
                or >= 401 and <= 407 or >= 409 and <= 503 or >= 505 and <= 523 or >= 525 and <= 599;
            Assert.AreEqual(expected, rules.Contains(code), $"status {code}");
        }
    }

    [TestMethod]
    [DataRow("99")]
    [DataRow("600")]
    [DataRow("500-400")]
    [DataRow("400,")]
    [DataRow("400,,500")]
    [DataRow("400-500-599")]
    [DataRow("abc")]
    public void InvalidRulesAreRejected(string input)
        => Assert.ThrowsExactly<FormatException>(() => ForwardRetryRules.Parse(input));
}
