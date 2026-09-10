using System.Reflection;
using System.Runtime.ExceptionServices;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;
using OrionERP.Infrastructure.Features.Contabilidad.Transacciones;
using OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services;
using OrionERP.Infrastructure.Features.Restaurante;
using CompanyConnectionFactory = OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper.SqlConnectionFactory;

namespace OrionERP.IntegrationTests.Restaurante;

public sealed class RestaurantIndividualAccountingDurabilitySqlTests
{
  [Fact]
  [Trait("Category", "SqlIntegration")]
  public async Task FailedCfdiLink_RetryReusesOneBalancedIndividualPolicy()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Falta conexión Sandbox.");
    var connectionString = new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" }.ConnectionString;
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["ConnectionStrings:OrionDb"] = connectionString
    }).Build();

    await using var bootstrap = new SqlConnection(connectionString);
    await bootstrap.OpenAsync();
    Assert.Equal("Orion_Sandbox", await bootstrap.ExecuteScalarAsync<string>("SELECT DB_NAME();"), ignoreCase: true);
    var row = await bootstrap.QuerySingleAsync<ScopeRow>("""
      SELECT TOP (1)
        company.CompanyId,
        platformSite.SiteId AS PlatformSiteId,
        platformSite.SiteKey,
        company.Rfc AS CompanyRfc
      FROM orion.Company company
      JOIN orion.Site platformSite ON platformSite.CompanyId=company.CompanyId AND platformSite.IsActive=1
      JOIN orion.CompanyModule companyModule
        ON companyModule.CompanyId=company.CompanyId AND companyModule.ModuleCode='RESTAURANT'
      JOIN orion.SiteCapability capability
        ON capability.CompanyId=company.CompanyId AND capability.SiteId=platformSite.SiteId
       AND capability.ModuleCode=companyModule.ModuleCode AND capability.IsEnabled=1
      WHERE company.IsActive=1 AND companyModule.[Status]='Enabled'
        AND (companyModule.EffectiveFromUtc IS NULL OR companyModule.EffectiveFromUtc<=SYSUTCDATETIME())
        AND (companyModule.EffectiveToUtc IS NULL OR companyModule.EffectiveToUtc>SYSUTCDATETIME())
      ORDER BY CASE WHEN company.Rfc='BRUNOS' THEN 0 ELSE 1 END,company.CompanyId,platformSite.SiteId;
      """);
    await AccountingConnectionFactory.InitializeAsync(bootstrap, row.CompanyRfc, row.CompanyId);
    var legacySiteId = await bootstrap.ExecuteScalarAsync<int>("""
      SELECT Id FROM restaurante.Site WHERE Rfc=@Rfc AND SiteCode=@SiteKey;
      """, new { Rfc = row.CompanyRfc, row.SiteKey });
    var scope = new RestaurantScope(row.CompanyId, row.PlatformSiteId, legacySiteId, row.CompanyRfc);
    var originalConfig = await bootstrap.QuerySingleOrDefaultAsync<ConfigurationRow>("""
      SELECT * FROM restaurante.AccountingConfiguration WHERE Rfc=@Rfc AND SiteId=@SiteId;
      """, new { Rfc = scope.CompanyRfc, SiteId = scope.LegacySiteId });
    var company = new FixedCompany(scope);
    using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Error));
    var companyConnections = new CompanyConnectionFactory(cfg, company);
    var scopeAccessor = new RestaurantScopeAccessor(companyConnections, company);
    var realTransactions = new TransaccionService(
      cfg,
      Unused<IFacturamaApiClient>(),
      Unused<ISatRfcProfileRepository>(),
      Unused<ICfdiStampingService>(),
      loggerFactory.CreateLogger<TransaccionService>(),
      companyContext: company);
    var transactions = DispatchProxy.Create<ITransaccionService, FailingCfdiLinkProxy>();
    var proxy = (FailingCfdiLinkProxy)(object)transactions;
    proxy.Target = realTransactions;
    var outbox = new AccountingOutboxService(new AccountingConnectionFactory(cfg, company), company);
    var service = new RestaurantAccountingService(
      companyConnections,
      transactions,
      scopeAccessor,
      outbox,
      NullLogger<RestaurantAccountingService>.Instance);

    var orderId = Guid.NewGuid();
    var paymentId = Guid.NewGuid();
    var marker = "RESTAURANT-INDIVIDUAL-" + Guid.NewGuid().ToString("N");
    var cfdiId = Random.Shared.Next(1_500_000_000, 1_900_000_000);
    var operationKey = $"INDIVIDUAL_CFDI:{scope.LegacySiteId}:{orderId:D}";
    var transactionId = 0;
    try
    {
      await bootstrap.ExecuteAsync("""
        IF EXISTS (SELECT 1 FROM restaurante.AccountingConfiguration WHERE Rfc=@Rfc AND SiteId=@SiteId)
          UPDATE restaurante.AccountingConfiguration
          SET CashAccount='101.01.01',SalesAccount='401.01.01',DailyPolicyEnabled=1
          WHERE Rfc=@Rfc AND SiteId=@SiteId;
        ELSE
          INSERT restaurante.AccountingConfiguration
            (Rfc,SiteId,CashAccount,SalesAccount,DailyPolicyEnabled)
          VALUES (@Rfc,@SiteId,'101.01.01','401.01.01',1);
        """, new { Rfc = scope.CompanyRfc, SiteId = scope.LegacySiteId });
      await bootstrap.ExecuteAsync("""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        DECLARE @Folio int;
        SELECT @Folio=ISNULL(MAX(Folio),0)+1
        FROM restaurante.[Order] WITH (UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND SiteId=@SiteId AND OperationalDate=CONVERT(date,SYSUTCDATETIME());
        INSERT restaurante.[Order]
          (Id,Rfc,SiteId,Folio,OperationalDate,OrderType,[Status],PaymentStatus,CustomerName,
           Subtotal,DiscountTotal,TaxTotal,TipTotal,Total,BalanceDue,TaxRateSnapshot,
           PricesIncludeTaxSnapshot,TheoreticalCost,IdempotencyKey,Notes,PaidAt)
        VALUES
          (@OrderId,@Rfc,@SiteId,@Folio,CONVERT(date,SYSUTCDATETIME()),'TakeAway','Completed','Paid',@Marker,
           123,0,0,0,123,0,0,1,0,@Marker,@Marker,SYSUTCDATETIME());
        INSERT restaurante.Payment
          (Id,Rfc,OrderId,PaymentMethod,Amount,TipAmount,[Status],IdempotencyKey,ReceivedBy,RefundedAmount)
        VALUES (@PaymentId,@Rfc,@OrderId,'Cash',123,0,'Captured',@Marker,@Marker,0);
        COMMIT TRANSACTION;
        """, new { OrderId = orderId, PaymentId = paymentId, Rfc = scope.CompanyRfc, SiteId = scope.LegacySiteId, Marker = marker });

      var first = await service.GenerateIndividualCfdiPolicyAsync(scope.CompanyRfc, orderId, cfdiId, marker);
      Assert.False(first.Success);
      Assert.Contains("se conserva", first.Message, StringComparison.OrdinalIgnoreCase);
      transactionId = await bootstrap.ExecuteScalarAsync<int>("""
        SELECT TransaccionId FROM contabilidad.AccountingOutbox
        WHERE CompanyId=@CompanyId AND SourceModule='RESTAURANT' AND OperationKey=@OperationKey;
        """, new { scope.CompanyId, OperationKey = operationKey });
      Assert.True(transactionId > 0);

      var retry = await service.GenerateIndividualCfdiPolicyAsync(scope.CompanyRfc, orderId, cfdiId, marker);
      Assert.True(retry.Success, retry.Message);
      Assert.Contains(transactionId.ToString(), retry.Message, StringComparison.Ordinal);
      var completedRetry = await service.GenerateIndividualCfdiPolicyAsync(scope.CompanyRfc, orderId, cfdiId, marker);
      Assert.True(completedRetry.Success, completedRetry.Message);

      var evidence = await bootstrap.QuerySingleAsync<EvidenceRow>("""
        SELECT
          (SELECT COUNT(*) FROM contabilidad.AccountingOutbox
           WHERE CompanyId=@CompanyId AND SourceModule='RESTAURANT' AND OperationKey=@OperationKey
             AND [Status]='Completed' AND TransaccionId=@TransactionId) AS CompletedOperations,
          (SELECT COUNT(*) FROM dbo.Transacciones WHERE ID=@TransactionId) AS Policies,
          (SELECT COUNT(*) FROM dbo.Registro_Contable WHERE TransaccionID=@TransactionId) AS MovementCount,
          (SELECT CAST(ISNULL(SUM(Debe),0) AS decimal(18,2)) FROM dbo.Registro_Contable WHERE TransaccionID=@TransactionId) AS Debit,
          (SELECT CAST(ISNULL(SUM(Haber),0) AS decimal(18,2)) FROM dbo.Registro_Contable WHERE TransaccionID=@TransactionId) AS Credit,
          (SELECT COUNT(*) FROM restaurante.AccountingLink
           WHERE Rfc=@Rfc AND OrderId=@OrderId AND LinkType='IndividualCfdi'
             AND TransactionId=@TransactionId AND CfdiId=@CfdiId) AS AccountingLinks,
          (SELECT COUNT(*) FROM restaurante.AccountingOrderLink
           WHERE Rfc=@Rfc AND OrderId=@OrderId AND LinkType='IndividualCfdi'
             AND TransactionId=@TransactionId AND CfdiId=@CfdiId) AS OrderLinks,
          (SELECT COUNT(*) FROM restaurante.OrderEvent
           WHERE Rfc=@Rfc AND OrderId=@OrderId AND SourceKey=CONCAT('accounting:',CONVERT(varchar(36),@OrderId),':IndividualCfdi:',@TransactionId)) AS Events;
        """, new
      {
        scope.CompanyId,
        OperationKey = operationKey,
        TransactionId = transactionId,
        Rfc = scope.CompanyRfc,
        OrderId = orderId,
        CfdiId = cfdiId
      });
      Assert.Equal(1, evidence.CompletedOperations);
      Assert.Equal(1, evidence.Policies);
      Assert.Equal(2, evidence.MovementCount);
      Assert.Equal(123m, evidence.Debit);
      Assert.Equal(evidence.Debit, evidence.Credit);
      Assert.Equal(1, evidence.AccountingLinks);
      Assert.Equal(1, evidence.OrderLinks);
      Assert.Equal(1, evidence.Events);
      Assert.Equal(2, proxy.InsertAttempts);
    }
    finally
    {
      if (transactionId == 0)
      {
        transactionId = await bootstrap.ExecuteScalarAsync<int?>("""
          SELECT TransaccionId FROM contabilidad.AccountingOutbox
          WHERE CompanyId=@CompanyId AND SourceModule='RESTAURANT' AND OperationKey=@OperationKey;
          """, new { scope.CompanyId, OperationKey = operationKey }) ?? 0;
      }
      await bootstrap.ExecuteAsync("""
        DELETE FROM restaurante.OrderEvent WHERE Rfc=@Rfc AND OrderId=@OrderId;
        DELETE FROM restaurante.AccountingOrderLink WHERE Rfc=@Rfc AND OrderId=@OrderId;
        DELETE FROM restaurante.AccountingLink WHERE Rfc=@Rfc AND OrderId=@OrderId;
        DELETE FROM contabilidad.AccountingOutbox
        WHERE CompanyId=@CompanyId AND SourceModule='RESTAURANT' AND OperationKey=@OperationKey;
        DELETE FROM dbo.Registro_Contable WHERE TransaccionID=@TransactionId;
        DELETE FROM dbo.Transacciones WHERE ID=@TransactionId;
        DELETE FROM restaurante.Payment WHERE Rfc=@Rfc AND OrderId=@OrderId AND Id=@PaymentId;
        DELETE FROM restaurante.[Order] WHERE Rfc=@Rfc AND Id=@OrderId AND CustomerName=@Marker;
        """, new
      {
        Rfc = scope.CompanyRfc,
        OrderId = orderId,
        PaymentId = paymentId,
        Marker = marker,
        scope.CompanyId,
        OperationKey = operationKey,
        TransactionId = transactionId
      });
      if (originalConfig is null)
      {
        await bootstrap.ExecuteAsync("""
          DELETE FROM restaurante.AccountingConfiguration WHERE Rfc=@Rfc AND SiteId=@SiteId;
          """, new { Rfc = scope.CompanyRfc, SiteId = scope.LegacySiteId });
      }
      else
      {
        await bootstrap.ExecuteAsync("""
          UPDATE restaurante.AccountingConfiguration
          SET CashAccount=@CashAccount,CardBankAccount=@CardBankAccount,
              TransferBankAccount=@TransferBankAccount,PlatformReceivableAccount=@PlatformReceivableAccount,
              SalesAccount=@SalesAccount,VatAccount=@VatAccount,DiscountAccount=@DiscountAccount,
              TipsPayableAccount=@TipsPayableAccount,PlatformCommissionAccount=@PlatformCommissionAccount,
              InventoryAccount=@InventoryAccount,CostOfSalesAccount=@CostOfSalesAccount,
              WasteAccount=@WasteAccount,DailyPolicyEnabled=@DailyPolicyEnabled
          WHERE Rfc=@Rfc AND SiteId=@SiteId;
          """, originalConfig);
      }
    }
  }

  private static T Unused<T>() where T : class => DispatchProxy.Create<T, ForbiddenExternalDependency>();

  public class ForbiddenExternalDependency : DispatchProxy
  {
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
      => throw new Xunit.Sdk.XunitException($"No external call is allowed: {targetMethod?.Name}");
  }

  public class FailingCfdiLinkProxy : DispatchProxy
  {
    public ITransaccionService Target { get; set; } = null!;
    public int InsertAttempts { get; private set; }
    private bool _linked;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
      ArgumentNullException.ThrowIfNull(targetMethod);
      if (targetMethod.Name == nameof(ITransaccionService.IsComprobanteLinkedToTransaccionAsync))
        return Task.FromResult(_linked);
      if (targetMethod.Name == nameof(ITransaccionService.InsertTransaccionComprobanteAsync))
      {
        InsertAttempts++;
        if (InsertAttempts == 1)
          return Task.FromResult(TransaccionCommandResult.Fail("Fallo inducido después de registrar la póliza."));
        _linked = true;
        return Task.FromResult(TransaccionCommandResult.Ok("Vínculo CFDI simulado."));
      }

      try
      {
        return targetMethod.Invoke(Target, args);
      }
      catch (TargetInvocationException ex) when (ex.InnerException is not null)
      {
        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        throw;
      }
    }
  }

  private sealed class FixedCompany(RestaurantScope scope) : ICurrentCompanyContext, ICurrentRfcAccessor
  {
    public string CurrentRfc => scope.CompanyRfc;
    public string DisplayName => scope.CompanyRfc;
    public int? EmployeeId => null;
    string? ICurrentRfcAccessor.CurrentRfc => scope.CompanyRfc;
    public string RequireRfc() => scope.CompanyRfc;
    public void EnsureRfc(string requestedRfc)
    {
      if (!string.Equals(scope.CompanyRfc, requestedRfc, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("Empresa ajena.");
    }
    public Task<long> RequireCompanyIdAsync(CancellationToken ct = default) => Task.FromResult(scope.CompanyId);
  }

  private sealed class ScopeRow
  {
    public long CompanyId { get; set; }
    public long PlatformSiteId { get; set; }
    public string SiteKey { get; set; } = "";
    public string CompanyRfc { get; set; } = "";
  }

  private sealed class ConfigurationRow
  {
    public string Rfc { get; set; } = "";
    public int SiteId { get; set; }
    public string? CashAccount { get; set; }
    public string? CardBankAccount { get; set; }
    public string? TransferBankAccount { get; set; }
    public string? PlatformReceivableAccount { get; set; }
    public string? SalesAccount { get; set; }
    public string? VatAccount { get; set; }
    public string? DiscountAccount { get; set; }
    public string? TipsPayableAccount { get; set; }
    public string? PlatformCommissionAccount { get; set; }
    public string? InventoryAccount { get; set; }
    public string? CostOfSalesAccount { get; set; }
    public string? WasteAccount { get; set; }
    public bool DailyPolicyEnabled { get; set; }
  }

  private sealed class EvidenceRow
  {
    public int CompletedOperations { get; set; }
    public int Policies { get; set; }
    public int MovementCount { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public int AccountingLinks { get; set; }
    public int OrderLinks { get; set; }
    public int Events { get; set; }
  }
}
