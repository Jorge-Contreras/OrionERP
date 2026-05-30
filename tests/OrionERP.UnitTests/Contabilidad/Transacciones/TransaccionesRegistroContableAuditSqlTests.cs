namespace OrionERP.UnitTests.Contabilidad.Transacciones;

public class TransaccionesRegistroContableAuditSqlTests
{
  [Fact]
  public void AuditScript_CreatesAuditTableTriggersAndRecentChangesView()
  {
    var sql = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Contabilidad",
      "Transacciones",
      "Sql",
      "20260530_transacciones_registro_contable_audit.sql")
      .Replace("\r\n", "\n", StringComparison.Ordinal);

    Assert.Contains("contabilidad.TransaccionesRegistroContableAudit", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE OR ALTER TRIGGER dbo.trg_Transacciones_Audit", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE OR ALTER TRIGGER dbo.trg_Registro_Contable_Audit", sql, StringComparison.Ordinal);
    Assert.Contains("SESSION_CONTEXT(N'OrionERP.UserName')", sql, StringComparison.Ordinal);
    Assert.Contains("N'dbo.Transacciones'", sql, StringComparison.Ordinal);
    Assert.Contains("N'dbo.Registro_Contable'", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE OR ALTER VIEW contabilidad.TransaccionesRegistroContableRecentChanges", sql, StringComparison.Ordinal);
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
