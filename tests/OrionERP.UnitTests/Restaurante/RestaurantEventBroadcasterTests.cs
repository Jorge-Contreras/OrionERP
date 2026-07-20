using System.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Application.Common;
using OrionERP.Web.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantEventBroadcasterTests
{
  [Fact]
  public async Task StopAsync_WhenTimerIsWaiting_CompletesWithoutCancellationException()
  {
    var services = new ServiceCollection()
      .AddScoped<IDbConnectionFactory, UnavailableConnectionFactory>()
      .BuildServiceProvider();

    await using (services)
    {
      var broadcaster = new RestaurantEventBroadcaster(
        services.GetRequiredService<IServiceScopeFactory>(),
        new UnusedHubContext(),
        NullLogger<RestaurantEventBroadcaster>.Instance);

      await broadcaster.StartAsync(CancellationToken.None);
      await Task.Delay(50);

      var exception = await Record.ExceptionAsync(() =>
        broadcaster.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token));

      Assert.Null(exception);
    }
  }

  private sealed class UnavailableConnectionFactory : IDbConnectionFactory
  {
    public IDbConnection Create() => throw new InvalidOperationException("Database unavailable in lifecycle test.");
  }

  private sealed class UnusedHubContext : IHubContext<RestaurantEventsHub>
  {
    public IHubClients Clients => throw new NotSupportedException();
    public IGroupManager Groups => throw new NotSupportedException();
  }
}
