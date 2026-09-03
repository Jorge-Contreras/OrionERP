using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Components.Routing;

namespace OrionERP.Web.Shared;

public static class NavigationCatalog
{
    private const string ArrendadoresRoleName = "Arrendadores";

    private static readonly string[] ArrendadoresPrivilegedRoles =
    [
        "Administrador",
        "SatOperator",
        "OrdenTrabajoAdmin",
        "OrdenTrabajoSupervisor",
        "OrdenTrabajoOperador",
        "APAdmin",
        "APOperator",
        "APReadOnly",
        "Logistica",
        "Conteo"
    ];

    public static IReadOnlyList<NavSectionDefinition> Sections { get; } =
    [
        new(
            "Fiscal y SAT",
            "CFDI",
            "Procesos de captura, validacion y abastecimiento de comprobantes fiscales.",
            "oi-document",
            [
                new("/cfdi/register", "Registrar RFC", "Alta rapida para nuevos emisores o receptores.", "oi-plus", "Registrar RFC", NavLinkMatch.Prefix, false, "Alta", "rfc", "registro", "cliente", "proveedor"),
                new("/cfdi/declaracion-previa", "CFDIs", "Revision previa y amarre antes de contabilizar.", "oi-document", "Declaracion previa", NavLinkMatch.Prefix, true, "Fiscal", "cfdi", "xml", "facturas", "declaracion"),
                new("/cfdi/descarga-masiva", "Descarga masiva SAT", "Trae XML directo del SAT para conciliacion y control.", "oi-cloud-download", "Descarga masiva SAT", NavLinkMatch.Prefix, true, "SAT", "sat", "descarga", "xml", "masiva"),
                new("/cfdi/cargar-XML-SAT", "Cargar XML", "Incorpora archivos manuales o recibidos por terceros.", "oi-cloud-upload", "Cargar XML", NavLinkMatch.Prefix, false, "XML", "cargar", "subir", "xml")
            ]),
        new(
            "Finanzas",
            "Contabilidad",
            "Movimientos bancarios, registros y polizas para una lectura mas operativa.",
            "oi-graph",
            [
                new("/contabilidad/Bancos", "Bancos", "Consulta movimientos, saldos y control de tesoreria.", "oi-bold", "Bancos", NavLinkMatch.Prefix, true, "Cash", "bancos", "tesoreria", "movimientos"),
                new("/cuentas-por-pagar/recurrentes", "CxCR", "Cuentas Por Cobrar Recurrentes.", "oi-clock", "Cuentas Por Cobrar Recurrentes", NavLinkMatch.Prefix, true, "AP", "ap", "servicios", "pagos recurrentes", "impuestos", "seguros"),
                new("/contabilidad/registros-contables", "Registros Contables", "Administra asientos, auxiliares y capturas contables.", "oi-folder", "Registros contables", NavLinkMatch.Prefix, false, "Ledger", "registros", "contables", "asientos"),
                new("/contabilidad/transacciones/list", "Polizas", "Seguimiento de polizas para cierre, ajustes y auditoria.", "oi-layers", "Polizas", NavLinkMatch.Prefix, true, "Cierre", "polizas", "transacciones", "contabilidad")
            ]),
        new(
            "Operacion",
            "Reservaciones",
            "Hospedaje",
            "oi-calendar",
            [
                new("/reservaciones/lista", "Reserv", "Gestion de bookings, cambios y seguimiento operativo.", "oi-calendar", "Reservaciones", NavLinkMatch.Prefix, true, "Agenda", "reservaciones", "booking", "agenda"),
                new("/reservaciones/calendario", "Calendario", "Vista visual para planeacion, huecos y conflictos.", "oi-grid-three-up", "Calendario de reservaciones", NavLinkMatch.Prefix, true, "Vista", "calendario", "ocupacion", "programacion"),
                new("/arrendadores", "Arrendadores", "Estado de cuenta por propiedad y pagos contabilizados.", "oi-spreadsheet", "Arrendadores", NavLinkMatch.Prefix, false, "Rentas", "arrendadores", "estado de cuenta", "bonhomia", "propietarios")
            ]),
        new(
            "Operacion",
            "Ordenes",
            "Trabajo operativo, limpieza, mantenimiento y evidencia movil.",
            "oi-task",
            [
                new("/ordenes-trabajo", "Ordenes OT", "Dashboard, asignacion y ejecucion de ordenes de trabajo.", "oi-task", "Ordenes de trabajo", NavLinkMatch.Prefix, true, "OT", "ordenes", "trabajo", "limpieza", "mantenimiento", "actividades"),
                new("/ordenes-trabajo/plantillas", "Plantillas OT", "Plantillas versionadas, pasos y mapeo por suite.", "oi-list-rich", "Plantillas de ordenes de trabajo", NavLinkMatch.Prefix, false, "Setup", "plantillas", "ruta critica", "pasos", "limpieza")
            ]),
        new(
            "Personas",
            "Capital Humano",
            "Asistencia, ausencias, equipos y preparación de incidencias para nómina.",
            "oi-people",
            [
                new("/capital-humano", "Capital Humano", "Colaboradores, datos laborales, archivos y fotografia.", "oi-people", "Capital Humano", NavLinkMatch.Prefix, false, "HR", "capital", "humano", "recursos humanos", "colaboradores", "empleados", "archivos"),
                new("/mi-trabajo", "Mi trabajo", "Registro personal, historial, correcciones y saldos de ausencia.", "oi-clock", "Mi trabajo", NavLinkMatch.Prefix, true, "Personal", "asistencia", "entrada", "salida", "ausencias"),
                new("/mi-equipo", "Mi equipo", "Quién está trabajando y colas de aprobación del supervisor.", "oi-people", "Mi equipo", NavLinkMatch.Prefix, true, "Equipo", "supervisor", "aprobaciones", "excepciones"),
                new("/capital-humano/asistencia", "Asistencia", "Calendario RH, anomalías, auditoría y preparación del periodo.", "oi-calendar", "Control de asistencia", NavLinkMatch.Prefix, false, "RH", "asistencia", "incidencias", "horas extra"),
                new("/capital-humano/configuracion-tiempo", "Configuración de tiempo", "Sitios, geocercas, horarios, políticas, responsables y kioscos.", "oi-cog", "Configuración de tiempo", NavLinkMatch.Prefix, false, "Setup", "sitios", "horarios", "geocercas", "kioscos"),
                new("/capital-humano/ausencias", "Ausencias", "Políticas, saldos, solicitudes y ajustes auditados.", "oi-calendar", "Ausencias", NavLinkMatch.Prefix, false, "Leave", "vacaciones", "permisos", "saldos"),
                new("/capital-humano/pre-nomina", "Pre-nómina", "Validación, aprobación, bloqueo y exportación de unidades de tiempo.", "oi-spreadsheet", "Pre-nómina", NavLinkMatch.Prefix, false, "Payroll", "nomina", "xlsx", "incidencias")
            ]),
        new(
            "Personas",
            "Capacitación",
            "Rutas de aprendizaje, sesiones guiadas, práctica segura y evidencia de dominio.",
            "oi-book",
            [
                new("/capacitacion", "Centro de capacitación", "Inicia sesiones, revisa asignaciones y consulta el avance del equipo.", "oi-book", "Centro de capacitación", NavLinkMatch.All, true, "Aprender", "capacitacion", "entrenamiento", "cursos", "sesiones"),
                new("/capacitacion/mi-plan", "Mi capacitación", "Consulta tus cursos asignados, práctica y resultados.", "oi-person", "Mi capacitación", NavLinkMatch.Prefix, true, "Mi plan", "capacitacion", "progreso", "asignaciones", "cursos"),
                new("/capacitacion/catalogo", "Catálogo", "Explora los cursos y rutas de aprendizaje publicados.", "oi-list-rich", "Catálogo de capacitación", NavLinkMatch.Prefix, false, "Cursos", "catalogo", "capacitacion", "modulos", "rutas")
            ]),
        new(
            "Inventario",
            "Logistica",
            "Materiales, compras, ubicaciones y control fisico de existencias.",
            "oi-box",
            [
                new("/logistica/materiales", "Materiales", "Catalogo base para insumos, productos y control de existencias.", "oi-box", "Materiales", NavLinkMatch.Prefix, true, "Stock", "materiales", "inventario", "productos"),
                new("/logistica/proveedores", "Proveedores", "Relacion comercial y abastecimiento por proveedor.", "oi-briefcase", "Proveedores", NavLinkMatch.Prefix, false, "Supply", "proveedores", "compras", "abasto"),
                new("/logistica/compras", "Compras", "Ordenes de compra, recepciones y planeacion por ubicacion.", "oi-cart", "Compras", NavLinkMatch.Prefix, true, "PO", "compras", "orden de compra", "recepciones", "purchase order"),
                new("/logistica/ubicaciones", "Ubicaciones", "Existencia por almacen, rack, sucursal o punto de consumo.", "oi-map-marker", "Ubicaciones e inventario", NavLinkMatch.Prefix, false, "Mapa", "ubicaciones", "almacen", "inventario"),
                new("/logistica/conteos", "Conteos", "Conteos fisicos, ajustes y validacion de diferencias.", "oi-task", "Conteos fisicos", NavLinkMatch.Prefix, false, "Conteo", "conteos", "inventario", "fisico")
            ]),
        new(
            "Operacion",
            "Restaurante",
            "Venta táctil, cocina, recetas, turnos y seguimiento de órdenes.",
            "oi-monitor",
            [
                new("/restaurante/pos", "POS táctil", "Captura, cobra y envía órdenes a cocina.", "oi-cart", "Punto de venta Restaurante", NavLinkMatch.Prefix, true, "Venta", "restaurante", "pos", "orden", "caja"),
                new("/restaurante/cocina", "Cocina KDS", "Comandas por partida, tiempos y productos listos.", "oi-timer", "Pantalla de cocina", NavLinkMatch.Prefix, true, "KDS", "cocina", "comandas", "preparar"),
                new("/restaurante/ordenes", "Órdenes y entregas", "Despacho, entrega y terminación por folio.", "oi-check", "Órdenes y entregas", NavLinkMatch.Prefix, true, "Barra", "órdenes", "entregas", "despacho"),
                new("/restaurante/pantalla", "Pantalla de órdenes", "Folios en preparación y listos sin datos personales.", "oi-bell", "Pantalla pública de órdenes", NavLinkMatch.Prefix, false, "Turnos", "pantalla", "folio", "listo"),
                new("/restaurante/turnos", "Turnos de caja", "Apertura, conteo ciego, diferencias y aprobación.", "oi-lock-unlocked", "Turnos y cortes de caja", NavLinkMatch.Prefix, false, "Caja", "turnos", "corte", "efectivo"),
                new("/restaurante/recetas", "Recetas de cocina", "Ingredientes, rendimientos, preparación y versiones.", "oi-list-rich", "Recetas", NavLinkMatch.Prefix, false, "recetas", "ingredientes", "preparación", "merma"),
                new("/restaurante/menus", "Menús", "Horarios, secciones, productos y modificadores.", "oi-grid-three-up", "Menús y modificadores", NavLinkMatch.Prefix, false, "Catálogo", "menús", "horarios", "modificadores"),
                new("/restaurante/produccion", "Producción", "Reservas FEFO, consumo, lotes, rendimiento y merma.", "oi-loop", "Producción por lotes", NavLinkMatch.Prefix, false, "Lotes", "producción", "lotes", "rendimiento", "merma"),
                new("/restaurante/inventario", "Movimientos de inventario", "Traspasos atómicos, ajustes y merma con evidencia.", "oi-transfer", "Inventario Restaurante", NavLinkMatch.Prefix, false, "Stock", "traspasos", "ajustes", "merma", "inventario"),
                new("/restaurante/reportes", "Reportes Restaurante", "Estado de resultados por agrupador SAT, conciliación con el POS y diagnóstico fiscal.", "oi-bar-chart", "Reportes por agrupador SAT", NavLinkMatch.Prefix, false, "KPIs", "reportes", "agrupador", "SAT", "pólizas", "conciliación", "diagnóstico", "food cost", "liquidaciones"),
                new("/restaurante/promociones", "Promociones y Club Bruno", "Reglas, códigos, membresías, puntos y desempeño comercial.", "oi-tags", "Promociones y membresía", NavLinkMatch.Prefix, false, "Club", "promociones", "códigos", "fidelidad", "puntos"),
                new("/restaurante/sitio-brunos", "Sitio Bruno's", "Contenido, contacto, redes, SEO y banderas de brunosgarden.com.", "oi-globe", "Sitio público Bruno's", NavLinkMatch.Prefix, false, "Web", "brunosgarden", "sitio", "seo", "redes"),
                new("/restaurante/admin", "Administrar Restaurante", "Sedes, productos, variantes, precios y habilitación.", "oi-cog", "Administración de Restaurante", NavLinkMatch.Prefix, false, "Setup", "restaurante", "configuración", "productos"),
                new("/restaurante/configuracion", "Configuración operativa", "Mesas, estaciones, proveedores, almacenes y cuentas.", "oi-settings", "Configuración operativa", NavLinkMatch.Prefix, false, "Setup", "mesas", "estaciones", "cuentas", "proveedores")
            ]),
        new(
            "Analitica",
            "Reportes",
            "Lectura financiera para revision, balance y rentabilidad.",
            "oi-spreadsheet",
            [
                new("/ReportesFinancieros/HojaTrabajo", "Hoja de Trabajo", "Prepara ajustes y revision previa al cierre.", "oi-spreadsheet", "Hoja de Trabajo", NavLinkMatch.Prefix, false, "Work", "hoja de trabajo", "revision", "cierre"),
                new("/ReportesFinancieros/BalanzaComprobacion", "Balanza de Comp", "Saldo y movimientos por cuenta para analisis contable.", "oi-clipboard", "Balanza de Comprobacion", NavLinkMatch.Prefix, true, "Balance", "balanza", "comprobacion", "saldos"),
                new("/ReportesFinancieros/EstadoPerdidasGanancias", "Perdidas y Ganancias", "Rentabilidad y desempeno financiero del negocio.", "oi-graph", "Estado de Perdidas y Ganancias", NavLinkMatch.Prefix, false, "P&L", "perdidas", "ganancias", "estado financiero"),
                new("/ReportesFinancieros/SaludEmpresa", "Salud Financiera", "Dashboard ejecutivo de ingresos, flujo, margen, ocupacion y conciliacion.", "oi-pulse", "Salud Financiera", NavLinkMatch.Prefix, true, "Health", "salud", "dashboard", "financiera", "ocupacion", "revpar", "cashflow")
            ])
    ];

