using OrionERP.Infrastructure.Features.Logistica.PhysicalCounts;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class PhysicalCountServiceTests
{
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
