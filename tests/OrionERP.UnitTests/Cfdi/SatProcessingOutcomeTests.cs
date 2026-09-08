using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Web.Features.Cfdi.DescargaMasiva;

namespace OrionERP.UnitTests.Cfdi;

public sealed class SatProcessingOutcomeTests
{
  [Fact]
  public void CompletedCleanly_WhenAllXmlsProcessedWithoutFailures_IsTrue()
  {
    var summary = new ProcessSummary { Packages = 2, Xmls = 40, Ok = 40, Fail = 0 };

    Assert.True(SatProcessingOutcome.CompletedCleanly(summary));
    Assert.False(SatProcessingOutcome.NoPackagesYet(summary));
  }

  [Fact]
  public void CompletedCleanly_WhenSomeXmlsFailed_IsFalse()
  {
    var summary = new ProcessSummary { Packages = 1, Xmls = 10, Ok = 7, Fail = 3 };

    Assert.False(SatProcessingOutcome.CompletedCleanly(summary));
    Assert.Equal(3, SatProcessingOutcome.Failures(summary));
  }

  [Fact]
  public void CompletedCleanly_WhenSatHasNoPackagesReady_IsFalseAndFlaggedAsPending()
  {
    var summary = new ProcessSummary { Packages = 0 };

    Assert.False(SatProcessingOutcome.CompletedCleanly(summary));
    Assert.True(SatProcessingOutcome.NoPackagesYet(summary));
  }

  [Fact]
  public void CompletedCleanly_CoversMetadataPackages()
  {
    var clean = new ProcessSummary { Packages = 1, MetaFiles = 5, MetaOk = 5, MetaFail = 0 };
    var dirty = new ProcessSummary { Packages = 1, MetaFiles = 5, MetaOk = 4, MetaFail = 1 };

    Assert.True(SatProcessingOutcome.CompletedCleanly(clean));
    Assert.False(SatProcessingOutcome.CompletedCleanly(dirty));
  }
}
