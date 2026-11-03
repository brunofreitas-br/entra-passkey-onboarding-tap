using './main.bicep'

param namePrefix = 'passkey-onboarding'
param environmentName = 'dev'
param location = 'eastus2'
param appServiceSku = 'B1'
param operatingSystem = 'Linux'
param tenantId = '00000000-0000-0000-0000-000000000000'
param webPackageUrl = 'https://github.com/brunofreitas-br/entra-passkey-onboarding-tap/releases/download/v1.1.0/web.zip'
param workerPackageUrl = 'https://github.com/brunofreitas-br/entra-passkey-onboarding-tap/releases/download/v1.1.0/worker.zip'

// Leave empty to create the groups automatically.
param onboardingGroupId = ''
param completionGroupId = ''
