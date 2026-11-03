using './main.bicep'

param namePrefix = 'passkey-onboarding'
param environmentName = 'dev'
param location = 'eastus2'
param appServiceSku = 'B1'
param operatingSystem = 'Linux'
param tenantId = '00000000-0000-0000-0000-000000000000'

// Leave empty to create the groups automatically.
param onboardingGroupId = ''
param completionGroupId = ''
