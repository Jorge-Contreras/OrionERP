# Preparación productiva de E8a, E8b y E8d

Fecha: 2026-09-10. Estado: paquetes preparados y previsualizados; **sin aplicación
productiva**.

Los tres scripts son distintos de sus migraciones Sandbox, admiten únicamente
`grupocarpio` y la base de ensayo autorizada, registran ledger/checksum y revierten
completamente con `ApplyChanges=0`.

| Paquete | Baseline productivo confirmado | Resultado del preview |
| --- | --- | --- |
| E8a contable | 8,102 pólizas y 18,706 movimientos; sólo cabeceras 53495/53496 y líneas 44843–44846 sin `CompanyId` | Seis predicados fail-closed; rollback |
| E8b inventario | 11,500 filas entre OHM y Bruno; 294 predicados legacy, nueve del lote | Nueve predicados nuevos y 285 legacy conservados; rollback |
| E8d fiscal | 16 filas OHM; 18 predicados legacy; cero sobre `cfdi.Comprobante` | 18 predicados fail-closed y CFDI fuera; rollback |

Recibos locales:

- `artifacts/database-previews/e8a-production-preview.json`
- `artifacts/database-previews/e8b-production-preview.json`
- `artifacts/database-previews/e8d-production-preview.json`

El corte posterior exige un respaldo `COPY_ONLY` con checksum, `RESTORE VERIFYONLY`,
previews todavía vigentes y autorización explícita para aplicar los tres paquetes.
