using System.Reflection;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Bonhomia;

public sealed class HospitalityPublicIsolationTests
{
  [Fact]
  public void ScopePolicy_CreatesScopeOnlyFromHospitalityBinding()
  {
    var scope = HospitalityWebsiteScopePolicy.FromBinding(CreateBinding(PlatformModuleCodes.Hospitality));

    Assert.Equal(9, scope.CompanyId);
    Assert.Equal(17, scope.SiteId);
    Assert.Equal("OHM191112Q26", scope.CompanyRfc);
    Assert.Equal("bonhomia-main", scope.PublicSiteKey);
    Assert.True(scope.Owns(9, 17));
    Assert.False(scope.Owns(9, 18));
    Assert.False(scope.Owns(10, 17));
  }

  [Fact]
  public void ScopePolicy_RejectsBindingForAnotherModule()
  {
    var exception = Assert.Throws<InvalidOperationException>(() =>
      HospitalityWebsiteScopePolicy.FromBinding(CreateBinding(PlatformModuleCodes.Restaurant)));

    Assert.Contains("not bound to the HOSPITALITY module", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void QuoteScopeGuard_RejectsQuoteFromAnotherPublicSite()
  {
    var quote = new BonhomiaQuoteDto { PublicSiteKey = "another-hotel" };
    var scope = new HospitalityWebsiteScope(9, 17, "OHM191112Q26", "bonhomia-main");

    var exception = Assert.Throws<BonhomiaPublicBookingException>(() =>
      HospitalityWebsiteScopePolicy.EnsureQuoteBelongsToScope(quote, scope));

    Assert.Equal("quote_scope_mismatch", exception.ErrorCode);
  }

  [Fact]
  public void QuoteFingerprint_IsBoundToPublicSiteKey()
  {
    var first = CreateFingerprintQuote("bonhomia-main");
    var second = CreateFingerprintQuote("another-hotel");

    Assert.NotEqual(
      BonhomiaQuoteCalculator.CreateFingerprint(first),
      BonhomiaQuoteCalculator.CreateFingerprint(second));
  }

  [Fact]
  public void PayPalOrderPolicy_RejectsCaptureFromAnotherWebsiteQuote()
  {
    var quote = CreateFingerprintQuote("bonhomia-main");
    quote.QuoteId = Guid.Parse("36ec61bf-139e-4824-b6e5-d26c4dc40e96");
    quote.Fingerprint = BonhomiaQuoteCalculator.CreateFingerprint(quote);
    var foreignCapture = new BonhomiaPayPalCaptureResult
    {
      CustomId = BonhomiaQuoteCalculator.CreateFingerprint(CreateFingerprintQuote("another-hotel")),
      ReferenceId = Guid.NewGuid().ToString("N")
    };

    var exception = Assert.Throws<BonhomiaPublicBookingException>(() =>
      BonhomiaPayPalOrderPolicy.EnsureCaptureBelongsToQuote(foreignCapture, quote));

    Assert.Equal("paypal_quote_mismatch", exception.ErrorCode);
  }

  [Fact]
  public void PayPalOrderPolicy_AcceptsExactProtectedQuoteBinding()
  {
    var quote = CreateFingerprintQuote("bonhomia-main");
    quote.QuoteId = Guid.Parse("36ec61bf-139e-4824-b6e5-d26c4dc40e96");
    quote.Fingerprint = BonhomiaQuoteCalculator.CreateFingerprint(quote);
    var capture = new BonhomiaPayPalCaptureResult
    {
      CustomId = quote.Fingerprint,
      ReferenceId = quote.QuoteId.ToString("N")
    };

    BonhomiaPayPalOrderPolicy.EnsureCaptureBelongsToQuote(capture, quote);
  }

  [Fact]
  public void PublicReader_RequiresCompositeScopeForEveryAggregate()
  {
    var source = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Bonhomia/PublicBooking/BonhomiaScopedPublicDataReader.cs");

    Assert.Contains("r.OrionCompanyId = @ScopeCompanyId", source, StringComparison.Ordinal);
    Assert.Contains("r.OrionSiteId = @ScopeSiteId", source, StringComparison.Ordinal);
    Assert.Contains("rc.OrionCompanyId = @ScopeCompanyId", source, StringComparison.Ordinal);
    Assert.Contains("reservation.OrionCompanyId = @ScopeCompanyId", source, StringComparison.Ordinal);
    Assert.Contains("extraLine.OrionCompanyId = @ScopeCompanyId", source, StringComparison.Ordinal);
    Assert.Contains("line.OrionCompanyId = @ScopeCompanyId", source, StringComparison.Ordinal);
    Assert.Contains("link.OrionCompanyId = @ScopeCompanyId", source, StringComparison.Ordinal);
    Assert.Contains("attachment.OrionCompanyId = @ScopeCompanyId", source, StringComparison.Ordinal);
    Assert.Contains("breakdown.OrionCompanyId = @ScopeCompanyId", source, StringComparison.Ordinal);
    Assert.Contains("orion.HospitalitySiteCustomer", source, StringComparison.Ordinal);
    Assert.Contains("reservation.ID = rc.ReservationId", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Calendar_GetRoomTimeline", source, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void PublicReader_EverySqlContractCarriesBothScopeParameters()
  {
    var sqlContracts = typeof(BonhomiaScopedPublicDataReader)
      .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
      .Where(field => field.FieldType == typeof(string) && field.Name.EndsWith("Sql", StringComparison.Ordinal))
      .Select(field => (field.Name, Sql: Assert.IsType<string>(field.GetRawConstantValue())))
      .ToArray();

    Assert.NotEmpty(sqlContracts);
    Assert.All(sqlContracts, contract =>
    {
      Assert.Contains("@ScopeCompanyId", contract.Sql, StringComparison.Ordinal);
      Assert.Contains("@ScopeSiteId", contract.Sql, StringComparison.Ordinal);
    });
  }

  [Fact]
  public void SchemaReadiness_RequiresExactHospitalityOwnershipContract()
  {
    var sql = Assert.IsType<string>(typeof(BonhomiaScopedPublicDataReader)
      .GetField("SchemaReadinessSql", BindingFlags.Static | BindingFlags.NonPublic)
      ?.GetRawConstantValue());

    Assert.Contains("UQ_orion_HospitalitySiteCustomer_Cliente", sql, StringComparison.Ordinal);
    Assert.Contains("customerIndex.is_unique_constraint = 1", sql, StringComparison.Ordinal);
    Assert.Contains("customerIndex.has_filter = 0", sql, StringComparison.Ordinal);
    Assert.Contains("customerColumn.name = N'ClienteId'", sql, StringComparison.Ordinal);
    Assert.Contains("20260905_hospitality_legal_consent_sandbox", sql, StringComparison.Ordinal);
    Assert.Contains("WHERE MigrationId IN (N'20260903_hospitality_public_scope_sandbox', N'20260908_production_hospitality_public_scope')", sql, StringComparison.Ordinal);
    Assert.Contains("WHERE MigrationId IN (N'20260905_hospitality_legal_consent_sandbox', N'20260908_production_hospitality_legal_consent')", sql, StringComparison.Ordinal);
    Assert.Contains("PrivacyVersionAccepted", sql, StringComparison.Ordinal);
    Assert.Contains("TermsVersionAccepted", sql, StringComparison.Ordinal);
    Assert.Contains("LegalAcceptedAtUtc", sql, StringComparison.Ordinal);
    Assert.Contains("CK_RESERVATION_HospitalityLegalConsent_AllOrNone", sql, StringComparison.Ordinal);

    var expectedForeignKeys = new[]
    {
      "FK_ROOM_CALENDAR_OrionRoom",
      "FK_ROOM_CALENDAR_OrionReservation",
      "FK_RESERVATION_HospitalitySiteCustomer",
      "FK_ReservationExtra_OrionReservation",
      "FK_ReservationExtra_OrionExtra",
      "FK_ReservationTransactions_OrionReservation",
      "FK_ReservationAttachment_OrionReservation",
      "FK_ReservationAirbnb_OrionReservation",
      "FK_ReservationExperience_OrionReservation",
      "FK_ReservationExperience_OrionExperience",
      "FK_ReservationExperience_OrionPackage",
      "FK_ReservationExperienceAddOn_OrionParent",
      "FK_ReservationExperienceAddOn_OrionCatalog"
    };
    Assert.All(expectedForeignKeys, foreignKeyName =>
      Assert.Contains(foreignKeyName, sql, StringComparison.Ordinal));
    Assert.Contains("foreignKey.parent_object_id = expected.parentObjectId", sql, StringComparison.Ordinal);
    Assert.Contains("foreignKey.referenced_object_id = expected.referencedObjectId", sql, StringComparison.Ordinal);
    Assert.Contains("foreignKey.is_disabled = 0", sql, StringComparison.Ordinal);
    Assert.Contains("foreignKey.is_not_trusted = 0", sql, StringComparison.Ordinal);
    Assert.Contains("actualColumn.constraint_column_id = expectedColumn.columnOrdinal", sql, StringComparison.Ordinal);
    Assert.Contains("COL_NAME(actualColumn.parent_object_id, actualColumn.parent_column_id) = expectedColumn.parentColumnName", sql, StringComparison.Ordinal);
    Assert.Contains("COL_NAME(actualColumn.referenced_object_id, actualColumn.referenced_column_id) = expectedColumn.referencedColumnName", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("AND 13 =", sql, StringComparison.Ordinal);

    Assert.Contains("UX_ROOM_CALENDAR_OrionScope_Room_Date", sql, StringComparison.Ordinal);
    Assert.Contains("UX_ROOM_CALENDAR_Unscoped_Room_Date", sql, StringComparison.Ordinal);
    Assert.Contains("N'orioncompanyidisnotnullandorionsiteidisnotnullandroomidisnotnull'", sql, StringComparison.Ordinal);
    Assert.Contains("N'orioncompanyidisnullandorionsiteidisnull'", sql, StringComparison.Ordinal);
    Assert.Contains("indexInfo.object_id = OBJECT_ID(N'dbo.ROOM_CALENDAR')", sql, StringComparison.Ordinal);
    Assert.Contains("indexInfo.is_unique = 1", sql, StringComparison.Ordinal);
    Assert.Contains("actualKey.key_ordinal = expectedColumn.keyOrdinal", sql, StringComparison.Ordinal);
    Assert.Contains("actualKey.is_descending_key = 0", sql, StringComparison.Ordinal);
    Assert.Contains("actualColumn.name = expectedColumn.columnName", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void CheckoutWrites_AreScopedAndCannotUpdateAnotherSitesCalendar()
  {
    var source = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Bonhomia/PublicBooking/BonhomiaPublicBookingService.cs");

    Assert.Contains("HospitalityWebsiteScopePolicy.EnsureQuoteBelongsToScope", source, StringComparison.Ordinal);
    Assert.Contains("BonhomiaPayPalOrderPolicy.EnsureCaptureBelongsToQuote", source, StringComparison.Ordinal);
    Assert.Contains("OrionCompanyId, OrionSiteId", source, StringComparison.Ordinal);
    Assert.Contains("AND OrionCompanyId = @ScopeCompanyId", source, StringComparison.Ordinal);
    Assert.Contains("AND OrionSiteId = @ScopeSiteId", source, StringComparison.Ordinal);
    Assert.Contains("ReservationId = @ReservationId", source, StringComparison.Ordinal);
    Assert.Contains("PrivacyVersionAccepted", source, StringComparison.Ordinal);
    Assert.Contains("TermsVersionAccepted", source, StringComparison.Ordinal);
    Assert.Contains("LegalAcceptedAtUtc", source, StringComparison.Ordinal);
    Assert.Contains("FROM orion.HospitalitySiteCustomer", source, StringComparison.Ordinal);
    Assert.DoesNotContain("_reservacionesService", source, StringComparison.Ordinal);
    Assert.DoesNotContain("_experiencesService", source, StringComparison.Ordinal);
  }

  [Fact]
  public void SandboxMigration_DoesNotClaimSharedMastersOrAmbiguousReservations()
  {
    var sql = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Bonhomia/PublicBooking/Sql/20260903_hospitality_public_scope_sandbox.sql");

    Assert.Contains("@ExpectedDatabase <> N'Orion_Sandbox'", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("grupocarpio", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("reservation.RFC = @LegacyRfc", sql, StringComparison.Ordinal);
    Assert.Contains("reservation.RFC = ''SIN_RFC''", sql, StringComparison.Ordinal);
    Assert.Contains("SIN_EVIDENCIA_DE_EMPRESA_SEDE", sql, StringComparison.Ordinal);
    Assert.Contains("orion.HospitalitySiteCustomer", sql, StringComparison.Ordinal);
    Assert.Contains("companyInfo.Rfc = @LegacyRfc", sql, StringComparison.Ordinal);
    Assert.Contains("assignment.[Status] = 'Enabled'", sql, StringComparison.Ordinal);
    Assert.Contains("capability.IsEnabled = 1", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("ALTER TABLE dbo.Clientes ADD OrionCompanyId", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("ALTER TABLE dbo.Transacciones ADD OrionCompanyId", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("SET OrionCompanyId=@CompanyId", ExtractTransactionStatements(sql), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void LegalConsentMigration_IsSandboxOnlyAdditiveAndAllOrNone()
  {
    var sql = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Bonhomia/PublicBooking/Sql/20260905_hospitality_legal_consent_sandbox.sql");

    Assert.Contains("@ExpectedDatabase <> N'Orion_Sandbox'", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("grupocarpio", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("PrivacyVersionAccepted nvarchar(30) NULL", sql, StringComparison.Ordinal);
    Assert.Contains("TermsVersionAccepted nvarchar(30) NULL", sql, StringComparison.Ordinal);
    Assert.Contains("LegalAcceptedAtUtc datetime2(0) NULL", sql, StringComparison.Ordinal);
    Assert.Contains("CK_RESERVATION_HospitalityLegalConsent_AllOrNone", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("UPDATE dbo.RESERVATION", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void PublicHost_DoesNotRegisterGlobalReservationReaders()
  {
    var program = RepoFile.Read("src/OrionERP.Bonhomia.Web/Program.cs");

    Assert.Contains("IBonhomiaScopedPublicDataReader", program, StringComparison.Ordinal);
    Assert.Contains("IHospitalityWebsiteScopeAccessor", program, StringComparison.Ordinal);
    Assert.DoesNotContain("IListaReservacionesService", program, StringComparison.Ordinal);
    Assert.DoesNotContain("IReservacionExperiencesService", program, StringComparison.Ordinal);
    Assert.Contains("hospitalityData.ValidateSchemaAsync", program, StringComparison.Ordinal);
  }

  private static string ExtractTransactionStatements(string migration)
  {
    var transactionTable = migration.IndexOf("dbo.Transacciones", StringComparison.Ordinal);
    Assert.True(transactionTable >= 0);
    var nextSection = migration.IndexOf("UPDATE calendarRow", transactionTable, StringComparison.Ordinal);
    Assert.True(nextSection > transactionTable);
    return migration[transactionTable..nextSection];
  }

  private static PublicSiteBinding CreateBinding(string moduleCode)
    => new(
      33,
      "bonhomia-main",
      9,
      "OHM191112Q26",
      null,
      "OHM191112Q26",
      17,
      "bonhomia-suites",
      "Bonhomia Suites",
      "America/Mexico_City",
      moduleCode,
      1,
      "bonhomiasuites.com",
      1,
      1,
      1);

  private static BonhomiaQuoteDto CreateFingerprintQuote(string publicSiteKey)
    => new()
    {
      PublicSiteKey = publicSiteKey,
      RoomName = "Suite Paris",
      CheckIn = new DateOnly(2026, 10, 1),
      CheckOut = new DateOnly(2026, 10, 2),
      Guests = 2,
      Total = 1250m,
      Currency = "MXN"
    };
}
