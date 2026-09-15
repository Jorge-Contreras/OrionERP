using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class OrionSqlSessionFactoryIsolationTests
{
  [Fact]
  public void SessionInitialization_ResetsPooledIsolationBeforeScopedWork()
  {
    // The online-order import claims with READPAST. After the POS order service
    // commits a SERIALIZABLE transaction, the pooled session keeps that level and
    // every later claim fails with error 650 unless initialization resets it.
    var source = RepoFile.Read("src/OrionERP.Infrastructure/Features/Platform/OrionSqlSessionFactory.cs");
    var reset = source.IndexOf("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", StringComparison.Ordinal);
    var firstContextReset = source.IndexOf("EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=NULL", StringComparison.Ordinal);

    Assert.True(reset >= 0, "OrionSqlSessionFactory must reset the isolation level of pooled sessions.");
    Assert.True(firstContextReset > reset, "The isolation reset must run in the same batch, before the session context.");
  }
}
