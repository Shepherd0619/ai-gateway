using System.Text.Json;

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
    /// Operates on an already-parsed <see cref="JsonElement"/> so the caller
    /// pays for a single JSON parse, not two.
    /// </summary>
    internal static bool IsClassifierRequest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("system", out var system) || system.ValueKind != JsonValueKind.Array)
            return false;

        var hasBilling = false;
        var hasMonitor = false;

        foreach (var block in system.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            if (!block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                continue;

            var value = text.GetString();
            if (value is null)
                continue;

            if (value.StartsWith("x-anthropic-billing-header:", StringComparison.Ordinal))
                hasBilling = true;
            if (value.StartsWith("You are a security monitor", StringComparison.Ordinal))
                hasMonitor = true;

            if (hasBilling && hasMonitor)
                return true;
        }

        return false;
    }
}
