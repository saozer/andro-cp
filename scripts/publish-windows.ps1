param([switch]$WithoutAdb)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Output = Join-Path $Root 'artifacts/AndroCp-win-x64'
dotnet run --project "$Root/tests/AndroCp.Tests" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Les tests ont échoué.' }
dotnet publish "$Root/src/AndroCp.App" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'La compilation a échoué.' }
if (-not $WithoutAdb) {
    $Archive = Join-Path $Root 'artifacts/platform-tools-windows.zip'
    Invoke-WebRequest 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $Archive
    Expand-Archive -Path $Archive -DestinationPath $Output -Force
    Get-FileHash $Archive -Algorithm SHA256 | Format-List | Out-File (Join-Path $Output 'platform-tools-sha256.txt')
}
Copy-Item (Join-Path $Root 'README.md') $Output
Write-Host "Application portable disponible dans $Output"
Write-Host 'Conserver le dossier platform-tools et ses notices avec AndroCp.exe.'
