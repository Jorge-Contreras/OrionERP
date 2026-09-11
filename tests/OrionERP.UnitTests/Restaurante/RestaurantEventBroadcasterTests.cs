using Microsoft.AspNetCore.SignalR;
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
      Options.Create(new RestaurantEventBroadcastOptions()),
      new UnusedHubContext(),
      NullLogger<RestaurantEventBroadcaster>.Instance,
      null!);

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
    Assert.Contains("sessions.OpenAsync(scope", source, StringComparison.Ordinal);
    Assert.Contains("PlatformExecutionScope", source, StringComparison.Ordinal);
    Assert.Contains("sites.ListAsync(PlatformModuleCodes.Restaurant", source, StringComparison.Ordinal);
    Assert.Contains("bindings.ResolveRequiredAsync", source, StringComparison.Ordinal);
    Assert.Contains("leases.TryAcquireAsync", source, StringComparison.Ordinal);
    Assert.DoesNotContain("legacySite.Rfc=eventInfo.Rfc AND legacySite.Id=eventInfo.SiteId", source, StringComparison.Ordinal);
  }

  private sealed class UnusedHubContext : IHubContext<RestaurantEventsHub>
  {
    public IHubClients Clients => throw new NotSupportedException();
    public IGroupManager Groups => throw new NotSupportedException();
  }
}
