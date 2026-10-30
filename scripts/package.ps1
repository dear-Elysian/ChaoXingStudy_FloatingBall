# 打包便携版（方案 §6）：自包含 .NET 运行时 + jlink 裁剪 JRE
# 成品输出到 学习通悬浮球\app\，不再有 dist 外层
# 用法：powershell -ExecutionPolicy Bypass -File scripts\package.ps1
param(
    [string]$OutDir = "学习通悬浮球",
    [string]$Rid = "win-x64",
    [string[]]$JreModules = @(
        "java.base", "java.net.http", "java.logging", "jdk.crypto.ec", "java.xml"
    )
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "[1/4] 构建 Java 核心 (mvn package) ..."
mvn -q -f src\core-java\pom.xml clean package
if ($LASTEXITCODE -ne 0) { throw "Maven 构建失败（退出码 $LASTEXITCODE）" }
if (-not (Test-Path app\core\core.jar)) { throw "core.jar 未生成，检查 Maven 输出" }

Write-Host "[2/4] 清空输出目录 $OutDir ..."
# data\ 由程序首次运行自动创建，不属于成品内容，打包时整体清空
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path "$OutDir\app" -Force | Out-Null

Write-Host "[3/4] 发布 C# 外壳（自包含单文件 → app\launcher.exe）..."
# 本机 PATH 里的 dotnet 可能只有运行时；挑第一个带 SDK 的
$cands = @()
$cands += @(Get-Command dotnet -All -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
$cands += "C:\Program Files (x86)\C#\dotnet.exe"
$dotnet = $null
foreach ($c in $cands) {
    if ($c -and (Test-Path $c) -and (& $c --list-sdks 2>$null)) { $dotnet = $c; break }
}
if (-not $dotnet) { throw "未找到 .NET SDK（dotnet --list-sdks 为空）" }
& $dotnet publish src\shell-csharp\CxBall.Shell\CxBall.Shell.csproj `
    -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:AssemblyName=launcher `
    -p:DebugType=none `
    -o "$OutDir\app" --nologo
if ($LASTEXITCODE -ne 0) { throw ".NET 发布失败（退出码 $LASTEXITCODE）" }
if (-not (Test-Path "$OutDir\app\launcher.exe")) { throw "launcher.exe 未生成" }

Write-Host "[4/4] 生成 jlink 运行时 + 复制只读资源 ..."
New-Item -ItemType Directory -Path "$OutDir\app\core" -Force | Out-Null
Copy-Item app\core\core.jar "$OutDir\app\core\core.jar" -Force
jlink --add-modules ($JreModules -join ",") --output "$OutDir\app\jre" `
    --strip-debug --no-header-files --no-man-pages --compress=2
if ($LASTEXITCODE -ne 0) { throw "jlink 运行时生成失败（退出码 $LASTEXITCODE）" }
Copy-Item app\resources "$OutDir\app\resources" -Recurse -Force
# 单文件发布仍会附带 WebView2 的 XML 文档，成品不需要
Get-ChildItem "$OutDir\app" -Filter *.xml | Remove-Item -Force

$exeMB  = [math]::Round((Get-Item "$OutDir\app\launcher.exe").Length / 1MB, 1)
$jreMB  = [math]::Round((Get-ChildItem "$OutDir\app\jre" -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
$coreMB = [math]::Round((Get-Item "$OutDir\app\core\core.jar").Length / 1MB, 2)
Write-Host ""
Write-Host "完成：$((Resolve-Path $OutDir).Path)"
Write-Host "  app\launcher.exe $exeMB MB（含 .NET 运行时）"
Write-Host "  app\jre          $jreMB MB（jlink 裁剪）"
Write-Host "  app\core\core.jar $coreMB MB"
