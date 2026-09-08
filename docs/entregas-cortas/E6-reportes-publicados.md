# E6 — Reportes sobre pólizas publicadas

Lee primero las [reglas permanentes](README.md#reglas-permanentes). Depende de: E4.
Superficie: consola.

## Estado actual

`ReportesFinancierosService` ya exige el RFC de sesión para balanza y resultados, y
usa el alcance de Hospedaje donde corresponde. Pero
`reporteFinanciero.Rpt_BalanzaComprobacion` agrega `Registro_Contable` unido a
`Transacciones` por fechas y RFC, **sin filtro de publicación**. Presentar hoy esos
reportes como basados en pólizas publicadas sería falso: el ciclo `Posted` no existía.

## Qué se escribe

Adaptar balanza y resultados **como una unidad**, procedimiento y servicio a la vez:

- `reporteFinanciero.Rpt_BalanzaComprobacion` y los procedimientos hermanos. Viven
  sólo en la base: vuelca con `OBJECT_DEFINITION` antes de parchar y versiona el
  resultado.
- [`ReportesFinancierosService`](../../src/OrionERP.Infrastructure/Features/ReportesFinancieros/Dapper/ReportesFinancierosService.cs).

Agregan únicamente asientos publicados, aplicando el contrato original/reversa de E4:
`Draft` fuera, `Posted` dentro, y la reversa restando donde corresponde.

Separar las métricas operativas de las contables, y conservar versiones
identificables de los reportes con sus parámetros, para poder comparar contra el
baseline de E4.

## Límite

**No** se oculta silenciosamente la historia legacy añadiendo un `WHERE Posted`.
Mientras el baseline histórico no esté aprobado, el reporte nuevo se ofrece para los
periodos compatibles y el reporte vigente se conserva claramente identificado;
registra qué falta para adoptar el nuevo como oficial.

No se toca el script ajeno `20260907_fiscal_declaracion_deploy.ps1`.

## Verificación

Build Release. Unitarias del comportamiento nuevo: `Draft` excluido, `Posted`
incluido, efecto de la reversa, y periodo y empresa correctos.
