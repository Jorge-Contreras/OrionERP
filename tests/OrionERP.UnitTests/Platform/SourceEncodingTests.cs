using System.Text.RegularExpressions;

namespace OrionERP.UnitTests.Platform;

/// <summary>
/// Un archivo re-guardado con la codificación equivocada convierte "transacción" en
/// "transacciÃ³n", y el usuario lo ve en cada mensaje de la pantalla. Así llegó con el
/// commit 551b7d3 a todos los avisos de Contabilidad/Transacciones.
/// </summary>
public sealed class SourceEncodingTests
{
  // Bytes UTF-8 de á é í ó ú ñ ¿ ¡ (y mayúsculas) leídos como Windows-1252: "Ã" o "Â"
  // seguidos de un byte de continuación, o "â€" de las comillas tipográficas.
  private static readonly Regex DoubleEncoded = new(
    "[ÃÂ][-¿ŒœŠšŸŽžƒˆ˜–—‘-„†-•…‰‹›€™]|â€",
    RegexOptions.CultureInvariant);

  private static readonly string[] Extensions = [".cs", ".razor", ".cshtml", ".js", ".css", ".sql", ".json", ".resx"];
  private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules"];

  // Este archivo repara a propósito textos mal codificados que llegan de la base.
  private static readonly string[] RepairsDoubleEncodingOnPurpose =
  [
    "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCashService.cs"
  ];

  [Fact]
  public void SourceFiles_DoNotCarryDoubleEncodedAccents()
  {
    var root = FindRepositoryRoot();
    var offenders = SourceFiles(Path.Combine(root, "src"))
      .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
      .Where(relative => !RepairsDoubleEncodingOnPurpose.Contains(relative, StringComparer.OrdinalIgnoreCase))
      .Select(relative => (relative, count: DoubleEncoded.Matches(File.ReadAllText(Path.Combine(root, relative))).Count))
      .Where(item => item.count > 0)
      .Select(item => $"{item.relative} ({item.count})")
      .ToList();

    Assert.True(offenders.Count == 0, "Acentos con doble codificación en: " + string.Join(", ", offenders));
  }

  private static IEnumerable<string> SourceFiles(string directory)
  {
    foreach (var file in Directory.EnumerateFiles(directory))
    {
      if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
          && !file.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
          && !file.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase))
        yield return file;
    }

    foreach (var child in Directory.EnumerateDirectories(directory))
    {
      if (SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;
      foreach (var file in SourceFiles(child)) yield return file;
    }
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
      directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
  }
}
