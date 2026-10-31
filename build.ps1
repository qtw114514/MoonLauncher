# 一键编译发布版：输出 dist\MoonLauncher.exe
#   - 默认自包含单文件（目标机不用装 .NET）
#   - -FrameworkDependent 则产出依赖 .NET 8 运行时的小体积版本
# 需要 .NET SDK 8+（https://dotnet.microsoft.com/download），不需要 Visual Studio。
# 其他平台用 sh tools/publish.sh <rid>。
param(
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
# 优先用项目内 .dotnet 里的 SDK（dotnet-install 脚本装的本地工具链）
if (Test-Path "$root\.dotnet\dotnet.exe") { $dotnet = "$root\.dotnet\dotnet.exe" }
if (-not $dotnet) { throw '找不到 dotnet，请先安装 .NET SDK' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# net8.0 之后 bin\ 里是一堆 dll + apphost，不能再只拷一个 exe；
# 要单文件必须走 publish。
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $dotnet publish "$root\src\MoonLauncher\MoonLauncher.csproj" `
    -c Release -r $Runtime --self-contained $selfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $dist -nologo
if ($LASTEXITCODE -ne 0) { throw '编译失败' }

$exe = Get-Item (Join-Path $dist 'MoonLauncher.exe')
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
"已输出 $($exe.FullName)"
"大小 $([math]::Round($exe.Length / 1MB, 1)) MB   SHA-256 $hash"
