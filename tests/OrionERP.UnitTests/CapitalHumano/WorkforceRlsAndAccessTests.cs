using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.CapitalHumano;

/// <summary>
/// Cuida las tres decisiones que hacen operable a Capital Humano y que son fáciles
/// de deshacer sin darse cuenta: la guarda de producción del DDL, el alcance real
/// de la seguridad a nivel de fila del esquema rh, y el acceso al expediente.
/// </summary>
public class WorkforceRlsAndAccessTests
{
  private const string SchemaScript =
    "src/OrionERP.Infrastructure/Features/CapitalHumano/Workforce/Sql/20260805_workforce_attendance_mvp.sql";
  private const string RlsScript =
    "src/OrionERP.Infrastructure/Features/CapitalHumano/Workforce/Sql/20260903_zz_rh_rls.sql";

  [Fact]
  public void DdlYRls_ExigenBaseEsperadaYPermitenRevisarSinAplicar()
  {
    foreach (var relativePath in new[] { SchemaScript, RlsScript })
    {
      var sql = RepoFile.Read(relativePath);

      Assert.Contains("$(ExpectedDatabase)", sql, StringComparison.Ordinal);
      Assert.Contains("$(ApplyChanges)", sql, StringComparison.Ordinal);
      Assert.Contains("THROW 51000", sql, StringComparison.Ordinal);
      Assert.Contains("THROW 51001", sql, StringComparison.Ordinal);
      Assert.Contains("ROLLBACK TRANSACTION;", sql, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void PoliticaRh_ProtegeLasTablasConRfc()
  {
    var sql = RepoFile.Read(RlsScript);

    Assert.Contains("CREATE FUNCTION rh.fn_RfcAccessPredicate", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE SECURITY POLICY rh.RfcSecurityPolicy", sql, StringComparison.Ordinal);
    Assert.Contains("ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.TimeEvent", sql, StringComparison.Ordinal);
    Assert.Contains("ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.TimeEvent AFTER INSERT", sql, StringComparison.Ordinal);
    Assert.Contains("ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.EmployeeKioskCredential", sql, StringComparison.Ordinal);
  }

  /// <summary>
  /// El kiosco localiza su dispositivo por el hash del token, antes de saber a qué
  /// empresa pertenece. Si rh.KioskDevice entrara en la política, esa consulta
  /// devolvería cero filas y el kiosco nunca podría vincularse ni registrar.
  /// </summary>
  [Fact]
  public void PoliticaRh_DejaFueraLasTablasQueElKioscoConsultaSinRfc()
  {
    var sql = RepoFile.Read(RlsScript);

    Assert.DoesNotContain("PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.KioskDevice", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.KioskPairingCode", sql, StringComparison.Ordinal);
    Assert.Contains("THROW 51004", sql, StringComparison.Ordinal);
  }

  /// <summary>
  /// La fábrica de conexiones deja SESSION_CONTEXT en '__UNSCOPED__' cuando no hay
  /// sesión, y con la política activa eso equivale a no ver nada. Las tres rutas que
  /// corren sin RFC de sesión tienen que fijarlo o limpiarlo a propósito.
  /// </summary>
  [Fact]
  public void RutasSinSesion_FijanOLimpianElAlcanceDeRfc()
  {
    var baseService = RepoFile.Read("src/OrionERP.Infrastructure/Features/CapitalHumano/Workforce/WorkforceServiceBase.cs");
    Assert.Contains("PinRfcScopeAsync", baseService, StringComparison.Ordinal);
    Assert.Contains("ClearRfcScopeAsync", baseService, StringComparison.Ordinal);

    var kiosk = RepoFile.Read("src/OrionERP.Infrastructure/Features/CapitalHumano/Workforce/KioskAttendanceService.cs");
    Assert.Equal(2, CountOccurrences(kiosk, "WorkforceServiceBase.PinRfcScopeAsync"));

    var attendance = RepoFile.Read("src/OrionERP.Infrastructure/Features/CapitalHumano/Workforce/AttendanceService.cs");
    Assert.Contains("WorkforceServiceBase.PinRfcScopeAsync(connection, null, command.Rfc, ct)", attendance, StringComparison.Ordinal);

    var retention = RepoFile.Read("src/OrionERP.Infrastructure/Features/CapitalHumano/Workforce/WorkforceRetentionMaintenance.cs");
    Assert.Contains("WorkforceServiceBase.ClearRfcScopeAsync", retention, StringComparison.Ordinal);
  }

  /// <summary>
  /// El expediente es anterior al módulo de asistencia: un CapitalHumanoAdmin debe
  /// poder abrirlo, y apagar la asistencia no debe esconderlo.
  /// </summary>
  [Fact]
  public void Expediente_UsaPoliticaPropiaSinDependerDelFlagDeAsistencia()
  {
    var page = RepoFile.Read("src/OrionERP.Web/Features/CapitalHumano/CapitalHumanoPage.razor");
    Assert.Contains("[Authorize(Policy = \"CapitalHumanoExpediente\")]", page, StringComparison.Ordinal);
    Assert.DoesNotContain("[Authorize(Roles = \"Administrador\")]", page, StringComparison.Ordinal);

    var program = RepoFile.Read("src/OrionERP.Web/Program.cs");
    var policyStart = program.IndexOf("options.AddPolicy(\"CapitalHumanoExpediente\"", StringComparison.Ordinal);
    Assert.True(policyStart >= 0, "Falta la politica CapitalHumanoExpediente.");
    var policyBody = program[policyStart..program.IndexOf("});", policyStart, StringComparison.Ordinal)];
    Assert.Contains("RequireCompanyRoles(\"CapitalHumanoAdmin\")", policyBody, StringComparison.Ordinal);
    Assert.DoesNotContain("AttendanceIsEnabled", policyBody, StringComparison.Ordinal);

    // Vive en la seccion de Personas, no en la de administracion: esa se dibuja
    // completa dentro de un AuthorizeView de Administrador.
    var navMenu = RepoFile.Read("src/OrionERP.Web/Shared/NavMenu.razor");
    Assert.Contains("if (path == \"capital-humano\") return admin;", navMenu, StringComparison.Ordinal);

    var catalog = RepoFile.Read("src/OrionERP.Web/Shared/NavigationCatalog.cs");
    var adminSectionStart = catalog.IndexOf("AdminSection { get; } = new(", StringComparison.Ordinal);
    Assert.True(adminSectionStart >= 0);
    Assert.DoesNotContain("\"/capital-humano\"", catalog[adminSectionStart..], StringComparison.Ordinal);
  }

  /// <summary>
  /// Razor trata <c>letra@palabra.palabra</c> como una direccion de correo y lo emite
  /// literal, asi que <c>v@p.Version</c> pintaba el texto "v@p.Version" en la columna
  /// Version en vez del numero. Los parentesis rompen esa heuristica.
  /// </summary>
  [Fact]
  public void Prenomina_PintaLaVersionYNoElTextoDeLaExpresion()
  {
    var page = RepoFile.Read("src/OrionERP.Web/Features/CapitalHumano/Workforce/PrenominaPage.razor");

    Assert.Contains("v@(p.Version)", page, StringComparison.Ordinal);
    Assert.DoesNotContain("v@p.Version", page, StringComparison.Ordinal);
  }

  private static int CountOccurrences(string haystack, string needle)
  {
    var count = 0;
    var index = haystack.IndexOf(needle, StringComparison.Ordinal);
    while (index >= 0)
    {
      count++;
      index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
    }
    return count;
  }
}
