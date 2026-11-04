targetScope = 'subscription'

extension microsoftGraphV1

@minLength(3)
@maxLength(24)
param namePrefix string = 'passkey-onboarding'

param environmentName string = 'dev'
param location string = 'westus2'
param appServiceSku string = 'B1'
@allowed([
  'Linux'
  'Windows'
])
param operatingSystem string = 'Linux'
param tenantId string
param webPackageUrl string = 'https://github.com/brunofreitas-br/entra-passkey-onboarding-tap/releases/download/v1.1.0/web.zip'
param workerPackageUrl string = 'https://github.com/brunofreitas-br/entra-passkey-onboarding-tap/releases/download/v1.1.0/worker.zip'

@description('Use an existing Assigned security group, or leave empty to create one.')
param onboardingGroupId string = ''

@description('Use an existing Assigned completion group, or leave empty to create one.')
param completionGroupId string = ''

param resourceGroupName string = 'rg-${namePrefix}-${environmentName}'

var suffix = uniqueString(subscription().id, namePrefix, environmentName)
var safePrefix = toLower(replace(namePrefix, '_', '-'))
var webAppName = take('${safePrefix}-${environmentName}-${suffix}', 60)
var planName = 'asp-${safePrefix}-${environmentName}'
var workspaceName = 'log-${safePrefix}-${environmentName}-${suffix}'
var appInsightsName = 'appi-${safePrefix}-${environmentName}-${suffix}'
var functionPlanName = 'asp-func-${safePrefix}-${environmentName}'
var functionAppName = take('func-${safePrefix}-${environmentName}-${suffix}', 60)
var storageAccountName = take('st${replace(safePrefix, '-', '')}${environmentName}${suffix}', 24)
var queueName = 'completion-transitions'
var redirectUri = 'https://${webAppName}.azurewebsites.net/signin-oidc'
var graphApplicationId = '00000003-0000-0000-c000-000000000000'
var graphOpenIdScopeId = '37f7f235-527c-4136-accd-4a02d197296e'
var graphProfileScopeId = '14dad69e-099b-42c9-810b-d002981feec1'
var graphUserAuthMethodPasskeyReadAllRoleId = '72e00c1d-3e3d-43bb-a0b9-c435611bb1d2'
var graphUserAuthMethodTapReadWriteAllRoleId = '627169a8-8c15-451c-861a-5b80e383de5c'
var graphGroupMemberReadWriteAllRoleId = 'dbaae8cf-10b5-4b86-a4a1-f871c94c6695'
var graphGroupMemberReadAllRoleId = '98830695-27a2-44f7-8c18-0c3ebc9698f6'
var graphUserReadAllRoleId = 'df021288-bdef-4463-88db-98f22de89214'
var graphRoleManagementReadDirectoryRoleId = '483bed4a-2ad3-4361-a73b-c83ccdbdc53c'
var graphPrivilegedGroupEligibilityReadRoleId = 'edb419d6-7edc-42a3-9345-509bfdf5d87c'
var tags = {
  application: 'entra-passkey-onboarding-tap'
  environment: environmentName
  'deployed-with': 'bicep'
}

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

resource createdOnboardingGroup 'Microsoft.Graph/groups@v1.0' = if (empty(onboardingGroupId)) {
  uniqueName: '${safePrefix}-${environmentName}-${suffix}-onboarding'
  displayName: '${namePrefix}-${environmentName}-onboarding'
  description: 'Users authorized to request a Temporary Access Pass for Passkey onboarding.'
  mailEnabled: false
  mailNickname: '${take(replace(safePrefix, '-', ''), 20)}${environmentName}onboard'
  securityEnabled: true
}

resource createdCompletionGroup 'Microsoft.Graph/groups@v1.0' = if (empty(completionGroupId)) {
  uniqueName: '${safePrefix}-${environmentName}-${suffix}-completed'
  displayName: '${namePrefix}-${environmentName}-completed'
  description: 'Users who completed Passkey registration.'
  mailEnabled: false
  mailNickname: '${take(replace(safePrefix, '-', ''), 20)}${environmentName}complete'
  securityEnabled: true
}

var resolvedOnboardingGroupId = empty(onboardingGroupId) ? createdOnboardingGroup!.id : onboardingGroupId
var resolvedCompletionGroupId = empty(completionGroupId) ? createdCompletionGroup!.id : completionGroupId

resource portalApplication 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: '${safePrefix}-${environmentName}-${suffix}'
  displayName: '${namePrefix} (${environmentName})'
  description: 'Single-tenant sign-in identity for Passkey onboarding.'
  signInAudience: 'AzureADMyOrg'
  isFallbackPublicClient: false
  optionalClaims: {
    idToken: [
      {
        name: 'amr'
        essential: true
      }
    ]
  }
  requiredResourceAccess: [
    {
      resourceAppId: graphApplicationId
      resourceAccess: [
        {
          id: graphOpenIdScopeId
          type: 'Scope'
        }
        {
          id: graphProfileScopeId
          type: 'Scope'
        }
      ]
    }
  ]
  web: {
    redirectUris: [
      redirectUri
    ]
    implicitGrantSettings: {
      enableAccessTokenIssuance: false
      enableIdTokenIssuance: true
    }
  }
  tags: [
    'entra-passkey-onboarding-tap'
  ]
}

