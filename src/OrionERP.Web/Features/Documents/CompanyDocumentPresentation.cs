using Microsoft.Extensions.Options;
using OrionERP.Application.Common;

namespace OrionERP.Web.Features.Documents;

public sealed class CompanyDocumentPresentationOptions
{
  public const string SectionName = "DocumentPresentation";
  public string? LogoPath { get; set; }
}

public interface ICompanyDocumentPresentation
{
  string DisplayName { get; }
  string LogoSvg { get; }
}

/// <summary>
/// Supplies document presentation from the authenticated company and neutral
/// process configuration. Business services never select presentation by RFC.
/// </summary>
public sealed class CompanyDocumentPresentation : ICompanyDocumentPresentation
{
  private readonly ICurrentCompanyContext? _companyContext;

  public CompanyDocumentPresentation(
    IWebHostEnvironment environment,
    ICurrentCompanyContext companyContext,
    IOptions<CompanyDocumentPresentationOptions> options)
    : this(environment, companyContext, options.Value.LogoPath)
  {
  }

  private CompanyDocumentPresentation(
    IWebHostEnvironment environment,
    ICurrentCompanyContext? companyContext,
    string? configuredLogoPath)
  {
    ArgumentNullException.ThrowIfNull(environment);
    _companyContext = companyContext;
    LogoSvg = LoadLogo(environment, configuredLogoPath);
  }

  public string DisplayName
    => string.IsNullOrWhiteSpace(_companyContext?.DisplayName)
      ? "OrionERP"
      : _companyContext.DisplayName.Trim();

  public string LogoSvg { get; }

  public static ICompanyDocumentPresentation CreateNeutral(IWebHostEnvironment environment)
    => new CompanyDocumentPresentation(environment, (ICurrentCompanyContext?)null, (string?)null);

  private static string LoadLogo(IWebHostEnvironment environment, string? configuredPath)
  {
    if (string.IsNullOrWhiteSpace(configuredPath))
      return FallbackLogoSvg;

    var webRoot = Path.GetFullPath(environment.WebRootPath
      ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
    var candidate = Path.IsPathRooted(configuredPath)
      ? Path.GetFullPath(configuredPath)
      : Path.GetFullPath(Path.Combine(webRoot, configuredPath));
    var prefix = webRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
      + Path.DirectorySeparatorChar;
    if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
      throw new InvalidOperationException("DocumentPresentation:LogoPath must reference an existing file under wwwroot.");
    return File.ReadAllText(candidate);
  }

  private const string FallbackLogoSvg = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <rect x="32" y="32" width="448" height="448" rx="72" fill="#0B5A68"/>
  <path d="M150 342V170h52l54 83 54-83h52v172h-48V245l-58 84-58-84v97z" fill="#F2E9D5"/>
</svg>
""";
}
