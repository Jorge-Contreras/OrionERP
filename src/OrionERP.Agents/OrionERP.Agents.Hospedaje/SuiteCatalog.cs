namespace OrionERP.Agents.Hospedaje;

public static class SuiteCatalog
{
  // Canonical suite codes that match your GET_FULL_CALENDAR columns
  public static readonly string[] SuiteCodes =
  [
      "BERLIN", "LONDON", "MANHATTAN", "MOSCU", "PARIS", "PENTHOUSE", "SEUL"
  ];

  // Optional: normalize common human names to canonical codes
  public static string? NormalizeSuite(string? input)
  {
    if (string.IsNullOrWhiteSpace(input)) return null;

    var s = input.Trim().ToUpperInvariant();

    // direct match
    if (SuiteCodes.Contains(s)) return s;

    // common names
    return s switch
    {
      "CASA BERLIN" or "CASA BERLÍN" or "BERLÍN" => "BERLIN",
      "CASA LONDON" => "LONDON",
      "SUITE SEOUL" or "SEÚL" => "SEUL",
      _ => null
    };
  }
}
