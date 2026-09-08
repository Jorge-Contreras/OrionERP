using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;

namespace OrionERP.UnitTests.Bonhomia;

public sealed class BonhomiaPublicBookingLegalConsentTests
{
  [Theory]
  [InlineData("2025-01-01", "2026-02-01")]
  [InlineData("2026-01-01", "version-manipulada")]
  public async Task CreatePaidReservationAsync_RejectsLegalVersionsThatAreNotCurrent(
    string privacyVersion,
    string termsVersion)
  {
    var scopeAccessor = new NeverCalledScopeAccessor();
    var service = CreateService(scopeAccessor);

    var exception = await Assert.ThrowsAsync<BonhomiaPublicBookingException>(() =>
      service.CreatePaidReservationAsync(
        new BonhomiaQuoteDto(),
        new BonhomiaCustomerInfo(),
        new BonhomiaPayPalCaptureResult(),
        new BonhomiaLegalAcceptance(privacyVersion, termsVersion, DateTimeOffset.UtcNow)));

    Assert.Equal("legal_documents_changed", exception.ErrorCode);
    Assert.Equal(0, scopeAccessor.ResolveCallCount);
  }

  private static BonhomiaPublicBookingService CreateService(
    NeverCalledScopeAccessor scopeAccessor)
  {
    var presentation = HospitalityPresentationTestData.CreatePresentation();
    var website = HospitalityWebsitePolicy.Create(
      HospitalityPresentationTestData.CreateHospitalityOptions(),
      presentation);
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["ConnectionStrings:OrionDb"] =
          "Server=localhost;Database=unused;Integrated Security=True;TrustServerCertificate=True;"
      })
      .Build();

    return new BonhomiaPublicBookingService(
      configuration,
      new NeverCalledPublicDataReader(),
      scopeAccessor,
      website,
      Options.Create(new BonhomiaCheckoutOptions()),
      NullLogger<BonhomiaPublicBookingService>.Instance);
  }

  private sealed class NeverCalledScopeAccessor : IHospitalityWebsiteScopeAccessor
  {
    public int ResolveCallCount { get; private set; }

    public Task<HospitalityWebsiteScope> ResolveRequiredAsync(CancellationToken ct = default)
    {
      ResolveCallCount++;
      throw new InvalidOperationException("The legal validation should run before resolving database scope.");
    }
  }

  private sealed class NeverCalledPublicDataReader : IBonhomiaScopedPublicDataReader
  {
    public Task<RoomCalendarTimelineDto> GetCalendarTimelineAsync(
      DateOnly startDate,
      DateOnly endDateExclusive,
      CancellationToken ct = default)
      => throw UnexpectedCall();

    public Task<IReadOnlyList<BonhomiaExtraOptionDto>> GetExtraOptionsAsync(
      CancellationToken ct = default)
      => throw UnexpectedCall();

    public Task<IReadOnlyList<ExperienceCatalogItemDto>> GetExperienceCatalogAsync(
      DateOnly startDate,
      DateOnly endDateExclusive,
      CancellationToken ct = default)
      => throw UnexpectedCall();

    public Task<IReadOnlyList<int>> GetRoomCalendarIdsAsync(
      string roomName,
      DateOnly checkIn,
      DateOnly checkOut,
      CancellationToken ct = default)
      => throw UnexpectedCall();

    public Task<ReservacionDetailDto?> GetReservationDetailAsync(
      int reservationId,
      CancellationToken ct = default)
      => throw UnexpectedCall();

    public Task ValidateSchemaAsync(CancellationToken ct = default)
      => throw UnexpectedCall();

    private static InvalidOperationException UnexpectedCall()
      => new("The legal validation should run before reading public booking data.");
  }
}
