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
      _connString = cfg.GetConnectionString("OrionDB")
          ?? throw new System.InvalidOperationException("Missing connection string 'DefaultConnection'.");
    }

    public async Task UpsertAsync(AppRfcs.SatRfcProfileUpsert dto)
    {
      const string sql = @"
MERGE dbo.SatRfcProfile AS t
USING (SELECT @Rfc AS Rfc) AS s
ON (t.Rfc = s.Rfc)
WHEN MATCHED THEN
    UPDATE SET
        RazonSocial             = COALESCE(@RazonSocial,             t.RazonSocial),
        NombreComercial         = COALESCE(@NombreComercial,         t.NombreComercial),
        RegimenCapital          = COALESCE(@RegimenCapital,          t.RegimenCapital),
        FechaInicioOperaciones  = COALESCE(@FechaInicioOperaciones,  t.FechaInicioOperaciones),
        EstatusPadron           = COALESCE(@EstatusPadron,           t.EstatusPadron),
        FechaUltCambioEstatus   = COALESCE(@FechaUltCambioEstatus,   t.FechaUltCambioEstatus),
        EmisionFecha            = COALESCE(@EmisionFecha,            t.EmisionFecha),
        AddressLine1            = COALESCE(@AddressLine1,            t.AddressLine1),
        AddressLine2            = COALESCE(@AddressLine2,            t.AddressLine2),
        Municipio               = COALESCE(@Municipio,               t.Municipio),
        EntidadFederativa       = COALESCE(@EntidadFederativa,       t.EntidadFederativa),
        CodigoPostal            = COALESCE(@CodigoPostal,            t.CodigoPostal),
        CsfDataJson             = COALESCE(@CsfDataJson,             t.CsfDataJson),
        SATFielCertificate      = CASE WHEN @SATFielCertificate IS NULL OR DATALENGTH(@SATFielCertificate)=0 THEN t.SATFielCertificate ELSE @SATFielCertificate END,
        SATFielKey              = CASE WHEN @SATFielKey         IS NULL OR DATALENGTH(@SATFielKey)        =0 THEN t.SATFielKey         ELSE @SATFielKey         END,
        SATFielPfx              = CASE WHEN @SATFielPfx         IS NULL OR DATALENGTH(@SATFielPfx)        =0 THEN t.SATFielPfx         ELSE @SATFielPfx         END,
        SATFielPasswordEnc      = CASE WHEN @SATFielPasswordEnc IS NULL OR DATALENGTH(@SATFielPasswordEnc)=0 THEN t.SATFielPasswordEnc ELSE @SATFielPasswordEnc END,
        Email                   = COALESCE(@Email,                   t.Email)
WHEN NOT MATCHED THEN
    INSERT (
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
      await con.ExecuteAsync(sql, dto);
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
