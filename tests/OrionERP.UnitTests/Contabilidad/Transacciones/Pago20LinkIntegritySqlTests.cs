namespace OrionERP.UnitTests.Contabilidad.Transacciones;

public sealed class Pago20LinkIntegritySqlTests
{
  private readonly string _sql = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Contabilidad",
      "Transacciones",
      "Sql",
      "20260808_pago20_link_integrity.sql");
  private readonly string _service = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Contabilidad",
      "Transacciones",
      "Services",
      "TransaccionService.cs");

  [Fact]
  public void Migration_PreservesLegacyRowsUnlessThereIsExactlyOneDocument()
  {
    Assert.Contains("singleDocument.DocumentCount = 1", _sql, StringComparison.Ordinal);
    Assert.Contains("Pago20_Link_Migration_Audit", _sql, StringComparison.Ordinal);
    Assert.Contains("OriginalMonto", _sql, StringComparison.Ordinal);
    Assert.Contains("RequiresAmountReview", _sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Summary_KeysPago20RowsByExactDocument()
  {
    Assert.Contains("td.Transaccion_ID=@Transaccion_ID", _sql, StringComparison.Ordinal);
    Assert.Contains("docs.DoctoRelacionadoId", _sql, StringComparison.Ordinal);
    Assert.Contains("linked.DoctoRelacionado_Id=docs.DoctoRelacionadoId", _sql, StringComparison.Ordinal);
    Assert.DoesNotContain("SELECT DISTINCT ComprobanteId\n    INTO #PaymentIds", _sql.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_AddsPago20TemplateContextAndAmountSources()
  {
    Assert.Contains("PAGO20_RECIBIDO", _sql, StringComparison.Ordinal);
    Assert.Contains("PAGO20_EMITIDO", _sql, StringComparison.Ordinal);
    Assert.Contains("PAGO20_TOTAL_ASIGNADO", _sql, StringComparison.Ordinal);
    Assert.Contains("PAGO20_RETENCION_IEPS", _sql, StringComparison.Ordinal);
    Assert.Contains("MissingAccountKeys", _sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Pago20Unlink_DeletesOnlyTheExactTransactionDocumentPair()
  {
    Assert.Contains("DELETE FROM dbo.Transaccion_DoctoRelacionado", _service, StringComparison.Ordinal);
    Assert.Contains(
        "WHERE Transaccion_ID = @TransaccionId\n  AND DoctoRelacionado_ID = @DoctoRelacionadoId;",
        _service.Replace("\r\n", "\n", StringComparison.Ordinal),
        StringComparison.Ordinal);
  }

  [Fact]
  public void Pago20Writes_AreSerializableAndExcludeTheCurrentPairWhenUpdating()
  {
    Assert.Contains("BeginTransactionAsync(IsolationLevel.Serializable", _service, StringComparison.Ordinal);
    Assert.Contains("DocumentAssignedOther", _service, StringComparison.Ordinal);
    Assert.Contains("TransaccionAssignedOther", _service, StringComparison.Ordinal);
    Assert.Contains("updateExisting != state.CurrentLinkExists", _service, StringComparison.Ordinal);
  }

  [Fact]
  public void Pago20Writes_EnforceCurrencyCategoryAndBothAllocationLimits()
  {
    Assert.Contains("!IsMxn(context.MonedaP) || !IsMxn(context.MonedaDr)", _service, StringComparison.Ordinal);
    Assert.Contains("state.HasDirectCfdiLinks", _service, StringComparison.Ordinal);
    Assert.Contains("context.ImpPagado - state.DocumentAssignedOther", _service, StringComparison.Ordinal);
    Assert.Contains("context.TransaccionTotal - state.TransaccionAssignedOther", _service, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_BlocksNewDirectTypePLinks()
  {
    Assert.Contains("TR_Transaccion_Comprobante_BlockPago20Direct", _sql, StringComparison.Ordinal);
    Assert.Contains("Los CFDI tipo P deben ligarse mediante Transaccion_DoctoRelacionado", _sql, StringComparison.Ordinal);
  }

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
