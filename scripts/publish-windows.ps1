param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo "artifacts/windows/$Runtime"
dotnet publish (Join-Path $repo 'src/CapFast/CapFast.csproj') --configuration Release --runtime $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:UsedAvaloniaProducts= --output $output
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
foreach ($file in @('CapFast.exe', 'onnxruntime.dll', 'Models/silero_vad.onnx', 'third_party/silero-vad/LICENSE')) {
    if (-not (Test-Path (Join-Path $output $file))) { throw "Missing packaged dependency: $file" }
}
Write-Host "Built $output. Distribute the whole directory, not just CapFast.exe."
Write-Host 'Install the Microsoft Visual C++ 2015-2022 Redistributable for the target architecture if needed.'
