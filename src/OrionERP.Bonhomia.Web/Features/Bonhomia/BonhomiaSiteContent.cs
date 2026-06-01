namespace OrionERP.Bonhomia.Web.Features.Bonhomia;

internal static class BonhomiaSiteContent
{
  public const string WhatsAppPhone = "527491103026";
  public const string WhatsAppDisplay = "+52 749 110 3026";
  public const string Email = "recepcion@bonhomiasuites.com";
  public const string Address = "Carr. Mexico-Veracruz km 74, 90202 Heroica Ciudad de Calpulalpan, Tlaxcala, Mexico.";
  public const string LegalVersion = "2026-05-31";
  public const string LegalUpdatedDisplay = "31 de mayo de 2026";
  public const string LegalManagerName = "Orion Habitat de Mexico, S.A. de C.V.";
  public const string LegalRfc = "OHM191112Q26";
  public const string LegalArcoEmail = "info@orion.land";
  public const string LegalFiscalAddress = "Calle Lazaro Cardenas 105, Col. Otra no especificada en el catalogo, Heroica Ciudad de Calpulalpan, Calpulalpan, Tlaxcala, C.P. 90204.";
  public const string LegalOperatingAddress = Address;
  public const string LegalResponsibleSummary = LegalManagerName + ", RFC " + LegalRfc;

  public static string WhatsAppUrl(string message)
    => $"https://wa.me/{WhatsAppPhone}?text={Uri.EscapeDataString(message)}";

  public static readonly SuiteSummary[] Suites =
  [
    new("Casa Berlin", "Casa completa", "Para familias o equipos de trabajo que necesitan amplitud, privacidad y tres recamaras.", "6 personas", "3 recamaras", "$2,950 MXN", BonhomiaSuiteGalleryCatalog.GetPrimaryImageForSuite("Casa Berlin")),
    new("Suite Manhattan", "Ejecutiva", "Dos recamaras y espacio comodo para compartir sin sacrificar privacidad.", "4 personas", "2 recamaras", "$1,750 MXN", BonhomiaSuiteGalleryCatalog.GetPrimaryImageForSuite("Suite Manhattan")),
    new("Suite Seul", "Larga estancia", "Estancias largas con habitaciones independientes, cocina y ambiente tranquilo.", "4 personas", "2 recamaras", "$1,750 MXN", BonhomiaSuiteGalleryCatalog.GetPrimaryImageForSuite("Suite Seul")),
    new("Suite Moscu", "Compacta", "Practicidad y confort para parejas o viajeros de negocio.", "2 personas", "1 recamara", "$1,250 MXN", BonhomiaSuiteGalleryCatalog.GetPrimaryImageForSuite("Suite Moscu")),
    new("Suite Paris", "Acogedora", "Un espacio cuidado para desconectar, celebrar o trabajar remoto.", "2 personas", "1 recamara", "$1,250 MXN", BonhomiaSuiteGalleryCatalog.GetPrimaryImageForSuite("Suite Paris")),
    new("Penthouse", "Premium", "Maxima privacidad con un toque premium y una vista mas abierta.", "2 personas", "1 recamara", "$2,750 MXN", BonhomiaSuiteGalleryCatalog.GetPrimaryImageForSuite("Penthouse")),
    new("Casa Grecia", "Grupos", "Casa completa para convivir, descansar y viajar en grupo.", "10 personas", "4 recamaras", "$4,500 MXN", BonhomiaSuiteGalleryCatalog.GetPrimaryImageForSuite("Casa Grecia")),
    new("Casa London", "Familiar", "Para familias y grupos que quieren una casa completa y funcional.", "6 personas", "3 recamaras", "$3,150 MXN", BonhomiaSuiteGalleryCatalog.GetPrimaryImageForSuite("Casa London"))
  ];

  public static readonly InfoCard[] GuestProfiles =
  [
    new("Ingenieros y equipos tecnicos", "Llegadas por proyecto, horarios cambiantes y necesidad de descanso real despues de jornada en planta o campo.", "bi bi-tools"),
    new("Viajeros de trabajo remoto", "Wi-Fi, espacios amueblados, cocina y privacidad para alternar reuniones, concentracion y descanso.", "bi bi-laptop"),
    new("Familias de ingreso medio", "Suites y casas con seguridad, comodidad, cocina y opciones por capacidad para cuidar presupuesto.", "bi bi-house-heart"),
    new("Conferencistas y visitantes institucionales", "Ubicacion practica, presentacion cuidada y soporte directo antes y durante la estancia.", "bi bi-mic"),
    new("Personas con agenda publica", "Privacidad, discrecion operativa y comunicacion directa para viajes con agenda definida.", "bi bi-shield-check")
  ];

