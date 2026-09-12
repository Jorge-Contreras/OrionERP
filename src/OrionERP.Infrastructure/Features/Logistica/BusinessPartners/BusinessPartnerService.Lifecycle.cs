using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Features.Logistica.BusinessPartners;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Infrastructure.Features.Logistica.BusinessPartners;

/// <summary>
/// Retiro seguro de un socio de negocio. El maestro es global: el vínculo con la
/// empresa vive en <c>dbo.BusinessPartnerRfcScope</c> y los documentos cuelgan de él,
/// así que el reporte revisa todo lo que apunta al socio —incluido el catálogo
/// heredado <c>dbo.Proveedores</c> que lo volvería a crear— antes de permitir el
/// borrado permanente.
/// </summary>
public sealed partial class BusinessPartnerService
{
  private const string DeleteConfirmationText = "Delete";
  private const int LifecycleExampleLimit = 5;

  private static readonly IReadOnlyDictionary<string, VendorDependencyDefinition> VendorDependencyDefinitions =
    new VendorDependencyDefinition[]
    {
      new("MaterialVendor", "Materiales del catálogo", "El socio está enlazado como proveedor de uno o más materiales.", "Revisar materiales", "/logistica/materiales"),
      new("PurchaseOrder", "Órdenes de compra", "El socio aparece en órdenes de compra, aunque estén terminadas o canceladas.", "Revisar compras", "/logistica/compras"),
      new("RecurringPayable", "Pagos recurrentes", "El socio es el beneficiario de pagos recurrentes de cuentas por pagar.", "Revisar pagos recurrentes", "/cuentas-por-pagar/recurrentes"),
      new("HospitalityFiscalCustomer", "Cliente fiscal de hospedaje", "El socio está publicado como cliente fiscal de una sede de hospedaje.", null, null),
      new("OtherCompanyScope", "Alta en otras empresas", "El socio también está dado de alta en otra empresa del grupo y sus documentos no se ven desde aquí.", null, null),
      new("LegacyPurchase", "Compras heredadas", "El proveedor heredado aparece en compras que alimentan las pólizas de contabilidad.", "Revisar pólizas", "/contabilidad/transacciones/list"),
      new("LegacyMaterial", "Materiales heredados", "El proveedor heredado sigue asignado a materiales del catálogo anterior.", "Revisar materiales", "/logistica/materiales"),
      new("LegacyRoomOwner", "Habitaciones en arrendamiento", "El proveedor heredado es propietario de habitaciones y alimenta el estado de cuenta de arrendadores.", "Revisar catálogos", "/ajustes/catalogos"),
      new("LegacySiteOwner", "Arrendador de una sede", "El proveedor heredado está asociado como arrendador de una empresa y sede.", "Revisar catálogos", "/ajustes/catalogos"),
      new("LegacyService", "Servicios heredados", "El proveedor heredado es la entidad de cobro de servicios del catálogo anterior.", "Revisar pagos recurrentes", "/cuentas-por-pagar/recurrentes"),
      new("LegacyPortalUser", "Usuarios del portal de arrendadores", "Una cuenta de acceso está ligada al proveedor heredado para consultar su estado de cuenta.", "Revisar seguridad", "/admin/seguridad"),
      new("RfcScope", "Alta en esta empresa", "El registro que da de alta al socio en la empresa actual.", null, null),
      new("PartnerRole", "Roles del socio", "Los roles asignados al socio de negocio.", null, null),
      new("VendorProfile", "Perfil vendor de logística", "Condiciones de pago, lead time y notas del perfil de proveedor.", null, null),
      new("CfdiProfile", "Perfil CFDI", "Nombre fiscal, régimen y uso de CFDI configurados para el socio.", null, null),
      new("LegacyPartner", "Registro heredado dbo.Proveedores", "El registro del catálogo anterior del que proviene el socio. Si se conserva, la importación volvería a crearlo.", null, null),
      new("MaterialVendorBackfill", "Respaldo de la migración multiproveedor", "La bitácora que guarda cuál era el proveedor principal antes de la migración.", null, null)
    }.ToDictionary(definition => definition.Code, StringComparer.Ordinal);

