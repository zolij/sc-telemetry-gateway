using VerseDataGateway.Core;

namespace VerseDataGateway.Windows;

internal static class InstallationUsageChoice
{
    public static GatewayOptions Apply(GatewayOptions options, bool choice) =>
        options with { UsageStatisticsEnabled = choice, UsageNoticeShown = true };
}