  public static readonly InfoCard[] BusinessReasons =
  [
    new("Reserva directa", "Consulta disponibilidad, genera cotizacion y paga con PayPal desde el sitio.", "bi bi-calendar-check"),
    new("Estancias listas", "Blancos, limpieza profesional, agua caliente, Wi-Fi y espacios amueblados.", "bi bi-stars"),
    new("Atencion cercana", "WhatsApp, correo y seguimiento para resolver dudas antes de llegar.", "bi bi-chat-dots"),
    new("Ubicacion practica", "Base comoda para actividades laborales, familiares o institucionales en Calpulalpan.", "bi bi-geo-alt")
  ];

  public static readonly InfoCard[] Services =
  [
    new("Wi-Fi y espacio de trabajo", "Ambientes tranquilos para reuniones, reportes y trabajo remoto.", "bi bi-wifi"),
    new("Cocina y estancias amuebladas", "Mas independencia que una habitacion tradicional, especialmente en estancias medias.", "bi bi-cup-hot"),
    new("Opciones para familias y equipos", "Suites compactas, suites de dos recamaras y casas completas para grupos.", "bi bi-people"),
    new("Check-in organizado", "Ingreso estandar desde las 15:00 hrs, con opciones anticipadas sujetas a disponibilidad.", "bi bi-door-open"),
    new("Extras bajo solicitud", "Alimentos, lavanderia, transporte, mascotas y salidas tardias segun disponibilidad.", "bi bi-plus-circle"),
    new("Soporte para facturacion", "Orientacion para comprobantes y datos fiscales conforme al proceso de reserva.", "bi bi-receipt")
  ];

  public static readonly ExtraSummary[] Extras =
  [
    new("Early check-in", "Ingreso desde 13:00 hrs", "$200 MXN"),
    new("Late check-out", "Salida de 12:00 a 14:00 hrs", "$200 MXN"),
    new("Mascota", "Admision por estancia", "$500 MXN"),
    new("Alimentos", "Desayuno o cena por persona", "$200 MXN"),
    new("Transporte AICM", "Sencillo hasta 3 personas", "$3,000 MXN"),
    new("Lavanderia", "Servicio por kilogramo", "$80 MXN")
  ];

  public static readonly FaqItem[] Faqs =
  [
    new("Reservaciones", "Como reservo en linea?", "Entra a Reservar, selecciona fechas, suite, extras y genera tu cotizacion. La reservacion se crea hasta que PayPal confirma el pago completo."),
    new("Reservaciones", "Puedo consultar antes de pagar?", "Si. Puedes escribir por WhatsApp con tus fechas, numero de huespedes y motivo de viaje para recibir orientacion."),
    new("Trabajo", "Es buena opcion para ingenieros o equipos de proyecto?", "Si. Las casas y suites permiten descansar con privacidad, cocinar, trabajar en linea y organizar estancias por capacidad."),
    new("Trabajo", "Puedo trabajar remoto desde la suite?", "La propuesta esta pensada para viajeros que necesitan Wi-Fi, tranquilidad y espacios amueblados. Si tienes requisitos tecnicos especificos, confirma por WhatsApp antes de reservar."),
    new("Familias", "Hay opciones para familias?", "Si. Hay suites de una y dos recamaras, ademas de casas completas para grupos familiares que buscan seguridad y comodidad."),
    new("Eventos", "Reciben conferencistas o visitantes institucionales?", "Si. Bonhomia puede servir como base de descanso para agenda de trabajo, conferencias o visitas publicas en la region."),
    new("Pagos", "Que metodos de pago aceptan en el sitio?", "El flujo web usa PayPal para pago seguro. La cotizacion y el pago se calculan en MXN."),
    new("Politicas", "Cual es el horario de entrada y salida?", "El check-in estandar inicia a las 15:00 hrs y el check-out es hasta las 11:00 hrs. Early check-in y late check-out dependen de disponibilidad."),
    new("Politicas", "Puedo cancelar?", "Las cancelaciones con mas de 30 dias de anticipacion pueden recibir reembolso neto del 85%. Con 30 dias o menos son no reembolsables, salvo condiciones especificas confirmadas al reservar."),
    new("Facturacion", "Puedo solicitar factura?", "Si requieres CFDI, confirma tus datos fiscales y uso requerido durante el seguimiento de la reserva.")
  ];
}

internal sealed record SuiteSummary(string Name, string Tag, string Description, string Capacity, string Bedrooms, string StartingRate, string Image);

internal sealed record InfoCard(string Title, string Text, string Icon);

internal sealed record ExtraSummary(string Name, string Detail, string Price);

internal sealed record FaqItem(string Category, string Question, string Answer);
