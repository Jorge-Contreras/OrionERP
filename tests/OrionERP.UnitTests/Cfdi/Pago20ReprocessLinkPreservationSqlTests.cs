namespace OrionERP.UnitTests.Cfdi;

public sealed class Pago20ReprocessLinkPreservationSqlTests
{
  private readonly string _sql = Normalize(ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Cfdi",
      "CargarXmlSat",
      "Sql",
      "20260905_pago20_preserve_poliza_links_on_reprocess.sql"));

  [Fact]
  public void Reprocess_SnapshotsPolizaLinksBeforeCleaningPago20Children()
  {
    var snapshotIndex = _sql.IndexOf("INSERT INTO #Pago20LinksToRestore", StringComparison.Ordinal);
    var deleteIndex = _sql.IndexOf("DELETE td\n                FROM dbo.Transaccion_DoctoRelacionado", StringComparison.Ordinal);

    Assert.True(snapshotIndex > 0, "El respaldo de vínculos debe existir.");
    Assert.True(deleteIndex > 0, "El borrado de vínculos debe seguir existiendo.");
    Assert.True(snapshotIndex < deleteIndex, "El respaldo debe ocurrir antes del borrado.");
  }

  [Fact]
  public void Reprocess_RestoresLinksOntoTheRebuiltDocumentIds()
  {
    var restoreIndex = _sql.IndexOf("INSERT INTO dbo.Transaccion_DoctoRelacionado", StringComparison.Ordinal);
    var rebuildIndex = _sql.IndexOf("INSERT INTO cfdi.Pagos20_DoctoRelacionado", StringComparison.Ordinal);

    Assert.True(rebuildIndex > 0, "La reinserción del complemento debe existir.");
    Assert.True(restoreIndex > rebuildIndex, "Los vínculos se reponen después de reconstruir el complemento.");
  }

  [Fact]
  public void Reprocess_MatchesDocumentsByBusinessKeyNotIdentity()
  {
    Assert.Contains("PARTITION BY pp.FechaPago, dr.IdDocumento, dr.NumParcialidad", _sql, StringComparison.Ordinal);
    Assert.Contains("newDocs.IdDocumento = link.IdDocumento", _sql, StringComparison.Ordinal);
    Assert.Contains("newDocs.FechaPago = link.FechaPago", _sql, StringComparison.Ordinal);
    Assert.Contains("ISNULL(newDocs.NumParcialidad, -1) = ISNULL(link.NumParcialidad, -1)", _sql, StringComparison.Ordinal);
    Assert.Contains("newDocs.KeyOrdinal = link.KeyOrdinal", _sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Reprocess_PreservesTheOriginalAllocationAmount()
  {
    Assert.Contains("link.Transaccion_ID,\n                        newDocs.DoctoRelacionado_Id,\n                        link.Monto", _sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Reprocess_AuditsLinksTheNewXmlCanNoLongerCarry()
  {
    Assert.Contains("cfdi.Pago20_Link_Reprocess_Audit", _sql, StringComparison.Ordinal);
    Assert.Contains("WHERE link.Restored = 0;", _sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Reprocess_KeepsTheRestoreBufferAvailableForBothComprobanteBranches()
  {
    var createIndex = _sql.IndexOf("CREATE TABLE #Pago20LinksToRestore", StringComparison.Ordinal);
    var branchIndex = _sql.IndexOf("IF @ComprobanteID IS NULL", StringComparison.Ordinal);

    Assert.True(createIndex > 0, "La tabla temporal debe crearse en el procedimiento.");
    Assert.True(createIndex < branchIndex, "Debe crearse antes del upsert para que exista en ambas ramas.");
  }

  private static string Normalize(string value)
      => value.Replace("\r\n", "\n", StringComparison.Ordinal);

  private static string ReadRepositoryFile(params string[] paths)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
      directory = directory.Parent;

    if (directory is null)
      throw new InvalidOperationException("Could not locate repository root.");

    return File.ReadAllText(Path.Combine([directory.FullName, .. paths]));
  }
}
