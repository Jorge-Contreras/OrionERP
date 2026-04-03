using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community;

var outputDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "output", "pdf"));
Directory.CreateDirectory(outputDir);
var outputPath = Path.Combine(outputDir, "orionerp-app-summary-one-page.pdf");

Document.Create(container =>
{
    container.Page(page =>
    {
        page.Size(PageSizes.Letter);
        page.Margin(28);
        page.DefaultTextStyle(x => x.FontSize(9));

        page.Content().Column(column =>
        {
            column.Spacing(4);

            column.Item().Text("OrionERP App Summary").FontSize(16).Bold();
            column.Item().Text("Evidence basis: OrionERP.sln, OrionERP.Web Program/DI, feature pages, and CI/global.json").FontSize(8).FontColor(Colors.Grey.Darken1);

            column.Item().PaddingTop(2).Element(SectionTitle).Text("What it is");
            column.Item().Text(
                "OrionERP is a modular monolith ERP built on ASP.NET Core and Blazor Server with SQL Server. " +
                "Current implemented modules focus on CFDI/SAT workflows, accounting operations, and financial reporting.");

            column.Item().PaddingTop(2).Element(SectionTitle).Text("Who it is for");
            column.Item().Text("Primary persona: accounting and SAT operations users at Bonhomia Suites, plus administrators.");

            column.Item().PaddingTop(2).Element(SectionTitle).Text("What it does");
            AddBullet(column, "Registers and switches RFC context for multi-RFC operations.");
            AddBullet(column, "Handles SAT massive-download lifecycle: request, verify, download, and package processing.");
            AddBullet(column, "Loads and processes SAT XML files through an inbox pipeline.");
            AddBullet(column, "Manages accounting transactions (polizas), including CFDI linking and attachments.");
            AddBullet(column, "Provides banking/accounting workflows under Contabilidad.");
            AddBullet(column, "Shows financial reports: Hoja de Trabajo, Balanza de Comprobacion, and Estado de Perdidas y Ganancias.");

            column.Item().PaddingTop(2).Element(SectionTitle).Text("How it works (repo-evidenced architecture)");
            AddBullet(column, "UI layer: Blazor Server pages/components in OrionERP.Web with role-based authorization.");
            AddBullet(column, "Application layer: OrionERP.Application contracts and DTOs.");
            AddBullet(column, "Infrastructure layer: OrionERP.Infrastructure services implemented with Dapper, EF Core, and stored procedures.");
            AddBullet(column, "External integration: Sat.MassiveDownload SOAP client for SAT auth/request/verify/download endpoints.");
            AddBullet(column, "Data flow: page action -> interface/service via DI -> SQL Server and/or SAT API -> rendered result.");
            AddBullet(column, "Security: ASP.NET Identity cookie auth, RFC-aware authorization handler, and DataProtection keys in App_Data.");

            column.Item().PaddingTop(2).Element(SectionTitle).Text("How to run (minimal)");
            AddBullet(column, "Install .NET SDK 10.0.100 (global.json).");
            AddBullet(column, "Set ConnectionStrings:OrionDb in src/OrionERP.Web/appsettings.json.");
            AddBullet(column, "Run: dotnet restore OrionERP.sln");
            AddBullet(column, "Build: dotnet build OrionERP.sln -c Debug");
            AddBullet(column, "Start web app: dotnet run --project src/OrionERP.Web");
            AddBullet(column, "Open Development URL from launchSettings.json: https://localhost:7089 or http://localhost:5221");

            column.Item().PaddingTop(2).Text("Not found in repo: documented local DB provisioning/migration steps and default seed credentials.")
                .Italic().FontSize(8).FontColor(Colors.Grey.Darken2);
        });
    });
}).GeneratePdf(outputPath);

Console.WriteLine(outputPath);
return;

static IContainer SectionTitle(IContainer container)
{
    return container.PaddingBottom(1).DefaultTextStyle(x => x.FontSize(10).Bold());
}

static void AddBullet(ColumnDescriptor column, string text)
{
    column.Item().Row(row =>
    {
        row.ConstantItem(10).Text("-").FontSize(9);
        row.RelativeItem().Text(text).FontSize(9);
    });
}
