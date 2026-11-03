param storageAccountName string
param webAppName string
param webPrincipalId string

var storageQueueSenderRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a'
)

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource sender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, webAppName, storageQueueSenderRoleId)
  scope: storage
  properties: {
    roleDefinitionId: storageQueueSenderRoleId
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
  }
}
