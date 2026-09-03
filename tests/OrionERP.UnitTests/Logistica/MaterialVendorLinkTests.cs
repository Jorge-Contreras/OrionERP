using System.Data;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

/// <summary>
/// Un material puede comprarse con varios proveedores. Estas pruebas cubren el caso que lo
/// motivó: el proveedor habitual no tenía el producto y hubo que comprarlo con otro sin perder
/// el dato de quién lo surte de costumbre.
/// </summary>
public class MaterialVendorLinkTests
{
  [Fact]
  public async Task SaveMaterialAsync_LeavesExactlyOnePrimaryVendor_WhenNobodyChoseOne()
  {
    var connection = CreateConnection(scopedPartnerCount: 2);
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMaterialAsync(CreateRequest(
    [
      new MaterialVendorLinkRequest { BusinessPartnerId = 11 },
      new MaterialVendorLinkRequest { BusinessPartnerId = 22 }
    ]));

    Assert.True(result.Success);

    var promotion = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("SET IsPrimary = 1", StringComparison.Ordinal));

    // Sin elección explícita manda el primer renglón capturado.
    Assert.Contains(promotion.Parameters, parameter => parameter.Name.TrimStart('@') == "BusinessPartnerId" && Equals(parameter.Value, 11));
    Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET IsPrimary = 0", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SaveMaterialAsync_HonoursTheChosenPrimaryVendor()
  {
    var connection = CreateConnection(scopedPartnerCount: 2);
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMaterialAsync(CreateRequest(
    [
      new MaterialVendorLinkRequest { BusinessPartnerId = 11 },
      new MaterialVendorLinkRequest { BusinessPartnerId = 22, IsPrimary = true }
    ]));

    Assert.True(result.Success);

    var promotion = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("SET IsPrimary = 1", StringComparison.Ordinal));

