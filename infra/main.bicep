// LabRoster on Azure: App Service (API + Angular app), Azure SQL with Entra-only
// authentication, and Application Insights. The web app reaches SQL with its managed
// identity, so the deployment creates no passwords or connection secrets.

targetScope = 'resourceGroup'

@allowed(['qa', 'production'])
param environmentName string

param location string = resourceGroup().location

@description('Object id of the Entra group that administers the SQL server. The deploy pipeline identity is a member.')
param sqlAdminGroupObjectId string

@description('Display name of that Entra group.')
param sqlAdminGroupName string

@description('Application ID URI or client id that access tokens for this API are issued to.')
param apiAudience string

var suffix = uniqueString(resourceGroup().id, environmentName)
var isProduction = environmentName == 'production'
var tags = {
  app: 'labroster'
  environment: environmentName
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-labroster-${environmentName}-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: isProduction ? 90 : 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-labroster-${environmentName}-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: 'sql-labroster-${environmentName}-${suffix}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      principalType: 'Group'
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: subscription().tenantId
    }
  }
}

// Lets Azure services (App Service, the deploy runner's Azure login) connect. Tighten to a
// private endpoint for production networks that require it.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: 'labroster'
  location: location
  tags: tags
  sku: isProduction
    ? { name: 'GP_Gen5_2', tier: 'GeneralPurpose' }
    : { name: 'GP_S_Gen5_1', tier: 'GeneralPurpose' }
  properties: {
    autoPauseDelay: isProduction ? -1 : 60
    minCapacity: isProduction ? null : json('0.5')
    zoneRedundant: false
    requestedBackupStorageRedundancy: isProduction ? 'Geo' : 'Local'
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-labroster-${environmentName}-${suffix}'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: isProduction ? 'P1v3' : 'B1' }
  properties: { reserved: true }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'app-labroster-${environmentName}-${suffix}'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: isProduction
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health/ready'
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'Auth__Entra__TenantId', value: 'organizations' }
        { name: 'Auth__Entra__Audience', value: apiAudience }
        { name: 'Auth__DevTokens__Enabled', value: 'false' }
      ]
      connectionStrings: [
        {
          name: 'LabRoster'
          type: 'SQLAzure'
          connectionString: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Managed Identity;Encrypt=True;Connect Timeout=30'
        }
      ]
    }
  }
}

resource webDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: webApp
  properties: {
    workspaceId: logs.id
    logs: [
      { category: 'AppServiceHTTPLogs', enabled: true }
      { category: 'AppServiceConsoleLogs', enabled: true }
      { category: 'AppServiceAppLogs', enabled: true }
    ]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource databaseDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: database
  properties: {
    workspaceId: logs.id
    logs: [
      { category: 'Errors', enabled: true }
      { category: 'Deadlocks', enabled: true }
      { category: 'Timeouts', enabled: true }
    ]
    metrics: [{ category: 'Basic', enabled: true }]
  }
}

output webAppName string = webApp.name
output webAppHostName string = webApp.properties.defaultHostName
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = database.name
output webAppPrincipalId string = webApp.identity.principalId
