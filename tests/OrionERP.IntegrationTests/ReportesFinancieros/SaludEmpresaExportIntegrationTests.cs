using System.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;
using OrionERP.Application.Common;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Infrastructure.Features.ReportesFinancieros.Dapper;
using OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;

namespace OrionERP.IntegrationTests.ReportesFinancieros;

public sealed class SaludEmpresaExportIntegrationTests
{
  [Fact]
  [Trait("Category", "ArtifactQa")]
  public async Task Exporters_CreateRealSandboxArtifactsForVisualQa()
  {
    if (!string.Equals(Environment.GetEnvironmentVariable("ORION_RUN_EXPORT_QA"), "1", StringComparison.Ordinal)) return;
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")!;
    var builder = new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" };
    var service = new ReportesFinancierosService(new SandboxConnectionFactory(builder.ConnectionString));
    var report = await service.GetSaludEmpresaAsync(new SaludEmpresaQuery(2026, 1, 2026, 8, "OHM191112Q26", new DateTime(2026, 8, 24)));
    var targets = await service.GetSaludEmpresaTargetsAsync("OHM191112Q26", new DateTime(2026, 1, 1), new DateTime(2027, 8, 1));
    var reconciliation = new List<SaludEmpresaReconciliationRow>();
    var page = 1;
    SaludEmpresaReconciliationPage result;
    do
    {
      result = await service.GetSaludEmpresaReconciliationAsync(new SaludEmpresaReconciliationQuery("OHM191112Q26", new DateTime(2026, 1, 1), new DateTime(2026, 8, 24), page, 100));
      reconciliation.AddRange(result.Items); page++;
    } while (page <= result.TotalPages);

    var model = new SaludEmpresaPdfDocumentModel("OHM191112Q26", new DateTime(2026, 1, 1), new DateTime(2026, 8, 31), DateTime.Now, report, targets, reconciliation);
    var pdf = new SaludEmpresaPdfService(new FakeEnvironment());
    var root = FindRepositoryRoot();
    var pdfDirectory = Directory.CreateDirectory(Path.Combine(root, "output", "pdf")).FullName;
    var xlsxDirectory = Directory.CreateDirectory(Path.Combine(root, "outputs", "salud-financiera-v2-qa")).FullName;
    await File.WriteAllBytesAsync(Path.Combine(pdfDirectory, "salud-financiera-interno-qa.pdf"), pdf.Generate(model));
    await File.WriteAllBytesAsync(Path.Combine(pdfDirectory, "salud-financiera-inversionistas-qa.pdf"), pdf.GenerateInvestor(model));
    await File.WriteAllBytesAsync(Path.Combine(xlsxDirectory, "salud-financiera-interno-qa.xlsx"), new SaludEmpresaExcelService().Generate(model));
  }

  private static string FindRepositoryRoot()
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln"))) current = current.Parent;
    return current?.FullName ?? throw new DirectoryNotFoundException("No se encontro OrionERP.sln.");
  }

  private sealed class SandboxConnectionFactory(string connectionString) : IDbConnectionFactory
  {
    public IDbConnection Create() => new SqlConnection(connectionString);
  }

  private sealed class FakeEnvironment : IWebHostEnvironment
  {
    public string ApplicationName { get; set; } = "OrionERP";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = Path.GetTempPath();
    public string EnvironmentName { get; set; } = "QA";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
  }
}
