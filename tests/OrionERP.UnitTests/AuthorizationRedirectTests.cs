namespace OrionERP.UnitTests;

public sealed class AuthorizationRedirectTests
{
  private static readonly string AppRazor = ReadRepoFile("src/OrionERP.Web/App.razor");
  private static readonly string AccessDeniedRazor = ReadRepoFile("src/OrionERP.Web/Shared/AccessDenied.razor");
  private static readonly string RedirectToLoginRazor = ReadRepoFile("src/OrionERP.Web/Shared/RedirectToLogin.razor");

  [Fact]
  public void SignedInUserWithoutTheRequiredRole_IsNotSentToTheLoginPage()
  {
    // Blazor raises NotAuthorized both for anonymous visitors and for signed-in
    // users who lack a page's role. Redirecting the second group to the login page
    // loops forever: Login redirects an already-authenticated user straight back to
    // returnUrl, which fails authorization again (ERR_TOO_MANY_REDIRECTS).
    var notAuthorized = AppRazor.IndexOf("<NotAuthorized>", StringComparison.Ordinal);
    var nestedCheck = AppRazor.IndexOf("<AuthorizeView", StringComparison.Ordinal);
    var authorizedBranch = AppRazor.IndexOf("<Authorized>", StringComparison.Ordinal);
    var accessDenied = AppRazor.IndexOf("<AccessDenied />", StringComparison.Ordinal);
    var redirect = AppRazor.IndexOf("<RedirectToLogin />", StringComparison.Ordinal);

    Assert.True(notAuthorized >= 0);
    Assert.True(nestedCheck > notAuthorized);

    // The signed-in branch must resolve to AccessDenied, never to RedirectToLogin.
    Assert.True(authorizedBranch > nestedCheck);
    Assert.True(accessDenied > authorizedBranch);
    Assert.True(accessDenied < redirect);
  }

  [Fact]
  public void RedirectToLogin_IsReservedForVisitorsWhoAreNotSignedIn()
  {
    // The component force-navigates without inspecting authentication state, so it
    // is only safe underneath a branch that has already established the visitor is
    // anonymous. Guarding that is App.razor's job, asserted above.
    Assert.Contains("/Identity/Account/Login?returnUrl=", RedirectToLoginRazor, StringComparison.Ordinal);
    Assert.Contains("forceLoad: true", RedirectToLoginRazor, StringComparison.Ordinal);
  }

  [Fact]
  public void AccessDenied_ExplainsTheRoleGapWithoutOfferingAnotherSignIn()
  {
    Assert.Contains("No tienes permiso", AccessDeniedRazor, StringComparison.Ordinal);
    // Offering a sign-in link here would walk the user back into the same loop.
    Assert.DoesNotContain("Account/Login", AccessDeniedRazor, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (File.Exists(candidate)) return File.ReadAllText(candidate);
      directory = directory.Parent;
    }

    throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}.");
  }
}
