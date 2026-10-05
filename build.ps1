# Gera dist\Estudio-Setup.exe (instalador). Requer .NET 8+ SDK no Windows.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'artifacts\app'
$payload = Join-Path $root 'installer\payload'
Remove-Item $app, $payload -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $payload, (Join-Path $root 'dist') | Out-Null

dotnet publish "$root\src\StudyDesk.Desktop\StudyDesk.Desktop.csproj" -c Release -r win-x64 --self-contained true -o $app
if ($LASTEXITCODE) { throw 'Falha ao publicar o aplicativo.' }
Copy-Item "$root\README.md" $app -ErrorAction SilentlyContinue
Copy-Item "$root\TERMOS.md" $app

Compress-Archive -Path "$app\*" -DestinationPath "$payload\app.zip" -CompressionLevel Optimal

dotnet publish "$root\installer\Estudio.Installer.csproj" -c Release -o (Join-Path $root 'dist')
if ($LASTEXITCODE) { throw 'Falha ao gerar o instalador.' }
Write-Host "Pronto: $root\dist\Estudio-Setup.exe"
