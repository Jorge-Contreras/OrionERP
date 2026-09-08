using System.Text.RegularExpressions;

namespace OrionERP.Infrastructure.Features.Cfdi;

/// <summary>
/// Contrato fiscal único de acceso a un CFDI: una empresa lo alcanza si es su
/// emisor <b>o</b> su receptor. Un mismo comprobante puede ser legítimamente
/// visible para dos empresas —una emite, la otra recibe— y eso no le da propiedad
/// exclusiva a ninguna. La asignación contable, en cambio, sí es por empresa: que
/// una lo haya ligado a su póliza no lo asigna para la otra.
/// </summary>
public static partial class CfdiCompanyScope
{
  /// <summary>Emisor o receptor. Los alias son propios para no ensombrecer los del query anfitrión.</summary>
  public static string AccessPredicateSql(string comprobanteIdExpression, string rfcParameter = "@Rfc")
  {
    ValidateSqlReference(comprobanteIdExpression);
    ValidateSqlReference(rfcParameter);
    return $"""
      (
        EXISTS (SELECT 1 FROM cfdi.Emisor cfdiIssuer
                WHERE cfdiIssuer.Comprobante_ID = {comprobanteIdExpression} AND cfdiIssuer.Rfc = {rfcParameter})
        OR EXISTS (SELECT 1 FROM cfdi.Receptor cfdiReceiver
                   WHERE cfdiReceiver.Comprobante_ID = {comprobanteIdExpression} AND cfdiReceiver.Rfc = {rfcParameter})
      )
      """;
  }

  /// <summary>
  /// Asignado <b>para esta empresa</b>: existe un vínculo a una póliza suya. Un CFDI
  /// que otra empresa ya ligó sigue pendiente para ésta.
  /// </summary>
  public static string AssignedToCompanySql(string comprobanteIdExpression, string rfcParameter = "@Rfc")
  {
    ValidateSqlReference(comprobanteIdExpression);
    ValidateSqlReference(rfcParameter);
    return $"""
      EXISTS
      (
        SELECT 1
        FROM dbo.Transaccion_Comprobante companyLink
        JOIN dbo.Transacciones companyTransaction ON companyTransaction.ID = companyLink.Transaccion_ID
        WHERE companyLink.Comprobante_ID = {comprobanteIdExpression}
          AND companyTransaction.RFC = {rfcParameter}
      )
      """;
  }

  private static void ValidateSqlReference(string value)
  {
    if (!SqlReference().IsMatch(value))
      throw new ArgumentException("Las referencias SQL deben ser identificadores o parámetros de confianza.", nameof(value));
  }

  [GeneratedRegex("^[@A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)]
  private static partial Regex SqlReference();
}
