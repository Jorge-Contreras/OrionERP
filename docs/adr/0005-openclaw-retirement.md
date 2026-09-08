# ADR 0005: Retiro de OpenClaw

- Estado: Aceptada
- Fecha: 2026-09-02

## Contexto

OpenClaw quedó obsoleto y no forma parte de la arquitectura objetivo. Aunque no
existían tablas, columnas, módulos ni datos identificables de esa integración
en `Orion_Sandbox`, el repositorio sí conservaba endpoints registrados, un
contrato de API key y un camino global de creación de reservaciones que no
estaba aislado por empresa y sede.

## Decisión

Se eliminan los endpoints `/api/openclaw/reservations`, sus contratos, tokens
PDF, configuración, registros de servicios, pruebas específicas y el método de
escritura global en `ListaReservacionesService`.

La normalización de nombres que también usan CFDI, galería y el recorrido
público de Hospedaje se conserva como la utilidad neutral
`ReservationCatalogNaming`. No se requiere migración de base de datos.

No se modifican scripts SQL ya aplicados aunque mencionen OpenClaw en un
comentario histórico: alterar sus bytes invalidaría el checksum registrado.
Tampoco se reescriben ADR anteriores; este documento los sustituye respecto al
estado vigente de OpenClaw.

## Consecuencias

OpenClaw deja de ser un pendiente de tenantización y no debe considerarse en
las fases futuras. La variable de entorno de máquina y cualquier secreto del
servicio publicado se retirarán durante el corte productivo autorizado; no se
tocan mientras producción continúe ejecutando el binario anterior.

