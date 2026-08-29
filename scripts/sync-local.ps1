param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$workRoot = Split-Path (Split-Path $repoRoot -Parent) -Parent
$localRoot = Join-Path $workRoot "local-current"
$sdk = Join-Path $workRoot "dotnet-sdk\dotnet.exe"
$project = Join-Path $repoRoot "src\CustomsClearanceConsole\CustomsClearanceConsole.csproj"
$buildOutput = Join-Path $repoRoot "src\CustomsClearanceConsole\bin\$Configuration\net8.0-windows"
$localApp = Join-Path $localRoot "app"
$runtime = Join-Path $localRoot "runtime\dotnet.exe"
$appDll = Join-Path $localApp "关单核验台.dll"

foreach ($required in @($sdk, $project, $localRoot, $localApp, $runtime)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "缺少本地开发依赖：$required"
    }
}

$cliHome = Join-Path $workRoot "dotnet-cli-home"
$buildTemp = Join-Path $workRoot "build-temp"
foreach ($directory in @($cliHome, $buildTemp)) {
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
}

$env:DOTNET_CLI_HOME = $cliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:TEMP = $buildTemp
$env:TMP = $buildTemp

Push-Location $repoRoot
try {
    & $sdk build $project -c $Configuration --no-restore -v:q
    if ($LASTEXITCODE -ne 0) { throw "程序构建失败，退出代码：$LASTEXITCODE" }

    try {
        Copy-Item -Path (Join-Path $buildOutput "*") -Destination $localApp -Recurse -Force
    }
    catch [System.UnauthorizedAccessException] {
        throw "本地程序文件正在使用。请先关闭关单核验台，再重新运行同步。"
    }

    & $runtime $appDll --ui-contract-self-test
    if ($LASTEXITCODE -ne 0) { throw "本地回归测试失败，退出代码：$LASTEXITCODE" }

    Write-Host "本地版本已更新：$localRoot"
    Write-Host "日常迭代未创建 ZIP，也未复制 OCR 模型或 .NET 运行时。"
}
finally {
    Pop-Location
}
