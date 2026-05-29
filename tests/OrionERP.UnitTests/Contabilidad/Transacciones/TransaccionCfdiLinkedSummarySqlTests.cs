namespace OrionERP.UnitTests.Contabilidad.Transacciones;

public class TransaccionCfdiLinkedSummarySqlTests
{
  [Fact]
  public void LinkedCfdiSummary_UsesCfdiDateForComprobanteSummary()
  {
    var sql = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Contabilidad",
      "Transacciones",
      "Sql",
      "20260510_cfdi_transaccion_congruence.sql")
      .Replace("\r\n", "\n", StringComparison.Ordinal);

    Assert.Contains("c.Fecha AS CfdiFecha", sql, StringComparison.Ordinal);
    Assert.Contains("MAX(r.CfdiFecha) AS Fecha", sql, StringComparison.Ordinal);
    Assert.Contains("ORDER BY MAX(r.CfdiFecha) DESC, r.ComprobanteId DESC;", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("MAX(r.Fecha) AS Fecha,\n        MAX(r.Tipo) AS Tipo", sql, StringComparison.Ordinal);
  }

  private static string ReadRepositoryFile(params string[] paths)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
    {
      directory = directory.Parent;
    }

    if (directory is null)
    {
      throw new InvalidOperationException("Could not locate repository root.");
    }

    var fullPathSegments = new string[paths.Length + 1];
    fullPathSegments[0] = directory.FullName;
    Array.Copy(paths, 0, fullPathSegments, 1, paths.Length);

    return File.ReadAllText(Path.Combine(fullPathSegments));
  }
}
