# 一键编译发布版：输出 dist\OpenLauncher.exe（单文件，Win10/11 自带 .NET Framework 4.8，双击即用）
# 需要 .NET SDK 8+（https://dotnet.microsoft.com/download），不需要 Visual Studio。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
# 优先用项目内 .dotnet 里的 SDK（dotnet-install 脚本装的本地工具链）
if (Test-Path "$root\.dotnet\dotnet.exe") { $dotnet = "$root\.dotnet\dotnet.exe" }
if (-not $dotnet) { throw '找不到 dotnet，请先安装 .NET SDK' }

& $dotnet build "$root\src\OpenLauncher\OpenLauncher.csproj" -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw '编译失败' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root\src\OpenLauncher\bin\Release\OpenLauncher.exe" $dist -Force
# App.config 里的长路径开关已在程序启动时用代码设置，config 文件可有可无
Copy-Item "$root\src\OpenLauncher\bin\Release\OpenLauncher.exe.config" $dist -Force

$exe = Get-Item "$dist\OpenLauncher.exe"
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
"已输出 $($exe.FullName)"
"大小 $([math]::Round($exe.Length / 1KB)) KB   SHA-256 $hash"
