param location string
param planName string
param webAppName string
param skuName string
param existingAppServicePlanResourceId string = ''
@allowed([
  'Linux'
  'Windows'
])
param operatingSystem string
param tenantId string
param clientId string
param onboardingGroupId string
param completionGroupId string
param completionQueueUri string
param appInsightsConnectionString string
param packageUrl string
param tags object

var commonSiteConfig = {
  alwaysOn: !contains([
    'F1'
    'D1'
  ], skuName)
  ftpsState: 'Disabled'
  minTlsVersion: '1.2'
  scmMinTlsVersion: '1.2'
  http20Enabled: true
  healthCheckPath: '/health/ready'
  appSettings: [
    {
      name: 'ASPNETCORE_ENVIRONMENT'
      value: 'Production'
    }
    {
      name: 'AzureAd__TenantId'
      value: tenantId
    }
    {
      name: 'AzureAd__ClientId'
      value: clientId
    }
    {
      name: 'AzureAd__CallbackPath'
      value: '/signin-oidc'
    }
    {
      name: 'Graph__BaseUrl'
      value: 'https://graph.microsoft.com/'
    }
    {
      name: 'Graph__OnboardingGroupId'
      value: onboardingGroupId
    }
    {
      name: 'Graph__CompletionGroupId'
      value: completionGroupId
    }
    {
      name: 'Graph__ManagedIdentityClientId'
      value: ''
    }
    {
      name: 'Graph__UseMock'
      value: 'false'
    }
    {
      name: 'Tap__LifetimeMinutes'
      value: '10'
    }
    {
      name: 'CompletionQueue__Uri'
      value: completionQueueUri
    }
    {
      name: 'Reconciliation__Enabled'
      value: 'true'
    }
    {
      name: 'Reconciliation__Interval'
      value: '00:10:00'
    }
    {
      name: 'Reconciliation__MaxConcurrency'
      value: '4'
    }
    {
      name: 'AllowedHosts'
      value: '*'
    }
    {
      name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
      value: appInsightsConnectionString
    }
    {
      name: 'WEBSITE_RUN_FROM_PACKAGE'
      value: empty(packageUrl) ? '1' : packageUrl
    }
  ]
}
var runtimeSiteConfig = operatingSystem == 'Linux'
  ? {
      linuxFxVersion: 'DOTNETCORE|10.0'
    }
  : {
      netFrameworkVersion: 'v10.0'
    }

resource plan 'Microsoft.Web/serverfarms@2024-11-01' = if (empty(existingAppServicePlanResourceId)) {
  name: planName
  location: location
  kind: operatingSystem == 'Linux' ? 'linux' : 'app'
  tags: tags
  sku: {
    name: skuName
    capacity: 1
  }
  properties: {
    reserved: operatingSystem == 'Linux'
    perSiteScaling: false
    zoneRedundant: false
  }
}

var appServicePlanId = empty(existingAppServicePlanResourceId)
  ? plan!.id
  : existingAppServicePlanResourceId

resource web 'Microsoft.Web/sites@2024-11-01' = {
  name: webAppName
  location: location
  kind: operatingSystem == 'Linux' ? 'app,linux' : 'app'
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlanId
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: 'Enabled'
    siteConfig: union(commonSiteConfig, runtimeSiteConfig)
  }
}

output webAppName string = web.name
output defaultHostName string = web.properties.defaultHostName
output principalId string = web.identity.principalId
output url string = 'https://${web.properties.defaultHostName}'
