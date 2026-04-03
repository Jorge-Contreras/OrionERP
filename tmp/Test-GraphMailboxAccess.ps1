param(
  [Parameter(Mandatory = $true)]
  [string]$TenantId,

  [Parameter(Mandatory = $true)]
  [string]$ClientId,

  [Parameter(Mandatory = $true)]
  [string]$ClientSecret,

  [string]$MailboxAddress,

  [switch]$ReadInbox,

  [string]$ProbeRecipient
)

$ErrorActionPreference = "Stop"

function Get-JwtPayload {
  param([Parameter(Mandatory = $true)][string]$Token)

  $parts = $Token.Split(".")
  if ($parts.Length -lt 2) {
    throw "Access token is not a JWT."
  }

  $payload = $parts[1].Replace("-", "+").Replace("_", "/")
  switch ($payload.Length % 4) {
    2 { $payload += "==" }
    3 { $payload += "=" }
  }

  $json = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload))
  return $json | ConvertFrom-Json
}

function Get-ErrorBody {
  param([Parameter(Mandatory = $true)]$ErrorRecord)

  if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
    return $ErrorRecord.ErrorDetails.Message
  }

  return $ErrorRecord.Exception.Message
}

function Get-GraphToken {
  param(
    [Parameter(Mandatory = $true)][string]$TenantId,
    [Parameter(Mandatory = $true)][string]$ClientId,
    [Parameter(Mandatory = $true)][string]$ClientSecret
  )

  try {
    return Invoke-RestMethod `
      -Method Post `
      -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
      -ContentType "application/x-www-form-urlencoded" `
      -Body @{
        client_id = $ClientId
        client_secret = $ClientSecret
        scope = "https://graph.microsoft.com/.default"
        grant_type = "client_credentials"
      }
  }
  catch {
    $body = Get-ErrorBody $_
    throw "Token request failed. $body"
  }
}

function Invoke-GraphJson {
  param(
    [Parameter(Mandatory = $true)][ValidateSet("GET", "POST")][string]$Method,
    [Parameter(Mandatory = $true)][string]$Uri,
    [Parameter(Mandatory = $true)][string]$AccessToken,
    [object]$Body
  )

  $headers = @{
    Authorization = "Bearer $AccessToken"
  }

  try {
    if ($PSBoundParameters.ContainsKey("Body")) {
      return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 8)
    }

    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
  }
  catch {
    $body = Get-ErrorBody $_
    throw "Graph request failed for $Uri. $body"
  }
}

$tokenResponse = Get-GraphToken -TenantId $TenantId -ClientId $ClientId -ClientSecret $ClientSecret
$accessToken = $tokenResponse.access_token
$payload = Get-JwtPayload -Token $accessToken
$roles = @($payload.roles)

$expiresUtc = [DateTimeOffset]::FromUnixTimeSeconds([int64]$payload.exp).UtcDateTime

Write-Host ""
Write-Host "Token acquired successfully."
[PSCustomObject]@{
  AppId      = $payload.appid
  TenantId   = $payload.tid
  Audience   = $payload.aud
  ExpiresUtc = $expiresUtc.ToString("u")
  Roles      = if ($roles.Count -gt 0) { $roles -join ", " } else { "<none>" }
} | Format-List

if ([string]::IsNullOrWhiteSpace($MailboxAddress)) {
  return
}

Write-Host ""
Write-Host "Mailbox target: $MailboxAddress"

$directoryRoles = @("User.Read.All", "User.ReadBasic.All", "Directory.Read.All")
$mailReadRoles = @("Mail.Read", "Mail.ReadWrite", "Mail.ReadBasic", "Mail.ReadBasic.All")
$canLookupMailbox = $roles | Where-Object { $directoryRoles -contains $_ }
$canReadMailbox = $roles | Where-Object { $mailReadRoles -contains $_ }
$canSendMail = $roles -contains "Mail.Send"

if ($canLookupMailbox) {
  $encodedMailbox = [Uri]::EscapeDataString($MailboxAddress)
  $user = Invoke-GraphJson `
    -Method GET `
    -Uri "https://graph.microsoft.com/v1.0/users/$encodedMailbox?`$select=id,displayName,mail,userPrincipalName" `
    -AccessToken $accessToken

  [PSCustomObject]@{
    Id                = $user.id
    DisplayName       = $user.displayName
    Mail              = $user.mail
    UserPrincipalName = $user.userPrincipalName
  } | Format-List
}
else {
  Write-Warning "Token does not include a directory-read role, so mailbox lookup by address could not be verified."
}

if ($ReadInbox) {
  if (-not $canReadMailbox) {
    Write-Warning "Token does not include a mail-read application role, so inbox access cannot be validated."
  }
  else {
    $encodedMailbox = [Uri]::EscapeDataString($MailboxAddress)
    $messages = Invoke-GraphJson `
      -Method GET `
      -Uri "https://graph.microsoft.com/v1.0/users/$encodedMailbox/mailFolders/inbox/messages?`$top=1&`$select=id,subject,receivedDateTime" `
      -AccessToken $accessToken

    $latest = $messages.value | Select-Object -First 1
    if ($null -eq $latest) {
      Write-Host "Inbox read succeeded, but no messages were returned."
    }
    else {
      [PSCustomObject]@{
        MessageId        = $latest.id
        Subject          = $latest.subject
        ReceivedDateTime = $latest.receivedDateTime
      } | Format-List
    }
  }
}

if (-not [string]::IsNullOrWhiteSpace($ProbeRecipient)) {
  if (-not $canSendMail) {
    Write-Warning "Token does not include Mail.Send, so a send probe cannot be executed."
  }
  else {
    $encodedMailbox = [Uri]::EscapeDataString($MailboxAddress)
    $probeBody = @{
      message = @{
        subject = "OrionERP Graph probe $(Get-Date -Format s)"
        body = @{
          contentType = "Text"
          content = "This is a Graph mailbox access probe generated by OrionERP troubleshooting."
        }
        toRecipients = @(
          @{
            emailAddress = @{
              address = $ProbeRecipient
            }
          }
        )
      }
      saveToSentItems = $false
    }

    Invoke-GraphJson `
      -Method POST `
      -Uri "https://graph.microsoft.com/v1.0/users/$encodedMailbox/sendMail" `
      -AccessToken $accessToken `
      -Body $probeBody | Out-Null

    Write-Host "Send probe accepted by Microsoft Graph."
  }
}