    Assert.Contains(promotion.Parameters, parameter => parameter.Name.TrimStart('@') == "BusinessPartnerId" && Equals(parameter.Value, 22));
  }

  [Fact]
  public async Task SaveMaterialAsync_MirrorsVendorCodeAndLinkFromThePrimaryVendor()
  {
    var connection = CreateConnection(scopedPartnerCount: 1);
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    await service.SaveMaterialAsync(CreateRequest(
    [
      new MaterialVendorLinkRequest { BusinessPartnerId = 11, IsPrimary = true, VendorCode = "PRV-9" }
    ]));

    Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("material.VendorCode = vendorLink.VendorCode", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SaveMaterialAsync_KeepsExistingVendors_WhenTheScreenDoesNotShowThem()
  {
    var connection = CreateConnection(scopedPartnerCount: 0);
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var request = CreateRequest(vendors: null);
    var result = await service.SaveMaterialAsync(request);

    Assert.True(result.Success);
    Assert.DoesNotContain(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("logistica.MaterialVendor", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SaveMaterialAsync_RemovesEveryVendor_WhenTheListArrivesEmpty()
  {
    var connection = CreateConnection(scopedPartnerCount: 0);
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMaterialAsync(CreateRequest([]));

    Assert.True(result.Success);
    Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("DELETE FROM logistica.MaterialVendor WHERE Rfc = @Rfc AND MaterialId = @MaterialId;", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SaveMaterialAsync_RejectsVendorsOutsideTheSessionCompany()
  {
    var connection = CreateConnection(scopedPartnerCount: 0);
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMaterialAsync(CreateRequest(
    [
      new MaterialVendorLinkRequest { BusinessPartnerId = 99, IsPrimary = true }
    ]));

    Assert.False(result.Success);
    Assert.Contains("empresa de tu sesión", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction!.WasRolledBack);
  }

  [Fact]
  public async Task GetMaterialsAsync_MarksTheMaterialsTheVendorAlreadySupplies()
  {
    var connection = new FakeQueryDbConnection();
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    await service.GetMaterialsAsync(new MaterialFilter { Rfc = "OHM191112Q26", HighlightVendorId = 11, Take = 25 });

    Assert.NotNull(connection.LastCommandText);
    Assert.Contains("AS IsHighlightedVendorMaterial", connection.LastCommandText!, StringComparison.Ordinal);
    // Marcar no es filtrar: sin VendorId la consulta no acota nada, aunque Compras normalmente
    // sí lo mande para quedarse con el catálogo del proveedor.
    Assert.DoesNotContain("fv.BusinessPartnerId = @VendorId", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains(connection.LastParameters, parameter => parameter.Name.TrimStart('@') == "HighlightVendorId" && Equals(parameter.Value, 11));
  }

  [Fact]
  public void PurchaseOrderService_LinksEmergencyPurchasesWithoutDisplacingThePrimaryVendor()
  {
    var service = RepoFile.Read("src/OrionERP.Infrastructure/Features/Logistica/Purchasing/PurchaseOrderService.cs");

    Assert.Contains("LinkPurchasedMaterialsToVendorAsync", service, StringComparison.Ordinal);
    Assert.Contains("INSERT INTO logistica.MaterialVendor", service, StringComparison.Ordinal);
    // El alta automática nunca nace como principal.
    Assert.DoesNotContain("m.BusinessPartnerId = @BusinessPartnerId", service, StringComparison.Ordinal);
  }

  [Fact]
  public void PurchaseOrderService_OnlyLetsThePrimaryVendorMoveTheReferenceCost()
  {
    var service = RepoFile.Read("src/OrionERP.Infrastructure/Features/Logistica/Purchasing/PurchaseOrderService.cs");

    Assert.Contains("UPDATE vendorLink", service, StringComparison.Ordinal);
    Assert.Contains("vendorLink.LastUnitPrice = actual.BaseUnitPrice", service, StringComparison.Ordinal);
    Assert.Contains("primaryVendor.IsPrimary = 1", service, StringComparison.Ordinal);
  }

  [Fact]
  public void ComprasPage_ListsOnlyWhatTheVendorSupplies()
  {
    var page = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor");
    var codeBehind = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor.cs");

    Assert.Contains("protected bool CanSearchMaterials => IsDraftMode;", codeBehind, StringComparison.Ordinal);
    // El alcance de arranque acota al catálogo del proveedor de la orden.
    Assert.Contains(
      "protected bool IsVendorScopedSearch => HasVendorSelected && !SearchOutsideVendorCatalog;",
      codeBehind,
      StringComparison.Ordinal);
    Assert.Contains("VendorId = vendorScoped ? Editor.BusinessPartnerId : null,", codeBehind, StringComparison.Ordinal);
    // Cambiar de proveedor no puede dejar en pantalla la lista del anterior.
    Assert.Contains("@bind-Value:after=\"OnVendorChanged\"", page, StringComparison.Ordinal);
  }

  [Fact]
  public void ComprasPage_KeepsTheWholeCatalogOneClickAwayAndOffersToRegisterTheVendor()
  {
    var page = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor");
    var codeBehind = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor.cs");

    // Comprar con otro proveedor sigue a un clic: es la excepción, no el arranque.
    Assert.Contains("BuscarEnTodoElCatalogoAsync", codeBehind, StringComparison.Ordinal);
    Assert.Contains("BuscarEnTodoElCatalogoAsync", page, StringComparison.Ordinal);
    Assert.Contains("Buscar otros materiales", page, StringComparison.Ordinal);
    Assert.Contains("HighlightVendorId", codeBehind, StringComparison.Ordinal);
    Assert.Contains("UnlinkedMaterialNames", codeBehind, StringComparison.Ordinal);
    Assert.Contains("LinkMaterialsToVendor = LinkMaterialsToVendor", codeBehind, StringComparison.Ordinal);
    Assert.Contains("Lo surte este proveedor", page, StringComparison.Ordinal);
    Assert.Contains("compras-link-vendor", page, StringComparison.Ordinal);
  }

  [Fact]
  public void MaterialesPage_EditsSeveralVendorsWithTheirOwnCommercialData()
  {
    var page = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor");
    var codeBehind = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs");

    Assert.Contains("Agregar proveedor", page, StringComparison.Ordinal);
    Assert.Contains("Hacer principal", page, StringComparison.Ordinal);
    Assert.Contains("vendorRow.VendorCode", page, StringComparison.Ordinal);
    Assert.Contains("vendorRow.LastUnitPrice", page, StringComparison.Ordinal);
    Assert.Contains("SetPrimaryVendor", codeBehind, StringComparison.Ordinal);
    Assert.DoesNotContain("Editor.BusinessPartnerId", codeBehind, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_BacksUpAndVerifiesBeforeDroppingTheColumn()
  {
    var migration = RepoFile.Read("src/OrionERP.Infrastructure/Features/Logistica/Sql/20260831_material_multi_vendor.sql");

    Assert.Contains("CREATE TABLE logistica.MaterialVendor", migration, StringComparison.Ordinal);
    Assert.Contains("UX_MaterialVendor_Primary", migration, StringComparison.Ordinal);
    Assert.Contains("logistica.MaterialVendorBackfill", migration, StringComparison.Ordinal);
    Assert.Contains("THROW 51502", migration, StringComparison.Ordinal);
    Assert.Contains("ALTER TABLE logistica.Material DROP COLUMN BusinessPartnerId", migration, StringComparison.Ordinal);
    Assert.Contains("ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialVendor", migration, StringComparison.Ordinal);

    // El respaldo y la verificación tienen que quedar antes del DROP para que sea reversible.
    Assert.True(
      migration.IndexOf("THROW 51502", StringComparison.Ordinal)
        < migration.IndexOf("ALTER TABLE logistica.Material DROP COLUMN BusinessPartnerId", StringComparison.Ordinal));
  }

  private static MaterialUpsertRequest CreateRequest(List<MaterialVendorLinkRequest>? vendors)
    => new()
    {
      Rfc = "OHM191112Q26",
      Id = 42,
      Description = "Aceite hidráulico",
      BaseUnitId = 1,
      PurchaseQuantity = 1m,
      Status = "ACTIVO",
      MaterialClass = "Consumable",
      Vendors = vendors
    };

  private static FakeQueryDbConnection CreateConnection(int scopedPartnerCount)
    => new()
    {
      ReaderResultFactory = (_, _) => CreateLifecycleStateTable(),
      NonQueryResultFactory = (_, _) => 1,
      ScalarResultFactory = (commandText, _) =>
        commandText.Contains("BusinessPartnerRfcScope", StringComparison.Ordinal) ? scopedPartnerCount : 1
    };

  private static DataTable CreateLifecycleStateTable()
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("MaterialCode", typeof(string));
    table.Columns.Add("Description", typeof(string));
    table.Columns.Add("IsActive", typeof(bool));
    table.Rows.Add(42, "MAT-000042", "Aceite hidráulico", true);
    return table;
  }
}
