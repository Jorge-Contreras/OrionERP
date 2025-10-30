using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Rfcs.Contracts;
using AppRfcs = OrionERP.Application.Features.Rfcs.Contracts;
namespace OrionERP.Infrastructure.Features.Rfcs.Dapper
{
  public sealed class SatRfcProfileRepository : AppRfcs.ISatRfcProfileRepository
  {
    private readonly string _connString;

    public SatRfcProfileRepository(IConfiguration cfg)
    {
      _connString = cfg.GetConnectionString("OrionDb")
          ?? throw new System.InvalidOperationException("Missing connection string 'OrionDb'.");
    }

    public async Task UpsertAsync(AppRfcs.SatRfcProfileUpsert dto)
    {
      const string existsSql = @"SELECT 1 FROM dbo.SatRfcProfile WHERE Rfc = @Rfc";
      const string updateSql = @"
UPDATE dbo.SatRfcProfile
SET
    RazonSocial             = COALESCE(@RazonSocial,             RazonSocial),
    NombreComercial         = COALESCE(@NombreComercial,         NombreComercial),
    RegimenCapital          = COALESCE(@RegimenCapital,          RegimenCapital),
    FechaInicioOperaciones  = COALESCE(@FechaInicioOperaciones,  FechaInicioOperaciones),
    EstatusPadron           = COALESCE(@EstatusPadron,           EstatusPadron),
    FechaUltCambioEstatus   = COALESCE(@FechaUltCambioEstatus,   FechaUltCambioEstatus),
    EmisionFecha            = COALESCE(@EmisionFecha,            EmisionFecha),
    AddressLine1            = COALESCE(@AddressLine1,            AddressLine1),
    AddressLine2            = COALESCE(@AddressLine2,            AddressLine2),
    Municipio               = COALESCE(@Municipio,               Municipio),
    EntidadFederativa       = COALESCE(@EntidadFederativa,       EntidadFederativa),
    CodigoPostal            = COALESCE(@CodigoPostal,            CodigoPostal),
    CsfDataJson             = COALESCE(@CsfDataJson,             CsfDataJson),
    SATFielCertificate      = CASE WHEN @SATFielCertificate IS NULL OR DATALENGTH(@SATFielCertificate)=0 THEN SATFielCertificate ELSE @SATFielCertificate END,
    SATFielKey              = CASE WHEN @SATFielKey         IS NULL OR DATALENGTH(@SATFielKey)        =0 THEN SATFielKey         ELSE @SATFielKey         END,
    SATFielPfx              = CASE WHEN @SATFielPfx         IS NULL OR DATALENGTH(@SATFielPfx)        =0 THEN SATFielPfx         ELSE @SATFielPfx         END,
    SATFielPasswordEnc      = CASE WHEN @SATFielPasswordEnc IS NULL OR DATALENGTH(@SATFielPasswordEnc)=0 THEN SATFielPasswordEnc ELSE @SATFielPasswordEnc END,
    Email                   = COALESCE(@Email,                   Email)
WHERE Rfc = @Rfc;";
      const string insertSql = @"
INSERT INTO dbo.SatRfcProfile (
    Rfc, RazonSocial, NombreComercial, RegimenCapital, FechaInicioOperaciones,
    EstatusPadron, FechaUltCambioEstatus, EmisionFecha, AddressLine1, AddressLine2,
    Municipio, EntidadFederativa, CodigoPostal, CsfDataJson,
    SATFielCertificate, SATFielKey, SATFielPfx, SATFielPasswordEnc, Email
)
VALUES (
    @Rfc, @RazonSocial, @NombreComercial, @RegimenCapital, @FechaInicioOperaciones,
    @EstatusPadron, @FechaUltCambioEstatus, @EmisionFecha, @AddressLine1, @AddressLine2,
    @Municipio, @EntidadFederativa, @CodigoPostal, @CsfDataJson,
    @SATFielCertificate, @SATFielKey, @SATFielPfx, @SATFielPasswordEnc, @Email
);";

      await using var con = new SqlConnection(_connString);
      var exists = await con.ExecuteScalarAsync<int?>(existsSql, new { dto.Rfc });
      if (exists.HasValue)
      {
        await con.ExecuteAsync(updateSql, dto);
      }
      else
      {
        await con.ExecuteAsync(insertSql, dto);
      }
    }

    public async Task<SatRfcProfile?> GetAsync(string rfc)
    {
      const string sql = @"
SELECT
    Rfc, RazonSocial, NombreComercial, RegimenCapital, FechaInicioOperaciones,
    EstatusPadron, FechaUltCambioEstatus, EmisionFecha, AddressLine1, AddressLine2,
    Municipio, EntidadFederativa, CodigoPostal, CsfDataJson,
    SATFielCertificate, SATFielKey, SATFielPfx, SATFielPasswordEnc, Email
FROM dbo.SatRfcProfile
WHERE Rfc = @rfc;";

      await using var con = new SqlConnection(_connString);
      return await con.QuerySingleOrDefaultAsync<SatRfcProfile>(sql, new { rfc });
    }
  }
}
