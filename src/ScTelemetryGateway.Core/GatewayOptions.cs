namespace ScTelemetryGateway.Core;

public sealed record GatewayOptions
{
    public int GamePort { get; init; } = 50051;
    public string Endpoint { get; init; } = "";
    public int IntervalSeconds { get; init; } = 5;
    public HashSet<string> Fields { get; init; } = [];
    public bool RemoteEnabled { get; init; } = true;
    public bool MfdEnabled { get; init; }
    public string MfdAddress { get; init; } = "";
    public int MfdPort { get; init; } = 50052;
    public HashSet<string> MfdFields { get; init; } = [];
    public bool AutomaticUpdateChecks { get; init; } = true;
    public bool IncludePreviewUpdates { get; init; }
    public bool UsageStatisticsEnabled { get; init; } = true;
    public bool UsageNoticeShown { get; init; }

    public void Validate(bool requireDestination = true)
    {
        if (Endpoint is null || Fields is null || MfdAddress is null || MfdFields is null)
            throw new ArgumentException("Endpoint, field selections and MFD settings cannot be null.");
        if (GamePort is < 1024 or > 65535)
            throw new ArgumentException("Game port must be between 1024 and 65535.");
        if (IntervalSeconds is < 1 or > 300)
            throw new ArgumentException("Interval must be between 1 and 300 seconds.");
        var remoteEnabled = RemoteEnabled && Endpoint.Length > 0;
        if (RemoteEnabled && Endpoint.Length > 0)
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != "wss" || uri.UserInfo.Length > 0 ||
                uri.Fragment.Length > 0 || uri.Query.Length > 0)
                throw new ArgumentException("Receiver must be a wss:// URL without credentials, query or fragment.");
        }
        if (Fields.Any(path => !FieldCatalog.All.ContainsKey(path)))
            throw new ArgumentException("The selected remote fields include an unknown protocol field.");
        if (MfdFields.Any(path => !FieldCatalog.All.ContainsKey(path)))
            throw new ArgumentException("The selected MFD fields include an unknown protocol field.");
        if (requireDestination && remoteEnabled && Fields.Count == 0)
            throw new ArgumentException("Select at least one remote telemetry field.");
        if (MfdEnabled)
        {
            if (MfdPort is < 1024 or > 65535)
                throw new ArgumentException("MFD port must be between 1024 and 65535.");
            if (!System.Net.IPAddress.TryParse(MfdAddress, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                address.Equals(System.Net.IPAddress.Any))
                throw new ArgumentException("Select an eligible local IPv4 address for the MFD listener.");
            if (requireDestination && MfdFields.Count == 0)
                throw new ArgumentException("Select at least one MFD telemetry field.");
        }
        if (requireDestination && !remoteEnabled && !MfdEnabled)
            throw new ArgumentException("Enable remote upload or local MFD access and configure at least one destination.");
    }

    public bool RemoteConfigured => RemoteEnabled && Endpoint.Length > 0;
}
