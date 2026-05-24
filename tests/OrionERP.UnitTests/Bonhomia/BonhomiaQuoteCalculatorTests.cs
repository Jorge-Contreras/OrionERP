using Microsoft.AspNetCore.DataProtection;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Web.Features.Bonhomia.Checkout;

namespace OrionERP.UnitTests.Bonhomia;

public class BonhomiaQuoteCalculatorTests
{
  [Fact]
  public void BuildQuote_ComputesFullReservationTotal()
  {
    var quote = BonhomiaQuoteCalculator.BuildQuote(
      new BonhomiaQuoteRequest
      {
        RoomName = "Suite Paris",
        CheckIn = new DateOnly(2026, 6, 10),
        CheckOut = new DateOnly(2026, 6, 12),
        Guests = 2,
        Extras =
        [
          new BonhomiaSelectedExtraRequest { Code = "early-checkin", Quantity = 1 }
        ]
      },
      CreateRoom(),
      CreateExtras(),
      DateTimeOffset.UtcNow.AddMinutes(30),
      "MXN",
      60);

    Assert.Equal(2, quote.Nights);
    Assert.Equal(2500m, quote.SuiteSubtotal);
    Assert.Equal(200m, quote.ExtrasSubtotal);
    Assert.Equal(2700m, quote.SubTotal);
    Assert.Equal(432m, quote.Tax);
    Assert.Equal(3132m, quote.Total);
    Assert.False(string.IsNullOrWhiteSpace(quote.Fingerprint));
  }

  [Fact]
  public void BuildQuote_RejectsUnavailableNight()
  {
    var room = CreateRoom();
    room.Days = room.Days
      .Select(day => day.Date == new DateOnly(2026, 6, 11)
        ? new BonhomiaDayAvailabilityDto { Date = day.Date, IsAvailable = false, StateCode = "reserved", Price = day.Price }
        : day)
      .ToArray();

    var ex = Assert.Throws<BonhomiaPublicBookingException>(() => BonhomiaQuoteCalculator.BuildQuote(
      new BonhomiaQuoteRequest
      {
        RoomName = "Suite Paris",
        CheckIn = new DateOnly(2026, 6, 10),
        CheckOut = new DateOnly(2026, 6, 12),
        Guests = 2
      },
      room,
      CreateExtras(),
      DateTimeOffset.UtcNow.AddMinutes(30),
      "MXN",
      60));

    Assert.Equal("not_available", ex.ErrorCode);
  }

  [Fact]
  public void BuildQuote_RejectsCapacityExceeded()
  {
    var ex = Assert.Throws<BonhomiaPublicBookingException>(() => BonhomiaQuoteCalculator.BuildQuote(
      new BonhomiaQuoteRequest
      {
        RoomName = "Suite Paris",
        CheckIn = new DateOnly(2026, 6, 10),
        CheckOut = new DateOnly(2026, 6, 12),
        Guests = 3
      },
      CreateRoom(),
      CreateExtras(),
      DateTimeOffset.UtcNow.AddMinutes(30),
      "MXN",
      60));

    Assert.Equal("capacity_exceeded", ex.ErrorCode);
  }

  [Fact]
  public void QuoteTokenService_RoundTripsCurrentQuote()
  {
    var service = new BonhomiaQuoteTokenService(new EphemeralDataProtectionProvider());
    var quote = CreateQuote();

    var token = service.CreateToken(quote);
    var valid = service.TryValidate(token, out var decoded, out var error);

    Assert.True(valid, error);
    Assert.NotNull(decoded);
    Assert.Equal(quote.Fingerprint, decoded!.Fingerprint);
    Assert.Equal(quote.RoomName, decoded.RoomName);
  }

  [Fact]
  public void QuoteTokenService_RejectsExpiredQuote()
  {
    var service = new BonhomiaQuoteTokenService(new EphemeralDataProtectionProvider());
    var quote = CreateQuote();
    quote.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);

    var token = service.CreateToken(quote);
    var valid = service.TryValidate(token, out _, out var error);

    Assert.False(valid);
    Assert.Contains("expiro", error, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void CreateFingerprint_ChangesWhenTotalChanges()
  {
    var quote = CreateQuote();
    var before = quote.Fingerprint;

    quote.Total += 1m;
    var after = BonhomiaQuoteCalculator.CreateFingerprint(quote);

    Assert.NotEqual(before, after);
  }

  private static BonhomiaQuoteDto CreateQuote()
    => BonhomiaQuoteCalculator.BuildQuote(
      new BonhomiaQuoteRequest
      {
        RoomName = "Suite Paris",
        CheckIn = new DateOnly(2026, 6, 10),
        CheckOut = new DateOnly(2026, 6, 12),
        Guests = 2
      },
      CreateRoom(),
      CreateExtras(),
      DateTimeOffset.UtcNow.AddMinutes(30),
      "MXN",
      60);

  private static BonhomiaRoomAvailabilityDto CreateRoom()
    => new()
    {
      RoomId = 1,
      RoomName = "Suite Paris",
      Capacity = 2,
      Bedrooms = 1,
      BasePrice = 1250m,
      Image = "/Images/Bonhomia/welcome-detail.png",
      Tag = "Acogedora",
      Ideal = "Prueba",
      Days =
      [
        new BonhomiaDayAvailabilityDto { Date = new DateOnly(2026, 6, 10), IsAvailable = true, StateCode = "available", Price = 1250m },
        new BonhomiaDayAvailabilityDto { Date = new DateOnly(2026, 6, 11), IsAvailable = true, StateCode = "available", Price = 1250m }
      ]
    };

  private static IReadOnlyList<BonhomiaExtraOptionDto> CreateExtras()
    =>
    [
      new BonhomiaExtraOptionDto
      {
        Code = "early-checkin",
        Name = "Early check-in",
        Detail = "Ingreso desde 13:00 hrs",
        CatalogName = "CHECK-IN ANTICIPADO",
        UnitPrice = 200m,
        MaxQuantity = 1
      }
    ];
}
