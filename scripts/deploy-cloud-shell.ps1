[CmdletBinding()]
param(
    [string] $SubscriptionId,
    [string] $Location = 'westus2',
    [ValidateLength(3, 24)]
    [string] $NamePrefix = 'passkey-onboarding',
    [string] $EnvironmentName = 'prod',
    [ValidateSet('F1', 'B1', 'S1', 'P0v3')]
    [string] $AppServiceSku = 'B1',
    [ValidateSet('Linux', 'Windows')]
    [string] $OperatingSystem = 'Linux',
    [string] $ExistingAppServicePlanResourceId = '',
    [string] $OnboardingGroupId = '',
    [string] $CompletionGroupId = '',
    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
$templateUri = 'https://raw.githubusercontent.com/brunofreitas-br/entra-passkey-onboarding-tap/84c8321b083183035192502fae54c5632276d2b7/azuredeploy.json'

function Read-RequiredValue {
    param([string] $Prompt, [string] $CurrentValue)

    if (-not [string]::IsNullOrWhiteSpace($CurrentValue)) {
        return $CurrentValue
    }

    $value = Read-Host $Prompt
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Prompt is required."
    }
    return $value
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required. Run this script from Azure Cloud Shell in PowerShell mode.'
}

$SubscriptionId = Read-RequiredValue 'Azure subscription ID' $SubscriptionId
az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) {
    throw 'The Azure subscription could not be selected.'
}

$account = az account show --output json | ConvertFrom-Json
$tenantId = $account.tenantId

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

function ConvertFrom-JwtPayload {
    param([Parameter(Mandatory)][string] $Token)

    $payload = $Token.Split('.')[1].Replace('-', '+').Replace('_', '/')
    switch ($payload.Length % 4) {
        2 { $payload += '==' }
        3 { $payload += '=' }
    }

    $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload))
    return $json | ConvertFrom-Json
}

Write-Host ''
Write-Host "Deployment region: $Location" -ForegroundColor Cyan
Write-Host "App Service SKU:  $AppServiceSku"
if (-not [string]::IsNullOrWhiteSpace($ExistingAppServicePlanResourceId)) {
    Write-Host 'App Service Plan: existing'
}
Write-Host ''
Write-Host 'Microsoft Entra prerequisites' -ForegroundColor Cyan
Write-Host 'Activate these roles before continuing:'
Write-Host '  - Global Administrator (validated deployment path)'
Write-Host '  - Azure Owner on the target subscription'
Write-Host ''
Write-Host 'The Global Administrator role can be activated temporarily through PIM and deactivated after deployment.'
Write-Host ''

$graphToken = az account get-access-token `
    --resource-type ms-graph `
    --query accessToken `
    --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($graphToken)) {
    throw "Azure CLI couldn't acquire a Microsoft Graph token."
}

$tokenClaims = ConvertFrom-JwtPayload -Token $graphToken
$grantedScopes = @($tokenClaims.scp -split ' ' | Where-Object { $_ })
$requiredScopes = @(
    'Application.ReadWrite.All',
    'AppRoleAssignment.ReadWrite.All',
    'DelegatedPermissionGrant.ReadWrite.All',
    'Group.ReadWrite.All'
)
$missingScopes = @($requiredScopes | Where-Object { $_ -notin $grantedScopes })
if ($missingScopes.Count -gt 0) {
    throw @"
The Azure CLI Microsoft Graph token is missing required delegated scopes:
  $($missingScopes -join "`n  ")

Refresh the Cloud Shell Azure CLI session with Microsoft Graph consent:

  az logout
  az login --tenant $tenantId --scope https://graph.microsoft.com/.default
  az account set --subscription $SubscriptionId

Then run this preflight again.
"@
}

az rest `
    --method GET `
    --url "https://graph.microsoft.com/v1.0/servicePrincipals(appId='00000003-0000-0000-c000-000000000000')?`$select=id" `
    --output none
if ($LASTEXITCODE -ne 0) {
    throw 'The Azure CLI session cannot read the Microsoft Graph service principal. Activate the required Microsoft Entra role and sign in again.'
}

if ([string]::IsNullOrWhiteSpace($ExistingAppServicePlanResourceId)) {
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
        throw "No remaining App Service quota for SKU '$AppServiceSku' in '$Location'. Current usage: $($selectedQuota.currentValue); limit: $($selectedQuota.limit). Use an existing plan, choose another region/SKU, or request quota."
    }
}

$deploymentParameters = @(
    "namePrefix=$NamePrefix",
    "environmentName=$EnvironmentName",
    "location=$Location",
    "appServiceSku=$AppServiceSku",
    "existingAppServicePlanResourceId=$ExistingAppServicePlanResourceId",
    "operatingSystem=$OperatingSystem",
    "tenantId=$tenantId",
    "onboardingGroupId=$OnboardingGroupId",
    "completionGroupId=$CompletionGroupId"
)

$validationName = "passkey-preflight-$(Get-Date -Format 'yyyyMMddHHmmss')"
Write-Host ''
Write-Host 'Running Azure + Microsoft Graph preflight. No resources are created.' -ForegroundColor Cyan

az deployment sub validate `
    --name $validationName `
    --location $Location `
    --template-uri $templateUri `
    --parameters $deploymentParameters `
    --output none

if ($LASTEXITCODE -ne 0) {
    throw @'
Preflight failed. No deployment was started.

Review the validation error printed above. Common causes include:
  - Global Administrator isn't active in Microsoft Entra ID
  - Owner isn't assigned on the target Azure subscription
  - the Cloud Shell Azure CLI session wasn't refreshed after role activation or consent
  - App Service quota is unavailable in the selected region
  - a parameter value is invalid
'@
}

Write-Host 'Preflight succeeded.' -ForegroundColor Green
if ($ValidateOnly) {
    Write-Host 'ValidateOnly was specified. No resources were created.'
    return
}

$confirmation = Read-Host 'Type DEPLOY to create the resources'
if ($confirmation -cne 'DEPLOY') {
    Write-Host 'Deployment cancelled. No resources were created.'
    return
}

$deploymentName = "passkey-onboarding-$EnvironmentName-$(Get-Date -Format 'yyyyMMddHHmmss')"
$outputsJson = az deployment sub create `
    --name $deploymentName `
    --location $Location `
    --template-uri $templateUri `
    --parameters $deploymentParameters `
    --query properties.outputs `
    --output json

if ($LASTEXITCODE -ne 0) {
    throw 'Deployment failed after a successful preflight. Review the deployment operations in the Azure portal.'
}

$outputs = $outputsJson | ConvertFrom-Json
Write-Host ''
Write-Host 'Deployment completed successfully.' -ForegroundColor Green
Write-Host "Application:       $($outputs.webUrl.value)"
Write-Host "Resource group:    $($outputs.resourceGroupName.value)"
Write-Host "Onboarding group:  $($outputs.onboardingGroupId.value)"
Write-Host "Completion group:  $($outputs.completionGroupId.value)"
Write-Host "App client ID:     $($outputs.portalApplicationClientId.value)"
