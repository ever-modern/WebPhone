param(
    [string]$Configuration = "Release",
    [switch]$Prod
)

$projectPath = "WebPhone/WebPhone.csproj"
$publishDir = "WebPhone/bin/$Configuration/net10.0/publish/wwwroot"

Write-Host "Publishing $projectPath ($Configuration)..."
dotnet publish $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$deployArgs = @("deploy", "--dir", $publishDir)
if ($Prod) {
    $deployArgs += "--prod"
}

Write-Host "Deploying to Netlify from $publishDir..."
netlify @deployArgs
