using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class LoyaltyService : ILoyaltyService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public LoyaltyService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<LoyaltyMemberDto?> FindMemberAsync(
    string rfc,
    string identifier,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (string.IsNullOrWhiteSpace(identifier))
    {
      return null;
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    var value = identifier.Trim();
    Guid? qrMemberId = null;
    if (value.StartsWith("BRQ1.", StringComparison.OrdinalIgnoreCase))
    {
      qrMemberId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
        """
        SELECT tokenInfo.MemberId
        FROM fidelidad.MemberQrToken tokenInfo
        JOIN fidelidad.MemberAccount member
          ON member.Rfc=tokenInfo.Rfc AND member.Id=tokenInfo.MemberId
        WHERE tokenInfo.Rfc=@Rfc AND tokenInfo.TokenHash=@Hash
          AND tokenInfo.ExpiresAt>SYSUTCDATETIME()
          AND member.[Status]='Active';
        """,
        new { Rfc = normalizedRfc, Hash = HashToken(value) },
        cancellationToken: ct));
      if (!qrMemberId.HasValue)
      {
        return null;
      }
    }

    var normalizedEmail = value.Contains('@') ? NormalizeEmail(value) : null;
    var normalizedPhone = NormalizePhone(value);
    var membershipNumber = value.ToUpperInvariant();
    var row = await conn.QuerySingleOrDefaultAsync<MemberLookupRow>(new CommandDefinition(
      """
      SELECT TOP(1) member.Id,member.MembershipNumber,member.FirstName,member.LastName,
             member.NormalizedEmail,member.NormalizedPhone,member.EmailVerified,member.PhoneVerified,
             member.[Status],member.PointsBalance,member.CreatedAt
      FROM fidelidad.MemberAccount member
      WHERE member.Rfc=@Rfc AND member.[Status]='Active'
        AND
        (
          (@QrMemberId IS NOT NULL AND member.Id=@QrMemberId)
          OR (@NormalizedEmail IS NOT NULL AND member.NormalizedEmail=@NormalizedEmail AND member.EmailVerified=1)
          OR (@NormalizedPhone IS NOT NULL AND member.NormalizedPhone=@NormalizedPhone AND member.PhoneVerified=1)
          OR member.MembershipNumber=@MembershipNumber
        )
      ORDER BY CASE WHEN member.Id=@QrMemberId THEN 0
                    WHEN member.NormalizedEmail=@NormalizedEmail THEN 1
                    WHEN member.NormalizedPhone=@NormalizedPhone THEN 2 ELSE 3 END;
      """,
      new
      {
        Rfc = normalizedRfc,
        QrMemberId = qrMemberId,
        NormalizedEmail = normalizedEmail,
        NormalizedPhone = normalizedPhone,
        MembershipNumber = membershipNumber
      },
      cancellationToken: ct));
    return row is null ? null : MapMember(row);
  }

  public Task<LoyaltyMemberProfileDto?> GetMemberProfileAsync(
    string rfc,
    Guid memberId,
    CancellationToken ct = default)
    => LoadProfileAsync(LogisticsRfc.Require(rfc), memberId, null, ct);

  public Task<LoyaltyMemberProfileDto?> GetMemberProfileByIdentityAsync(
    string rfc,
    string identityUserId,
    CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(identityUserId);
    return LoadProfileAsync(LogisticsRfc.Require(rfc), null, identityUserId, ct);
  }

  public async Task<LoyaltyMemberProfileDto> CreateMemberAsync(
    LoyaltyMemberCreateRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (!request.IsAdultConfirmed)
      throw new InvalidOperationException("La membresía requiere confirmar mayoría de edad.");
    var rfc = LogisticsRfc.Require(request.Rfc);
    var normalizedEmail = NormalizeEmail(request.Email);
    var normalizedPhone = NormalizePhone(request.Phone)
      ?? throw new InvalidOperationException("El teléfono no tiene un formato válido.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      if (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM brunos_auth.AspNetUsers WHERE Id=@Id) THEN 1 ELSE 0 END AS bit);",
        new { Id = request.IdentityUserId },
        tx,
        cancellationToken: ct)))
        throw new InvalidOperationException("La cuenta de acceso no existe.");
      if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS
        (
          SELECT 1 FROM fidelidad.MemberAccount
          WHERE Rfc=@Rfc AND
            (IdentityUserId=@IdentityUserId OR NormalizedEmail=@Email OR NormalizedPhone=@Phone)
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new
        {
          Rfc = rfc,
          request.IdentityUserId,
          Email = normalizedEmail,
          Phone = normalizedPhone
        },
        tx,
        cancellationToken: ct)))
        throw new InvalidOperationException("El correo o teléfono ya pertenece a otra membresía.");

      var memberId = Guid.NewGuid();
      string? membershipNumber = null;
      for (var attempt = 0; attempt < 10 && membershipNumber is null; attempt++)
      {
        var candidate = $"BG{RandomNumberGenerator.GetInt32(0, 100_000_000):D8}";
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
          "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM fidelidad.MemberAccount WHERE Rfc=@Rfc AND MembershipNumber=@Number) THEN 1 ELSE 0 END AS bit);",
          new { Rfc = rfc, Number = candidate },
          tx,
          cancellationToken: ct));
        if (!exists) membershipNumber = candidate;
      }
      if (membershipNumber is null)
        throw new InvalidOperationException("No fue posible generar un número de membresía único.");

      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT fidelidad.MemberAccount
        (
          Id,Rfc,IdentityUserId,MembershipNumber,FirstName,LastName,
          NormalizedEmail,NormalizedPhone,[Status],IsAdultConfirmed
        )
        VALUES
        (
          @Id,@Rfc,@IdentityUserId,@MembershipNumber,@FirstName,@LastName,
          @Email,@Phone,'PendingVerification',1
        );
        """,
        new
        {
          Id = memberId,
          Rfc = rfc,
          request.IdentityUserId,
          MembershipNumber = membershipNumber,
          FirstName = request.FirstName.Trim(),
          LastName = request.LastName.Trim(),
          Email = normalizedEmail,
          Phone = normalizedPhone
        },
        tx,
        cancellationToken: ct));

      await InsertConsentAsync(conn, tx, rfc, memberId, "Privacy", request.PrivacyVersion, true, ct);
      await InsertConsentAsync(conn, tx, rfc, memberId, "Terms", request.TermsVersion, true, ct);
      await InsertConsentAsync(conn, tx, rfc, memberId, "EmailMarketing", request.TermsVersion, request.EmailMarketingConsent, ct);
      await InsertConsentAsync(conn, tx, rfc, memberId, "SmsMarketing", request.TermsVersion, request.SmsMarketingConsent, ct);
      await InsertConsentAsync(conn, tx, rfc, memberId, "WhatsAppMarketing", request.TermsVersion, request.WhatsAppMarketingConsent, ct);

      await tx.CommitAsync(ct);
      return await GetMemberProfileAsync(rfc, memberId, ct)
        ?? throw new InvalidOperationException("No fue posible recuperar la membresía creada.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> UpdateVerificationAsync(
    LoyaltyMemberVerificationRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    using var conn = CreateConnection();
    var affected = await conn.ExecuteAsync(new CommandDefinition(
      """
      UPDATE fidelidad.MemberAccount
      SET EmailVerified=CASE WHEN @EmailVerified=1 THEN 1 ELSE EmailVerified END,
          PhoneVerified=CASE WHEN @PhoneVerified=1 THEN 1 ELSE PhoneVerified END,
          [Status]=CASE
            WHEN (EmailVerified=1 OR @EmailVerified=1)
            THEN 'Active' ELSE [Status] END,
          UpdatedAt=SYSUTCDATETIME()
      WHERE Rfc=@Rfc AND Id=@MemberId AND [Status]<>'Closed';
      """,
      new
      {
        Rfc = rfc,
        request.MemberId,
        request.EmailVerified,
        request.PhoneVerified
      },
      cancellationToken: ct));
    return affected == 0
      ? RestaurantCommandResult.Fail("La membresía no existe o está cerrada.")
      : RestaurantCommandResult.Ok("La verificación fue actualizada.");
  }

  public async Task<LoyaltyQrTokenDto> CreateQrTokenAsync(
    string rfc,
    Guid memberId,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var token = $"BRQ1.{Convert.ToHexString(RandomNumberGenerator.GetBytes(24))}";
    var expiresAt = DateTime.UtcNow.AddMinutes(5);
    using var conn = CreateConnection();
    var affected = await conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT fidelidad.MemberQrToken(Id,Rfc,MemberId,TokenHash,ExpiresAt)
      SELECT @Id,@Rfc,@MemberId,@Hash,@ExpiresAt
      WHERE EXISTS
      (
        SELECT 1 FROM fidelidad.MemberAccount
        WHERE Rfc=@Rfc AND Id=@MemberId AND [Status]='Active'
          AND EmailVerified=1
      );
      """,
      new
      {
        Id = Guid.NewGuid(),
        Rfc = normalizedRfc,
        MemberId = memberId,
        Hash = HashToken(token),
        ExpiresAt = expiresAt
      },
      cancellationToken: ct));
    if (affected == 0)
      throw new InvalidOperationException("La membresía debe estar activa y verificada para generar el QR.");
    await conn.ExecuteAsync(new CommandDefinition(
      """
      DELETE FROM fidelidad.MemberQrToken
      WHERE Rfc=@Rfc AND ExpiresAt<DATEADD(hour,-1,SYSUTCDATETIME());
      """,
      new { Rfc = normalizedRfc },
      cancellationToken: ct));
    return new LoyaltyQrTokenDto { Token = token, ExpiresAtUtc = expiresAt };
  }

  public async Task<RestaurantCommandResult> AdjustPointsAsync(
    LoyaltyAdjustmentRequest request,
    string adjustedBy,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (request.PointsDelta == 0 || string.IsNullOrWhiteSpace(request.Reason))
      return RestaurantCommandResult.Fail("El ajuste requiere puntos y motivo.");
    var rfc = LogisticsRfc.Require(request.Rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var balance = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
        "SELECT PointsBalance FROM fidelidad.MemberAccount WITH(UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@MemberId AND [Status]<>'Closed';",
        new { Rfc = rfc, request.MemberId },
        tx,
        cancellationToken: ct));
      if (!balance.HasValue)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La membresía no existe o está cerrada.");
      }
      var newBalance = balance.Value + request.PointsDelta;
      if (newBalance < 0)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El ajuste no puede dejar un saldo negativo.");
      }
      var sourceKey = $"adjustment:{Guid.NewGuid():N}";
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE fidelidad.MemberAccount
        SET PointsBalance=@NewBalance,UpdatedAt=SYSUTCDATETIME()
        WHERE Rfc=@Rfc AND Id=@MemberId;
        INSERT fidelidad.PointLedger
          (Rfc,MemberId,EntryType,PointsDelta,BalanceAfter,SourceKey,Reason,CreatedBy)
        VALUES
          (@Rfc,@MemberId,'AdminAdjustment',@PointsDelta,@NewBalance,@SourceKey,@Reason,@AdjustedBy);
        """,
        new
        {
          Rfc = rfc,
          request.MemberId,
          request.PointsDelta,
          NewBalance = newBalance,
          SourceKey = sourceKey,
          Reason = request.Reason.Trim(),
          AdjustedBy = adjustedBy
        },
        tx,
        cancellationToken: ct));
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok($"Saldo actualizado a {newBalance} puntos.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> RequestClosureAsync(
    LoyaltyClosureRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var affected = await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT fidelidad.MemberClosureRequest(Id,Rfc,MemberId,Reason,[Status])
        SELECT @Id,@Rfc,@MemberId,@Reason,'Pending'
        WHERE EXISTS
        (
          SELECT 1 FROM fidelidad.MemberAccount WITH(UPDLOCK,HOLDLOCK)
          WHERE Rfc=@Rfc AND Id=@MemberId AND [Status]<>'Closed'
        );
        UPDATE fidelidad.MemberAccount
        SET [Status]='Closed',ClosedAt=SYSUTCDATETIME(),UpdatedAt=SYSUTCDATETIME(),
            FirstName=N'Miembro',LastName=N'cerrado',
            NormalizedEmail=CONCAT('CLOSED-',CONVERT(varchar(36),Id)),
            NormalizedPhone=CONCAT('CLOSED-',CONVERT(varchar(36),Id)),
            EmailVerified=0,PhoneVerified=0
        WHERE Rfc=@Rfc AND Id=@MemberId AND [Status]<>'Closed';
        INSERT fidelidad.MemberConsent(Rfc,MemberId,ConsentType,DocumentVersion,IsGranted,Source)
        SELECT @Rfc,@MemberId,consentType,'closure',0,'MemberPortal'
        FROM (VALUES('EmailMarketing'),('SmsMarketing'),('WhatsAppMarketing')) valueInfo(consentType)
        WHERE EXISTS(SELECT 1 FROM fidelidad.MemberAccount WHERE Rfc=@Rfc AND Id=@MemberId);
        UPDATE identityUser
        SET UserName=CONCAT('closed-',CONVERT(nvarchar(36),@MemberId)),
            NormalizedUserName=UPPER(CONCAT('closed-',CONVERT(nvarchar(36),@MemberId))),
            Email=NULL,NormalizedEmail=NULL,EmailConfirmed=0,
            PhoneNumber=NULL,PhoneNumberConfirmed=0,
            PasswordHash=NULL,SecurityStamp=CONVERT(nvarchar(36),NEWID()),
            FirstName=N'Miembro',LastName=N'cerrado',
            ClosedAt=SYSUTCDATETIME(),LockoutEnd='9999-12-31T23:59:59+00:00'
        FROM brunos_auth.AspNetUsers identityUser
        JOIN fidelidad.MemberAccount member
          ON member.IdentityUserId=identityUser.Id
        WHERE member.Rfc=@Rfc AND member.Id=@MemberId;
        """,
        new
        {
          Id = Guid.NewGuid(),
          Rfc = rfc,
          request.MemberId,
          Reason = request.Reason.Trim()
        },
        tx,
        cancellationToken: ct));
      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La membresía no existe o ya está cerrada.");
      }
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("La membresía fue desactivada y la solicitud de privacidad quedó registrada.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> UpdateConsentsAsync(
    LoyaltyConsentUpdateRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var memberExists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS
        (
          SELECT 1 FROM fidelidad.MemberAccount WITH(UPDLOCK,HOLDLOCK)
          WHERE Rfc=@Rfc AND Id=@MemberId AND [Status]<>'Closed'
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new { Rfc = rfc, request.MemberId },
        tx,
        cancellationToken: ct));
      if (!memberExists)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La membresía no existe o está cerrada.");
      }

      await InsertConsentAsync(conn, tx, rfc, request.MemberId, "Privacy", request.PrivacyVersion, true, ct);
      await InsertConsentAsync(conn, tx, rfc, request.MemberId, "Terms", request.TermsVersion, true, ct);
      await InsertConsentAsync(conn, tx, rfc, request.MemberId, "EmailMarketing", request.TermsVersion, request.EmailMarketingConsent, ct);
      await InsertConsentAsync(conn, tx, rfc, request.MemberId, "SmsMarketing", request.TermsVersion, request.SmsMarketingConsent, ct);
      await InsertConsentAsync(conn, tx, rfc, request.MemberId, "WhatsAppMarketing", request.TermsVersion, request.WhatsAppMarketingConsent, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("Tus preferencias quedaron actualizadas.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LoyaltyProgramReportDto> GetReportAsync(
    string rfc,
    DateTime from,
    DateTime to,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    return await conn.QuerySingleAsync<LoyaltyProgramReportDto>(new CommandDefinition(
      """
      SELECT
        (SELECT COUNT(*) FROM fidelidad.MemberAccount WHERE Rfc=@Rfc AND [Status]='Active') AS ActiveMembers,
        (SELECT COUNT(*) FROM fidelidad.MemberAccount WHERE Rfc=@Rfc AND CreatedAt>=@From AND CreatedAt<DATEADD(day,1,@To)) AS NewMembers,
        (SELECT ISNULL(SUM(CASE WHEN PointsDelta>0 THEN PointsDelta ELSE 0 END),0)
         FROM fidelidad.PointLedger WHERE Rfc=@Rfc AND OccurredAt>=@From AND OccurredAt<DATEADD(day,1,@To)) AS PointsIssued,
        (SELECT ABS(ISNULL(SUM(CASE WHEN EntryType='RefundReversal' THEN PointsDelta ELSE 0 END),0))
         FROM fidelidad.PointLedger WHERE Rfc=@Rfc AND OccurredAt>=@From AND OccurredAt<DATEADD(day,1,@To)) AS PointsReversed,
        (SELECT ISNULL(SUM(PointsBalance),0) FROM fidelidad.MemberAccount WHERE Rfc=@Rfc) AS OutstandingPoints;
      """,
      new { Rfc = normalizedRfc, From = from.Date, To = to.Date },
      cancellationToken: ct));
  }

  private async Task<LoyaltyMemberProfileDto?> LoadProfileAsync(
    string rfc,
    Guid? memberId,
    string? identityUserId,
    CancellationToken ct)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    var profile = await conn.QuerySingleOrDefaultAsync<LoyaltyMemberProfileDto>(new CommandDefinition(
      """
      SELECT member.Id,member.MembershipNumber,member.FirstName,member.LastName,
             userInfo.Email,userInfo.PhoneNumber AS Phone,
             member.EmailVerified,member.PhoneVerified,member.[Status],
             member.PointsBalance,member.CreatedAt
      FROM fidelidad.MemberAccount member
      JOIN brunos_auth.AspNetUsers userInfo ON userInfo.Id=member.IdentityUserId
      WHERE member.Rfc=@Rfc
        AND (@MemberId IS NULL OR member.Id=@MemberId)
        AND (@IdentityUserId IS NULL OR member.IdentityUserId=@IdentityUserId);
      """,
      new { Rfc = rfc, MemberId = memberId, IdentityUserId = identityUserId },
      cancellationToken: ct));
    if (profile is null)
    {
      return null;
    }

    profile.MaskedEmail = MaskEmail(profile.Email);
    profile.MaskedPhone = MaskPhone(profile.Phone);
    var consents = (await conn.QueryAsync<ConsentRow>(new CommandDefinition(
      """
      WITH latest AS
      (
        SELECT ConsentType,IsGranted,
               ROW_NUMBER() OVER(PARTITION BY ConsentType ORDER BY RecordedAt DESC,Id DESC) AS rowNumber
        FROM fidelidad.MemberConsent
        WHERE Rfc=@Rfc AND MemberId=@MemberId
      )
      SELECT ConsentType,IsGranted FROM latest WHERE rowNumber=1;
      """,
      new { Rfc = rfc, MemberId = profile.Id },
      cancellationToken: ct))).AsList();
    profile.EmailMarketingConsent = consents.FirstOrDefault(row => row.ConsentType == "EmailMarketing")?.IsGranted ?? false;
    profile.SmsMarketingConsent = consents.FirstOrDefault(row => row.ConsentType == "SmsMarketing")?.IsGranted ?? false;
    profile.WhatsAppMarketingConsent = consents.FirstOrDefault(row => row.ConsentType == "WhatsAppMarketing")?.IsGranted ?? false;
    profile.PointHistory = (await conn.QueryAsync<LoyaltyPointLedgerDto>(new CommandDefinition(
      """
      SELECT TOP(100) Id,EntryType,PointsDelta,BalanceAfter,OrderId,RefundId,Reason,OccurredAt
      FROM fidelidad.PointLedger
      WHERE Rfc=@Rfc AND MemberId=@MemberId
      ORDER BY OccurredAt DESC,Id DESC;
      """,
      new { Rfc = rfc, MemberId = profile.Id },
      cancellationToken: ct))).AsList();
    return profile;
  }

  private static LoyaltyMemberDto MapMember(MemberLookupRow row)
    => new()
    {
      Id = row.Id,
      MembershipNumber = row.MembershipNumber,
      FirstName = row.FirstName,
      LastName = row.LastName,
      MaskedEmail = MaskEmail(row.NormalizedEmail),
      MaskedPhone = MaskPhone(row.NormalizedPhone),
      EmailVerified = row.EmailVerified,
      PhoneVerified = row.PhoneVerified,
      Status = row.Status,
      PointsBalance = row.PointsBalance,
      CreatedAt = row.CreatedAt
    };

  private static Task InsertConsentAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    Guid memberId,
    string type,
    string version,
    bool granted,
    CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT fidelidad.MemberConsent(Rfc,MemberId,ConsentType,DocumentVersion,IsGranted,Source)
      VALUES(@Rfc,@MemberId,@Type,@Version,@Granted,'Website');
      """,
      new { Rfc = rfc, MemberId = memberId, Type = type, Version = version, Granted = granted },
      tx,
      cancellationToken: ct));

  internal static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

  internal static string? NormalizePhone(string? phone)
  {
    if (string.IsNullOrWhiteSpace(phone)) return null;
    var digits = new string(phone.Where(char.IsDigit).ToArray());
    if (digits.Length == 10) digits = $"52{digits}";
    return digits.Length is >= 12 and <= 15 ? digits : null;
  }

  private static string HashToken(string token)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

  private static string MaskEmail(string? email)
  {
    if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return string.Empty;
    var parts = email.Split('@', 2);
    var visible = parts[0].Length <= 2 ? parts[0][..1] : parts[0][..2];
    return $"{visible}***@{parts[1].ToLowerInvariant()}";
  }

  private static string MaskPhone(string? phone)
  {
    var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
    return digits.Length < 4 ? string.Empty : $"*** *** {digits[^4..]}";
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica no devolvió una DbConnection.");

  private sealed class MemberLookupRow
  {
    public Guid Id { get; set; }
    public string MembershipNumber { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string NormalizedPhone { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public bool PhoneVerified { get; set; }
    public string Status { get; set; } = string.Empty;
    public int PointsBalance { get; set; }
    public DateTime CreatedAt { get; set; }
  }
  private sealed class ConsentRow
  {
    public string ConsentType { get; set; } = string.Empty;
    public bool IsGranted { get; set; }
  }
}

internal static class RestaurantLoyaltyTransaction
{
  internal static async Task<LoyaltyMemberSnapshot?> ValidateMemberAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    Guid? memberId,
    CancellationToken ct)
  {
    if (!memberId.HasValue)
    {
      return null;
    }
    return await conn.QuerySingleOrDefaultAsync<LoyaltyMemberSnapshot>(new CommandDefinition(
      """
      SELECT Id,MembershipNumber,PointsBalance
      FROM fidelidad.MemberAccount WITH(UPDLOCK,HOLDLOCK)
      WHERE Rfc=@Rfc AND Id=@MemberId AND [Status]='Active'
        AND EmailVerified=1;
      """,
      new { Rfc = rfc, MemberId = memberId.Value },
      tx,
      cancellationToken: ct))
      ?? throw new InvalidOperationException("La membresía no está activa o le falta verificación.");
  }

  internal static async Task<LoyaltyAwardResult?> AwardPaidOrderAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    Guid orderId,
    Guid? memberId,
    decimal eligibleMerchandise,
    string createdBy,
    CancellationToken ct)
  {
    if (!memberId.HasValue)
    {
      return null;
    }
    var settings = await conn.QuerySingleOrDefaultAsync<ProgramSettingsRow>(new CommandDefinition(
      """
      SELECT PesosPerPoint,IsAccrualEnabled
      FROM fidelidad.ProgramSettings WITH(UPDLOCK,HOLDLOCK)
      WHERE Rfc=@Rfc;
      """,
      new { Rfc = rfc },
      tx,
      cancellationToken: ct));
    if (settings is null || !settings.IsAccrualEnabled)
    {
      return null;
    }

    var sourceKey = $"order:{orderId:N}:earn";
    var existing = await conn.QuerySingleOrDefaultAsync<LoyaltyAwardResult>(new CommandDefinition(
      """
      SELECT ledger.PointsDelta AS Points,ledger.BalanceAfter
      FROM fidelidad.PointLedger ledger
      WHERE ledger.Rfc=@Rfc AND ledger.SourceKey=@SourceKey;
      """,
      new { Rfc = rfc, SourceKey = sourceKey },
      tx,
      cancellationToken: ct));
    if (existing is not null)
    {
      return existing;
    }

    var member = await ValidateMemberAsync(conn, tx, rfc, memberId, ct)
      ?? throw new InvalidOperationException("La orden no tiene una membresía válida.");
    var points = LoyaltyPointsCalculator.CalculateEarnedPoints(eligibleMerchandise, settings.PesosPerPoint);
    if (points <= 0)
    {
      await conn.ExecuteAsync(new CommandDefinition(
        "UPDATE restaurante.[Order] SET PointsEarned=0 WHERE Rfc=@Rfc AND Id=@OrderId;",
        new { Rfc = rfc, OrderId = orderId },
        tx,
        cancellationToken: ct));
      return new LoyaltyAwardResult(0, member.PointsBalance);
    }

    var balanceAfter = checked(member.PointsBalance + points);
    await conn.ExecuteAsync(new CommandDefinition(
      """
      UPDATE fidelidad.MemberAccount
      SET PointsBalance=@BalanceAfter,UpdatedAt=SYSUTCDATETIME()
      WHERE Rfc=@Rfc AND Id=@MemberId;
      INSERT fidelidad.PointLedger
        (Rfc,MemberId,EntryType,PointsDelta,BalanceAfter,EligibleMerchandiseAmount,
         OrderId,SourceKey,Reason,CreatedBy)
      VALUES
        (@Rfc,@MemberId,'Earn',@Points,@BalanceAfter,@EligibleMerchandise,
         @OrderId,@SourceKey,N'Compra pagada',@CreatedBy);
      UPDATE restaurante.[Order]
      SET PointsEarned=@Points
      WHERE Rfc=@Rfc AND Id=@OrderId;
      """,
      new
      {
        Rfc = rfc,
        MemberId = memberId.Value,
        Points = points,
        BalanceAfter = balanceAfter,
        EligibleMerchandise = eligibleMerchandise,
        OrderId = orderId,
        SourceKey = sourceKey,
        CreatedBy = createdBy
      },
      tx,
      cancellationToken: ct));
    return new LoyaltyAwardResult(points, balanceAfter);
  }

  internal static async Task<LoyaltyAwardResult?> AwardExistingPaidOrderAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    Guid orderId,
    string createdBy,
    CancellationToken ct)
  {
    var order = await conn.QuerySingleOrDefaultAsync<PaidOrderLoyaltyRow>(new CommandDefinition(
      """
      SELECT MemberId,EligibleMerchandiseTotal
      FROM restaurante.[Order] WITH(UPDLOCK,HOLDLOCK)
      WHERE Rfc=@Rfc AND Id=@OrderId AND PaymentStatus='Paid';
      """,
      new { Rfc = rfc, OrderId = orderId },
      tx,
      cancellationToken: ct));
    return order is null
      ? null
      : await AwardPaidOrderAsync(
        conn, tx, rfc, orderId, order.MemberId, order.EligibleMerchandiseTotal, createdBy, ct);
  }

  internal static async Task<LoyaltyAwardResult?> ReverseRefundAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    Guid orderId,
    Guid refundId,
    string createdBy,
    CancellationToken ct)
  {
    var order = await conn.QuerySingleOrDefaultAsync<RefundLoyaltyRow>(new CommandDefinition(
      """
      SELECT orderInfo.MemberId,orderInfo.Total,orderInfo.EligibleMerchandiseTotal,orderInfo.PointsEarned,
             CAST(ISNULL((SELECT SUM(refund.Amount)
                          FROM restaurante.PaymentRefund refund
                          JOIN restaurante.Payment paymentInfo
                            ON paymentInfo.Rfc=refund.Rfc AND paymentInfo.Id=refund.PaymentId
                          WHERE refund.Rfc=orderInfo.Rfc AND paymentInfo.OrderId=orderInfo.Id),0) AS decimal(18,2)) AS RefundedTotal,
             CAST(ISNULL((SELECT SUM(ledger.PointsDelta)
                          FROM fidelidad.PointLedger ledger
                          WHERE ledger.Rfc=orderInfo.Rfc AND ledger.OrderId=orderInfo.Id
                            AND ledger.EntryType='RefundReversal'),0) AS int) AS ReversedPoints
      FROM restaurante.[Order] orderInfo WITH(UPDLOCK,HOLDLOCK)
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.Id=@OrderId;
      """,
      new { Rfc = rfc, OrderId = orderId },
      tx,
      cancellationToken: ct));
    if (order?.MemberId is null || order.PointsEarned <= 0)
    {
      return null;
    }

    var sourceKey = $"refund:{refundId:N}:points";
    var existing = await conn.QuerySingleOrDefaultAsync<LoyaltyAwardResult>(new CommandDefinition(
      "SELECT PointsDelta AS Points,BalanceAfter FROM fidelidad.PointLedger WHERE Rfc=@Rfc AND SourceKey=@SourceKey;",
      new { Rfc = rfc, SourceKey = sourceKey },
      tx,
      cancellationToken: ct));
    if (existing is not null)
    {
      return existing;
    }

    var member = await conn.QuerySingleAsync<LoyaltyMemberSnapshot>(new CommandDefinition(
      "SELECT Id,MembershipNumber,PointsBalance FROM fidelidad.MemberAccount WITH(UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@MemberId;",
      new { Rfc = rfc, MemberId = order.MemberId.Value },
      tx,
      cancellationToken: ct));
    var settings = await conn.QuerySingleAsync<ProgramSettingsRow>(new CommandDefinition(
      "SELECT PesosPerPoint,IsAccrualEnabled FROM fidelidad.ProgramSettings WHERE Rfc=@Rfc;",
      new { Rfc = rfc },
      tx,
      cancellationToken: ct));
    var refundCalculation = LoyaltyPointsCalculator.CalculateRefund(
      order.Total,
      order.EligibleMerchandiseTotal,
      order.RefundedTotal,
      order.PointsEarned,
      Math.Abs(order.ReversedPoints),
      member.PointsBalance,
      settings.PesosPerPoint);
    if (refundCalculation.PointsToReverse <= 0)
    {
      return new LoyaltyAwardResult(0, member.PointsBalance);
    }

    var actualReversal = refundCalculation.PointsToReverse;
    var balanceAfter = member.PointsBalance - actualReversal;
    await conn.ExecuteAsync(new CommandDefinition(
      """
      UPDATE fidelidad.MemberAccount
      SET PointsBalance=@BalanceAfter,UpdatedAt=SYSUTCDATETIME()
      WHERE Rfc=@Rfc AND Id=@MemberId;
      INSERT fidelidad.PointLedger
        (Rfc,MemberId,EntryType,PointsDelta,BalanceAfter,EligibleMerchandiseAmount,
         OrderId,RefundId,SourceKey,Reason,CreatedBy)
      VALUES
        (@Rfc,@MemberId,'RefundReversal',@PointsDelta,@BalanceAfter,@RetainedEligible,
         @OrderId,@RefundId,@SourceKey,N'Reversión proporcional por reembolso',@CreatedBy);
      """,
      new
      {
        Rfc = rfc,
        MemberId = order.MemberId.Value,
        PointsDelta = -actualReversal,
        BalanceAfter = balanceAfter,
        RetainedEligible = refundCalculation.RetainedEligibleMerchandise,
        OrderId = orderId,
        RefundId = refundId,
        SourceKey = sourceKey,
        CreatedBy = createdBy
      },
      tx,
      cancellationToken: ct));
    return new LoyaltyAwardResult(-actualReversal, balanceAfter);
  }

  internal sealed class LoyaltyMemberSnapshot
  {
    public Guid Id { get; set; }
    public string MembershipNumber { get; set; } = string.Empty;
    public int PointsBalance { get; set; }
  }
  internal sealed record LoyaltyAwardResult(int Points, int BalanceAfter);
  private sealed class ProgramSettingsRow
  {
    public decimal PesosPerPoint { get; set; }
    public bool IsAccrualEnabled { get; set; }
  }
  private sealed class PaidOrderLoyaltyRow
  {
    public Guid? MemberId { get; set; }
    public decimal EligibleMerchandiseTotal { get; set; }
  }
  private sealed class RefundLoyaltyRow
  {
    public Guid? MemberId { get; set; }
    public decimal Total { get; set; }
    public decimal EligibleMerchandiseTotal { get; set; }
    public int PointsEarned { get; set; }
    public decimal RefundedTotal { get; set; }
    public int ReversedPoints { get; set; }
  }
}
