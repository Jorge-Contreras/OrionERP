# Aplicación productiva de los paquetes contables E3 y E4

Fecha: 2026-09-08. Autorización: el usuario dio luz verde explícita para aplicar en
`grupocarpio` tras terminar E4.

## Respaldo

`BACKUP DATABASE [grupocarpio] ... WITH COPY_ONLY, CHECKSUM`, seguido de
`RESTORE VERIFYONLY FROM DISK ... WITH CHECKSUM`, con el mismo procedimiento y el
mismo directorio de respaldo predeterminado que el corte del 2026-09-08. Archivo:
`grupocarpio_pre_accounting_cycle_20260908_181921_0a6f43aa.bak`. Recibo local en
`artifacts/production-cutover-20260908/backup-receipt-accounting-cycle.json`. El recibo
del corte anterior no se sobrescribió.

## Migraciones aplicadas

Cada una con `ApplyChanges=0` revisado antes de `ApplyChanges=1`, su propio recibo de
preview y referencia al respaldo de arriba.

| MigrationId | Preview | Apply |
| --- | --- | --- |
| `20260908_production_accounting_company_identity` | `VALIDADO_SIN_CAMBIOS` | `APLICADO_CORTE_20260908` |
| `20260908_production_accounting_cycle` | `VALIDADO_SIN_CAMBIOS` | `APLICADO_CORTE_20260908` |

Cifras reales de `grupocarpio`: **8,093 pólizas** y **18,683 movimientos**
identificados por el vínculo legacy exacto, **cero sin empresa**, cero movimientos con
empresa distinta a la de su póliza. Cada preview y cada apply tardó menos de 3
segundos, así que la ventana de bloqueo sobre `dbo.Transacciones` fue despreciable.

El ciclo quedó **apagado**: 0 empresas activadas, 0 periodos declarados, 0 pólizas en
el ciclo, con los dos triggers de inmutabilidad y el índice único de reversa creados.

`--mode verify` sobre el manifiesto productivo completo devuelve **9 VERIFIED**.

## Lo que no se hizo

**No se desplegaron binarios.** `deployment/Publish-Safety.ps1` exige
`HEAD == origin/main` y el push está bloqueado en la sesión, así que producción sigue
corriendo el código anterior. Es seguro en ambos sentidos: las columnas nuevas son
nullable con `DEFAULT` desde el contexto de sesión, y el código viejo, que no fija
`OrionERP.CompanyId`, escribe `NULL` igual que antes; los triggers del ciclo sólo
actúan sobre filas ya publicadas, y ninguna lo está.

**No se activó el ciclo para ninguna empresa.** Encenderlo es escribir la fila en
`contabilidad.CompanyCycleActivation`, y eso espera la aprobación del baseline por
empresa.

## Salud de producción tras aplicar

Los tres servicios responden **200** en su endpoint de readiness real, `/readyz`:

| Servicio | Puerto | `/readyz` |
| --- | --- | --- |
| Consola `OrionERP` | 5000 | `200` · `{"status":"ready","database":{"catalog":"grupocarpio","reachable":true}}` |
| Bonhomía | 5010 | `200` |
| Bruno | 5020 | `200` |

### Corrección de una alarma que no era tal

La primera versión de esta acta reportó una consola productiva rota. **Era falso**, por
tres lecturas equivocadas, y queda anotado para que nadie repita el diagnóstico:

- **El 500 del login es esperado sobre HTTP plano.** El propio `Publish-All-prod.ps1` lo
  documenta al elegir `/readyz` como health check: renderizar ese formulario sin TLS
  falla cuando las cookies antiforgery exigen petición segura. Probar `/` sigue la
  redirección al login y da ese 500.
- **El 404 del puerto 5020 fue una URL equivocada.** El endpoint es `/healthz` o
  `/readyz`, no `/health/ready`; con la ruta correcta responde 200.
- **No faltan DLL.** `src/OrionERP.Web/OrionERP.Web.csproj` publica con
  `PublishSingleFile=true` e `IncludeNativeLibrariesForSelfExtract=true`, así que los
  ensamblados van dentro de `OrionERP.Web.exe`, de 83 MB. Sólo la consola lo hace; por
  eso Bonhomía sí tiene DLL sueltos y ella no. El contraste entre las dos carpetas,
  que pareció evidencia, era la diferencia de modo de publicación.
