using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrionERP.Web.Identity;

namespace OrionERP.IntegrationTests.Web;

public class LoginAntiforgeryRecoveryMiddlewareTests
{
  [Fact]
  public async Task RejectedLoginPost_RedirectsToFreshLoginAndDeletesCookie()
  {
    const string cookieName = ".OrionERP.Management.Antiforgery";
    var options = Options.Create(new AntiforgeryOptions());
    options.Value.Cookie.Name = cookieName;
    var middleware = new LoginAntiforgeryRecoveryMiddleware(
      context =>
      {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return Task.CompletedTask;
      },
      options,
      NullLogger<LoginAntiforgeryRecoveryMiddleware>.Instance);
    var context = new DefaultHttpContext();
    context.Request.Method = HttpMethods.Post;
    context.Request.Path = "/Identity/Account/Login";
    context.Request.QueryString = new QueryString("?returnUrl=%2F");

    await middleware.InvokeAsync(context);

    Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
    Assert.Equal(
      "/Identity/Account/Login?returnUrl=%2F&securityTokenExpired=true",
      context.Response.Headers.Location.ToString());
    Assert.Contains(
      context.Response.Headers.SetCookie,
      value => value is not null
               && value.StartsWith($"{cookieName}=", StringComparison.Ordinal)
               && value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
  }

  [Theory]
  [InlineData("GET", "/Identity/Account/Login")]
  [InlineData("POST", "/Identity/Account/ForgotPassword")]
  public async Task OtherBadRequests_RemainBadRequests(string method, string path)
  {
    var options = Options.Create(new AntiforgeryOptions());
    options.Value.Cookie.Name = ".OrionERP.Management.Antiforgery";
    var middleware = new LoginAntiforgeryRecoveryMiddleware(
      context =>
      {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return Task.CompletedTask;
      },
      options,
      NullLogger<LoginAntiforgeryRecoveryMiddleware>.Instance);
    var context = new DefaultHttpContext();
    context.Request.Method = method;
    context.Request.Path = path;

    await middleware.InvokeAsync(context);

    Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    Assert.False(context.Response.Headers.ContainsKey("Location"));
  }
}
