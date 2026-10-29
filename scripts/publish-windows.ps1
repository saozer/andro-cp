param([switch]$WithoutAdb, [switch]$WithArft)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
if ($WithoutAdb -and $WithArft) { throw 'La variante ARFT exige ADB intégré.' }
$Name = if ($WithArft) { 'AndroCp-win-arft-x64' } elseif ($WithoutAdb) { 'AndroCp-win-tiny-x64' } else { 'AndroCp-win-adb-x64' }
$Output = Join-Path $Root "artifacts/$Name"
dotnet run --project "$Root/tests/AndroCp.Tests" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Les tests ont échoué.' }
dotnet publish "$Root/src/AndroCp.App" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'La compilation a échoué.' }
if (-not $WithoutAdb) {
    $Archive = Join-Path $Root 'artifacts/platform-tools-windows.zip'
    Invoke-WebRequest 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $Archive
    Expand-Archive -Path $Archive -DestinationPath $Output -Force
    $PlatformTools = Join-Path $Output 'platform-tools'
    foreach ($File in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PlatformTools $File) -PathType Leaf)) {
            throw "Distribution ADB incomplète : $File est absent."
        }
    }
    & (Join-Path $PlatformTools 'adb.exe') version
    if ($LASTEXITCODE -ne 0) { throw 'La vérification de ADB intégré a échoué.' }
    Get-FileHash $Archive -Algorithm SHA256 | Format-List | Out-File (Join-Path $Output 'platform-tools-sha256.txt')
}
if ($WithArft) {
    $PythonVersion = '3.14.10'
    $PythonArchive = Join-Path $Root "artifacts/python-$PythonVersion-embed-amd64.zip"
    Invoke-WebRequest "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip" -OutFile $PythonArchive
    $PythonDirectory = Join-Path $Output 'python'
    Expand-Archive -Path $PythonArchive -DestinationPath $PythonDirectory -Force
    $PathFile = Get-ChildItem -LiteralPath $PythonDirectory -Filter '*._pth' -File | Select-Object -First 1
    if (-not $PathFile) { throw 'Configuration Python portable absente.' }
    $PythonPaths = (Get-Content -LiteralPath $PathFile.FullName) -replace '^#import site$', 'import site'
    $PythonPaths += 'Lib\site-packages'
    Set-Content -LiteralPath $PathFile.FullName -Value $PythonPaths -Encoding ascii
    python -m pip install --disable-pip-version-check --only-binary=:all: --require-hashes --no-compile `
        --target (Join-Path $PythonDirectory 'Lib/site-packages') --requirement (Join-Path $Root 'python/requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Installation de ARFT et de ses dépendances échouée.' }
    $BridgeDirectory = Join-Path $Output 'arft'
    New-Item -ItemType Directory -Path $BridgeDirectory -Force | Out-Null
    Copy-Item (Join-Path $Root 'python/bridge.py') $BridgeDirectory
    Copy-Item (Join-Path $Root 'python/requirements.txt') $BridgeDirectory
    & (Join-Path $PythonDirectory 'python.exe') (Join-Path $BridgeDirectory 'bridge.py') --check
    if ($LASTEXITCODE -ne 0) { throw 'La vérification de Python/ARFT portable a échoué.' }
    dotnet run --project "$Root/tests/AndroCp.Tests" -c Release --no-build -- --arft-smoke `
        (Join-Path $PythonDirectory 'python.exe') (Join-Path $BridgeDirectory 'bridge.py')
    if ($LASTEXITCODE -ne 0) { throw 'Le test C# → Python portable → ARFT → ADB simulé a échoué.' }
    Get-FileHash $PythonArchive -Algorithm SHA256 | Format-List | Out-File (Join-Path $Output 'python-sha256.txt')
}
Copy-Item (Join-Path $Root 'README.md') $Output
Write-Host "Application portable disponible dans $Output"
if (-not $WithoutAdb) {
    Write-Host 'ADB intégré et vérifié. Conserver le dossier platform-tools et ses notices avec AndroCp.exe.'
} else {
    Write-Host 'ADB non inclus : adb.exe doit être disponible dans le PATH du PC cible.'
}
