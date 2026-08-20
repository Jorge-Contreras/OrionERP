using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Globalization;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.Features.Logistica.Purchasing;
using OrionERP.Web.State;

namespace OrionERP.UnitTests.Logistica;

public class PurchaseOrderWebSupportTests
{
  private static readonly byte[] TinyPngBytes = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WnZ8iYAAAAASUVORK5CYII=");

  [Fact]
  public async Task ThumbnailHydrator_ReturnsMaterialIdToDataUrlMap()
  {
    var materialService = new FakeMaterialService
    {
      Thumbnails =
      [
        new LogisticsBinaryContent
        {
          Id = 9,
          FileName = "agua-thumb.png",
          ContentType = "image/png",
          Bytes = TinyPngBytes
        }
      ]
    };

    var hydrator = new PurchaseMaterialThumbnailHydrator(materialService, new FakeRfcState());

    var result = await hydrator.GetDataUrlsAsync([9, 9, 15]);

    Assert.Single(result);
    Assert.True(result.ContainsKey(9));
    Assert.StartsWith("data:image/png;base64,", result[9], StringComparison.Ordinal);
    Assert.Equal([9, 15], materialService.LastThumbnailIds);
  }

  [Fact]
  public async Task PurchaseOrderPdfFactoryAndService_GeneratePdfWithThumbnail()
  {
    var materialService = new FakeMaterialService
    {
      Thumbnails =
      [
        new LogisticsBinaryContent
        {
          Id = 9,
          FileName = "agua-thumb.png",
          ContentType = "image/png",
          Bytes = TinyPngBytes
        }
      ]
    };

    var factory = new PurchaseOrderPdfDocumentFactory(materialService, new FakeRfcState());
    var pdfService = new PurchaseOrderPdfService(new FakeWebHostEnvironment());

    var model = await factory.CreateFromDetailAsync(new PurchaseOrderDetailDto
    {
      Id = 40,
      PurchaseOrderCode = "PO-000040",
      BusinessPartnerId = 7,
      VendorName = "Bodega Aurrera",
      VendorRfc = "XAXX010101000",
      Status = PurchaseOrderStatuses.Issued,
      OrderDate = new DateTime(2026, 4, 17),
      ExpectedDate = new DateTime(2026, 4, 18),
      Notes = "Reabasto de minibares",
      OrderedQuantity = 48m,
      ReceivedQuantity = 0m,
      RemainingQuantity = 48m,
      CreatedBy = "Ana",
      Lines =
      [
        new PurchaseOrderLineDto
        {
          Id = 21,
          MaterialId = 9,
          MaterialCode = "MAT-000009",
          MaterialDescription = "Agua",
          VendorCode = "AA-001",
          BaseUnitName = "Pieza",
          PurchaseQuantity = 24m,
          PurchaseUnitName = "Paquete",
          BaseUnitPrice = 2.82m,
          OrderedQuantity = 48m,
          ReceivedQuantity = 0m,
          RemainingQuantity = 48m,
          Allocations =
          [
            new PurchaseOrderAllocationDto
            {
              Id = 11,
              PurchaseOrderLineId = 21,
              LocationId = 5,
              LocationName = "Minibar 101",
              LocationCode = "LOC-000005",
              PlannedQuantity = 48m,
              ReceivedQuantity = 0m,
              RemainingQuantity = 48m
            }
          ]
        }
      ]
    });

    Assert.Single(model.Lines);
    Assert.NotNull(model.Lines[0].ThumbnailBytes);
    Assert.Single(model.Allocations);
    Assert.NotNull(model.Allocations[0].ThumbnailBytes);
    Assert.Equal("1", model.MaterialCount);
    Assert.Equal("1", model.AllocationCount);
    Assert.Equal("1", model.PendingAllocationCount);
    Assert.Equal("Paquete", model.Lines[0].UnitName);
    Assert.Equal(PurchaseQuantityDisplay.FormatBaseUnitPrice(2.82m, "Pieza", CultureInfo.CurrentCulture), model.Lines[0].BaseUnitPrice);
    Assert.Equal(PurchaseQuantityDisplay.FormatQuantity(48m, 24m, "Pieza", "Paquete", CultureInfo.CurrentCulture), model.Lines[0].OrderedQuantity);
    Assert.Equal(PurchaseQuantityDisplay.FormatQuantity(48m, 24m, "Pieza", "Paquete", CultureInfo.CurrentCulture), model.Allocations[0].PlannedQuantity);

    var pdfBytes = pdfService.Generate(model);

    Assert.NotEmpty(pdfBytes);
    Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 4), StringComparison.Ordinal);
  }

  [Fact]
  public async Task PurchaseOrderPdfFactory_SortsAllocationsByLocationThenMaterial()
  {
    var factory = new PurchaseOrderPdfDocumentFactory(new FakeMaterialService(), new FakeRfcState());

    var model = await factory.CreateFromDetailAsync(new PurchaseOrderDetailDto
    {
      Id = 41,
      PurchaseOrderCode = "PO-000041",
      BusinessPartnerId = 8,
      VendorName = "Proveedor",
      VendorRfc = "XAXX010101000",
      Status = PurchaseOrderStatuses.Issued,
      OrderDate = new DateTime(2026, 4, 20),
      ExpectedDate = new DateTime(2026, 4, 21),
      CreatedBy = "Ana",
      Lines =
      [
        new PurchaseOrderLineDto
        {
          Id = 30,
          MaterialId = 30,
          MaterialCode = "MAT-B",
          MaterialDescription = "Botella",
          BaseUnitName = "Pieza",
          PurchaseQuantity = 1m,
          PurchaseUnitName = "Pieza",
          Allocations =
          [
            new PurchaseOrderAllocationDto
            {
              Id = 100,
              PurchaseOrderLineId = 30,
              LocationId = 7,
              LocationName = "Cocina",
              LocationCode = "LOC-007",
              PlannedQuantity = 3m,
              RemainingQuantity = 3m
            },
            new PurchaseOrderAllocationDto
            {
              Id = 101,
              PurchaseOrderLineId = 30,
              LocationId = 9,
              LocationName = "Bar",
              LocationCode = "LOC-009",
              PlannedQuantity = 1m,
              RemainingQuantity = 1m
            }
          ]
        },
        new PurchaseOrderLineDto
        {
          Id = 31,
          MaterialId = 31,
          MaterialCode = "MAT-A",
          MaterialDescription = "Agua",
          BaseUnitName = "Pieza",
          PurchaseQuantity = 1m,
          PurchaseUnitName = "Pieza",
          Allocations =
          [
            new PurchaseOrderAllocationDto
            {
              Id = 102,
              PurchaseOrderLineId = 31,
              LocationId = 7,
              LocationName = "Cocina",
              LocationCode = "LOC-007",
              PlannedQuantity = 2m,
              RemainingQuantity = 2m
            }
          ]
        }
      ]
    });

    Assert.Equal(
      [
        ("LOC-007 · Cocina", "MAT-A"),
        ("LOC-007 · Cocina", "MAT-B"),
        ("LOC-009 · Bar", "MAT-B")
      ],
      model.Allocations.Select(item => (item.LocationName, item.MaterialCode)).ToArray());
  }

  [Fact]
  public void PurchaseQuantityDisplay_FormatsPurchaseUnitsAndInternalSummary()
  {
    var culture = CultureInfo.GetCultureInfo("en-US");

    var formatted = PurchaseQuantityDisplay.FormatQuantity(3m, 3m, "Pieza", "Paquete", culture);
    var summary = PurchaseQuantityDisplay.BuildPresentationSummary("Pieza", 3m, "Paquete", culture);
    var baseQuantity = PurchaseQuantityDisplay.ToBaseQuantity(2m, 3m, "Paquete");
    var basePrice = PurchaseQuantityDisplay.FormatBaseUnitPrice(0.054267m, "Gramo", culture);
    var presentationPrice = PurchaseQuantityDisplay.BuildPurchasePresentationPriceEquivalent(0.054267m, 2027m, "Bolsa", culture);

    Assert.Equal("1.00 Paquete", formatted);
    Assert.Equal("Internamente: 1 Paquete = 3.00 Pieza", summary);
    Assert.Equal(6m, baseQuantity);
    Assert.Equal("$0.054267 / Gramo", basePrice);
    Assert.Equal("Equivale a $110.00 / Bolsa", presentationPrice);
  }

  [Fact]
  public void PurchaseOrderSurfaces_LabelPriceAsBaseUnitPrice()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor");
    var pdf = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Purchasing/PurchaseOrderPdfService.cs");

    Assert.Contains("Precio por unidad base", page, StringComparison.Ordinal);
    Assert.Contains("precio se captura por unidad base", page, StringComparison.Ordinal);
    Assert.Contains("Precio por unidad base", pdf, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln")))
    {
      current = current.Parent;
    }

    Assert.NotNull(current);
    return File.ReadAllText(Path.Combine(current!.FullName, relativePath));
  }

  private sealed class FakeMaterialService : IMaterialService
  {
    public IReadOnlyList<LogisticsBinaryContent> Thumbnails { get; set; } = Array.Empty<LogisticsBinaryContent>();
    public IReadOnlyList<int> LastThumbnailIds { get; private set; } = Array.Empty<int>();

    public Task<IReadOnlyList<MaterialListItemDto>> GetMaterialsAsync(MaterialFilter filter, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<MaterialDetailDto?> GetMaterialAsync(string rfc, int materialId, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<MaterialCatalogDto> GetCatalogAsync(string rfc, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsBinaryContent?> GetMaterialImageAsync(string rfc, int materialId, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsBinaryContent?> GetMaterialThumbnailAsync(string rfc, int materialId, CancellationToken ct = default)
      => Task.FromResult<LogisticsBinaryContent?>(Thumbnails.FirstOrDefault(thumbnail => thumbnail.Id == materialId));

    public Task<IReadOnlyList<LogisticsBinaryContent>> GetMaterialThumbnailsAsync(string rfc, IEnumerable<int> materialIds, CancellationToken ct = default)
    {
      LastThumbnailIds = materialIds.Distinct().OrderBy(id => id).ToArray();
      return Task.FromResult<IReadOnlyList<LogisticsBinaryContent>>(Thumbnails.Where(thumbnail => LastThumbnailIds.Contains(thumbnail.Id)).ToList());
    }

    public Task<MaterialInventorySnapshotDto> GetMaterialInventoryAsync(string rfc, int materialId, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<IReadOnlyList<MaterialMovementDto>> GetMaterialMovementsAsync(MaterialMovementFilter filter, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<MaterialLifecycleAssessmentDto> GetMaterialLifecycleAssessmentAsync(string rfc, int materialId, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsCommandResult> DeleteMaterialAsync(MaterialDeleteRequest request, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsCommandResult> DeactivateMaterialAsync(MaterialDeactivateRequest request, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsCommandResult> ReactivateMaterialAsync(MaterialReactivateRequest request, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsCommandResult> SaveMaterialAsync(MaterialUpsertRequest request, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsCommandResult> CreateCategoryAsync(MaterialCategoryCreateRequest request, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsCommandResult> CreateUnitAsync(UnitOfMeasureCreateRequest request, CancellationToken ct = default)
      => throw new NotSupportedException();
  }

  private sealed class FakeRfcState : IUserRfcState
  {
    public string? CurrentRfc => "OHM191112Q26";
    public IReadOnlyList<string> AllowedRfcs => ["OHM191112Q26"];
    public event Action? Changed { add { } remove { } }
    public void InitializeFromClaims(System.Security.Claims.ClaimsPrincipal user) { }
    public bool TrySet(string rfc) => string.Equals(rfc, CurrentRfc, StringComparison.OrdinalIgnoreCase);
    public void ResetToDefault() { }
  }

  private sealed class FakeWebHostEnvironment : IWebHostEnvironment
  {
    public string ApplicationName { get; set; } = "OrionERP";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = Path.GetTempPath();
    public string EnvironmentName { get; set; } = "UnitTest";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
  }
}