resource portalServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: portalApplication.appId
  appRoleAssignmentRequired: false
  tags: [
    'entra-passkey-onboarding-tap'
  ]
}

resource microsoftGraph 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: graphApplicationId
}

resource portalConsent 'Microsoft.Graph/oauth2PermissionGrants@v1.0' = {
  clientId: portalServicePrincipal.id
  consentType: 'AllPrincipals'
  resourceId: microsoftGraph.id
  scope: 'openid profile'
}

module observability 'modules/observability.bicep' = {
  name: 'observability'
  scope: rg
  params: {
    location: location
    workspaceName: workspaceName
    appInsightsName: appInsightsName
    tags: tags
  }
}

module transitionWorker 'modules/transition-worker.bicep' = {
  name: 'transitionWorker'
  scope: rg
  params: {
    location: location
    functionPlanName: functionPlanName
    functionAppName: functionAppName
    storageAccountName: storageAccountName
    queueName: queueName
    onboardingGroupId: resolvedOnboardingGroupId
    completionGroupId: resolvedCompletionGroupId
    appInsightsConnectionString: observability.outputs.connectionString
    packageUrl: workerPackageUrl
    tags: tags
  }
}

module appService 'modules/app-service.bicep' = {
  name: 'appService'
  scope: rg
  params: {
    location: location
    planName: planName
    webAppName: webAppName
    skuName: appServiceSku
    operatingSystem: operatingSystem
    tenantId: tenantId
    clientId: portalApplication.appId
    onboardingGroupId: resolvedOnboardingGroupId
    completionGroupId: resolvedCompletionGroupId
    completionQueueUri: transitionWorker.outputs.queueUri
    appInsightsConnectionString: observability.outputs.connectionString
    packageUrl: webPackageUrl
    tags: tags
  }
}

resource passkeyRead 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphUserAuthMethodPasskeyReadAllRoleId
  principalId: appService.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
}

resource tapReadWrite 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphUserAuthMethodTapReadWriteAllRoleId
  principalId: appService.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
  dependsOn: [
    passkeyRead
  ]
}

resource groupMemberRead 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphGroupMemberReadAllRoleId
  principalId: appService.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
  dependsOn: [
    tapReadWrite
  ]
}

resource userRead 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphUserReadAllRoleId
  principalId: appService.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
  dependsOn: [
    groupMemberRead
  ]
}

resource roleManagementRead 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphRoleManagementReadDirectoryRoleId
  principalId: appService.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
  dependsOn: [
    userRead
  ]
}

resource privilegedGroupEligibilityRead 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphPrivilegedGroupEligibilityReadRoleId
  principalId: appService.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
  dependsOn: [
    roleManagementRead
  ]
}

module webQueueSender 'modules/queue-sender-role.bicep' = {
  name: 'webQueueSender'
  scope: rg
  params: {
    storageAccountName: transitionWorker.outputs.storageAccountName
    webAppName: appService.outputs.webAppName
    webPrincipalId: appService.outputs.principalId
  }
}

resource workerPasskeyRead 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphUserAuthMethodPasskeyReadAllRoleId
  principalId: transitionWorker.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
}

resource workerGroupMemberReadWrite 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphGroupMemberReadWriteAllRoleId
  principalId: transitionWorker.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
  dependsOn: [
    workerPasskeyRead
  ]
}

resource workerUserRead 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphUserReadAllRoleId
  principalId: transitionWorker.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
  dependsOn: [
    workerGroupMemberReadWrite
  ]
}

resource workerRoleManagementRead 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphRoleManagementReadDirectoryRoleId
  principalId: transitionWorker.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
  dependsOn: [
    workerUserRead
  ]
}

resource workerPrivilegedGroupEligibilityRead 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: graphPrivilegedGroupEligibilityReadRoleId
  principalId: transitionWorker.outputs.principalId
  resourceId: microsoftGraph.id
  resourceDisplayName: 'Microsoft Graph'
  dependsOn: [
    workerRoleManagementRead
  ]
}

output resourceGroupName string = rg.name
output webAppName string = appService.outputs.webAppName
output webUrl string = appService.outputs.url
output portalApplicationClientId string = portalApplication.appId
output onboardingGroupId string = resolvedOnboardingGroupId
output completionGroupId string = resolvedCompletionGroupId
output managedIdentityPrincipalId string = appService.outputs.principalId
output transitionFunctionName string = transitionWorker.outputs.functionAppName
output transitionIdentityPrincipalId string = transitionWorker.outputs.principalId
