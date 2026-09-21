namespace AiGateway.Configuration;

internal sealed class BackendOptions : Dictionary<string, BackendConfig>
{
    internal const string DefaultBackendName = "openrouter";

    public BackendOptions()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}
