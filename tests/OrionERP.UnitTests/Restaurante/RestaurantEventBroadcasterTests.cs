using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrionERP.Web.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantEventBroadcasterTests
{
  [Fact]
  public async Task StopAsync_WhenTimerIsWaiting_CompletesWithoutCancellationException()
  {
    var broadcaster = new RestaurantEventBroadcaster(
      new ConfigurationBuilder().Build(),
      Options.Create(new RestaurantEventBroadcastOptions()),
      new UnusedHubContext(),
      NullLogger<RestaurantEventBroadcaster>.Instance);

    await broadcaster.StartAsync(CancellationToken.None);
    await Task.Delay(50);

    var exception = await Record.ExceptionAsync(() =>
      broadcaster.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token));

    Assert.Null(exception);
  }

  [Fact]
  public void Broadcaster_IsFailClosedByDefaultAndScopesEveryWorkerConnection()
  {
    var options = new RestaurantEventBroadcastOptions();
    var source = Common.RepoFile.Read(
      "src/OrionERP.Web/Features/Restaurante/RestaurantEventBroadcaster.cs");

    Assert.False(options.Enabled);
    Assert.Contains("OrionRfc", source, StringComparison.Ordinal);
    Assert.Contains("OrionERP.CompanyId", source, StringComparison.Ordinal);
    Assert.Contains("companyModule.ModuleCode='RESTAURANT'", source, StringComparison.Ordinal);
    Assert.Contains("capability.IsEnabled=1", source, StringComparison.Ordinal);
    Assert.Contains("legacySite.SiteCode=@SiteKey", source, StringComparison.Ordinal);
  }

  private sealed class UnusedHubContext : IHubContext<RestaurantEventsHub>
  {
    public IHubClients Clients => throw new NotSupportedException();
    public IGroupManager Groups => throw new NotSupportedException();
  }
}
