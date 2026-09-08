using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;
using OrionERP.Infrastructure.Features.Cfdi.HtmlCFDI;

namespace OrionERP.IntegrationTests.Cfdi;

public sealed class TransactionAttachmentScopeTests
{
  [Fact]
  public async Task MissingCompany_IsRejectedBeforeOpeningSql()
  {
    var company = new TestCompany(null);
    var repository = Repository("Server=unavailable.invalid;Database=Orion_Sandbox;Integrated Security=true;Connect Timeout=1", company);

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.GetAttachmentAsync(1));
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task CompanyAttachments_AndCanonicalSharedCfdi_RespectOwnershipInSql()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Missing Sandbox connection")) { InitialCatalog = "Orion_Sandbox" };
    Assert.Equal("Orion_Sandbox", builder.InitialCatalog, ignoreCase: true);
    await using var observer = new SqlConnection(builder.ConnectionString);
    await observer.OpenAsync();
    Assert.Equal("Orion_Sandbox", await observer.ExecuteScalarAsync<string>("SELECT DB_NAME();"), ignoreCase: true);

    var companies = (await observer.QueryAsync<string>("""
      SELECT Rfc FROM orion.Company WHERE Rfc IN ('OHM191112Q26', 'BSU210121M77');
      """)).ToArray();
    Assert.Equal(2, companies.Length);
    var companyA = new TestCompany("OHM191112Q26");
    var companyB = new TestCompany("BSU210121M77");
    var repositoryA = Repository(builder.ConnectionString, companyA);
    var repositoryB = Repository(builder.ConnectionString, companyB);
    var unrelated = Repository(builder.ConnectionString, new TestCompany("TST010101AAA"));
    var marker = "attachment-scope-" + Guid.NewGuid().ToString("N");
    var content = Encoding.UTF8.GetBytes(marker);
    var transactionIds = new List<int>();
    var attachmentIds = new List<int>();
    var comprobanteIds = new List<int>();
    try
    {
      var transactionA = await CreateTransactionAsync(companyA.RequireRfc());
      var transactionB = await CreateTransactionAsync(companyB.RequireRfc());
      var privateA = await CreateAttachmentAsync(transactionA);
      var privateB = await CreateAttachmentAsync(transactionB);
      var canonical = await CreateAttachmentAsync(null);
      var unassigned = await CreateAttachmentAsync(null);
      await CreateSharedCfdiAsync(canonical);
      // The CFDI association must not override the attachment's explicit transaction owner.
      await CreateSharedCfdiAsync(privateA);

      Assert.Equal(content, (await repositoryA.GetAttachmentAsync(privateA))?.Content);
      Assert.Equal(content, (await repositoryB.GetAttachmentAsync(privateB))?.Content);
      Assert.Null(await repositoryA.GetAttachmentAsync(privateB));
      Assert.Null(await repositoryB.GetAttachmentAsync(privateA));
      Assert.Equal(content, (await repositoryA.GetAttachmentAsync(canonical))?.Content);
      Assert.Equal(content, (await repositoryB.GetAttachmentAsync(canonical))?.Content);
      Assert.Null(await unrelated.GetAttachmentAsync(canonical));
      Assert.Null(await unrelated.GetAttachmentAsync(privateA));
      Assert.Null(await repositoryA.GetAttachmentAsync(unassigned));
      Assert.Null(await repositoryB.GetAttachmentAsync(unassigned));
      Assert.Null(await repositoryA.GetAttachmentAsync(-1));
      await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
        Repository(builder.ConnectionString, new TestCompany(null)).GetAttachmentAsync(canonical));

      // Revalidate on each operation, including when a repository outlives session loss.
      companyA.Rfc = null;
      await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repositoryA.GetAttachmentAsync(privateA));
    }
    finally
    {
      // Cleanup is restricted to identities created by this run; existing rows are untouched.
      await observer.ExecuteAsync("""
        DELETE FROM cfdi.Emisor WHERE Comprobante_ID IN @ComprobanteIds;
        DELETE FROM cfdi.Receptor WHERE Comprobante_ID IN @ComprobanteIds;
        DELETE FROM cfdi.Comprobante WHERE Comprobante_Id IN @ComprobanteIds;
        DELETE FROM dbo.TRANSACTION_ATTACHMENT WHERE ID IN @AttachmentIds AND AttachmentName=@Marker;
        DELETE FROM dbo.Transacciones WHERE ID IN @TransactionIds AND Concepto=@Marker;
        """, new { ComprobanteIds = comprobanteIds, AttachmentIds = attachmentIds, TransactionIds = transactionIds, Marker = marker });
    }

    async Task<int> CreateTransactionAsync(string rfc)
    {
      var id = await observer.ExecuteScalarAsync<int>("""
        INSERT dbo.Transacciones (Concepto, Monto, RFC)
        VALUES (@Marker, 1, @Rfc);
        SELECT CONVERT(int, SCOPE_IDENTITY());
        """, new { Marker = marker, Rfc = rfc });
      transactionIds.Add(id);
      return id;
    }

    async Task<int> CreateAttachmentAsync(int? transactionId)
    {
      var id = await observer.ExecuteScalarAsync<int>("""
        INSERT dbo.TRANSACTION_ATTACHMENT
          (TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
        VALUES (@TransactionId, @Content, @Marker, 'xml', @Marker);
        SELECT CONVERT(int, SCOPE_IDENTITY());
        """, new { TransactionId = transactionId, Content = content, Marker = marker });
      attachmentIds.Add(id);
      return id;
    }

    async Task CreateSharedCfdiAsync(int attachmentId)
    {
      var id = await observer.ExecuteScalarAsync<int>("""
        INSERT cfdi.Comprobante (Fecha, SubTotal, Total, XML_Attachment_ID)
        VALUES (GETDATE(), 1, 1, @AttachmentId);
        SELECT CONVERT(int, SCOPE_IDENTITY());
        """, new { AttachmentId = attachmentId });
      comprobanteIds.Add(id);
      await observer.ExecuteAsync("""
        INSERT cfdi.Emisor (Rfc, Nombre, Comprobante_ID) VALUES (@RfcA, @Marker, @Id);
        INSERT cfdi.Receptor (Rfc, Nombre, Comprobante_ID) VALUES (@RfcB, @Marker, @Id);
        """, new { RfcA = companyA.RequireRfc(), RfcB = companyB.RequireRfc(), Marker = marker, Id = id });
    }
  }

  private static TransactionAttachmentRepository Repository(string connectionString, TestCompany company)
  {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["ConnectionStrings:OrionDb"] = connectionString
    }).Build();
    return new TransactionAttachmentRepository(new SqlConnectionFactory(configuration, company), company);
  }

  private sealed class TestCompany(string? rfc) : ICurrentCompanyContext
  {
    public string? Rfc { get; set; } = rfc;
    public string? CurrentRfc => Rfc;
    public string? DisplayName => Rfc;
    public int? EmployeeId => null;
    public string RequireRfc() => Rfc ?? throw new UnauthorizedAccessException("Missing authenticated company.");
    public void EnsureRfc(string requestedRfc)
    {
      if (!string.Equals(RequireRfc(), requestedRfc, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("Company mismatch.");
    }
  }
}
