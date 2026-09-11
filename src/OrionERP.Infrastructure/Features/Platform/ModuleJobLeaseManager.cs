using System.Data.Common;
using Dapper;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed class ModuleJobLeaseManager(IOrionSqlSessionFactory sessions) : IModuleJobLeaseManager
{
  public async Task<IModuleJobLease> TryAcquireAsync(
    string jobName,PlatformExecutionScope scope,CancellationToken ct=default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
    scope.EnsureValid();
    if (!scope.SiteId.HasValue || string.IsNullOrWhiteSpace(scope.ModuleCode))
      throw new ArgumentException("A module job lease requires module and site scope.",nameof(scope));
    var normalizedJob=new string(jobName.Trim().Where(character=>char.IsLetterOrDigit(character)||character is '-' or '_').ToArray());
    if (normalizedJob.Length is 0 or >80) throw new ArgumentException("Invalid job name.",nameof(jobName));
    var connection=await sessions.OpenAsync(scope,ct);
    try
    {
      var resource=$"OrionERP:Job:{normalizedJob}:{scope.ModuleCode}:{scope.CompanyId}:{scope.SiteId}";
      var result=await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
        DECLARE @Result int;
        EXEC @Result=sys.sp_getapplock @Resource=@Resource,@LockMode=N'Exclusive',
          @LockOwner=N'Session',@LockTimeout=0,@DbPrincipal=N'public';
        SELECT @Result;
        """,new { Resource=resource },cancellationToken:ct));
      if (result>=0) return new ModuleJobLease(connection,resource,true);
      await connection.DisposeAsync();
      return new ModuleJobLease(null,resource,false);
    }
    catch
    {
      await connection.DisposeAsync();
      throw;
    }
  }

  private sealed class ModuleJobLease(DbConnection? connection,string resource,bool acquired) : IModuleJobLease
  {
    private int _disposed;
    public bool IsAcquired { get; }=acquired;
    public async ValueTask DisposeAsync()
    {
      if (Interlocked.Exchange(ref _disposed,1)!=0) return;
      if (connection is null) return;
      try
      {
        await connection.ExecuteAsync(new CommandDefinition("""
          DECLARE @Result int;
          EXEC @Result=sys.sp_releaseapplock @Resource=@Resource,@LockOwner=N'Session',@DbPrincipal=N'public';
          """,new { Resource=resource }));
      }
      finally { await connection.DisposeAsync(); }
    }
  }
}
