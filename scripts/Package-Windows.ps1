param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$OutputDirectory = 'artifacts/releases'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    # A fresh staging folder avoids accidentally shipping files left by older builds.
    $stage = Join-Path $repoRoot ('artifacts/staging-' + [guid]::NewGuid().ToString('N'))
    $releases = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Velopack 工具恢复失败。' }
    dotnet publish src/WordCatcher.App/WordCatcher.App.csproj `
        -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'Windows 应用发布失败。' }

    # Keep installation separate from %LOCALAPPDATA%/WordCatcher (the user's database/settings).
    dotnet vpk pack --packId WordCatcherDesktop --packVersion $Version `
        --packTitle 'Word Catcher' --packAuthors qup1010 `
        --packDir $stage --mainExe WordCatcher.App.exe --runtime win-x64 `
        --icon src/WordCatcher.App/Assets/WordCatcher.ico --outputDir $releases
    if ($LASTEXITCODE -ne 0) { throw '安装器与更新包生成失败。' }
    $requiredAssets = @(
        'WordCatcherDesktop-win-Setup.exe',
        'WordCatcherDesktop-win-Portable.zip',
        "WordCatcherDesktop-$Version-full.nupkg",
        'releases.win.json'
    )
    $missingAssets = @($requiredAssets | Where-Object { !(Test-Path -LiteralPath (Join-Path $releases $_) -PathType Leaf) })
    if ($missingAssets.Count -gt 0) {
        throw "打包缺少必要产物：$($missingAssets -join ', ')"
    }
    Write-Output "安装器、便携包和更新源已生成：$releases"
}
finally {
    try {
        if ($stage -and (Test-Path -LiteralPath $stage)) {
            $stagePath = [IO.Path]::GetFullPath($stage)
            $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
            if (!$stagePath.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) `
                -or (Split-Path $stagePath -Leaf) -notmatch '^staging-[0-9a-f]{32}$') {
                throw "拒绝清理工作区外的暂存目录：$stagePath"
            }
            Remove-Item -LiteralPath $stagePath -Recurse -Force
        }
    }
    finally {
        Pop-Location
    }
}
