[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $TenantId,

    [Parameter(Mandatory)]
    [string] $SubscriptionId,

    [string] $Location = 'westus2',
    [ValidateLength(3, 24)]
    [string] $NamePrefix = 'passkey-onboarding',
    [string] $EnvironmentName = 'dev',
    [ValidateSet('F1', 'B1', 'S1', 'P0v3')]
    [string] $AppServiceSku = 'B1',
    [ValidateSet('Linux', 'Windows')]
    [string] $OperatingSystem = 'Linux',
    [string] $ExistingAppServicePlanResourceId = '',
    [string] $OnboardingGroupId = '',
    [string] $CompletionGroupId = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path ([System.IO.Path]::GetTempPath()) "entra-passkey-onboarding-$PID"
$publishPath = Join-Path $artifacts 'publish'
$zipPath = Join-Path $artifacts 'app.zip'
$workerPublishPath = Join-Path $artifacts 'worker-publish'
$workerZipPath = Join-Path $artifacts 'worker.zip'
$template = Join-Path $root 'infra/main.bicep'
$project = Join-Path $root 'src/TapPasskeyOnboarding.Web/TapPasskeyOnboarding.Web.csproj'
$workerProject = Join-Path $root 'src/TapPasskeyOnboarding.TransitionWorker/TapPasskeyOnboarding.TransitionWorker.csproj'

foreach ($command in @('az', 'dotnet')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command '$command' was not found."
    }
}

az account set --subscription $SubscriptionId
$accountTenant = az account show --query tenantId -o tsv
if ($accountTenant -ne $TenantId) {
    throw "Azure CLI is signed in to tenant '$accountTenant', not '$TenantId'."
}

az bicep install | Out-Null

if (-not [string]::IsNullOrWhiteSpace($ExistingAppServicePlanResourceId)) {
    $existingPlan = az resource show `
        --ids $ExistingAppServicePlanResourceId `
        --api-version 2024-11-01 `
        --output json |
        ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $existingPlan.type -ne 'Microsoft.Web/serverfarms') {
        throw "ExistingAppServicePlanResourceId isn't a valid App Service Plan."
    }
    $Location = $existingPlan.location
    $AppServiceSku = $existingPlan.sku.name
    $planIsLinux = $existingPlan.kind -match 'linux' -or $existingPlan.properties.reserved -eq $true
    $OperatingSystem = $planIsLinux ? 'Linux' : 'Windows'
}
else {
    $quotaResponse = az rest `
        --method GET `
        --url "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.Web/locations/$Location/usages?api-version=2024-11-01" `
        --output json | ConvertFrom-Json
    $selectedQuota = $quotaResponse.value |
        Where-Object { $_.name.value -eq $AppServiceSku } |
        Select-Object -First 1
    $sharedQuota = $quotaResponse.value |
        Where-Object { $_.name.value -eq '*' } |
        Select-Object -First 1
    if (-not $selectedQuota -and $sharedQuota) {
        $selectedQuota = $sharedQuota
    }
    if (-not $selectedQuota -or $selectedQuota.limit -le $selectedQuota.currentValue) {
        throw "No remaining App Service quota for SKU '$AppServiceSku' in '$Location'."
    }
}

$deploymentName = "passkey-onboarding-$EnvironmentName-$(Get-Date -Format 'yyyyMMddHHmmss')"
$outputsJson = az deployment sub create `
    --name $deploymentName `
    --location $Location `
    --template-file $template `
    --parameters `
        namePrefix=$NamePrefix `
        environmentName=$EnvironmentName `
        location=$Location `
        appServiceSku=$AppServiceSku `
        operatingSystem=$OperatingSystem `
        existingAppServicePlanResourceId=$ExistingAppServicePlanResourceId `
        tenantId=$TenantId `
        onboardingGroupId=$OnboardingGroupId `
        completionGroupId=$CompletionGroupId `
        webPackageUrl='' `
        workerPackageUrl='' `
    --query properties.outputs `
    --output json

if ($LASTEXITCODE -ne 0) {
    throw 'Infrastructure deployment failed.'
}

$outputs = $outputsJson | ConvertFrom-Json
$resourceGroup = $outputs.resourceGroupName.value
$webAppName = $outputs.webAppName.value
$functionAppName = $outputs.transitionFunctionName.value
$webUrl = $outputs.webUrl.value

Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item $publishPath -ItemType Directory -Force | Out-Null

dotnet publish $project -c Release -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw 'Application publish failed.'
}

[System.IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $zipPath)

New-Item $workerPublishPath -ItemType Directory -Force | Out-Null
dotnet publish $workerProject -c Release -o $workerPublishPath
if ($LASTEXITCODE -ne 0) {
    throw 'Transition worker publish failed.'
}
[System.IO.Compression.ZipFile]::CreateFromDirectory($workerPublishPath, $workerZipPath)

az webapp deploy `
    --resource-group $resourceGroup `
    --name $webAppName `
    --src-path $zipPath `
    --type zip `
    --clean true `
    --restart true `
    --async true `
    --output none

if ($LASTEXITCODE -ne 0) {
    throw 'ZIP deployment failed.'
}

az functionapp deployment source config-zip `
    --resource-group $resourceGroup `
    --name $functionAppName `
    --src $workerZipPath `
    --build-remote false `
    --output none

if ($LASTEXITCODE -ne 0) {
    throw 'Transition worker ZIP deployment failed.'
}

$healthUrl = "$webUrl/health/ready"
$healthy = $false
for ($attempt = 1; $attempt -le 30; $attempt++) {
    try {
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
        if ($response.status -eq 'healthy') {
            $healthy = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 10
    }
}

if (-not $healthy) {
    throw "Deployment completed, but '$healthUrl' didn't become healthy."
}

Write-Host ''
Write-Host 'Deployment completed successfully.' -ForegroundColor Green
Write-Host "Application:       $webUrl"
Write-Host "Transition worker: $functionAppName"
Write-Host "Resource group:    $resourceGroup"
Write-Host "Onboarding group:  $($outputs.onboardingGroupId.value)"
Write-Host "Completion group:  $($outputs.completionGroupId.value)"
Write-Host "App client ID:     $($outputs.portalApplicationClientId.value)"

Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
