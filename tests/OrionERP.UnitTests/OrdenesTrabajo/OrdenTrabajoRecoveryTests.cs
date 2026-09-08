using System.Data;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Features.OrdenesTrabajo;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.OrdenesTrabajo;

public class OrdenTrabajoRecoveryTests
{
  [Fact]
  public void Permissions_RequireOwnerOrHelperEmployee()
  {
    Assert.False(OrdenTrabajoPermissions.CanExecute(null, ownerEmployeeId: 10, helperEmployeeIds: [20]));
    Assert.False(OrdenTrabajoPermissions.CanExecute(0, ownerEmployeeId: 10, helperEmployeeIds: [20]));
    Assert.True(OrdenTrabajoPermissions.CanExecute(10, ownerEmployeeId: 10, helperEmployeeIds: [20]));
    Assert.True(OrdenTrabajoPermissions.CanExecute(20, ownerEmployeeId: 10, helperEmployeeIds: [20]));
    Assert.False(OrdenTrabajoPermissions.CanExecute(30, ownerEmployeeId: 10, helperEmployeeIds: [20]));
  }

  [Theory]
  [InlineData("Administrador", true)]
  [InlineData("OrdenTrabajoAdmin", true)]
  [InlineData("OrdenTrabajoSupervisor", true)]
  [InlineData("Empleado", false)]
  [InlineData("CapitalHumanoSupervisor", false)]
  public void ManagementVisibility_IsLimitedToWorkOrderPrivilegedRoles(string role, bool expected)
  {
    var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { role };

    Assert.Equal(expected, OrdenTrabajoPermissions.CanAccessManagement(roles.Contains));
  }

  [Fact]
  public async Task StartWorkOrderAsync_RejectsMissingActorEmployee()
  {
    var connection = new FakeQueryDbConnection();
    var service = CreateService(connection);

    var result = await service.StartWorkOrderAsync(42, "admin-user", actorEmployeeId: null);

    Assert.False(result.Success);
    Assert.Contains("responsable o ayudantes", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("SELECT 1 FROM dbo.OrdenTrabajo ot WITH (UPDLOCK, HOLDLOCK) WHERE", Assert.Single(connection.ExecutedCommands).CommandText);
    Assert.True(connection.LastTransaction?.WasRolledBack);
  }

  [Fact]
  public async Task StartWorkOrderAsync_RejectsUnassignedActor()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => false
    };
    var service = CreateService(connection);

    var result = await service.StartWorkOrderAsync(42, "operator-user", actorEmployeeId: 99);

    Assert.False(result.Success);
    Assert.Contains("responsable o ayudantes", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(connection.ExecutedCommands, command => command.CommandText.Contains("OrdenTrabajoParticipante", StringComparison.Ordinal));
    Assert.True(connection.LastTransaction?.WasRolledBack);
  }

