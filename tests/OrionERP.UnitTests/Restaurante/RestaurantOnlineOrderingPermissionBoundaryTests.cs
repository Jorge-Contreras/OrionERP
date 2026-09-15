using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantOnlineOrderingPermissionBoundaryTests
{
  private const string CorrectionMigration =
    "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260914_restaurant_online_ordering_runtime_corrections.sql";
  private const string StatusViewPermissionHotfix =
    "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260914_restaurant_online_ordering_status_view_permission_hotfix.sql";

  [Fact]
  public void ClubBruno_public_mutations_cross_scoped_stored_procedures()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Restaurante/LoyaltyService.cs");

    AssertMethodUsesProcedure(service, "CreateMemberAsync(", "PublicMemberCreate");
    AssertMethodUsesProcedure(service, "UpdateVerificationAsync(", "PublicMemberVerificationUpdate");
    AssertMethodUsesProcedure(service, "CreateQrTokenAsync(", "PublicMemberQrIssue");
    AssertMethodUsesProcedure(service, "RequestClosureAsync(", "PublicMemberClosureRequest");
    AssertMethodUsesProcedure(service, "UpdateConsentsAsync(", "PublicMemberConsentsUpdate");
  }

  [Fact]
  public void RestaurantPublicV8_removes_broad_writes_and_grants_only_scoped_entry_points()
  {
    var migration = RepoFile.Read(CorrectionMigration);

    Assert.Contains("'RESTAURANT_PUBLIC',8,'RESTAURANT'", migration, StringComparison.Ordinal);
    Assert.Contains("entryInfo.SecurableClass='SCHEMA' AND entryInfo.SchemaName='public_identity'", migration, StringComparison.Ordinal);
    Assert.Contains("('GRANT','INSERT','public_identity','AspNetUsers')", migration, StringComparison.Ordinal);
    Assert.Contains("('GRANT','UPDATE','public_identity','AspNetUsers')", migration, StringComparison.Ordinal);
    Assert.Contains("('GRANT','DELETE','public_identity','AspNetUsers')", migration, StringComparison.Ordinal);
    Assert.Contains("('DENY','UPDATE','restaurante','Order')", migration, StringComparison.Ordinal);
    Assert.Contains("('GRANT','EXECUTE','restaurante','OnlineOrderNotificationProviderMessageSet')", migration, StringComparison.Ordinal);
    Assert.Contains("FROM (VALUES('INSERT'),('UPDATE'),('DELETE'))", migration, StringComparison.Ordinal);
    Assert.Contains("('MemberAccount'),('MemberQrToken'),('PointLedger')", migration, StringComparison.Ordinal);
  }

  [Fact]
  public void Recovery_correction_reclaims_expired_leases_and_never_releases_failed_refund_amounts()
  {
    var migration = RepoFile.Read(CorrectionMigration);

    Assert.Contains("eventInfo.ProcessingStatus=''Processing''", migration, StringComparison.Ordinal);
    Assert.Contains("refundInfo.[Status]=''Processing''", migration, StringComparison.Ordinal);
    Assert.Contains("IF @Outcome=''Completed'' AND @ProviderRefundId IS NULL", migration, StringComparison.Ordinal);
    Assert.Contains("CASE WHEN @ProviderRefundId IS NOT NULL", migration, StringComparison.Ordinal);
    Assert.Contains("FROM restaurante.PaymentGatewayRefund WITH(UPDLOCK,HOLDLOCK)", migration, StringComparison.Ordinal);
    Assert.Contains("AND GatewayTransactionId=@TransactionId;", migration, StringComparison.Ordinal);
    Assert.DoesNotContain("AND [Status]<>''Failed'';", migration, StringComparison.Ordinal);
    Assert.Contains("CREATE UNIQUE INDEX UX_PaymentGatewayRefund_UnresolvedTransaction", migration, StringComparison.Ordinal);
    Assert.Contains("WHERE LocalRefundId IS NULL;", migration, StringComparison.Ordinal);
    Assert.Contains("INDEX(UX_PaymentGatewayRefund_UnresolvedTransaction)", migration, StringComparison.Ordinal);
    Assert.Contains("Ya existe un reembolso sin resolver con otros datos", migration, StringComparison.Ordinal);
    Assert.Contains("notificationFailure.NotificationId IS NOT NULL", migration, StringComparison.Ordinal);
    Assert.Contains("THEN ''PartiallyRefunded''", migration, StringComparison.Ordinal);
  }

  [Fact]
  public void Refund_financials_and_verified_webhook_linkage_are_durable_and_scoped()
  {
    var migration = RepoFile.Read(CorrectionMigration);

    Assert.Contains("ProviderGrossAmount decimal(18,2) NULL", migration, StringComparison.Ordinal);
    Assert.Contains("ProviderFeeAmount decimal(18,2) NULL", migration, StringComparison.Ordinal);
    Assert.Contains("ProviderNetAmount decimal(18,2) NULL", migration, StringComparison.Ordinal);
    Assert.Contains("ReconciledAtUtc datetime2(3) NULL", migration, StringComparison.Ordinal);
    Assert.Contains("CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayRefundEventBind", migration, StringComparison.Ordinal);
    Assert.Contains("eventInfo.VerificationStatus=''Verified''", migration, StringComparison.Ordinal);
    Assert.Contains("eventInfo.ProcessingStatus=''Processing'' AND eventInfo.LeaseId=@LeaseId", migration, StringComparison.Ordinal);
    Assert.Contains("@EventType NOT LIKE ''PAYMENT.REFUND.%''", migration, StringComparison.Ordinal);
    Assert.Contains("refundInfo.LocalRefundId IS NULL", migration, StringComparison.Ordinal);
    Assert.Contains("('GRANT','EXECUTE','restaurante','PaymentGatewayRefundEventBind')", migration, StringComparison.Ordinal);
  }

  [Fact]
  public void Capture_authorization_rechecks_the_durable_quote_boundary_atomically()
  {
    var migration = RepoFile.Read(CorrectionMigration);

    Assert.Contains("SettingsConfigurationVersion bigint NOT NULL", migration, StringComparison.Ordinal);
    Assert.Contains("@SettingsConfigurationVersion bigint", migration, StringComparison.Ordinal);
    Assert.Contains("attempt.SettingsConfigurationVersion=@SettingsConfigurationVersion", migration, StringComparison.Ordinal);
    Assert.Contains("@ProcessorHeartbeatMaxAgeSeconds int", migration, StringComparison.Ordinal);
    Assert.Contains("@AttemptConfigurationVersion<>@CurrentConfigurationVersion", migration, StringComparison.Ordinal);
    Assert.Contains("@QuoteExpiresAtUtc<=@Now", migration, StringComparison.Ordinal);
    Assert.Contains("@AttemptTermsVersion<>@CurrentTermsVersion", migration, StringComparison.Ordinal);
    Assert.Contains("@AttemptPrivacyVersion<>@CurrentPrivacyVersion", migration, StringComparison.Ordinal);
    Assert.Contains("@AttemptMerchantProfileKey<>@CurrentMerchantProfileKey", migration, StringComparison.Ordinal);
    Assert.Contains("OPENJSON(@WeeklyScheduleJson", migration, StringComparison.Ordinal);
    Assert.Contains("@GatewayReadinessAtUtc IS NULL", migration, StringComparison.Ordinal);
    Assert.Contains("@ProcessorHeartbeatAtUtc IS NULL", migration, StringComparison.Ordinal);
    Assert.Contains("DATEADD(SECOND,-@ProcessorHeartbeatMaxAgeSeconds,@Now)", migration, StringComparison.Ordinal);
  }

  [Fact]
  public void RestaurantPublicV9_denies_the_global_status_view_and_retains_only_the_scoped_procedure()
  {
    var migration = RepoFile.Read(StatusViewPermissionHotfix);

    Assert.Contains("'RESTAURANT_PUBLIC',9,'RESTAURANT'", migration, StringComparison.Ordinal);
    Assert.Contains(
      "('RESTAURANT_PUBLIC',9,'DENY','SELECT','OBJECT','restaurante','vw_PublicOnlineCheckoutStatus')",
      migration,
      StringComparison.Ordinal);
    Assert.Contains("entryInfo.PermissionState='GRANT' AND entryInfo.PermissionName='SELECT'", migration, StringComparison.Ordinal);
    Assert.Contains("permissionInfo.state='D'", migration, StringComparison.Ordinal);
    Assert.Contains("OBJECT_ID(N'restaurante.OnlineCheckoutStatusGet')", migration, StringComparison.Ordinal);
    Assert.Contains("permissionInfo.permission_name='EXECUTE'", migration, StringComparison.Ordinal);
  }

  private static void AssertMethodUsesProcedure(
    string service,
    string methodMarker,
    string procedureName)
  {
    var start = service.IndexOf(methodMarker, StringComparison.Ordinal);
    Assert.True(start >= 0, $"No se encontró {methodMarker}.");
    var nextMethod = service.IndexOf("\n  public ", start + methodMarker.Length, StringComparison.Ordinal);
    var method = nextMethod < 0 ? service[start..] : service[start..nextMethod];

    Assert.Contains($"restaurante.{procedureName}", method, StringComparison.Ordinal);
    Assert.Contains("CommandType.StoredProcedure", method, StringComparison.Ordinal);
    Assert.DoesNotContain("INSERT fidelidad.", method, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("UPDATE fidelidad.", method, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("DELETE FROM fidelidad.", method, StringComparison.OrdinalIgnoreCase);
  }
}
