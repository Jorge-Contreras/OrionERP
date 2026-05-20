using System.Collections;
using System.Reflection;
using OrionERP.Web.Shared;

namespace OrionERP.UnitTests.Shared;

public class NavMenuSearchTests
{
  [Fact]
  public void BuildVisibleAdminItems_FindsCapitalHumanoForAdminSearch()
  {
    var items = InvokeBuildVisibleAdminItems("capital humano", isAdminUser: true);

    var labels = items
      .Select(item => item.GetType().GetProperty("Label", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item)?.ToString())
      .ToArray();

    Assert.Contains("Capital Humano", labels);
  }

  [Fact]
  public void BuildVisibleAdminItems_HidesAdminItemsForNonAdminSearch()
  {
    var items = InvokeBuildVisibleAdminItems("capital humano", isAdminUser: false);

    Assert.Empty(items);
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
