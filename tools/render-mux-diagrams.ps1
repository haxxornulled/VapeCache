param(
    [string]$MermaidCliVersion = "11.12.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$sourceRoot = Join-Path $repoRoot "docs\assets\mux"

$diagrams = @(
    @{ Input = "fast-path-flow.mmd"; Output = "fast-path-flow.svg" },
    @{ Input = "lane-connection-management.mmd"; Output = "lane-connection-management.svg" }
)

foreach ($diagram in $diagrams) {
    $inputPath = Join-Path $sourceRoot $diagram.Input
    $outputPath = Join-Path $sourceRoot $diagram.Output

    if (-not (Test-Path $inputPath)) {
        throw "Missing Mermaid source: $inputPath"
    }

    Write-Host "Rendering $($diagram.Input) -> $($diagram.Output)"
    npx -y "@mermaid-js/mermaid-cli@$MermaidCliVersion" -i $inputPath -o $outputPath
}

Write-Host "Mux diagram rendering complete."
