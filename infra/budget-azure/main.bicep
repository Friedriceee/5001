targetScope = 'resourceGroup'

@description('Temporary single-host SWE5001 validation environment; not the managed-services proposal.')
@minLength(3)
@maxLength(20)
param prefix string = 'jikeda5001-demo'
param location string = 'eastus2'
@allowed(['Standard_D2als_v7', 'Standard_B2als_v2', 'Standard_B2as_v2'])
param vmSize string = 'Standard_D2als_v7'
param adminUsername string = 'jikeda'
param ubuntuImageVersion string = 'latest'
@description('SSH public key only. Keep the private key outside source control.')
param sshPublicKey string
@description('Enable only after password rotation and private smoke checks. SSH and database ports stay closed.')
param publicWeb bool = false

var commonTags = {
  course: 'SWE5001'
  purpose: 'temporary-validation'
  owner: 'member1'
  budgetCny: '100'
  budgetIsAutomaticCap: 'false'
}

resource nsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: '${prefix}-nsg'
  location: location
  tags: commonTags
  properties: {
    // Use Azure Run Command for bootstrap. No administrator IP lookup or public SSH.
    securityRules: publicWeb ? [
      {
        name: 'public-https'
        properties: {
          priority: 200
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'Internet'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '443'
        }
      }
      {
        name: 'http-redirect-and-acme'
        properties: {
          priority: 210
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'Internet'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '80'
        }
      }
    ] : []
  }
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${prefix}-vnet'
  location: location
  tags: commonTags
  properties: {
    addressSpace: { addressPrefixes: ['10.42.0.0/16'] }
    subnets: [{
      name: 'demo'
      properties: {
        addressPrefix: '10.42.1.0/24'
        networkSecurityGroup: { id: nsg.id }
      }
    }]
  }
}

resource publicIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: '${prefix}-ip'
  location: location
  tags: commonTags
  sku: { name: 'Standard', tier: 'Regional' }
  properties: {
    publicIPAllocationMethod: 'Static'
    dnsSettings: { domainNameLabel: '${prefix}-${uniqueString(resourceGroup().id)}' }
  }
}

resource nic 'Microsoft.Network/networkInterfaces@2024-05-01' = {
  name: '${prefix}-nic'
  location: location
  tags: commonTags
  properties: {
    ipConfigurations: [{
      name: 'primary'
      properties: {
        privateIPAllocationMethod: 'Dynamic'
        subnet: { id: '${network.id}/subnets/demo' }
        publicIPAddress: { id: publicIp.id }
      }
    }]
  }
}

resource vm 'Microsoft.Compute/virtualMachines@2024-07-01' = {
  name: '${prefix}-vm'
  location: location
  tags: commonTags
  properties: {
    hardwareProfile: { vmSize: vmSize }
    storageProfile: {
      diskControllerType: vmSize == 'Standard_D2als_v7' ? 'NVMe' : 'SCSI'
      imageReference: {
        publisher: 'Canonical'
        offer: 'ubuntu-24_04-lts'
        sku: 'server'
        version: ubuntuImageVersion
      }
      osDisk: {
        name: '${prefix}-os'
        createOption: 'FromImage'
        diskSizeGB: 32
        managedDisk: { storageAccountType: 'StandardSSD_LRS' }
        deleteOption: 'Delete'
      }
    }
    osProfile: {
      computerName: '${prefix}-vm'
      adminUsername: adminUsername
      customData: base64(loadTextContent('cloud-init.yml'))
      linuxConfiguration: {
        disablePasswordAuthentication: true
        provisionVMAgent: true
        ssh: {
          publicKeys: [{
            path: '/home/${adminUsername}/.ssh/authorized_keys'
            keyData: sshPublicKey
          }]
        }
      }
    }
    networkProfile: {
      networkInterfaces: [{ id: nic.id, properties: { deleteOption: 'Delete' } }]
    }
    diagnosticsProfile: { bootDiagnostics: { enabled: true } }
  }
}

output vmName string = vm.name
output publicAddress string = publicIp.properties.ipAddress
output publicHostname string = publicIp.properties.dnsSettings.fqdn
output vmResourceId string = vm.id
