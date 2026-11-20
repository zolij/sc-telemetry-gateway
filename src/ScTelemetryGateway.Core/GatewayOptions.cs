namespace ScTelemetryGateway.Core;

public sealed record GatewayOptions
{
    public int GamePort { get; init; } = 50051;
    public string Endpoint { get; init; } = "";
    public int IntervalSeconds { get; init; } = 5;
    public HashSet<string> Fields { get; init; } = [];

    public void Validate(bool requireEndpoint = true)
    {
        if (GamePort is < 1024 or > 65535)
            throw new ArgumentException("Game port must be between 1024 and 65535.");
        if (IntervalSeconds is < 1 or > 300)
            throw new ArgumentException("Interval must be between 1 and 300 seconds.");
        if (requireEndpoint || Endpoint.Length > 0)
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != "wss" || uri.UserInfo.Length > 0 ||
                uri.Fragment.Length > 0 || uri.Query.Length > 0)
                throw new ArgumentException("Receiver must be a wss:// URL without credentials, query or fragment.");
        }
        if (Fields.Any(path => !FieldCatalog.All.ContainsKey(path)))
            throw new ArgumentException("The selected fields include an unknown protocol field.");
        if (requireEndpoint && Fields.Count == 0)
            throw new ArgumentException("Select at least one telemetry field.");
    }
}
