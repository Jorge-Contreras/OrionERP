using System.Collections;
using System.Reflection;
using OrionERP.Web.Shared;

namespace OrionERP.UnitTests.Shared;

public class NavMenuSearchTests
{
  /// <summary>
  /// El expediente de empleados vive en la seccion de Personas, no en la de
  /// administracion: esta ultima se dibuja completa dentro de un AuthorizeView de
  /// Administrador y ahi el rol CapitalHumanoAdmin nunca lo habria visto.
  /// </summary>
  [Fact]
  public void BuildVisibleSections_FindsCapitalHumanoForWorkforceAdminSearch()
  {
    var sections = InvokeBuildVisibleSections("capital humano", admin: true);

    Assert.Contains("/capital-humano", HrefsOf(sections));
  }

  [Fact]
  public void BuildVisibleSections_HidesCapitalHumanoFromPlainEmployeeSearch()
  {
    var sections = InvokeBuildVisibleSections("capital humano", admin: false);

    Assert.DoesNotContain("/capital-humano", HrefsOf(sections));
  }

  [Fact]
  public void BuildVisibleAdminItems_NoLongerCarriesCapitalHumano()
  {
    var items = InvokeBuildVisibleAdminItems("capital humano", isAdminUser: true);

    Assert.Empty(items);
  }

  [Fact]
  public void BuildVisibleAdminItems_HidesAdminItemsForNonAdminSearch()
  {
    var items = InvokeBuildVisibleAdminItems("capital humano", isAdminUser: false);

    Assert.Empty(items);
  }

  private static IReadOnlyList<string> HrefsOf(IEnumerable<object> sections)
  {
    var hrefs = new List<string>();
    foreach (var section in sections)
    {
      var items = section.GetType().GetProperty("Items", BindingFlags.Instance | BindingFlags.Public)?.GetValue(section);
      if (items is null) continue;
      foreach (var item in (IEnumerable)items)
      {
        var href = item.GetType().GetProperty("Href", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item)?.ToString();
        if (href is not null) hrefs.Add(href);
      }
    }
    return hrefs;
  }

  private static IReadOnlyList<object> InvokeBuildVisibleSections(string filter, bool admin)
  {
    var method = typeof(NavMenu).GetMethod(
      "BuildVisibleSections",
      BindingFlags.Static | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("NavMenu.BuildVisibleSections was not found.");

    var result = method.Invoke(null, [
      NavigationCatalog.Sections,
      filter,
      /* arrendadoresOnly */ false,
      /* featureEnabled  */ true,
      /* employee        */ false,
      /* supervisor      */ admin,
      /* admin           */ admin,
      /* payroll         */ false,
      /* financeReader   */ false,
      /* globalAdmin     */ admin])
      ?? throw new InvalidOperationException("NavMenu.BuildVisibleSections returned null.");

    return ((IEnumerable)result).Cast<object>().ToArray();
  }

  private static IReadOnlyList<object> InvokeBuildVisibleAdminItems(string filter, bool isAdminUser)
  {
    var method = typeof(NavMenu).GetMethod(
      "BuildVisibleAdminItems",
      BindingFlags.Static | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("NavMenu.BuildVisibleAdminItems was not found.");

    var result = method.Invoke(null, [filter, isAdminUser])
      ?? throw new InvalidOperationException("NavMenu.BuildVisibleAdminItems returned null.");

    return ((IEnumerable)result).Cast<object>().ToArray();
  }
}
