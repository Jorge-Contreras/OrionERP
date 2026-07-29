using System.Security.Claims;
using OrionERP.Web.Shared;

namespace OrionERP.UnitTests.Shared;

public class NavigationCatalogTests
{
    private static readonly IReadOnlyList<NavigationDestination> StandardDestinations =
        NavigationCatalog.GetDestinations(includeAdmin: false, arrendadoresOnly: false);

    [Fact]
    public void Search_IsAccentInsensitiveAndUnderstandsTaskKeywords()
    {
        var results = NavigationCatalog.Search(StandardDestinations, "ordenes tactil");

        Assert.NotEmpty(results);
        Assert.Equal("/restaurante/pos", results[0].Entry.Href);
    }

    [Fact]
    public void Search_RanksExactLabelAheadOfDescriptionMatches()
    {
        var results = NavigationCatalog.Search(StandardDestinations, "materiales");

        Assert.NotEmpty(results);
        Assert.Equal("Materiales", results[0].Entry.Label);
    }

    [Fact]
    public void GetDestinations_DoesNotExposeAdminPagesToStandardUsers()
    {
        var standardPaths = StandardDestinations.Select(item => item.Entry.Href);
        var adminPaths = NavigationCatalog
            .GetDestinations(includeAdmin: true, arrendadoresOnly: false)
            .Select(item => item.Entry.Href);

        Assert.DoesNotContain("/admin/seguridad", standardPaths);
        Assert.Contains("/admin/seguridad", adminPaths);
    }

    [Fact]
    public void GetDestinations_LimitsArrendadoresOnlyUsersToTheirTwoWorkflows()
    {
        var destinations = NavigationCatalog
            .GetDestinations(includeAdmin: false, arrendadoresOnly: true)
            .Select(item => NavigationCatalog.NormalizePath(item.Entry.Href))
            .ToArray();

        Assert.Equal(2, destinations.Length);
        Assert.Contains("arrendadores", destinations);
        Assert.Contains("reservaciones/calendario", destinations);
    }

    [Fact]
    public void IsArrendadoresOnly_RecognizesPrivilegedAndRestrictedProfiles()
    {
        var restricted = BuildUser("Arrendadores");
        var privileged = BuildUser("Arrendadores", "Administrador");

        Assert.True(NavigationCatalog.IsArrendadoresOnly(restricted));
        Assert.False(NavigationCatalog.IsArrendadoresOnly(privileged));
    }

    [Fact]
    public void FindByPath_PrefersTheMostSpecificCatalogDestination()
    {
        var result = NavigationCatalog.FindByPath(
            StandardDestinations,
            "ordenes-trabajo/plantillas?vista=activas");

        Assert.NotNull(result);
        Assert.Equal("/ordenes-trabajo/plantillas", result.Entry.Href);
    }

    private static ClaimsPrincipal BuildUser(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "test@orionerp.local")
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
