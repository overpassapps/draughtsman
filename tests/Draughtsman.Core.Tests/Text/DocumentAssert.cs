using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Tests.Text;

/// <summary>
/// Structural equality for documents. The records hold lists and JsonElements, which compare by reference, so the
/// canonical JSON form (the interchange contract) is the comparison: same JSON, same document.
/// </summary>
internal static class DocumentAssert
{
    public static bool AreEquivalent(Document expected, Document actual, out string difference)
    {
        string expectedJson = DocumentJson.Serialize(expected, indented: true);
        string actualJson = DocumentJson.Serialize(actual, indented: true);
        if (string.Equals(expectedJson, actualJson, StringComparison.Ordinal))
        {
            difference = string.Empty;
            return true;
        }

        difference = $"--- expected JSON ---{Environment.NewLine}{expectedJson}{Environment.NewLine}--- actual JSON ---{Environment.NewLine}{actualJson}";
        return false;
    }

    public static void Equivalent(Document expected, Document actual, string context)
    {
        if (!AreEquivalent(expected, actual, out string difference))
        {
            Assert.Fail($"{context}{Environment.NewLine}{difference}");
        }
    }
}
