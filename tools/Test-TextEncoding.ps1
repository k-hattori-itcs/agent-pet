$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$extensions = @(
    '.cs', '.csproj', '.json', '.md', '.ps1', '.sln',
    '.xaml', '.xml', '.yaml', '.yml'
)
$excludedDirectories = @('.git', 'bin', 'obj', 'TestResults')
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
$failures = [System.Collections.Generic.List[string]]::new()
$mojibakePattern = [regex]'\uFFFD|\u7E3A[^\s]|\u7E67[^\s]|\u8B41[^\s]|\u873F[^\s]|\?{3,}'

$files = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File |
    Where-Object {
        $fullName = $_.FullName
        $extensions -contains $_.Extension -and
        -not ($excludedDirectories | Where-Object {
            $fullName -match "\\$([regex]::Escape($_))\\"
        })
    }

foreach ($file in $files) {
    try {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $hasUtf8Bom = $bytes.Length -ge 3 -and
            $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $containsNonAscii = $bytes | Where-Object { $_ -ge 0x80 } | Select-Object -First 1
        if ($file.Extension -eq '.ps1' -and $containsNonAscii -and -not $hasUtf8Bom) {
            $failures.Add("$($file.FullName): Windows PowerShell 5.1互換のUTF-8 BOMがありません。")
        }
        $text = $utf8.GetString($bytes)
    }
    catch {
        $failures.Add("$($file.FullName): UTF-8として読み取れません。")
        continue
    }

    $match = $mojibakePattern.Match($text)
    if ($match.Success) {
        $line = ($text.Substring(0, $match.Index) -split "\r?\n").Count
        $failures.Add("$($file.FullName):${line}: 文字化けの可能性がある文字列 '$($match.Value)'")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    throw "$($failures.Count)件の文字コード問題を検出しました。"
}

Write-Output "UTF-8・文字化け検査: $($files.Count)ファイル、問題なし"
