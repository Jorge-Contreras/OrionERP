using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.Features.Logistica.Purchasing;

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

    var hydrator = new PurchaseMaterialThumbnailHydrator(materialService);

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

    var factory = new PurchaseOrderPdfDocumentFactory(materialService);
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
      OrderedQuantity = 2m,
      ReceivedQuantity = 0m,
      RemainingQuantity = 2m,
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
          OrderedQuantity = 2m,
          ReceivedQuantity = 0m,
          RemainingQuantity = 2m,
          Allocations =
          [
            new PurchaseOrderAllocationDto
            {
              Id = 11,
              PurchaseOrderLineId = 21,
              LocationId = 5,
              LocationName = "Minibar 101",
              LocationCode = "LOC-000005",
              PlannedQuantity = 2m,
              ReceivedQuantity = 0m,
              RemainingQuantity = 2m
            }
          ]
        }
      ]
    });

    Assert.Single(model.Lines);
    Assert.NotNull(model.Lines[0].ThumbnailBytes);
    Assert.Single(model.Allocations);
    Assert.NotNull(model.Allocations[0].ThumbnailBytes);

    var pdfBytes = pdfService.Generate(model);

    Assert.NotEmpty(pdfBytes);
    Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 4), StringComparison.Ordinal);
  }

  private sealed class FakeMaterialService : IMaterialService
  {
    public IReadOnlyList<LogisticsBinaryContent> Thumbnails { get; set; } = Array.Empty<LogisticsBinaryContent>();
    public IReadOnlyList<int> LastThumbnailIds { get; private set; } = Array.Empty<int>();

    public Task<IReadOnlyList<MaterialListItemDto>> GetMaterialsAsync(MaterialFilter filter, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<MaterialDetailDto?> GetMaterialAsync(int materialId, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<MaterialCatalogDto> GetCatalogAsync(CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsBinaryContent?> GetMaterialImageAsync(int materialId, CancellationToken ct = default)
      => throw new NotSupportedException();

    public Task<LogisticsBinaryContent?> GetMaterialThumbnailAsync(int materialId, CancellationToken ct = default)
      => Task.FromResult<LogisticsBinaryContent?>(Thumbnails.FirstOrDefault(thumbnail => thumbnail.Id == materialId));

    public Task<IReadOnlyList<LogisticsBinaryContent>> GetMaterialThumbnailsAsync(IEnumerable<int> materialIds, CancellationToken ct = default)
    {
      LastThumbnailIds = materialIds.Distinct().OrderBy(id => id).ToArray();
      return Task.FromResult<IReadOnlyList<LogisticsBinaryContent>>(Thumbnails.Where(thumbnail => LastThumbnailIds.Contains(thumbnail.Id)).ToList());
    }

    public Task<LogisticsCommandResult> SaveMaterialAsync(MaterialUpsertRequest request, CancellationToken ct = default)
      => throw new NotSupportedException();
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
