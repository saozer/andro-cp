param([switch]$Tiny)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Name = if ($Tiny) { 'AndroCp-win-tiny-x64' } else { 'AndroCp-win-full-x64' }
$Output = Join-Path $Root "artifacts/$Name"
if ($Tiny) {
    foreach ($Folder in @('platform-tools', 'python', 'arft')) {
        if (Test-Path -LiteralPath (Join-Path $Output $Folder)) { throw "Sortie Tiny non propre : ancien dossier $Folder. Utilisez un dossier de sortie propre." }
    }
}
dotnet run --project "$Root/tests/AndroCp.Tests" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Les tests ont échoué.' }
$SelfContained = if ($Tiny) { 'false' } else { 'true' }
dotnet publish "$Root/src/AndroCp.App" -c Release -r win-x64 --self-contained $SelfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'La compilation a échoué.' }
if (-not $Tiny) {
    $Archive = Join-Path $Root 'artifacts/platform-tools-windows.zip'
    Invoke-WebRequest 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $Archive
    Expand-Archive -Path $Archive -DestinationPath $Output -Force
    $PlatformTools = Join-Path $Output 'platform-tools'
    foreach ($File in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PlatformTools $File) -PathType Leaf)) { throw "Distribution ADB incomplète : $File est absent." }
    }
    & (Join-Path $PlatformTools 'adb.exe') version
    if ($LASTEXITCODE -ne 0) { throw 'La vérification de ADB intégré a échoué.' }
    Get-FileHash $Archive -Algorithm SHA256 | Format-List | Out-File (Join-Path $Output 'platform-tools-sha256.txt')
}
Copy-Item (Join-Path $Root 'README.md') $Output
Write-Host "Application disponible dans $Output"
if ($Tiny) { Write-Host 'Installer le runtime .NET 10 x64 et ADB (PATH) sur le PC cible. Toutes les fonctionnalités sont incluses.' }
else { Write-Host 'Runtime .NET et ADB inclus. Conserver tout le dossier de distribution, notices comprises.' }
