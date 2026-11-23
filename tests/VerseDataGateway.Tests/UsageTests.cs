using System.Net;
using System.Text.Json;
using VerseDataGateway.Core;
using VerseDataGateway.Windows;

namespace VerseDataGateway.Tests;

public sealed class UsageTests
{
    [Fact]
    public void InstallationChoiceFilePersistsAndRejectsInvalidValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vdg-choice-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "choice");
        try
        {
            Assert.Null(InstallationChoiceFile.Read(path));
            InstallationChoiceFile.Write(path, true);
            Assert.True(InstallationChoiceFile.Read(path));
            InstallationChoiceFile.Write(path, false);
            Assert.False(InstallationChoiceFile.Read(path));
            File.WriteAllText(path, "invalid");
            Assert.Throws<InvalidDataException>(() => InstallationChoiceFile.Read(path));
        }
        finally { Clean(directory); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstallationChoicePersistsWithoutChangingOtherSettings(bool enabled)
    {
        var existing = new GatewayOptions { GamePort = 12345 };
        var updated = InstallationUsageChoice.Apply(existing, enabled);
        var restored = JsonSerializer.Deserialize<GatewayOptions>(JsonSerializer.Serialize(updated, WireJson.Options), WireJson.Options)!;
        Assert.Equal(enabled, restored.UsageStatisticsEnabled);
        Assert.True(restored.UsageNoticeShown);
        Assert.Equal(12345, restored.GamePort);
    }

    [Fact]
    public void ExistingSettingsRequireNoticeAndPersistDisabledChoice()
    {
        var existing = JsonSerializer.Deserialize<GatewayOptions>("""{}""", WireJson.Options)!;
        Assert.True(existing.UsageStatisticsEnabled);
        Assert.False(existing.UsageNoticeShown);
        var updated = existing with { UsageStatisticsEnabled = false, UsageNoticeShown = true };
        var restored = JsonSerializer.Deserialize<GatewayOptions>(JsonSerializer.Serialize(updated, WireJson.Options), WireJson.Options)!;
        Assert.False(restored.UsageStatisticsEnabled);
        Assert.True(restored.UsageNoticeShown);
    }

    [Fact]
    public async Task ReportsOnlyRequiredFieldsAndRotatesIdAfterDeletion()
    {
        var path = Path.Combine(Path.GetTempPath(), "vdg-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var identity = new UsageIdentityStore(path);
            var handler = new CaptureHandler();
            using var client = new HttpClient(handler);
            var reporter = new UsageReporter(client, identity, new Uri("https://example.invalid/v1/activity"));
            await reporter.ReportAsync(false, CancellationToken.None);
            var first = JsonDocument.Parse(handler.Bodies[0]).RootElement;
            Assert.Equal(3, first.EnumerateObject().Count());
            Assert.False(first.GetProperty("collection").GetBoolean());
            Assert.Equal(BuildInfo.Version, first.GetProperty("version").GetString());
            var id = first.GetProperty("installationId").GetString();
            await reporter.ReportAsync(true, CancellationToken.None);
            var second = JsonDocument.Parse(handler.Bodies[1]).RootElement;
            Assert.True(second.GetProperty("collection").GetBoolean());
            Assert.Equal(id, second.GetProperty("installationId").GetString());
            identity.Delete();
            await reporter.ReportAsync(false, CancellationToken.None);
            Assert.NotEqual(id, JsonDocument.Parse(handler.Bodies[2]).RootElement.GetProperty("installationId").GetString());
        }
        finally { Clean(path); }
    }

    [Fact]
    public async Task CancelledAndFailedReportsAreNotReportedAsSuccess()
    {
        var path = Path.Combine(Path.GetTempPath(), "vdg-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new CaptureHandler { Status = HttpStatusCode.ServiceUnavailable };
            using var client = new HttpClient(handler);
            var reporter = new UsageReporter(client, new UsageIdentityStore(path), new Uri("https://example.invalid/v1/activity"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reporter.ReportAsync(false, new CancellationToken(true)));
            Assert.Empty(handler.Bodies);
            Assert.False(Directory.Exists(path));
            await Assert.ThrowsAsync<HttpRequestException>(() => reporter.ReportAsync(false, CancellationToken.None));
        }
        finally { Clean(path); }
    }

    [Fact]
    public void InvalidStoredIdIsNotSilentlyReplaced()
    {
        var path = Path.Combine(Path.GetTempPath(), "vdg-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "usage-id"), "invalid");
            Assert.Throws<InvalidDataException>(() => new UsageIdentityStore(path).GetOrCreate());
        }
        finally { Clean(path); }
    }

    [Fact]
    public void DeletingIdDoesNotRequireAnExistingSettingsDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "vdg-usage-" + Guid.NewGuid().ToString("N"));
        new UsageIdentityStore(path).Delete();
        Assert.False(Directory.Exists(path));
    }

    private static void Clean(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path)) File.Delete(file);
        Directory.Delete(path);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Status);
        }
    }
}
