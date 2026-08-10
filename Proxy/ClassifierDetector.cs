using System.Text.Json.Nodes;

namespace AiGateway.Proxy;

/// <summary>
/// Detects Claude Code auto-mode security classifier requests by matching
/// known signature strings in the system prompt.
/// </summary>
internal static class ClassifierDetector
{
    /// <summary>
    /// Returns true when the request body matches the classifier signature:
    /// the system array contains both a block starting with
    /// "x-anthropic-billing-header:" and one starting with "You are a security monitor".
    /// </summary>
    internal static bool IsClassifierRequest(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            var json = JsonNode.Parse(body);
            var system = json?["system"];
            if (system is not JsonArray arr)
                return false;

            var hasBilling = false;
            var hasMonitor = false;

            foreach (var block in arr)
            {
                var text = block?["text"]?.GetValue<string>();
                if (text is null)
                    continue;

                if (text.StartsWith("x-anthropic-billing-header:", StringComparison.Ordinal))
                    hasBilling = true;
                if (text.StartsWith("You are a security monitor", StringComparison.Ordinal))
                    hasMonitor = true;

                if (hasBilling && hasMonitor)
                    return true;
            }

            return false;
        }
        catch
        {
            // Not valid JSON or unexpected structure — not a classifier
            return false;
        }
    }
}
