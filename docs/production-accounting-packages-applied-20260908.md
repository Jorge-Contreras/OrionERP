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

## Hallazgo independiente, anterior a este cambio

El servicio de la consola en el puerto 5000 responde `302` a
`/Identity/Account/Login`, y **esa página de login devuelve 500**. La causa no es esta
migración: el directorio desplegado
`GitHubs\Production\OrionERP` **no contiene ningún archivo `.dll`** —sólo `.pdb`, el
`.exe`, `appsettings.json` y `web.config`—, y la publicación productiva es
`--self-contained false`, así que esos ensamblados son obligatorios. El proceso sigue
en pie porque los cargó antes de que desaparecieran; un reinicio no arrancaría.

Por contraste, `GitHubs\Production\OrionERP.Bonhomia.Web` conserva sus DLL y su
readiness responde `200` con el Host correcto. El puerto 5020 responde `404` en
`/health/ready`, que es otra cosa a revisar aparte.

El directorio vive dentro de Dropbox, así que la hipótesis principal es deshidratación
o conflicto de sincronización sobre esa carpeta. No se pudo leer la excepción ni el
estado del servicio: la inspección de producción (servicios, logs, SQL ad-hoc) está
bloqueada en esta sesión.

Reparar la consola es volver a publicar sus binarios, lo que a su vez exige el push
pendiente.
