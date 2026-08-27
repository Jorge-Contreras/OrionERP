using System.Data;
using OrionERP.Application.Features.Logistica.PhysicalCounts;
using OrionERP.Infrastructure.Features.Logistica.PhysicalCounts;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class PhysicalCountServiceTests
{
  [Fact]
  public async Task GetSessionsAsync_MapsCountedLineProgress()
  {
    var resultTable = new DataTable();
    resultTable.Columns.Add("Id", typeof(int));
    resultTable.Columns.Add("LineCount", typeof(int));
    resultTable.Columns.Add("CountedLineCount", typeof(int));
    resultTable.Rows.Add(51, 8, 3);

    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => resultTable
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.GetSessionsAsync();

    var session = Assert.Single(result);
    Assert.Equal(8, session.LineCount);
    Assert.Equal(3, session.CountedLineCount);
    var commandText = Assert.Single(connection.ExecutedCommands).CommandText;
    Assert.Contains("s.[Status] = 'Recount'", commandText, StringComparison.Ordinal);
    Assert.Contains("activePlanLine.Id IS NOT NULL AND line.CountedQuantity IS NOT NULL", commandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CaptureLineAsync_RejectsStaleSaveAndDoesNotInsertAttachment()
  {
    var originalCapturedAt = new DateTime(2026, 8, 27, 14, 0, 0, DateTimeKind.Utc);
    var newerCapturedAt = originalCapturedAt.AddMinutes(2);
    var lockedLineTable = new DataTable();
    lockedLineTable.Columns.Add("Id", typeof(int));
    lockedLineTable.Columns.Add("CapturedAt", typeof(DateTime));
    lockedLineTable.Rows.Add(10, newerCapturedAt);

    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) => commandText.Contains("SELECT [Status]", StringComparison.Ordinal) ? "Draft" : null,
      ReaderResultFactory = (commandText, _) => commandText.Contains("WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal)
        ? lockedLineTable
        : new DataTable(),
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CaptureLineAsync(new PhysicalCountLineCaptureRequest
    {
      SessionId = 51,
      LineId = 10,
      ExpectedCapturedAt = originalCapturedAt,
      CountedQuantity = 12.5m,
      CapturedBy = "contador@orionerp.local",
      AttachmentBytes = [1, 2, 3],
      AttachmentFileName = "evidencia.jpg",
      AttachmentContentType = "image/jpeg"
    });

    Assert.False(result.Success);
    Assert.Equal("Otro empleado actualizó este material. Se recargó el conteo para proteger su captura.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("SET CountedQuantity = @CountedQuantity", StringComparison.Ordinal));
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountAttachment", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RequestRecountAsync_Fails_WhenNoLinesAreSelected()
  {
    var connection = new FakeQueryDbConnection();
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.RequestRecountAsync(new PhysicalCountRecountRequest
    {
      SessionId = 51,
      RequestedBy = "admin@orionerp.local"
    });

    Assert.False(result.Success);
    Assert.Equal("Selecciona al menos una línea para enviar a reconteo.", result.Message);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task RequestRecountAsync_Fails_WhenIssueCodeIsInvalid()
  {
    var connection = new FakeQueryDbConnection();
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.RequestRecountAsync(new PhysicalCountRecountRequest
    {
      SessionId = 51,
      RequestedBy = "admin@orionerp.local",
      Lines =
      [
        new PhysicalCountRecountLineRequest
        {
          LineId = 10,
          IssueCode = "Unknown",
          Reason = "La cantidad capturada no cuadra."
        }
      ]
    });

    Assert.False(result.Success);
    Assert.Equal("Selecciona un tipo de incidencia válido para cada línea de reconteo.", result.Message);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task RequestRecountAsync_Fails_WhenSessionIsNotSubmittedOrApproved()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) => commandText.Contains("SELECT [Status]", StringComparison.Ordinal) ? "Draft" : null
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.RequestRecountAsync(new PhysicalCountRecountRequest
    {
      SessionId = 51,
      RequestedBy = "admin@orionerp.local",
      Lines =
      [
        new PhysicalCountRecountLineRequest
        {
          LineId = 10,
          IssueCode = PhysicalCountRecountIssueCodes.QuantityMismatch,
          Reason = "La cantidad capturada no cuadra."
        }
      ]
    });

    Assert.False(result.Success);
    Assert.Equal("Solo las sesiones enviadas o aprobadas pueden enviarse a reconteo.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountRecountPlan", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RequestRecountAsync_CreatesPlanSnapshotsClearsLinesAndMarksSessionForRecount()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT [Status]", StringComparison.Ordinal))
        {
          return "Approved";
        }

        if (commandText.Contains("FROM logistica.PhysicalCountRecountPlan", StringComparison.Ordinal)
          && commandText.Contains("COUNT(*)", StringComparison.Ordinal))
        {
          return 0;
        }

        if (commandText.Contains("FROM logistica.PhysicalCountLine", StringComparison.Ordinal)
          && commandText.Contains("COUNT(*)", StringComparison.Ordinal))
        {
          return 2;
        }

        if (commandText.Contains("INSERT INTO logistica.PhysicalCountRecountPlan", StringComparison.Ordinal))
        {
          return 701;
        }

        return null;
      }
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.RequestRecountAsync(new PhysicalCountRecountRequest
    {
      SessionId = 51,
      RequestedBy = "admin@orionerp.local",
      Lines =
      [
        new PhysicalCountRecountLineRequest
        {
          LineId = 10,
          IssueCode = PhysicalCountRecountIssueCodes.QuantityMismatch,
          Reason = "La cantidad capturada no cuadra."
        },
        new PhysicalCountRecountLineRequest
        {
          LineId = 11,
          IssueCode = PhysicalCountRecountIssueCodes.EvidenceMissing,
          Reason = "Falta evidencia del conteo."
        }
      ]
    });

    Assert.True(result.Success);
    Assert.Equal(51, result.EntityId);
    Assert.Equal("Sesión enviada a reconteo correctamente.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var planInsert = Assert.Single(connection.ExecutedCommands, command =>
      command.CommandText.Contains("INSERT INTO logistica.PhysicalCountRecountPlan", StringComparison.Ordinal)
      && !command.CommandText.Contains("PhysicalCountRecountPlanLine", StringComparison.Ordinal));
    AssertParameter(planInsert.Parameters, "@SessionId", 51);
    AssertParameter(planInsert.Parameters, "@RequestedBy", "admin@orionerp.local");

    var planLineInserts = connection.ExecutedCommands
      .Where(command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountRecountPlanLine", StringComparison.Ordinal))
      .ToList();
    Assert.Equal(2, planLineInserts.Count);
    Assert.All(planLineInserts, command => AssertParameter(command.Parameters, "@RecountPlanId", 701));

    var lineClears = connection.ExecutedCommands
      .Where(command => command.CommandText.Contains("SET CountedQuantity = NULL", StringComparison.Ordinal))
      .ToList();
    Assert.Equal(2, lineClears.Count);

    var sessionUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET [Status] = 'Recount'", StringComparison.Ordinal));
    AssertParameter(sessionUpdate.Parameters, "@SessionId", 51);
  }

  [Fact]
  public async Task CancelSessionAsync_Fails_WhenReasonIsMissing()
  {
    var connection = new FakeQueryDbConnection();
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CancelSessionAsync(new PhysicalCountCancelRequest
    {
      SessionId = 51,
      CanceledBy = "admin@orionerp.local",
      Reason = " "
    });

    Assert.False(result.Success);
    Assert.Equal("Captura una razón para cancelar el conteo.", result.Message);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task CancelSessionAsync_Fails_WhenSessionIsPosted()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) => commandText.Contains("SELECT [Status]", StringComparison.Ordinal) ? "Posted" : null
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CancelSessionAsync(new PhysicalCountCancelRequest
    {
      SessionId = 51,
      CanceledBy = "admin@orionerp.local",
      Reason = "Conteo duplicado."
    });

    Assert.False(result.Success);
    Assert.Equal("Las sesiones contabilizadas no se pueden cancelar.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("SET [Status] = 'Canceled'", StringComparison.Ordinal));
  }

  [Fact]
  public async Task CancelSessionAsync_SoftCancelsUnpostedSessionWithoutDeletingRows()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) => commandText.Contains("SELECT [Status]", StringComparison.Ordinal) ? "Approved" : null,
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CancelSessionAsync(new PhysicalCountCancelRequest
    {
      SessionId = 51,
      CanceledBy = "admin@orionerp.local",
      Reason = "Conteo creado por error."
    });

    Assert.True(result.Success);
    Assert.Equal(51, result.EntityId);
    Assert.Equal("Sesión cancelada correctamente.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountLine", StringComparison.Ordinal));
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE attachment", StringComparison.Ordinal));

    var sessionUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET [Status] = 'Canceled'", StringComparison.Ordinal));
    AssertParameter(sessionUpdate.Parameters, "@SessionId", 51);
    AssertParameter(sessionUpdate.Parameters, "@CanceledBy", "admin@orionerp.local");
    AssertParameter(sessionUpdate.Parameters, "@CancelReason", "Conteo creado por error.");
  }

  [Fact]
  public async Task SubmitSessionAsync_ClosesActiveRecountPlan_WhenSessionIsInRecount()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT [Status]", StringComparison.Ordinal))
        {
          return "Recount";
        }

        if (commandText.Contains("FROM logistica.PhysicalCountLine", StringComparison.Ordinal)
          && commandText.Contains("COUNT(*)", StringComparison.Ordinal))
        {
          return 0;
        }

        return null;
      },
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.SubmitSessionAsync(51, "contador@orionerp.local");

    Assert.True(result.Success);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var recountPlanClose = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE logistica.PhysicalCountRecountPlan", StringComparison.Ordinal));
    AssertParameter(recountPlanClose.Parameters, "@SessionId", 51);
    AssertParameter(recountPlanClose.Parameters, "@SubmittedBy", "contador@orionerp.local");
  }

  [Fact]
  public async Task DeleteDraftSessionAsync_Fails_WhenSessionDoesNotExist()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (_, _) => null
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteDraftSessionAsync(51);

    Assert.False(result.Success);
    Assert.Equal("La sesión de conteo no existe.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountLine", StringComparison.Ordinal));
  }

  [Fact]
  public async Task DeleteDraftSessionAsync_Fails_WhenSessionIsNotDraft()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (_, _) => "Posted"
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteDraftSessionAsync(51);

    Assert.False(result.Success);
    Assert.Equal("Solo las sesiones en borrador se pueden cancelar o eliminar.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE attachment", StringComparison.Ordinal));
  }

  [Fact]
  public async Task DeleteDraftSessionAsync_DeletesAttachmentsLinesAndSession()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (_, _) => "Draft",
      NonQueryResultFactory = (commandText, _) => commandText.Contains("DELETE FROM logistica.PhysicalCountSession", StringComparison.Ordinal) ? 1 : 2
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteDraftSessionAsync(51);

    Assert.True(result.Success);
    Assert.Equal(51, result.EntityId);
    Assert.Equal("Sesión en borrador eliminada correctamente.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var attachmentDelete = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE attachment", StringComparison.Ordinal));
    AssertParameter(attachmentDelete.Parameters, "@SessionId", 51);

    var lineDelete = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountLine", StringComparison.Ordinal));
    AssertParameter(lineDelete.Parameters, "@SessionId", 51);

    var sessionDelete = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountSession", StringComparison.Ordinal));
    AssertParameter(sessionDelete.Parameters, "@SessionId", 51);
  }

  private static void AssertParameter(IReadOnlyList<FakeQueryParameter> parameters, string name, object expectedValue)
  {
    var parameter = Assert.Single(parameters, parameter => HasParameterName(parameter, name));
    Assert.Equal(expectedValue, parameter.Value);
  }

  private static bool HasParameterName(FakeQueryParameter parameter, string expectedName)
    => string.Equals(parameter.Name.TrimStart('@'), expectedName.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
}
