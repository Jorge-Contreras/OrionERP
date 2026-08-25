using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Identity;

namespace OrionERP.UnitTests.Auth;

public sealed class CompanyScopeGuardMiddlewareTests
{
  [Theory]
  [InlineData("?rfc=OHM191112Q26")]
  [InlineData("?companyRfc=ohm191112q26")]
  [InlineData("?empresaRfc=%20OHM191112Q26%20")]
  public async Task Matching_query_company_continues(string query)
  {
    var nextCalled = false;
    var middleware = new CompanyScopeGuardMiddleware(context =>
    {
      nextCalled = true;
      context.Response.StatusCode = StatusCodes.Status204NoContent;
      return Task.CompletedTask;
    });
    var context = AuthenticatedContext("OHM191112Q26", query);

    await middleware.InvokeAsync(context);

    Assert.True(nextCalled);
    Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
  }

  [Fact]
  public async Task Mismatched_query_company_is_denied_without_invoking_endpoint()
  {
    var nextCalled = false;
    var middleware = new CompanyScopeGuardMiddleware(_ =>
    {
      nextCalled = true;
      return Task.CompletedTask;
    });
    var context = AuthenticatedContext("OHM191112Q26", "?rfc=BRUNOS260707L26");
    context.Response.Body = new MemoryStream();

    await middleware.InvokeAsync(context);

    Assert.False(nextCalled);
    Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    context.Response.Body.Position = 0;
    using var reader = new StreamReader(context.Response.Body);
    Assert.Contains("no corresponde", await reader.ReadToEndAsync(), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Mismatched_route_company_is_denied()
  {
    var middleware = new CompanyScopeGuardMiddleware(_ => throw new InvalidOperationException("Endpoint must not execute."));
    var context = AuthenticatedContext("OHM191112Q26");
    context.Request.RouteValues["rfc"] = "BRUNOS260707L26";

    await middleware.InvokeAsync(context);

    Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
  }

  [Fact]
  public async Task Anonymous_requests_are_not_company_scoped()
  {
    var nextCalled = false;
    var middleware = new CompanyScopeGuardMiddleware(_ =>
    {
      nextCalled = true;
      return Task.CompletedTask;
    });
    var context = new DefaultHttpContext();
    context.Request.QueryString = new QueryString("?rfc=BRUNOS260707L26");

    await middleware.InvokeAsync(context);

    Assert.True(nextCalled);
  }

  private static DefaultHttpContext AuthenticatedContext(string rfc, string? query = null)
  {
    var context = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(CompanyClaimTypes.Rfc, rfc)],
        authenticationType: "test"))
    };
    if (query is not null) context.Request.QueryString = new QueryString(query);
    return context;
  }
}