  [Theory]
  [InlineData(1, 0, 0, "Todos los pasos")]
  [InlineData(0, 1, 0, "fotografia requerida")]
  [InlineData(0, 0, 1, "incidencia o no aplica requieren notas")]
  public async Task SubmitForReviewAsync_BlocksInvalidStepState(int pendingSteps, int missingRequiredPhotos, int missingNotes, string expectedMessage)
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT CAST(CASE WHEN EXISTS", StringComparison.Ordinal))
        {
          return true;
        }

        if (commandText.Contains("SELECT Estado FROM dbo.OrdenTrabajo", StringComparison.Ordinal))
        {
          return OrdenTrabajoCodes.EstadoEnProceso;
        }

        if (commandText.Contains("Estado = 'PENDIENTE'", StringComparison.Ordinal))
        {
          return pendingSteps;
        }

        if (commandText.Contains("p.PoliticaFoto = 'REQUERIDA'", StringComparison.Ordinal))
        {
          return missingRequiredPhotos;
        }

        if (commandText.Contains("RequiereNotasEnIncidencia", StringComparison.Ordinal)
          && commandText.Contains("RequiereNotasEnNoAplica", StringComparison.Ordinal))
        {
          return missingNotes;
        }

        return 0;
      }
    };
    var service = CreateService(connection);

    var result = await service.SubmitForReviewAsync(42, "operator-user", actorEmployeeId: 10);

    Assert.False(result.Success);
    Assert.Contains(expectedMessage, result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction?.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("SET Estado = 'EN_REVISION'", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RemoveStepEvidenceAsync_ChecksFirstReviewSubmissionBeforeSoftDelete()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => true,
      NonQueryResultFactory = static (_, _) => 1
    };
    var service = CreateService(connection);

    var result = await service.RemoveStepEvidenceAsync(42, 7, 100, "operator-user", actorEmployeeId: 10);

    Assert.True(result.Success);
    var evidenceUpdate = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("UPDATE ev", StringComparison.Ordinal));
    Assert.Contains("Eliminada = 1", evidenceUpdate.CommandText, StringComparison.Ordinal);
    Assert.Contains("NOT EXISTS", evidenceUpdate.CommandText, StringComparison.Ordinal);
    Assert.Contains("ENVIADA_REVISION", evidenceUpdate.CommandText, StringComparison.Ordinal);
    Assert.True(connection.LastTransaction?.WasCommitted);
  }

  [Fact]
  public async Task LinkTransactionAsync_RejectsCrossRfcTransaction()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => false,
      NonQueryResultFactory = static (_, _) => 1
    };
    var service = CreateService(connection);

    var result = await service.LinkTransactionAsync(42, 9001, "supervisor");

    Assert.False(result.Success);
    Assert.Contains("mismo RFC", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO dbo.OrdenTrabajoTransaccion", StringComparison.Ordinal));
    Assert.True(connection.LastTransaction?.WasRolledBack);
  }

  [Fact]
  public async Task DeleteWorkOrderAsync_RemovesOrderWithoutStatusFilter()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (commandText, _) =>
        commandText.Contains("SELECT Folio", StringComparison.Ordinal) ? "OT-2026-000123" : null,
      NonQueryResultFactory = static (_, _) => 1
    };
    var service = CreateService(connection);

    var result = await service.DeleteWorkOrderAsync(42, "supervisor");

    Assert.True(result.Success);
    Assert.Contains("eliminada", result.Message, StringComparison.OrdinalIgnoreCase);

    var transactionDelete = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("DELETE FROM dbo.OrdenTrabajoTransaccion", StringComparison.Ordinal));
    Assert.Contains(transactionDelete.Parameters, parameter => string.Equals(parameter.Name, "Id", StringComparison.OrdinalIgnoreCase)
      && Convert.ToInt32(parameter.Value) == 42);

    var orderDelete = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("DELETE FROM dbo.OrdenTrabajo", StringComparison.Ordinal)
        && !command.CommandText.Contains("OrdenTrabajoTransaccion", StringComparison.Ordinal));
    Assert.DoesNotContain("Estado", orderDelete.CommandText, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction?.WasCommitted);
  }

  [Fact]
  public async Task AddStepEvidenceAsync_StoresCaptureSource()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("OrdenTrabajoParticipante", StringComparison.Ordinal))
        {
          return true;
        }

        if (commandText.Contains("SELECT p.PoliticaFoto", StringComparison.Ordinal))
        {
          return OrdenTrabajoCodes.FotoOpcional;
        }

        if (commandText.Contains("SCOPE_IDENTITY", StringComparison.Ordinal))
        {
          return 123;
        }

        return null;
      }
    };
    var service = CreateService(connection);

    var result = await service.AddStepEvidenceAsync(
      42,
      7,
      new OrdenTrabajoEvidenceCreateRequest
      {
        ImageBytes = [1, 2, 3],
        ThumbnailBytes = [1],
        ContentType = "image/jpeg",
        ThumbnailContentType = "image/jpeg",
        CaptureSource = OrdenTrabajoCodes.EvidenciaCamera,
        CapturedBy = "operator-user",
        ActorEmployeeId = 10
      });

    Assert.True(result.Success);
    var insert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO dbo.OrdenTrabajoEvidencia", StringComparison.Ordinal));
    Assert.Contains("CaptureSource", insert.CommandText, StringComparison.Ordinal);
    Assert.Contains(insert.Parameters, parameter => string.Equals(parameter.Name, "CaptureSource", StringComparison.OrdinalIgnoreCase)
      && string.Equals(parameter.Value?.ToString(), OrdenTrabajoCodes.EvidenciaCamera, StringComparison.Ordinal));
  }

  [Theory]
  [InlineData("2026-12-31T16:20:00", "2027-01-01")]
  [InlineData("2028-02-28T00:00:00", "2028-02-29")]
  [InlineData("2026-09-08T23:59:59", "2026-09-09")]
  public void CleaningSchedule_SchedulesOrderForNextDay(string occupied, string expected)
    => Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
      OrdenTrabajoSchedule.CleaningDate(DateTime.Parse(occupied, System.Globalization.CultureInfo.InvariantCulture)));


  [Fact]
  public void EvidenceCaptureSource_IsSelectedAndMigrated()
  {
    var serviceSource = File.ReadAllText(GetRepoFile("src/OrionERP.Infrastructure/Features/OrdenesTrabajo/OrdenTrabajoService.cs"));
    var sql = File.ReadAllText(GetRepoFile("src/OrionERP.Infrastructure/Features/OrdenesTrabajo/Sql/20260425_ordenes_trabajo_v1.sql"));

    Assert.Contains("ev.CaptureSource", serviceSource, StringComparison.Ordinal);
    Assert.Contains("CaptureSource varchar(20) NOT NULL", sql, StringComparison.Ordinal);
    Assert.Contains("CK_OrdenTrabajoEvidencia_CaptureSource", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void SqlScript_RecreatesDuplicateGuardForCleaningOnly()
  {
    var sql = File.ReadAllText(GetRepoFile("src/OrionERP.Infrastructure/Features/OrdenesTrabajo/Sql/20260425_ordenes_trabajo_v1.sql"));

    Assert.Contains("DROP INDEX IF EXISTS UX_OrdenTrabajo_OpenCleaningRoomDate", sql, StringComparison.Ordinal);
    Assert.Contains("WHERE Codigo = 'LIMPIEZA'", sql, StringComparison.Ordinal);
    Assert.Contains("ON dbo.OrdenTrabajo (RoomId, FechaProgramada)", sql, StringComparison.Ordinal);
    Assert.Contains("AND CategoriaId = ' + CONVERT(varchar(20), @CleaningCategoryId)", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("ON dbo.OrdenTrabajo (RoomId, FechaProgramada, CategoriaId)", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void DetailQuery_ReturnsOnlyActiveEvidenceAndSubmittedFlag()
  {
    var source = File.ReadAllText(GetRepoFile("src/OrionERP.Infrastructure/Features/OrdenesTrabajo/OrdenTrabajoService.cs"));

    Assert.Contains("AS HasBeenSubmittedForReview", source, StringComparison.Ordinal);
    Assert.Contains("AND ev.Eliminada = 0", source, StringComparison.Ordinal);
    Assert.Contains("Falta aplicar el esquema de Ordenes de Trabajo", source, StringComparison.Ordinal);
  }

  [Fact]
  public void StepNoteInput_PersistsDraftWithoutServerRoundTripPerKeystroke()
  {
    var page = File.ReadAllText(GetRepoFile("src/OrionERP.Web/Features/OrdenesTrabajo/OrdenTrabajoDetailPage.razor"));
    var script = File.ReadAllText(GetRepoFile("src/OrionERP.Web/wwwroot/js/orion-work-order-reconnect.js"));

    Assert.Contains("data-orion-work-order-note-draft", page, StringComparison.Ordinal);
    Assert.Contains("@onchange=\"args => SetStepNotes", page, StringComparison.Ordinal);
    Assert.DoesNotContain("@oninput=\"args => SetStepNotes", page, StringComparison.Ordinal);
    Assert.Contains("document.addEventListener(\"input\", persistStepNoteDraft)", script, StringComparison.Ordinal);
    Assert.Contains("window.sessionStorage.setItem(storageKey, noteInput.value)", script, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LegacySeed_RequiresExplicitSuiteAttribution()
  {
    var connection = new FakeQueryDbConnection();
    var result = await new OrdenTrabajoService(new FakeQueryConnectionFactory(connection), companyContext: new TestCompanyContext("BRUNOS260707L26"))
      .SeedCleaningTemplatesFromLegacyAsync("RFC", "test");
    Assert.False(result.Success);
    Assert.Contains("explícitamente", result.Message);
    Assert.Empty(connection.ExecutedCommands);
  }


  [Fact]
  public async Task ChecklistLegacySeed_RequiresExplicitActivityAttribution()
  {
    var connection = new FakeQueryDbConnection();
    var result = await new OrdenTrabajoService(new FakeQueryConnectionFactory(connection), companyContext: new TestCompanyContext("BRUNOS260707L26"))
      .SeedChecklistTemplatesFromLegacyAsync("RFC", "test");
    Assert.False(result.Success);
    Assert.Empty(connection.ExecutedCommands);
  }


  private static OrdenTrabajoService CreateService(FakeQueryDbConnection connection)
  {
    var scalar = connection.ScalarResultFactory;
    connection.ScalarResultFactory = (sql, parameters) => sql.StartsWith("SELECT 1 FROM dbo.OrdenTrabajo ot WITH (UPDLOCK, HOLDLOCK) WHERE", StringComparison.Ordinal)
      ? 1 : scalar?.Invoke(sql, parameters);
    return new OrdenTrabajoService(new FakeQueryConnectionFactory(connection), companyContext: new TestCompanyContext("BRUNOS260707L26"));
  }

  private static string GetRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
    {
      directory = directory.Parent;
    }

    if (directory is null)
    {
      throw new InvalidOperationException("Could not locate repository root.");
    }

    return Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
  }

  private static DataTable Table(string[] columns, params object?[][] rows)
  {
    var table = new DataTable();
    for (var i = 0; i < columns.Length; i++)
    {
      var columnType = rows.FirstOrDefault(row => row.Length > i && row[i] is not null)?[i]?.GetType() ?? typeof(string);
      table.Columns.Add(columns[i], columnType);
    }

    foreach (var row in rows)
    {
      table.Rows.Add(row);
    }

    return table;
  }
}
