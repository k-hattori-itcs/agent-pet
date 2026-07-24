$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$failures = [System.Collections.Generic.List[string]]::new()
$referenceCount = 0

foreach ($markdownName in @('README.md', 'README_EN.md')) {
    $markdownPath = Join-Path $repositoryRoot $markdownName
    $content = [System.IO.File]::ReadAllText($markdownPath, [System.Text.Encoding]::UTF8)
    $matches = [regex]::Matches(
        $content,
        '(?<path>(?:\./)?docs/assets/[A-Za-z0-9_./-]+\.(?:png|jpg|jpeg|webp))',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    foreach ($match in $matches) {
        $referenceCount++
        $relativePath = $match.Groups['path'].Value -replace '^\./', ''
        $assetPath = Join-Path $repositoryRoot ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
            $failures.Add("$markdownName references a missing asset: $relativePath")
        }
    }
}

$pngFiles = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'docs\assets') -Recurse -File -Filter '*.png'
foreach ($file in $pngFiles) {
    try {
        $image = [System.Drawing.Image]::FromFile($file.FullName)
        try {
            if ($image.Width -le 0 -or $image.Height -le 0) {
                $failures.Add("Invalid image dimensions: $($file.FullName)")
            }
        }
        finally {
            $image.Dispose()
        }
    }
    catch {
        $failures.Add("Image decode failed: $($file.FullName)")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    throw "$($failures.Count) documentation asset checks failed."
}

Write-Output "Documentation asset check: $referenceCount references, $($pngFiles.Count) PNG files"
