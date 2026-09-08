# Chat 19: Contabilización durable de Hospedaje

Copia todo el contenido siguiente como primer mensaje de un chat nuevo. No ejecutes varios chats con escritura/despliegue simultáneamente.

---

Continúa OrionERP con este único incremento: **Contabilización durable de Hospedaje**.

Objetivo orientativo: 60–120 min, 12–18k tokens incluyendo lecturas y salidas. Dependencias: chat 11 terminado o evidencia equivalente en main; chat 17 terminado o evidencia equivalente en main; chat 18 terminado o evidencia equivalente en main. Superficie de publicación: Consola y esquema aditivo.

Trabaja en el checkout principal:
`C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Development\OrionERP`.

Este encargo autoriza código, Orion_Sandbox y publicación productiva **sólo del incremento descrito**, después de validar su paquete concreto. La activación de pagos, timbrados, correos, sincronizaciones reales o decisiones de atribución histórica no está incluida. Si falta evidencia empresarial, pregunta sólo lo indispensable y avanza en lo independiente.

Primero verifica Git y el estado vigente: código publicado inicialmente 91e2362, acta aa09074, son checkpoints y no instrucciones de reset. Trabaja directamente en main. Conserva cambios concurrentes y el script ajeno `src/OrionERP.Infrastructure/Features/ReportesFinancieros/Sql/20260907_fiscal_declaracion_deploy.ps1`; agrega al commit sólo tus archivos. No abras otra rama/worktree. Si otro chat escribe o despliega, no compartas el índice ni publiques hasta que termine; haz sólo lectura mientras tanto. No lances subagentes para este incremento.

Lee AGENTS.md, docs/production-cutover-executed-20260908.md y únicamente la sección pertinente de docs/multiempresa-accounting-status-20260908.md o del documento específico indicado. No releas todo el handoff ni reaudites toda la plataforma. Si main ya cerró este objetivo, verifica su evidencia y actualiza estado sin duplicar implementación.

Mantén CompanyId técnico, TaxRfc fiscal y Rfc legacy separados; no infieras RFC fiscal. CompanyModule/SiteCapability y permisos son la autoridad. Un solo proyecto por rama pública: Bonhomia.Web, Bruno.Web; consola OrionERP.Web. No reintroduzcas OpenClaw.

Antes de cada conexión de prueba fija Database=Orion_Sandbox dentro del proceso: en PowerShell usa set_ConnectionString, set_InitialCatalog y get_ConnectionString; comprueba catálogo antes de abrir y DB_NAME() después, sin distinguir caja. Error terminante, nunca imprimir conexiones/secretos ni cambiar variables globales de pruebas. Producción grupocarpio sólo para preflight y corte de este incremento; su conexión debe estar fijada y comprobada de forma igualmente explícita. Usa dobles/proveedores de prueba verificados; Graph Calendar apagado. Credenciales de navegador Development sólo en Sandbox desde AGENTS.md.

Preserva los bytes de todas las migraciones aplicadas, incluidas las siete productivas. Nuevos cambios de esquema requieren migraciones aditivas, ledger/checksum y paquetes específicos por entorno; no conviertas scripts Sandbox en productivos cambiando bases permitidas. Evalúa SQL y Blazor juntos.

Antes de producción: termina código, pruebas focalizadas y commit; prepara artefactos, configuración y reversión compatibles, respaldo verificado y ensayo proporcional al riesgo. Ejecuta preview ApplyChanges=0, revisa, aplica ApplyChanges=1 y verifica cuando haya migración; publica sólo los servicios afectados. Si afecta consumidores de un esquema compartido, coordina su parada/arranque. No reviertas sólo binarios contra RLS incompatible ni restaures datos perdiendo operaciones posteriores. Conserva configuración privada, App_Data, llaves y perfiles; valida overrides de puertos de Windows. No copies los helpers fechados de artifacts sin revisarlos. No repitas autorización que este prompt ya concede.

Usa el presupuesto como objetivo orientativo, no como garantía ni razón para publicar incompleto. Corre sólo pruebas que correspondan al cambio; no repitas suites completas sin motivo. Si surge una dependencia fuera de alcance, entrega el menor commit seguro con su pendiente preciso; no abras otra arquitectura. Si el incremento exige varios lotes, publica únicamente el lote que esté completo y genera un prompt acotado para continuar.

Cierra con: commit, comportamiento cambiado, pruebas realmente ejecutadas, migraciones/servicios realmente publicados, bloqueos conservados y siguiente pendiente. Actualiza docs/entregas-cortas/README.md y el documento del área con evidencia breve. Distingue implementación, preparación, ejecución productiva y adopción empresarial. Si sólo cambian pruebas/documentación, no reinicies producción.

## Alcance concreto

Reutiliza el contrato durable ya implementado, adaptándolo a una operación de Hospedaje con identidad empresa/sede/reserva. Configura referencias explícitas de cuentas/categorías y habilita CreateTransaccionesForRoom únicamente mediante ese contrato y con mappings completos. Empieza por una operación definida, sin recorrer calendarios históricos para generar cargos retrospectivos.

## Límite de esta entrega

No infieras cuentas/categorías ni produzcas transacciones por texto. No incluyas pagos cruzados excluidos. Si no hay configuración empresarial válida, publica la capacidad apagada y solicita sólo los mappings faltantes; no anuncies el flujo operativo como terminado.

## Pruebas y aceptación

Operación válida contabiliza una sola vez; duplicación, fallo intermedio, sede ajena, suspensión y mappings incompletos no dejan pólizas huérfanas. Corrige mediante reversa conforme al ciclo formal.
