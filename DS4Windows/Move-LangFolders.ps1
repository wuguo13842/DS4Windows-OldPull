param([string]$OutputPath)

Write-Host "Moving language folders..."
if (![System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, $OutputPath))
}
Write-Host "Output: $OutputPath"

if (!(Test-Path $OutputPath)) {
    Write-Host "ERROR: Path not found"
    exit 1
}

$exclude = @('BezierCurveEditor','Lang','Logs','Profiles','runtimes')
$langPath = Join-Path $OutputPath "Lang"

if (!(Test-Path $langPath)) {
    New-Item -ItemType Directory -Path $langPath -Force | Out-Null
}

Get-ChildItem -Path $OutputPath -Directory | ForEach-Object {
    if ($exclude -contains $_.Name) {
        Write-Host "Skip: $($_.Name)"
    } else {
        Write-Host "Move: $($_.Name)"
        $dest = Join-Path $langPath $_.Name
        if (Test-Path $dest) { Remove-Item -Path $dest -Recurse -Force }
        Move-Item -Path $_.FullName -Destination $dest -Force
    }
}

Write-Host "Done"
exit 0