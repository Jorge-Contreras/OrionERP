using System.Security.Claims;
using OrionERP.Application.Common;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Cfdi;
using OrionERP.UnitTests.Common;
using OrionERP.Web.State;

namespace OrionERP.UnitTests.Contabilidad;

public sealed class AccountingCompanyIdentityTests
{
  [Fact]
  public async Task CompanyId_ComesFromTheExactLegacyKeyOfTheSession()
  {
    var resolver = new RecordingResolver { CompanyId = 42 };
    var context = Session(resolver, "OHM191112Q26");

    Assert.Equal(42, await context.RequireCompanyIdAsync());
    Assert.Equal(["OHM191112Q26"], resolver.Asked);
  }

  [Fact]
  public async Task CompanyId_FailsWhenTheCompanyIsNotOnThePlatform()
  {
    var context = Session(new RecordingResolver { CompanyId = null }, "OHM191112Q26");

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.RequireCompanyIdAsync());
  }

  [Fact]
  public async Task CompanyId_FailsBeforeAskingWhenTheSessionHasNoCompany()
  {
    var resolver = new RecordingResolver { CompanyId = 42 };
    var context = new CurrentCompanyContext(resolver);

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.RequireCompanyIdAsync());
    Assert.Empty(resolver.Asked);
  }

  [Fact]
  public async Task CompanyId_IsNeverResolvedForAnotherCompany()
  {
    var resolver = new RecordingResolver { CompanyId = 42 };
    var context = Session(resolver, "OHM191112Q26");

    Assert.Throws<UnauthorizedAccessException>(() => context.EnsureRfc("BRUNOS260707L26"));
    await context.RequireCompanyIdAsync();
    Assert.DoesNotContain("BRUNOS260707L26", resolver.Asked);
  }

  [Fact]
  public async Task CompanyId_IsResolvedOncePerSession()
  {
    var resolver = new RecordingResolver { CompanyId = 42 };
    var context = Session(resolver, "OHM191112Q26");

    Assert.Equal(42, await context.RequireCompanyIdAsync());
    Assert.Equal(42, await context.RequireCompanyIdAsync());
    Assert.Single(resolver.Asked);
  }

  [Fact]
  public void CfdiScope_ReachesTheIssuerAndTheReceiverAlike()
  {
    var predicate = CfdiCompanyScope.AccessPredicateSql("c.Comprobante_Id");

    Assert.Contains("cfdi.Emisor", predicate, StringComparison.Ordinal);
    Assert.Contains("cfdi.Receptor", predicate, StringComparison.Ordinal);
    Assert.Contains(" OR ", predicate, StringComparison.Ordinal);
    Assert.Equal(2, CountOccurrences(predicate, "c.Comprobante_Id"));
    Assert.Equal(2, CountOccurrences(predicate, "@Rfc"));
  }

  [Fact]
  public void OneCfdiSplitAcrossSeveralPoliciesOfTheSameCompany_StaysAllowed()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Services/TransaccionService.cs");

    // Repartir un CFDI entre varias pólizas es práctica viva —35 comprobantes y 78
    // vínculos en producción—, y el par exacto ya lo impide
    // UQ_Transaccion_Comprobante_TransaccionID_ComprobanteID. Rechazar el segundo
    // vínculo en bloque rompía ese reparto.
    Assert.DoesNotContain("CompanyLinkElsewhere", service, StringComparison.Ordinal);
    Assert.DoesNotContain("ya está ligado a otra póliza de la misma empresa", service, StringComparison.Ordinal);
  }

  [Fact]
  public void TheCfdiBalanceIsMeasuredPerCompany()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Services/TransaccionService.cs");

    // Lo asignado por otra empresa no consume el margen de ésta sobre un CFDI que
    // ambas alcanzan legítimamente, una como emisora y la otra como receptora.
    Assert.Contains("assignedTransaction.RFC = @CompanyRfc", service, StringComparison.Ordinal);
  }

  [Fact]
  public void CfdiScope_EvaluatesAssignmentPerCompany()
  {
    var assigned = CfdiCompanyScope.AssignedToCompanySql("c.Comprobante_Id");

    // Un vínculo global no basta: tiene que colgar de una póliza de esta empresa.
    Assert.Contains("dbo.Transaccion_Comprobante", assigned, StringComparison.Ordinal);
    Assert.Contains("JOIN dbo.Transacciones", assigned, StringComparison.Ordinal);
    Assert.Contains("companyTransaction.RFC = @Rfc", assigned, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("c.Comprobante_Id; DROP TABLE dbo.Transacciones")]
  [InlineData("1 OR 1=1")]
  [InlineData("")]
  public void CfdiScope_RefusesUntrustedReferences(string reference)
  {
    Assert.Throws<ArgumentException>(() => CfdiCompanyScope.AccessPredicateSql(reference));
    Assert.Throws<ArgumentException>(() => CfdiCompanyScope.AssignedToCompanySql("c.Comprobante_Id", reference));
  }

  private static CurrentCompanyContext Session(ICompanyIdentityResolver resolver, string rfc)
  {
    var context = new CurrentCompanyContext(resolver);
    context.InitializeFromClaims(new ClaimsPrincipal(new ClaimsIdentity(
      [new Claim(CompanyClaimTypes.Rfc, rfc)], authenticationType: "Test")));
    return context;
  }

  private static int CountOccurrences(string source, string value)
  {
    var count = 0;
    for (var index = source.IndexOf(value, StringComparison.Ordinal); index >= 0;
         index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
      count++;
    return count;
  }

  private sealed class RecordingResolver : ICompanyIdentityResolver
  {
    public long? CompanyId { get; set; }
    public List<string> Asked { get; } = [];

    public Task<long?> ResolveCompanyIdAsync(string legacyRfc, CancellationToken ct = default)
    {
      Asked.Add(legacyRfc);
      return Task.FromResult(CompanyId);
    }
  }
}
