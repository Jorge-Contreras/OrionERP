# Despliegue de brunosgarden.com

`OrionERP.Bruno.Web` es una superficie pública aislada:

- servicio Windows: `OrionERP.Bruno`;
- proceso: `OrionERP.Bruno.Web.exe`;
- origen local de producción: `http://127.0.0.1:5020`;
- dominio canónico: `https://brunosgarden.com`;
- `www` redirige permanentemente al apex;
- Identity: esquema `brunos_auth`;
- cookie: `__Host-BrunosGarden.Member`;
- protección de datos: aplicación y directorio de llaves exclusivos.

## Secuencia segura de salida

1. Respaldar `grupocarpio` antes de cualquier migración de producción.
2. Aplicar `20260730_bruno_promotions_loyalty.sql` mediante `sqlcmd -f 65001`, primero con `ApplyChanges=0` y luego con `ApplyChanges=1`. En desarrollo usar exclusivamente `Orion_Sandbox`.
3. Crear el servicio Windows apuntando al ejecutable publicado y configurar las variables de `production.env.example` en el almacén aprobado. No copiar secretos a Git.
4. Ejecutar el lanzador canónico `Publish-All-prod.ps1 -Applications Bruno`; el script valida Git, publica, reinicia el servicio y comprueba la aplicación. `Publish-Bruno-prod.ps1` queda como herramienta interna para mantenimiento dirigido.
5. Confirmar `http://127.0.0.1:5020/healthz` (proceso) y `http://127.0.0.1:5020/readyz` (base de datos y configuración pública).
6. Copiar todos los registros DNS actuales de GoDaddy y verificar, especialmente correo, verificaciones y registros de terceros.
7. Agregar `brunosgarden.com` y `www.brunosgarden.com` al túnel existente con las reglas de `cloudflared-ingress.example.yml`, antes del catch-all. Validar la configuración y reiniciar cloudflared.
8. Publicar los CNAME administrados por Tunnel y probar el hostname antes de cambiar nameservers cuando el flujo de Cloudflare lo permita.
9. Crear en Microsoft 365 `info@brunosgarden.com` como la única dirección de correo de Bruno's Garden. Publicar y validar MX, SPF, DKIM y DMARC.
10. Configurar Turnstile para `brunosgarden.com` con SiteKey y SecretKey, y después Cloudflare Web Analytics. La verificación telefónica queda fuera de esta versión; la membresía se activa al confirmar el correo.
11. Cambiar nameservers en GoDaddy solo después de comparar la zona Cloudflare con el inventario original.
12. Mantener apagadas las cuatro banderas. Activar en orden: sitio, membresía, acumulación y promociones.

## Servicio Windows

Ejemplo de creación inicial en PowerShell elevado, ajustando la ruta final:

```powershell
New-Service -Name "OrionERP.Bruno" `
  -BinaryPathName '"C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Bruno.Web\OrionERP.Bruno.Web.exe"' `
  -DisplayName "OrionERP Bruno Public Website" `
  -StartupType Automatic
```

La identidad del servicio debe tener lectura/escritura únicamente sobre su carpeta de publicación y `App_Data\bruno-keys`, además del acceso requerido a la configuración de entorno.

El servicio se ejecuta como `LocalSystem`, por lo que los secretos de producción definidos como variables de entorno deben existir en el ámbito de máquina antes de reiniciarlo. Turnstile requiere ambas variables `ASPNETCORE_Turnstile__SiteKey` y `ASPNETCORE_Turnstile__SecretKey`; configurar solamente una impide el arranque. Cuando la membresía está habilitada, `/readyz` devuelve `503` si falta el par de llaves para impedir una publicación aparentemente saludable con registro e inicio de sesión inutilizables. `ASPNETCORE_Turnstile__ExpectedHostname` debe permanecer en `brunosgarden.com`.

## Correo en Development

`OrionERP.Bruno.Web` carga las credenciales locales desde .NET User Secrets. Configurarlas desde la raíz del repositorio, reemplazando los marcadores con los valores de la aplicación de Microsoft Entra:

```powershell
$Project = "src\OrionERP.Bruno.Web\OrionERP.Bruno.Web.csproj"

dotnet user-secrets set "BrunoGraphMail:TenantId" "<TENANT-ID>" --project $Project
dotnet user-secrets set "BrunoGraphMail:ClientId" "<CLIENT-ID>" --project $Project
dotnet user-secrets set "BrunoGraphMail:ClientSecret" "<CLIENT-SECRET-VALUE>" --project $Project
dotnet user-secrets set "BrunoGraphMail:SenderAddress" "info@brunosgarden.com" --project $Project
```

Usar el valor del secreto, no su identificador. Las credenciales nunca deben guardarse en `appsettings.json` ni confirmarse en Git. La aplicación valida esta sección al arrancar y rechaza configuraciones incompletas o un remitente distinto de `info@brunosgarden.com`.

## DNS y correo

Antes del corte, registrar para cada entrada: tipo, nombre, contenido, TTL, proxy y finalidad. Verificar:

- apex y `www` del túnel;
- MX de Microsoft 365;
- TXT SPF con un único registro SPF válido;
- los dos CNAME DKIM entregados por Microsoft 365;
- DMARC inicial con reportes y política acordada;
- verificaciones de Microsoft, Cloudflare y Facebook si existieran;
- subdominios que hoy resuelvan desde GoDaddy.

## Pruebas de salida

- `/healthz` y `/readyz` local; `/healthz` público;
- redirección HTTPS y `www` → apex;
- encabezados reenviados sin aceptar proxies remotos;
- registro, confirmación por enlace Graph, bloqueo y recuperación;
- cookie de Bruno no visible en OrionERP ni Bonhomia;
- menú, imágenes, alérgenos/dietas, promociones y condiciones;
- canonical, Open Graph, sitemap, robots y `FoodEstablishment`;
- vistas móvil y escritorio;
- banderas apagadas restauran el comportamiento seguro sin modificar ledger ni auditoría.

El aviso de privacidad y los términos son borradores operativos. La revisión jurídica es requisito de producción.
