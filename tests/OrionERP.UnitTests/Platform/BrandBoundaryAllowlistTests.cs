using System.Text.Json;
using System.Text.RegularExpressions;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class BrandBoundaryAllowlistTests
{
  [Fact]
  public void Neutral_runtime_has_no_unregistered_brand_references()
  {
    var root = FindRepositoryRoot();
    var document = JsonSerializer.Deserialize<AllowlistDocument>(
      RepoFile.Read("architecture/brand-boundary-allowlist.json"),
      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
      ?? throw new InvalidOperationException("La allowlist de marcas no se pudo leer.");
    Assert.Equal(1, document.Version);
    Assert.NotEmpty(document.ScanRoots);
    Assert.All(document.Entries, ValidateEntry);

    var brandPattern = new Regex(
      string.Join('|', document.Patterns.Select(Regex.Escape)),
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    var violations = new List<string>();

    foreach (var relativeRoot in document.ScanRoots)
    {
      var physicalRoot = Path.Combine(root, relativeRoot.Replace('/', Path.DirectorySeparatorChar));
      if (!Directory.Exists(physicalRoot))
        continue;

      foreach (var file in Directory.EnumerateFiles(physicalRoot, "*", SearchOption.AllDirectories)
        .Where(IsVersionedSourceCandidate))
      {
        var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (brandPattern.IsMatch(relativePath) && !IsAllowed(relativePath, relativePath, document.Entries))
          violations.Add($"{relativePath}: branded path");

        var lines = File.ReadAllLines(file);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
          var line = lines[lineIndex];
          if (brandPattern.IsMatch(line) && !IsAllowed(relativePath, line, document.Entries))
            violations.Add($"{relativePath}:{lineIndex + 1}: {brandPattern.Match(line).Value}");
        }
      }
    }

    Assert.True(violations.Count == 0,
      "Referencias de marca no registradas:\n" + string.Join("\n", violations.Distinct().Order()));
  }

  private static bool IsAllowed(string relativePath, string value, IEnumerable<AllowlistEntry> entries)
    => entries.Any(entry =>
      Regex.IsMatch(relativePath, entry.Path, RegexOptions.CultureInvariant) &&
      Regex.IsMatch(value, entry.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

  private static bool IsVersionedSourceCandidate(string path)
  {
    var normalized = path.Replace('\\', '/');
    if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
        normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
      return false;
    return Path.GetExtension(path) is ".cs" or ".cshtml" or ".razor" or ".json" or ".sql" or ".xml" or ".props" or ".targets" or ".csproj" or ".svg";
  }

  private static void ValidateEntry(AllowlistEntry entry)
  {
    Assert.False(string.IsNullOrWhiteSpace(entry.Path));
    Assert.False(string.IsNullOrWhiteSpace(entry.Pattern));
    Assert.InRange(entry.Category, 1, 5);
    Assert.False(string.IsNullOrWhiteSpace(entry.Owner));
    Assert.False(string.IsNullOrWhiteSpace(entry.Justification));
    if (entry.RemoveAfter is not null)
      Assert.True(DateOnly.TryParse(entry.RemoveAfter, out _), $"removeAfter inválido para {entry.Path}");
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
      directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
  }

  private sealed class AllowlistDocument
  {
    public int Version { get; set; }
    public List<string> ScanRoots { get; set; } = [];
    public List<string> Patterns { get; set; } = [];
    public List<AllowlistEntry> Entries { get; set; } = [];
  }

  private sealed class AllowlistEntry
  {
    public string Path { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public int Category { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;
    public string? RemoveAfter { get; set; }
  }
}