    public static NavSectionDefinition AdminSection { get; } = new(
        "Control y seguridad",
        "Administracion",
        "Gestiona accesos, roles y mantenimiento del portal.",
        "oi-lock-locked",
        [
            new("/admin/empresas", "Empresas", "RFC, branding, disponibilidad y revisiones de acceso.", "oi-building", "Directorio de empresas", NavLinkMatch.Prefix, false, "Admin", "empresas", "rfc", "branding", "membresias"),
            new("/admin/seguridad", "Portal de Seguridad", "Usuarios, permisos y configuracion administrativa.", "oi-lock-locked", "Portal de seguridad", NavLinkMatch.Prefix, false, "Admin", "seguridad", "usuarios", "permisos", "roles"),
            new("/ajustes", "Ajustes", "Plantillas contables y configuracion operativa.", "oi-cog", "Ajustes", NavLinkMatch.Prefix, false, "Setup", "ajustes", "configuracion", "plantillas")
        ]);

    public static bool IsArrendadoresOnly(ClaimsPrincipal user)
    {
        return user.Identity?.IsAuthenticated == true
            && user.IsInRole(ArrendadoresRoleName)
            && !ArrendadoresPrivilegedRoles.Any(user.IsInRole);
    }

    public static IReadOnlyList<NavigationDestination> GetDestinations(bool includeAdmin, bool arrendadoresOnly)
    {
        var destinations = Sections
            .SelectMany(section => section.Items.Select(item => new NavigationDestination(section, item)));

        if (arrendadoresOnly)
        {
            destinations = destinations.Where(destination => IsArrendadoresEntry(destination.Entry));
        }

        if (includeAdmin)
        {
            destinations = destinations.Concat(
                AdminSection.Items.Select(item => new NavigationDestination(AdminSection, item)));
        }

        return destinations.ToArray();
    }

