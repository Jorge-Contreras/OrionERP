using System.Globalization;
using OrionERP.Application.Features.Logistica.Materials;

namespace OrionERP.Web.Features.Restaurante;

public sealed record RestaurantMaterialOption(
  int Id,
  string Code,
  string Description,
  string? Detail = null)
{
  private static readonly CompareInfo SearchComparer = CultureInfo.GetCultureInfo("es-MX").CompareInfo;
  private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

  public string DisplayText => string.IsNullOrWhiteSpace(Code)
    ? Description
    : $"{Code} · {Description}";

  public bool Matches(string searchText)
  {
    if (string.IsNullOrWhiteSpace(searchText))
    {
      return true;
    }

    var term = searchText.Trim();
    return Contains(Code, term)
      || Contains(Description, term)
      || Contains(Detail, term);
  }

  public static IReadOnlyList<RestaurantMaterialOption> FromMaterials(
    IEnumerable<MaterialListItemDto> materials)
    => materials
      .Select(material => new RestaurantMaterialOption(
        material.Id,
        material.MaterialCode,
        material.Description,
        JoinDetails(material.CategoryName, material.BaseUnitName)))
      .OrderBy(material => material.Description, StringComparer.CurrentCultureIgnoreCase)
      .ThenBy(material => material.Code, StringComparer.CurrentCultureIgnoreCase)
      .ToList();

  private static bool Contains(string? value, string term)
    => !string.IsNullOrWhiteSpace(value)
      && SearchComparer.IndexOf(value, term, SearchOptions) >= 0;

  private static string? JoinDetails(params string?[] values)
  {
    var details = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
    return details.Length == 0 ? null : string.Join(" · ", details);
  }
}
