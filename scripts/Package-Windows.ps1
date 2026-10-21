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
    $stage = Join-Path $repoRoot ('artifacts/staging-' + [guid]::NewGuid().ToString('N'))
    $releases = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (!$releases.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "产物目录必须位于仓库的 artifacts 目录中：$releases"
    }
    $null = New-Item -ItemType Directory -Path $releases -Force

    $isccCandidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    if (!$isccCandidates) { throw '未找到 Inno Setup 6 编译器 ISCC.exe。请先安装 Inno Setup 6。' }
    $iscc = $isccCandidates[0]

    # Clear only known output names from previous package runs.
    $staleArtifacts = Get-ChildItem -LiteralPath $releases -File | Where-Object {
        $_.Name -in @('WordCatcherDesktop-win-Setup.exe', 'WordCatcherDesktop-win-Portable.zip', 'releases.win.json', 'assets.win.json', 'RELEASES') `
            -or $_.Name -match '^WordCatcherDesktop-.+\.nupkg$'
    }
    $staleArtifacts | Remove-Item -Force

    dotnet publish src/WordCatcher.App/WordCatcher.App.csproj `
        -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'Windows 应用发布失败。' }

    $portable = Join-Path $releases 'WordCatcherDesktop-win-Portable.zip'
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $portable -CompressionLevel Optimal
    if ($LASTEXITCODE -ne 0) { throw '便携包生成失败。' }

    $installerScript = Join-Path $repoRoot 'installer/WordCatcherDesktop.iss'
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$stage" "/DOutputDir=$releases" $installerScript
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 安装包生成失败。' }

    $requiredAssets = @(
        'WordCatcherDesktop-win-Setup.exe',
        'WordCatcherDesktop-win-Portable.zip'
    )
    $missingAssets = @($requiredAssets | Where-Object { !(Test-Path -LiteralPath (Join-Path $releases $_) -PathType Leaf) })
    if ($missingAssets.Count -gt 0) {
        throw "打包缺少必要产物：$($missingAssets -join ', ')"
    }
    Write-Output "Inno Setup 安装包和便携包已生成：$releases"
}
finally {
    try {
        if ($stage -and (Test-Path -LiteralPath $stage)) {
            $stagePath = [IO.Path]::GetFullPath($stage)
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