    public static IReadOnlyList<NavigationDestination> Search(
        IEnumerable<NavigationDestination> source,
        string query,
        int maxResults = 12)
    {
        var normalizedQuery = NormalizeSearchText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery) || maxResults <= 0)
        {
            return Array.Empty<NavigationDestination>();
        }

        var terms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return source
            .Select(destination => new
            {
                Destination = destination,
                Score = Score(destination, normalizedQuery, terms)
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Destination.Entry.IsQuickAccess)
            .ThenBy(result => result.Destination.Entry.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxResults)
            .Select(result => result.Destination)
            .ToArray();
    }

    public static NavigationDestination? FindByPath(
        IEnumerable<NavigationDestination> source,
        string path)
    {
        var currentPath = NormalizePath(path);

        return source
            .OrderByDescending(destination => NormalizePath(destination.Entry.Href).Length)
            .FirstOrDefault(destination => IsRouteMatch(currentPath, destination.Entry));
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var endOfPath = path.IndexOfAny(['?', '#']);
        var value = endOfPath >= 0 ? path[..endOfPath] : path;
        return value.Trim().Trim('/').ToLowerInvariant();
    }

    public static bool IsRouteMatch(string currentPath, NavEntryDefinition item)
    {
        var normalizedCurrentPath = NormalizePath(currentPath);
        var targetPath = NormalizePath(item.Href);

        if (item.Match == NavLinkMatch.All)
        {
            return normalizedCurrentPath == targetPath;
        }

        return normalizedCurrentPath == targetPath
            || (!string.IsNullOrEmpty(targetPath)
                && normalizedCurrentPath.StartsWith($"{targetPath}/", StringComparison.OrdinalIgnoreCase));
    }

    private static int Score(
        NavigationDestination destination,
        string normalizedQuery,
        IReadOnlyList<string> terms)
    {
        var entry = destination.Entry;
        var label = NormalizeSearchText(entry.Label);
        var title = NormalizeSearchText(entry.Title);
        var badge = NormalizeSearchText(entry.Badge ?? string.Empty);
        var section = NormalizeSearchText($"{destination.Section.Title} {destination.Section.Kicker}");
        var keywords = NormalizeSearchText(string.Join(' ', entry.Keywords));
        var description = NormalizeSearchText(entry.Description);
        var searchable = $"{label} {title} {badge} {section} {keywords} {description}";

        if (terms.Any(term => !searchable.Contains(term, StringComparison.Ordinal)))
        {
            return IsSubsequence(normalizedQuery, label) ? 80 : 0;
        }

        var score = 100;
        if (label == normalizedQuery)
        {
            score += 1_000;
        }
        else if (label.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            score += 760;
        }
        else if (label.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 600;
        }
        else if (title.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            score += 520;
        }
        else if (keywords.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 420;
        }
        else if (section.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 300;
        }

        score += terms.Count(term => label.Contains(term, StringComparison.Ordinal)) * 55;
        score += terms.Count(term => keywords.Contains(term, StringComparison.Ordinal)) * 25;
        score += entry.IsQuickAccess ? 10 : 0;

        return score;
    }

    private static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return string.Join(
            ' ',
            builder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsSubsequence(string query, string value)
    {
        if (query.Length < 2)
        {
            return false;
        }

        var queryIndex = 0;
        foreach (var character in value)
        {
            if (character == query[queryIndex] && ++queryIndex == query.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsArrendadoresEntry(NavEntryDefinition item)
    {
        var path = NormalizePath(item.Href);
        return path is "reservaciones/calendario" or "arrendadores";
    }
}

public sealed class NavigationDestination
{
    public NavigationDestination(NavSectionDefinition section, NavEntryDefinition entry)
    {
        Section = section;
        Entry = entry;
    }

    public NavSectionDefinition Section { get; }
    public NavEntryDefinition Entry { get; }
}

public sealed class NavSectionDefinition
{
    public NavSectionDefinition(
        string title,
        string kicker,
        string description,
        string icon,
        IReadOnlyList<NavEntryDefinition> items)
    {
        Title = title;
        Kicker = kicker;
        Description = description;
        Icon = icon;
        Items = items;
    }

    public string Title { get; }
    public string Kicker { get; }
    public string Description { get; }
    public string Icon { get; }
    public IReadOnlyList<NavEntryDefinition> Items { get; }
}

public sealed class NavEntryDefinition
{
    public NavEntryDefinition(
        string href,
        string label,
        string description,
        string icon,
        string title,
        NavLinkMatch match,
        bool isQuickAccess,
        string? badge,
        params string[] keywords)
    {
        Href = href;
        Label = label;
        Description = description;
        Icon = icon;
        Title = title;
        Match = match;
        IsQuickAccess = isQuickAccess;
        Badge = badge;
        Keywords = keywords ?? Array.Empty<string>();
    }

    public string Href { get; }
    public string Label { get; }
    public string Description { get; }
    public string Icon { get; }
    public string Title { get; }
    public NavLinkMatch Match { get; }
    public bool IsQuickAccess { get; }
    public string? Badge { get; }
    public IReadOnlyList<string> Keywords { get; }
}
