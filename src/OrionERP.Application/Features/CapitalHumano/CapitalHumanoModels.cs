using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.CapitalHumano;

public sealed class CapitalHumanoCommandResult
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public int? EntityId { get; set; }

  public static CapitalHumanoCommandResult Ok(string message, int? entityId = null)
    => new()
    {
      Success = true,
      Message = message,
      EntityId = entityId
    };

  public static CapitalHumanoCommandResult Fail(string message, int? entityId = null)
    => new()
    {
      Success = false,
      Message = message,
      EntityId = entityId
    };
}

public sealed class CapitalHumanoBinaryContent
{
  public int Id { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "image/jpeg";
  public byte[] Bytes { get; set; } = Array.Empty<byte>();
}

public sealed class CapitalHumanoFilter
{
  [Required]
  [StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  public string? SearchText { get; set; }
  public string? Status { get; set; }
  public string? Puesto { get; set; }
  public bool? HasPhoto { get; set; }
  public int Skip { get; set; }
  public int Take { get; set; } = 50;
}

public class CapitalHumanoListItemDto
{
  public int Id { get; set; }
  public string Nombre { get; set; } = string.Empty;
  public string ApellidoPaterno { get; set; } = string.Empty;
  public string ApellidoMaterno { get; set; } = string.Empty;
  public string NombreCorto { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string? Puesto { get; set; }
  public string? RFC_Capital_Humano { get; set; }
  public string? Telefono { get; set; }
  public DateTime? Fecha_Alta { get; set; }
  public DateTime? Fecha_Baja { get; set; }
  public bool HasPhoto { get; set; }
  public bool HasAuthUser { get; set; }
  public int AuthUserCount { get; set; }
  public string? AuthUserName { get; set; }
  public string? AuthEmail { get; set; }
}

public sealed class CapitalHumanoDetailDto : CapitalHumanoListItemDto
{
  public string Rfc { get; set; } = string.Empty;
  public string? CURP { get; set; }
  public DateTime? Fecha_Nacimiento { get; set; }
  public string? Seguro_Social { get; set; }
  public string? Calle { get; set; }
  public string? Colonia { get; set; }
  public string? Comunidad { get; set; }
  public string? Ciudad { get; set; }
  public string? Estado { get; set; }
  public string? Tipo_Sangre { get; set; }
  public string? Numero_Emergencia { get; set; }
  public decimal? Sueldo_Mensual { get; set; }
  public string? Sexo { get; set; }
  public string? Edad { get; set; }
  public string? Dependientes { get; set; }
  public string? Beneficiarios { get; set; }
  public string? Nacionalidad { get; set; }
  public string? Tipo_Contrato { get; set; }
  public string? Sede_Contratada { get; set; }
  public string? Jornada { get; set; }
  public string? Lactancia { get; set; }
  public string? Horario_Alimentos { get; set; }
  public string? Esquema_Pagos { get; set; }
  public string? Tipo_Capital_Humano { get; set; }
  public string? Nivel_Maximo_Estudios { get; set; }
  public string? Descanso_Semanal { get; set; }
}

public sealed class CapitalHumanoSaveRequest
{
  public int? Id { get; set; }

  [Required]
  [StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  [Required]
  [StringLength(255)]
  public string Nombre { get; set; } = string.Empty;

  [Required]
  [StringLength(255)]
  public string ApellidoPaterno { get; set; } = string.Empty;

  [Required]
  [StringLength(255)]
  public string ApellidoMaterno { get; set; } = string.Empty;

  [Required]
  [StringLength(255)]
  public string NombreCorto { get; set; } = string.Empty;

  [Required]
  [StringLength(255)]
  public string Status { get; set; } = "ACTIVO";

  [StringLength(255)]
  public string? CURP { get; set; }

  public DateTime? Fecha_Nacimiento { get; set; }

  [StringLength(50)]
  public string? RFC_Capital_Humano { get; set; }

  [StringLength(50)]
  public string? Seguro_Social { get; set; }

  [StringLength(255)]
  public string? Calle { get; set; }

  [StringLength(255)]
  public string? Colonia { get; set; }

  [StringLength(255)]
  public string? Comunidad { get; set; }

  [StringLength(255)]
  public string? Ciudad { get; set; }

  [StringLength(255)]
  public string? Estado { get; set; }

  [StringLength(10)]
  public string? Tipo_Sangre { get; set; }

  [StringLength(50)]
  public string? Telefono { get; set; }

  [StringLength(50)]
  public string? Numero_Emergencia { get; set; }

  public decimal? Sueldo_Mensual { get; set; }

  [StringLength(50)]
  public string? Puesto { get; set; }

  [StringLength(50)]
  public string? Sexo { get; set; }

  public string? Dependientes { get; set; }
  public string? Beneficiarios { get; set; }
  public DateTime? Fecha_Alta { get; set; }
  public DateTime? Fecha_Baja { get; set; }

  [StringLength(100)]
  public string? Nacionalidad { get; set; }

  [StringLength(100)]
  public string? Tipo_Contrato { get; set; }

  [StringLength(500)]
  public string? Sede_Contratada { get; set; }

  public string? Jornada { get; set; }

  [StringLength(500)]
  public string? Lactancia { get; set; }

  [StringLength(500)]
  public string? Horario_Alimentos { get; set; }

  [StringLength(100)]
  public string? Esquema_Pagos { get; set; }

  [StringLength(100)]
  public string? Tipo_Capital_Humano { get; set; }

  [StringLength(100)]
  public string? Nivel_Maximo_Estudios { get; set; }

  [StringLength(100)]
  public string? Descanso_Semanal { get; set; }

  public byte[]? FotografiaBytes { get; set; }
}

public sealed class CapitalHumanoCatalogDto
{
  public IReadOnlyList<string> Statuses { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> Puestos { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> Sexos { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> TiposSangre { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> TiposContrato { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> Sedes { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> EsquemasPago { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> TiposCapitalHumano { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> NivelesEstudios { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> Nacionalidades { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> Estados { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> DescansosSemanales { get; set; } = Array.Empty<string>();
}