  public async Task<VendorLifecycleAssessmentDto> GetVendorLifecycleAssessmentAsync(
    string rfc,
    int businessPartnerId,
    CancellationToken ct = default)
  {
    if (businessPartnerId <= 0)
    {
      return new VendorLifecycleAssessmentDto();
    }

    var ownerRfc = LogisticsRfc.Require(rfc);
    await using var connection = await OpenVendorScopedAsync(ownerRfc, ct);
    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    var assessment = await LoadVendorLifecycleAssessmentAsync(
      connection,
      transaction,
      ownerRfc,
      businessPartnerId,
      lockPartner: false,
      ct);
    await transaction.CommitAsync(ct);
    return assessment;
  }

  public async Task<LogisticsCommandResult> DeleteVendorAsync(VendorDeleteRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    if (!string.Equals(request.ConfirmationText, DeleteConfirmationText, StringComparison.Ordinal))
    {
      return LogisticsCommandResult.Fail($"Escribe exactamente {DeleteConfirmationText} para confirmar la eliminación permanente.");
    }

    if (request.BusinessPartnerId <= 0)
    {
      return LogisticsCommandResult.Fail("Selecciona un socio de negocio válido para eliminar.");
    }

    var ownerRfc = LogisticsRfc.Require(request.OwnerRfc);
    var deletedBy = NullIfWhiteSpace(request.DeletedBy) ?? "OrionERP";

    await using var connection = await OpenVendorScopedAsync(ownerRfc, ct);
    await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var assessment = await LoadVendorLifecycleAssessmentAsync(
        connection,
        tx,
        ownerRfc,
        request.BusinessPartnerId,
        lockPartner: true,
        ct);

      if (!assessment.Exists)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El socio ya no existe o no pertenece a la empresa seleccionada.");
      }

