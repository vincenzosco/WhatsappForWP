<#
Updates the phone's app in place, keeping the account, the chats and the logs.

A plain install removes the app before it puts the new one on, and the removal
takes the app's isolated storage with it: the next start prints
`start: no account stored, the pair page` and the person is back at the QR
code, with the chats gone until the pairing's history sync brings them back.
The platform has one other door - `AppDeployCmd /update` replaces the package
and leaves the storage alone - but it is refused outright when the package does
not carry a higher version than the one on the phone, which is why every
delivered XAP bumps the version.

This script walks both doors and keeps the data whichever opens:

  1. one ISETool snapshot of the whole store, taken before anything is touched;
  2. the update (`AppDeployCmd /update`); when it answers with `Errore` the
     phone is left as it was and the script falls through to the next step;
  3. the plain install, then a restore of the snapshot over the emptied store -
     the same dance the deploy ritual proved on the phone;
  4. a read back of the store that reports whether the session is in place.

`AppDeployCmd` exits 0 even when it prints `Errore`, so every step judges its
printed output and not its exit code. The phone must be awake for the two
tools; the launch at the end is the one step whose failure is harmless (the
screen may be locked), so it is reported and never judged.
#>
param([string]$Xap)

$ErrorActionPreference = "Continue"

$root = Split-Path -Parent $PSScriptRoot
$deploy = "C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\AppDeploy\AppDeployCmd.exe"
$ise = "C:\Program Files (x86)\Microsoft SDKs\Windows Phone\v8.1\Tools\IsolatedStorageExplorerTool\ISETool.exe"
$productId = "7ccc5b77-3cf2-4020-92a7-9542b250bb49"

# --- which package -----------------------------------------------------------

if (-not $Xap) {
    $desktop = [Environment]::GetFolderPath("Desktop")
    $newest = Get-ChildItem -Path $desktop -Filter "WhatsApp-*-ARM-Release.xap" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest -ne $null) { $Xap = $newest.FullName }
}

if (-not $Xap -or -not (Test-Path $Xap)) {
    Write-Output "Nessun XAP da installare. Passa il percorso con -Xap, o metti il file"
    Write-Output "WhatsApp-<versione>-ARM-Release.xap sul desktop."
    exit 1
}

Write-Output ("Pacchetto: " + $Xap)

if (-not (Test-Path $deploy)) { Write-Output ("Manca AppDeployCmd: " + $deploy); exit 1 }
if (-not (Test-Path $ise)) { Write-Output ("Manca ISETool: " + $ise); exit 1 }

# --- 1. the snapshot ---------------------------------------------------------

$stamp = Get-Date -Format "yyyyMMdd-HHmm"
$backup = Join-Path $root (".tools\phone-backup\" + $stamp)
Write-Output ("")
Write-Output ("1/4 Snapshot dello storage in " + $backup)
$out = & $ise ts de $productId $backup 2>&1 | Out-String
$files = Get-ChildItem -Recurse $backup -File -ErrorAction SilentlyContinue
if ($files -eq $null -or $files.Count -eq 0) {
    Write-Output "Lo snapshot non contiene nulla: il telefono e' collegato e acceso?"
    Write-Output "Niente viene installato senza uno snapshot."
    Write-Output ($out.Trim())
    exit 1
}
Write-Output ("    " + $files.Count + " file salvati.")

# --- 2. the update that keeps the storage ------------------------------------

Write-Output ("")
Write-Output ("2/4 Aggiornamento in loco (lo storage resta al suo posto)")
$out = & $deploy /update $Xap /targetdevice:0 2>&1 | Out-String
# The tool prints `Errore` in Italian and `Error` in English, and it exits 0 even
# when it failed, so the answer counts only when it is present and carries neither
# word. A blank answer proved nothing.
$updated = ($out.Trim() -ne "" -and $out -notmatch "Errore|Error")

if ($updated) {
    Write-Output "    Fatto: il pacchetto e' nuovo e i dati sono quelli di prima."
}

# --- 3. the fallback: install, then put the snapshot back --------------------

if (-not $updated) {
    Write-Output "    L'aggiornamento in loco non e' accettato:"
    foreach ($line in ($out -split "`r?`n")) {
        if ($line.Trim() -ne "") { Write-Output ("    " + $line) }
    }
    Write-Output ("")
    Write-Output ("3/4 Installazione, poi ripristino dello snapshot")
    $out2 = & $deploy /install $Xap /targetdevice:0 2>&1 | Out-String
    $installed = ($out2.Trim() -ne "" -and $out2 -notmatch "Errore|Error")
    if (-not $installed) {
        Write-Output "    L'installazione non e' riuscita:"
        foreach ($line in ($out2 -split "`r?`n")) {
            if ($line.Trim() -ne "") { Write-Output ("    " + $line) }
        }
    }

    # Whatever the install answered, the snapshot goes back: an install that
    # emptied the store is exactly what it is here to undo, and a restore over
    # a store that still holds the same words is a no-op.
    $out3 = & $ise rs de $productId $backup 2>&1 | Out-String
    if ($out3 -match "Errore|Error") {
        Write-Output "    Il ripristino non e' riuscito:"
        foreach ($line in ($out3 -split "`r?`n")) {
            if ($line.Trim() -ne "") { Write-Output ("    " + $line) }
        }
        Write-Output ("    Lo snapshot resta in " + $backup)
        exit 1
    }
    Write-Output "    Dati rimessi al loro posto."

    if (-not $installed) {
        Write-Output ("    Lo snapshot resta in " + $backup)
        exit 1
    }
}

# --- 4. the read back --------------------------------------------------------

Write-Output ("")
Write-Output ("4/4 Controllo finale")
$out = & $deploy /launch $productId /targetdevice:0 2>&1 | Out-String
if ($out -match "Errore|Error") {
    Write-Output "    L'avvio automatico non e' riuscito (schermo spento?): apri l'app a mano."
}

$check = Join-Path $env:TEMP "phone-update-check"
if (Test-Path $check) { Remove-Item -Recurse -Force $check }
& $ise ts de $productId $check 2>&1 | Out-Null

$session = Get-ChildItem -Recurse $check -Filter "session-settings.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
$chats = Get-ChildItem -Recurse $check -Filter "chats.json" -ErrorAction SilentlyContinue | Select-Object -First 1

if ($session -ne $null -and $chats -ne $null) {
    Write-Output "    La sessione e' al suo posto: account, chat e log sono quelli di prima."
}
elseif ($session -ne $null) {
    Write-Output "    La sessione c'e', ma la lista delle chat no. Guarda l'app: se chiede il QR,"
    Write-Output ("    lo snapshot da cui ripristinare e' " + $backup)
}
else {
    Write-Output "    La sessione NON e' sul telefono. Lo snapshot da cui ripristinare e':"
    Write-Output ("    " + $backup)
    Write-Output ("    ISETool.exe rs de " + $productId + " <cartella dello snapshot>")
    exit 1
}