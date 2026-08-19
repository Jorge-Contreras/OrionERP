using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OrionERP.UnitTests.Capacitacion;

public sealed class TrainingCfdiFixtureParserSqlTests
{
  private const string FixtureSha256 =
    "6B5863304AA8E607EBE20A274A2AF84042EB7001906AB0C505E9B4AB2E71040B";

  private static readonly string ParserSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260818_orion_training_cfdi_fixture_parser.sql");
  private static readonly string ProcedureSql = ExtractProcedureDefinition(ParserSql);
  private static readonly string FixturePath = GetRepoFile(
    "src/OrionERP.Web/wwwroot/training/fixtures/cfdi-ficticio-no-timbrable.xml");

  [Fact]
  public void Installer_IsGuardedToTheExactTrainingResetAndAttestsLocalDependencies()
  {
    var catalogGuard = ParserSql.IndexOf(
      "DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training'",
      StringComparison.Ordinal);
    var sessionGuard = ParserSql.IndexOf(
      "SESSION_CONTEXT(N'OrionTrainingSanitizerApply')",
      StringComparison.Ordinal);
    var procedure = ParserSql.IndexOf(
      "CREATE OR ALTER PROCEDURE [cfdi].[PROCESAR_SAT_XML_V2]",
      StringComparison.Ordinal);

    Assert.InRange(catalogGuard, 0, sessionGuard - 1);
    Assert.InRange(sessionGuard, catalogGuard + 1, procedure - 1);
    Assert.DoesNotMatch(new Regex(@"(?im)^\s*GO\s*$"), ParserSql);
    Assert.Contains("DECLARE @TrainingParserDefinition nvarchar(max)", ParserSql, StringComparison.Ordinal);
    Assert.Contains("EXEC sys.sp_executesql @TrainingParserDefinition", ParserSql, StringComparison.Ordinal);
    Assert.Contains("@TransaccionID int = NULL", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("@AttachmentID int", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("execute_as_principal_id IS NOT NULL", ParserSql, StringComparison.Ordinal);
    Assert.Contains("referenced_server_name IS NOT NULL OR referenced_database_name IS NOT NULL", ParserSql, StringComparison.Ordinal);
    Assert.Contains("OrionERP.Training.CfdiFixtureParser.v1:", ParserSql, StringComparison.Ordinal);
  }

  [Fact]
  public void Parser_PinsThePublishedFixtureBytesAndEveryFictionalMarker()
  {
    var fixtureBytes = File.ReadAllBytes(FixturePath);
    var actualHash = Convert.ToHexString(SHA256.HashData(fixtureBytes));

    Assert.Equal(2934, fixtureBytes.Length);
    Assert.Equal(FixtureSha256, actualHash);
    Assert.Contains(FixtureSha256, ParserSql, StringComparison.Ordinal);
    Assert.Contains("DATALENGTH(@Attachment) <> 2934", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("HASHBYTES('SHA2_256', @Attachment)", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("urn:orionerp:training-only", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("@training:Ficticio", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("@training:NoValidoFiscal", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("XAXX010101000", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("NO-TIMBRABLE-001", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("00000000-0000-4000-8000-000000000001", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("NO_VALIDO_ENTRENAMIENTO", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("00000000000000000000", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("el ejercicio 1000/160/1160 no coincide", ProcedureSql, StringComparison.Ordinal);
  }

  [Fact]
  public void Parser_UsesXmlVariablesAndOnlyLocalReviewedEffects()
  {
    Assert.DoesNotContain(".nodes(", ProcedureSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotMatch(
      new Regex(@"(?i)(?<!@)\b[A-Za-z_]\w*\.(?:value|nodes|query|exist)\s*\("),
      ProcedureSql);

    string[] expectedLocalTables =
    [
      "dbo.TRANSACTION_ATTACHMENT", "dbo.Transacciones", "dbo.Transaccion_Comprobante",
      "cfdi.Comprobante", "cfdi.TimbreFiscalDigital", "cfdi.Emisor", "cfdi.Receptor",
      "cfdi.InformacionGlobal", "cfdi.Conceptos", "cfdi.Concepto", "cfdi.Impuestos",
      "cfdi.Traslados", "cfdi.Traslado"
    ];
    foreach (var table in expectedLocalTables)
      Assert.Contains(table, ProcedureSql, StringComparison.Ordinal);

    string[] forbiddenPrimitives =
    [
      "grupocarpio", "Orion_Sandbox", "timbralofacil", "Desktop-qga22ta",
      "OPENROWSET", "OPENDATASOURCE", "OPENQUERY", "BULK INSERT",
      "sp_send_dbmail", "xp_cmdshell", "sp_OA", "sp_execute_external_script",
      "EXECUTE AS", "CREATE SYNONYM"
    ];
    foreach (var primitive in forbiddenPrimitives)
      Assert.DoesNotContain(primitive, ProcedureSql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Parser_IsIdempotentAndLinksOnlyAnExplicitSyntheticTransaction()
  {
    Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("WHERE stampInfo.UUID = @Uuid", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("IF @ComprobanteId IS NULL", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("Estatus = 'TRAINING_NO_VALIDO'", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM cfdi.TimbreFiscalDigital", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("UPDATE cfdi.Comprobante", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("IF @TransaccionID IS NOT NULL", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("AND transactionInfo.RFC COLLATE Latin1_General_100_BIN2", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("INSERT dbo.Transaccion_Comprobante", ProcedureSql, StringComparison.Ordinal);
    Assert.Contains("SELECT @ComprobanteId AS Comprobante_ID", ProcedureSql, StringComparison.Ordinal);
  }

  private static string ExtractProcedureDefinition(string sql)
  {
    const string prefix = "DECLARE @TrainingParserDefinition nvarchar(max) = N'";
    const string suffix = "';\nEXEC sys.sp_executesql @TrainingParserDefinition;";
    var start = sql.IndexOf(prefix, StringComparison.Ordinal);
    Assert.True(start >= 0, "No se encontró el inicio de la definición dinámica.");
    start += prefix.Length;
    var end = sql.IndexOf(suffix, start, StringComparison.Ordinal);
    Assert.True(end > start, "No se encontró el final de la definición dinámica.");
    return sql[start..end].Replace("''", "'", StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(GetRepoFile(relativePath));

  private static string GetRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (File.Exists(candidate)) return candidate;
      directory = directory.Parent;
    }

    throw new FileNotFoundException($"No se encontró {relativePath} desde {AppContext.BaseDirectory}.");
  }
}
