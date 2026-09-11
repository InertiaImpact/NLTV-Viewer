[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputDirectory = [System.IO.Path]::GetFullPath($OutputRoot)
$publishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $outputDirectory 'NLTV-Viewer-win-x64'))
$zipPath = Join-Path $outputDirectory 'NLTV-Viewer-win-x64.zip'

$requiredPrefix = $outputDirectory.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $publishDirectory.StartsWith(
    $requiredPrefix,
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish directory must be inside the selected output directory.'
}

Push-Location $repositoryRoot
try {
    dotnet restore NLTVViewer.slnx
    dotnet test tests\NltvViewer.Tests\NltvViewer.Tests.csproj --no-restore --configuration Release --maxcpucount:1

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    dotnet publish src\NltvViewer.App\NltvViewer.App.csproj --no-restore --configuration Release --runtime win-x64 --self-contained true --output $publishDirectory --maxcpucount:1

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force
    Write-Output "Portable release: $zipPath"
}
finally {
    Pop-Location
}