      if (!assessment.CanDelete)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail(
          $"El socio no se puede eliminar porque conserva {assessment.TotalReferences:N0} referencia(s) en {assessment.Dependencies.Count:N0} grupo(s). Revisa el reporte actualizado.");
      }

      var affected = await DeleteVendorRecordsAsync(connection, tx, ownerRfc, assessment, ct);
      if (affected != 1)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El socio cambió mientras se procesaba la solicitud. Vuelve a revisar el reporte.");
      }

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok($"Socio {assessment.DisplayName} eliminado permanentemente.", assessment.BusinessPartnerId);
    }
    catch (SqlException ex) when (ex.Number == 547)
    {
      await tx.RollbackAsync(ct);
      return LogisticsCommandResult.Fail("Se creó o detectó una referencia nueva mientras se eliminaba el socio. Revisa el reporte actualizado.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  /// <summary>
  /// Borra al socio con lo que le pertenece. El registro heredado se va con él: mientras
  /// exista, la importación de <c>dbo.Proveedores</c> volvería a crear el socio.
  /// </summary>
  private async Task<int> DeleteVendorRecordsAsync(
    DbConnection connection,
    DbTransaction transaction,
    string ownerRfc,
    VendorLifecycleAssessmentDto assessment,
    CancellationToken ct)
  {
    var parameters = new { BusinessPartnerId = assessment.BusinessPartnerId, LegacyProveedorId = assessment.LegacyProveedorId };

    await connection.ExecuteAsync(new CommandDefinition(
      """
      DELETE FROM logistica.MaterialVendorBackfill WHERE OldBusinessPartnerId = @BusinessPartnerId;
      DELETE FROM logistica.VendorProfile WHERE BusinessPartnerId = @BusinessPartnerId;
      DELETE FROM dbo.BusinessPartnerCfdiProfile WHERE BusinessPartnerId = @BusinessPartnerId;
      DELETE FROM dbo.BusinessPartnerRole WHERE BusinessPartnerId = @BusinessPartnerId;
      DELETE FROM dbo.BusinessPartnerRfcScope WHERE BusinessPartnerId = @BusinessPartnerId;
      """,
      parameters,
      transaction,
      cancellationToken: ct));

    var affected = await connection.ExecuteAsync(new CommandDefinition(
      "DELETE FROM dbo.BusinessPartner WHERE Id = @BusinessPartnerId;",
      parameters,
      transaction,
      cancellationToken: ct));

    if (affected == 1 && assessment.LegacyProveedorId.HasValue)
    {
      await connection.ExecuteAsync(new CommandDefinition(
        "DELETE FROM dbo.Proveedores WHERE id = @LegacyProveedorId;",
        parameters,
        transaction,
        cancellationToken: ct));
    }

    if (affected == 1)
    {
      _logger?.LogInformation(
        "Business partner {PartnerName} ({BusinessPartnerId}) deleted permanently for RFC {Rfc}; legacy provider {LegacyProveedorId}.",
        assessment.DisplayName,
        assessment.BusinessPartnerId,
        ownerRfc,
        assessment.LegacyProveedorId);
    }

    return affected;
  }

  private async Task<DbConnection> OpenVendorScopedAsync(string ownerRfc, CancellationToken ct)
  {
    var connection = _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("Logística requiere una conexión de base de datos.");
    try
    {
      if (connection.State != ConnectionState.Open)
      {
        await connection.OpenAsync(ct);
      }

      await connection.ExecuteAsync(new CommandDefinition(
        """
        IF @RequestedRfc <> CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))
          THROW 51864, 'El RFC solicitado no pertenece a la sesion de logistica.', 1;
        """,
        new { RequestedRfc = ownerRfc },
        cancellationToken: ct));
      return connection;
    }
    catch
    {
      await connection.DisposeAsync();
      throw;
    }
  }

  private static async Task<VendorLifecycleAssessmentDto> LoadVendorLifecycleAssessmentAsync(
    DbConnection connection,
    DbTransaction? transaction,
    string ownerRfc,
    int businessPartnerId,
    bool lockPartner,
    CancellationToken ct)
  {
    await EnsureVendorFiscalCustomersAsync(connection, transaction, businessPartnerId, ct);
    var sql = VendorLifecycleAssessmentSql.Replace(
      "/*PARTNER_LOCK*/",
      lockPartner ? "WITH (UPDLOCK, HOLDLOCK)" : string.Empty,
      StringComparison.Ordinal);

    var rows = (await connection.QueryAsync<VendorLifecycleAssessmentRow>(
      new CommandDefinition(
        sql,
        new { Rfc = ownerRfc, BusinessPartnerId = businessPartnerId, ExampleLimit = LifecycleExampleLimit },
        transaction,
        cancellationToken: ct))).AsList();

    if (rows.Count == 0)
    {
      return new VendorLifecycleAssessmentDto();
    }

    var partner = rows[0];
    var groups = rows
      .Where(row => !string.IsNullOrWhiteSpace(row.BlockerCode))
      .GroupBy(row => (Code: row.BlockerCode!, row.DependencyKind))
      .OrderBy(group => DependencyKindSortOrder(group.Key.DependencyKind))
      .ThenBy(group => group.Min(row => row.BlockerSortOrder))
      .Select(group =>
      {
        var definition = VendorDependencyDefinitions[group.Key.Code];
        return new VendorDependencyDto
        {
          Code = definition.Code,
          Kind = group.Key.DependencyKind,
          Title = definition.Title,
          Explanation = GetDependencyExplanation(definition, group.Key.DependencyKind),
          ReferenceCount = group.Max(row => row.ReferenceCount),
          Examples = group
            .Select(row => row.Example)
            .Where(example => !string.IsNullOrWhiteSpace(example))
            .Select(example => example!)
            .Distinct(StringComparer.Ordinal)
            .Take(LifecycleExampleLimit)
            .ToArray(),
          ResolutionLabel = definition.ResolutionLabel,
          ResolutionUrl = definition.ResolutionUrl
        };
      })
      .ToArray();

    return new VendorLifecycleAssessmentDto
    {
      Exists = true,
      BusinessPartnerId = partner.BusinessPartnerId,
      DisplayName = partner.DisplayName,
      Rfc = partner.Rfc,
      IsActive = partner.IsActive,
      LegacyProveedorId = partner.LegacyProveedorId,
      Dependencies = groups.Where(group => group.Kind != VendorDependencyKinds.Owned).ToArray(),
      OwnedRecords = groups.Where(group => group.Kind == VendorDependencyKinds.Owned).ToArray()
    };
  }

  /// <summary>
  /// <c>orion.HospitalityFiscalCustomer</c> sólo es visible para la empresa y sede que trae
  /// la sesión, y una conexión de logística las deja en NULL: consultarla directamente
  /// reportaría cero referencias aunque existieran. El barrido recorre el catálogo de sedes
  /// para que el reporte vea lo mismo que verá la llave foránea al borrar.
  /// </summary>
  private static async Task EnsureVendorFiscalCustomersAsync(
    DbConnection connection,
    DbTransaction? transaction,
    int businessPartnerId,
    CancellationToken ct)
  {
    // El lote que crea la tabla temporal no lleva parámetros a propósito: Dapper envuelve
    // los que sí los llevan en sp_executesql y la tabla moriría con ese ámbito interno
    // —mismo motivo por el que #OrionVisibleLocations se crea sin parámetros.
    await connection.ExecuteAsync(new CommandDefinition(
      """
      IF OBJECT_ID('tempdb..#OrionVendorFiscalCustomers') IS NOT NULL
        DROP TABLE #OrionVendorFiscalCustomers;
      CREATE TABLE #OrionVendorFiscalCustomers
      (
        CompanyId bigint NOT NULL,
        SiteId bigint NOT NULL,
        SiteName nvarchar(200) NULL,
        CreatedAtUtc datetime2(0) NULL
      );
      """,
      transaction: transaction,
      cancellationToken: ct));

    await connection.ExecuteAsync(new CommandDefinition(
      """
      DECLARE @CompanyId bigint, @SiteId bigint, @SiteName nvarchar(200);
      DECLARE hospitalitySites CURSOR LOCAL FAST_FORWARD FOR
        SELECT site.CompanyId, site.SiteId, site.DisplayName FROM orion.Site site;
      OPEN hospitalitySites;
      FETCH NEXT FROM hospitalitySites INTO @CompanyId, @SiteId, @SiteName;
      WHILE @@FETCH_STATUS = 0
      BEGIN
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId', @value=@CompanyId, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId', @value=@SiteId, @read_only=0;

        INSERT INTO #OrionVendorFiscalCustomers (CompanyId, SiteId, SiteName, CreatedAtUtc)
        SELECT fiscalCustomer.CompanyId, fiscalCustomer.SiteId, @SiteName, fiscalCustomer.CreatedAtUtc
        FROM orion.HospitalityFiscalCustomer fiscalCustomer
        WHERE fiscalCustomer.BusinessPartnerId = @BusinessPartnerId;

        FETCH NEXT FROM hospitalitySites INTO @CompanyId, @SiteId, @SiteName;
      END;
      CLOSE hospitalitySites;
      DEALLOCATE hospitalitySites;

      EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId', @value=NULL, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId', @value=NULL, @read_only=0;
      """,
      new { BusinessPartnerId = businessPartnerId },
      transaction,
      cancellationToken: ct));
  }

  private const string VendorLifecycleAssessmentSql =
    """
    ;WITH PartnerIdentity AS
    (
      SELECT partner.Id, partner.LegacyProveedorId
      FROM dbo.BusinessPartner partner
      WHERE partner.Id = @BusinessPartnerId
    ),
    DependencyRows AS
    (
      SELECT
        N'MaterialVendor' AS BlockerCode,
        CASE WHEN link.IsActive = 1 THEN N'Operational' ELSE N'Configuration' END AS DependencyKind,
        10 AS BlockerSortOrder,
        CONVERT(nvarchar(100), link.Id) AS ReferenceKey,
        link.UpdatedAt AS SortDate,
        CAST(CONCAT(
          COALESCE(NULLIF(material.MaterialCode, ''), CONCAT('Material #', link.MaterialId)),
          CASE WHEN NULLIF(material.[Description], '') IS NULL THEN '' ELSE CONCAT(' · ', material.[Description]) END,
          CASE WHEN link.IsPrimary = 1 THEN ' · Proveedor principal' ELSE '' END,
          CASE WHEN link.IsActive = 1 THEN ' · Activo' ELSE ' · Inactivo' END
        ) AS nvarchar(1000)) AS Example
      FROM logistica.MaterialVendor link
      LEFT JOIN logistica.Material material
        ON material.Rfc = link.Rfc AND material.Id = link.MaterialId
      WHERE link.Rfc = @Rfc AND link.BusinessPartnerId = @BusinessPartnerId

      UNION ALL

      SELECT
        N'PurchaseOrder',
        CASE WHEN purchaseOrder.Status IN ('Completed', 'Cancelled') THEN N'Historical' ELSE N'Operational' END,
        20, CONVERT(nvarchar(100), purchaseOrder.Id), purchaseOrder.UpdatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(purchaseOrder.PurchaseOrderCode, ''), CONCAT('OC #', purchaseOrder.Id)),
          ' · ', purchaseOrder.Status,
          ' · ', CONVERT(varchar(10), purchaseOrder.OrderDate, 120),
          ' · ', CONVERT(varchar(20), (SELECT COUNT_BIG(*) FROM logistica.PurchaseOrderLine orderLine
                                       WHERE orderLine.Rfc = purchaseOrder.Rfc AND orderLine.PurchaseOrderId = purchaseOrder.Id)),
          ' renglón(es)'
        ) AS nvarchar(1000))
      FROM logistica.PurchaseOrder purchaseOrder
      WHERE purchaseOrder.Rfc = @Rfc AND purchaseOrder.BusinessPartnerId = @BusinessPartnerId

      UNION ALL

      SELECT
        N'RecurringPayable',
        CASE WHEN payable.IsActive = 1 THEN N'Operational' ELSE N'Historical' END,
        30, CONVERT(nvarchar(100), payable.Id), payable.UpdatedAt,
        CAST(CASE
          WHEN payable.Rfc = @Rfc THEN CONCAT(
            COALESCE(NULLIF(payable.Name, ''), CONCAT('Pago recurrente #', payable.Id)),
            ' · ', payable.FrequencyUnit,
            CASE WHEN payable.IsActive = 1 THEN ' · Activo' ELSE ' · Inactivo' END,
            ' · ', CONVERT(varchar(20), (SELECT COUNT_BIG(*) FROM AP.PayableOccurrence occurrence
                                         WHERE occurrence.RecurringPayableId = payable.Id)),
            ' ocurrencia(s)')
          ELSE CONCAT('Otra empresa (', payable.Rfc, ') · Pago recurrente #', payable.Id)
        END AS nvarchar(1000))
      FROM AP.RecurringPayable payable
      WHERE payable.BusinessPartnerId = @BusinessPartnerId

      UNION ALL

      SELECT
        N'HospitalityFiscalCustomer', N'Operational', 40,
        CONVERT(nvarchar(100), CONCAT(fiscalCustomer.CompanyId, '-', fiscalCustomer.SiteId)),
        fiscalCustomer.CreatedAtUtc,
        CAST(CONCAT(COALESCE(NULLIF(fiscalCustomer.SiteName, ''), CONCAT('Sede ', fiscalCustomer.SiteId)),
          ' · Cliente fiscal desde ', CONVERT(varchar(10), fiscalCustomer.CreatedAtUtc, 120)) AS nvarchar(1000))
      FROM #OrionVendorFiscalCustomers fiscalCustomer

      UNION ALL

      SELECT
        N'OtherCompanyScope', N'Operational', 50,
        CONVERT(nvarchar(100), scope.Rfc), scope.CreatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(company.DisplayName, ''), NULLIF(company.LegalName, ''), scope.Rfc),
          ' · ', scope.Rfc,
          CASE WHEN scope.IsActive = 1 THEN ' · Alta activa' ELSE ' · Alta inactiva' END
        ) AS nvarchar(1000))
      FROM dbo.BusinessPartnerRfcScope scope
      LEFT JOIN orion.Company company ON company.Rfc = scope.Rfc
      WHERE scope.BusinessPartnerId = @BusinessPartnerId AND scope.Rfc <> @Rfc

      UNION ALL

      SELECT
        N'LegacyPurchase', N'Historical', 60, CONVERT(nvarchar(100), purchase.ID), purchase.FechaCompra,
        CAST(CONCAT('Compra #', purchase.ID,
          ' · ', COALESCE(NULLIF(purchase.Descripcion, ''), 'Sin descripción'),
          CASE WHEN NULLIF(purchase.[Status], '') IS NULL THEN '' ELSE CONCAT(' · ', purchase.[Status]) END,
          ' · ', CONVERT(varchar(10), purchase.FechaCompra, 120)) AS nvarchar(1000))
      FROM dbo.Compra purchase
      JOIN PartnerIdentity partnerIdentity ON partnerIdentity.LegacyProveedorId = purchase.Proveedor_ID

      UNION ALL

      SELECT
        N'LegacyMaterial',
        CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(legacyMaterial.[STATUS], '')))) = 'ACTIVO' THEN N'Operational' ELSE N'Configuration' END,
        70, CONVERT(nvarchar(100), legacyMaterial.ID), legacyMaterial.FECHA_ACTUALIZADO,
        CAST(CONCAT('Material heredado #', legacyMaterial.ID,
          ' · ', COALESCE(NULLIF(legacyMaterial.DESCRIPCION, ''), 'Sin descripción'),
          CASE WHEN NULLIF(legacyMaterial.[STATUS], '') IS NULL THEN '' ELSE CONCAT(' · ', legacyMaterial.[STATUS]) END) AS nvarchar(1000))
      FROM logistica.MATERIALES legacyMaterial
      JOIN PartnerIdentity partnerIdentity ON partnerIdentity.LegacyProveedorId = legacyMaterial.PROVEEDOR_ID

      UNION ALL

      SELECT
        N'LegacyRoomOwner', N'Operational', 80, CONVERT(nvarchar(100), room.ID), CAST(NULL AS datetime2),
        CAST(CONCAT('Habitación #', room.ID,
          ' · ', COALESCE(NULLIF(room.ROOM_NAME, ''), 'Sin nombre')) AS nvarchar(1000))
      FROM dbo.ROOM room
      JOIN PartnerIdentity partnerIdentity ON partnerIdentity.LegacyProveedorId = room.OWNER_ID

      UNION ALL

      SELECT
        N'LegacySiteOwner',
        CASE WHEN siteOwner.IsActive = 1 THEN N'Operational' ELSE N'Configuration' END,
        90, CONVERT(nvarchar(100), CONCAT(siteOwner.CompanyId, '-', siteOwner.SiteId)), siteOwner.UpdatedAtUtc,
        CAST(CONCAT('Empresa ', siteOwner.CompanyId, ' · Sede ', siteOwner.SiteId,
          CASE WHEN siteOwner.IsActive = 1 THEN ' · Asociación activa' ELSE ' · Asociación inactiva' END) AS nvarchar(1000))
      FROM orion.HospitalitySiteOwner siteOwner
      JOIN PartnerIdentity partnerIdentity ON partnerIdentity.LegacyProveedorId = siteOwner.ProveedorId

      UNION ALL

      SELECT
        N'LegacyService', N'Operational', 100, CONVERT(nvarchar(100), legacyService.id), legacyService.Fecha_Proximo_Pago,
        CAST(CONCAT('Servicio #', legacyService.id,
          ' · ', COALESCE(NULLIF(legacyService.Descripcion, ''), NULLIF(legacyService.RazonSocial, ''), 'Sin descripción'),
          CASE WHEN NULLIF(legacyService.Categoria, '') IS NULL THEN '' ELSE CONCAT(' · ', legacyService.Categoria) END) AS nvarchar(1000))
      FROM dbo.Servicios legacyService
      JOIN PartnerIdentity partnerIdentity ON partnerIdentity.LegacyProveedorId = legacyService.Entidad_Cobro_ID

      UNION ALL

      SELECT
        N'LegacyPortalUser', N'Operational', 110, CONVERT(nvarchar(100), portalUser.Id), CAST(NULL AS datetime2),
        CAST(CONCAT('Cuenta ', COALESCE(NULLIF(portalUser.Email, ''), NULLIF(portalUser.UserName, ''), portalUser.Id)) AS nvarchar(1000))
      FROM auth.AspNetUsers portalUser
      JOIN PartnerIdentity partnerIdentity ON partnerIdentity.LegacyProveedorId = portalUser.ArrendadorProveedorId

      UNION ALL

      SELECT
        N'RfcScope', N'Owned', 200, CONVERT(nvarchar(100), scope.Rfc), scope.CreatedAt,
        CAST(CONCAT('Alta en ', scope.Rfc,
          CASE WHEN scope.IsActive = 1 THEN ' · Activa' ELSE ' · Inactiva' END) AS nvarchar(1000))
      FROM dbo.BusinessPartnerRfcScope scope
      WHERE scope.BusinessPartnerId = @BusinessPartnerId AND scope.Rfc = @Rfc

      UNION ALL

      SELECT
        N'PartnerRole', N'Owned', 210, CONVERT(nvarchar(100), partnerRole.Id), partnerRole.CreatedAt,
        CAST(partnerRole.RoleCode AS nvarchar(1000))
      FROM dbo.BusinessPartnerRole partnerRole
      WHERE partnerRole.BusinessPartnerId = @BusinessPartnerId

      UNION ALL

      SELECT
        N'VendorProfile', N'Owned', 220, CONVERT(nvarchar(100), vendorProfile.Rfc), vendorProfile.UpdatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(vendorProfile.PaymentTerms, ''), 'Sin condiciones de pago'),
          ' · Lead time: ', COALESCE(CONVERT(varchar(20), vendorProfile.DefaultLeadTimeDays), 'N/D'),
          CASE WHEN vendorProfile.IsApproved = 1 THEN ' · Aprobado' ELSE ' · No aprobado' END) AS nvarchar(1000))
      FROM logistica.VendorProfile vendorProfile
      WHERE vendorProfile.BusinessPartnerId = @BusinessPartnerId AND vendorProfile.Rfc = @Rfc

      UNION ALL

      SELECT
        N'CfdiProfile', N'Owned', 230, CONVERT(nvarchar(100), cfdiProfile.BusinessPartnerId), cfdiProfile.UpdatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(cfdiProfile.FiscalName, ''), 'Sin nombre fiscal'),
          CASE WHEN NULLIF(cfdiProfile.FiscalRegime, '') IS NULL THEN '' ELSE CONCAT(' · Régimen ', cfdiProfile.FiscalRegime) END,
          CASE WHEN NULLIF(cfdiProfile.DefaultCfdiUse, '') IS NULL THEN '' ELSE CONCAT(' · Uso ', cfdiProfile.DefaultCfdiUse) END) AS nvarchar(1000))
      FROM dbo.BusinessPartnerCfdiProfile cfdiProfile
      WHERE cfdiProfile.BusinessPartnerId = @BusinessPartnerId

      UNION ALL

      SELECT
        N'LegacyPartner', N'Owned', 240, CONVERT(nvarchar(100), legacyPartner.id), CAST(NULL AS datetime2),
        CAST(CONCAT('dbo.Proveedores #', legacyPartner.id,
          ' · ', COALESCE(NULLIF(legacyPartner.RazonSocial, ''), 'Sin razón social')) AS nvarchar(1000))
      FROM dbo.Proveedores legacyPartner
      JOIN PartnerIdentity partnerIdentity ON partnerIdentity.LegacyProveedorId = legacyPartner.id

      UNION ALL

      SELECT
        N'MaterialVendorBackfill', N'Owned', 250, CONVERT(nvarchar(100), backfill.Id), backfill.AppliedAtUtc,
        CAST(CONCAT('Material #', backfill.MaterialId,
          CASE WHEN NULLIF(backfill.[Description], '') IS NULL THEN '' ELSE CONCAT(' · ', backfill.[Description]) END) AS nvarchar(1000))
      FROM logistica.MaterialVendorBackfill backfill
      WHERE backfill.OldBusinessPartnerId = @BusinessPartnerId
    ),
    RankedDependencies AS
    (
      SELECT
        BlockerCode,
        DependencyKind,
        BlockerSortOrder,
        Example,
        COUNT_BIG(*) OVER (PARTITION BY BlockerCode, DependencyKind) AS ReferenceCount,
        ROW_NUMBER() OVER
        (
          PARTITION BY BlockerCode, DependencyKind
          ORDER BY CASE WHEN SortDate IS NULL THEN 1 ELSE 0 END, SortDate DESC, ReferenceKey DESC
        ) AS ExampleOrdinal
      FROM DependencyRows
    )
    SELECT
      partner.Id AS BusinessPartnerId,
      partner.PartnerName AS DisplayName,
      partner.Rfc,
      partner.IsActive,
      partner.LegacyProveedorId,
      dependency.BlockerCode,
      dependency.DependencyKind,
      dependency.BlockerSortOrder,
      dependency.ReferenceCount,
      dependency.Example
    FROM dbo.BusinessPartner partner /*PARTNER_LOCK*/
    LEFT JOIN RankedDependencies dependency
      ON dependency.ExampleOrdinal <= @ExampleLimit
    WHERE partner.Id = @BusinessPartnerId
      AND EXISTS
      (
        SELECT 1 FROM dbo.BusinessPartnerRfcScope partnerScope
        WHERE partnerScope.Rfc = @Rfc
          AND partnerScope.BusinessPartnerId = partner.Id
          AND partnerScope.IsActive = 1
      )
    ORDER BY
      CASE dependency.DependencyKind
        WHEN 'Operational' THEN 0
        WHEN 'Historical' THEN 1
        WHEN 'Configuration' THEN 2
        ELSE 3
      END,
      dependency.BlockerSortOrder,
      dependency.ExampleOrdinal;
    """;

  private static int DependencyKindSortOrder(string kind)
    => kind switch
    {
      VendorDependencyKinds.Operational => 0,
      VendorDependencyKinds.Historical => 1,
      VendorDependencyKinds.Configuration => 2,
      _ => 3
    };

  private static string GetDependencyExplanation(VendorDependencyDefinition definition, string kind)
    => (definition.Code, kind) switch
    {
      ("MaterialVendor", VendorDependencyKinds.Operational) => "El socio sigue enlazado como proveedor activo de estos materiales. Retira el enlace desde la ficha del material.",
      ("MaterialVendor", VendorDependencyKinds.Configuration) => "El enlace ya está inactivo pero conserva código, precio y unidad de compra. Elimínalo desde la ficha del material.",
      ("PurchaseOrder", VendorDependencyKinds.Operational) => "La orden de compra todavía no está terminada ni cancelada. Cierra su flujo antes de retirar al socio.",
      ("PurchaseOrder", VendorDependencyKinds.Historical) => "La orden terminada o cancelada debe conservar al proveedor que la surtió.",
      ("RecurringPayable", VendorDependencyKinds.Operational) => "El pago recurrente sigue activo con este beneficiario. Desactívalo o cámbialo de beneficiario.",
      ("RecurringPayable", VendorDependencyKinds.Historical) => "El pago recurrente está inactivo y conserva el historial de ocurrencias pagadas a este beneficiario.",
      ("LegacyMaterial", VendorDependencyKinds.Operational) => "El material heredado sigue activo con este proveedor. Actualízalo o retíralo desde el catálogo anterior.",
      ("LegacyMaterial", VendorDependencyKinds.Configuration) => "El material heredado está inactivo pero conserva la llave del proveedor.",
      ("LegacySiteOwner", VendorDependencyKinds.Operational) => "La asociación de arrendador sigue activa para una empresa y sede. Retírala desde Ajustes · Catálogos.",
      ("LegacySiteOwner", VendorDependencyKinds.Configuration) => "La asociación de arrendador está inactiva pero conserva la llave del proveedor.",
      _ => definition.Explanation
    };

  private sealed record VendorDependencyDefinition(
    string Code,
    string Title,
    string Explanation,
    string? ResolutionLabel,
    string? ResolutionUrl);

  private sealed class VendorLifecycleAssessmentRow
  {
    public int BusinessPartnerId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Rfc { get; set; }
    public bool IsActive { get; set; }
    public int? LegacyProveedorId { get; set; }
    public string? BlockerCode { get; set; }
    public string DependencyKind { get; set; } = string.Empty;
    public int BlockerSortOrder { get; set; }
    public long ReferenceCount { get; set; }
    public string? Example { get; set; }
  }
}
