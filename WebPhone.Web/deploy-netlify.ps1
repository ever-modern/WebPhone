param(
    [string]$Configuration = "Release",
    [switch]$Prod
)

$projectPath = "WebPhone.csproj"
$publishDir = "bin/$Configuration/net10.0/publish/wwwroot"
$functionsDir = "netlify/functions"
$rootFunctions = "..\netlify\functions"

Write-Host "Publishing $projectPath ($Configuration)..."
dotnet publish $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path $functionsDir)) {
    Write-Host "Creating Netlify functions directory at $functionsDir..."
    New-Item -ItemType Directory -Path $functionsDir -Force | Out-Null
}

if (Test-Path $rootFunctions) {
    Copy-Item -Path "$rootFunctions/*" -Destination $functionsDir -Recurse -Force
}

$deployArgs = @("deploy", "--dir", $publishDir, "--functions", $functionsDir)
if ($Prod) {
    $deployArgs += "--prod"
}

Write-Host "Deploying to Netlify from $publishDir..."
netlify @deployArgs
