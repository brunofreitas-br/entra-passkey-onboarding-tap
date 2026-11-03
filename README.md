# Microsoft Entra Passkey Onboarding with Temporary Access Pass

Reference application that helps an eligible Microsoft Entra ID user request a short-lived, single-use Temporary Access Pass (TAP), register a Passkey, and complete onboarding.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fbrunofreitas-br%2Fentra-passkey-onboarding-tap%2Faaa6f7bff204b9d4f69b594195d1036aa4f814f3%2Fazuredeploy.json)

The default deployment target is **Azure App Service on Linux**. The deployment script creates the Azure resources, Microsoft Entra application, security groups when needed, managed identity permissions, and publishes the .NET application without Docker or client secrets.

The **Deploy to Azure** button performs the complete installation using versioned application packages from the project's GitHub Release. No local tooling is required. The signed-in administrator still needs sufficient Azure and Microsoft Entra permissions.

GitHub immutable releases are enforced for this repository. Release tags and assets can't be replaced after publication, and GitHub generates a release attestation for provenance.

## What the user experiences

1. Sign in with a recent password-authenticated session.
2. Request a single-use TAP that expires after 10 minutes.
3. Open Security info in a new browser tab and register a Passkey.
4. Return to the application and confirm registration.
5. The application adds the user to the completion group before removing them from the onboarding group.

## Architecture

```text
Microsoft Entra ID
├── App registration (openid + profile)
├── Onboarding security group
├── Completion security group
└── Microsoft Graph permissions for the Web App managed identity

Azure
├── App Service Plan
├── Linux Web App (.NET 10)
│   └── System-assigned managed identity
├── Storage Queue
├── Azure Function transition worker
│   └── Separate system-assigned managed identity
├── Application Insights
└── Log Analytics workspace
```

No client secret, certificate, database, TAP value, or user-supplied object ID is stored by the application. The internet-facing Web App has `GroupMember.Read.All`, not group-write permission. Only the queue-triggered Function receives `GroupMember.ReadWrite.All`; it has no HTTP trigger and revalidates eligibility, PIM assignments, and Passkey registration before every transition.

## Prerequisites

- Azure subscription
- Microsoft Entra tenant
- Azure CLI
- Bicep CLI
- PowerShell 7
- .NET 10 SDK
- Azure `Owner` or equivalent permissions for resource deployment
- Microsoft Entra permissions that allow creation of applications, groups, consent grants, and Microsoft Graph app-role assignments

The deployment identity is highly privileged. Use a dedicated administrative workstation and review `infra/main.bicep` before deployment.

## One-command deployment

### Deploy to Azure

Select the button at the top of this README, review the parameters, and deploy. The default package URLs are pinned to release `v1.0.0`.

### PowerShell

```powershell
pwsh ./scripts/deploy.ps1 `
  -TenantId "00000000-0000-0000-0000-000000000000" `
  -SubscriptionId "00000000-0000-0000-0000-000000000000"
```

By default, the template:

- deploys to `eastus2`;
- uses an App Service `B1` Linux plan;
- creates the onboarding and completion groups;
- creates a single-tenant app registration;
- grants tenant-wide consent only for `openid profile`;
- grants the Web App managed identity the minimum reviewed Microsoft Graph application roles;
- isolates `GroupMember.ReadWrite.All` in a queue-triggered Function with no HTTP trigger;
- publishes the application using ZIP Deploy;
- verifies `/health/ready`.

To reuse existing Assigned security groups:

```powershell
pwsh ./scripts/deploy.ps1 `
  -TenantId "<tenant-id>" `
  -SubscriptionId "<subscription-id>" `
  -OnboardingGroupId "<group-object-id>" `
  -CompletionGroupId "<group-object-id>"
```

Use `-OperatingSystem Windows` when a subscription doesn't have Linux App Service quota. Linux remains the default public deployment.

## Tenant configuration after deployment

1. Enable Temporary Access Pass for the onboarding group.
   - minimum lifetime: 10 minutes;
   - one-time use allowed.
2. Enable Passkey (FIDO2) self-service setup for the intended users.
3. Expose the My Sign-ins first-party application in the tenant, as described below.
4. Configure the Conditional Access and Terms of Use policies for the onboarding and completion groups.
5. Add the deployed portal and My Sign-ins as exclusions only in the onboarding-specific Conditional Access policy.
6. Add only approved, nonprivileged users to the onboarding group.
7. Test with a dedicated nonprivileged account before broader rollout.

### Expose My Sign-ins in Conditional Access

Conditional Access can target a Microsoft first-party cloud application only after its service principal exists in the tenant. The My Sign-ins application uses this Microsoft-owned application ID:

```text
19db86c3-b2b9-44cc-b339-36da233a3be2
```

Use **Azure Cloud Shell in PowerShell mode**:

1. Open [Azure Cloud Shell](https://shell.azure.com) and select **PowerShell**.
2. Run the following idempotent script:

```powershell
$mySignInsAppId = "19db86c3-b2b9-44cc-b339-36da233a3be2"

if (-not (Get-Module Microsoft.Graph.Applications -ListAvailable)) {
    Install-Module Microsoft.Graph.Applications -Scope CurrentUser -Force
}

Import-Module Microsoft.Graph.Applications
Connect-MgGraph -Scopes "Application.ReadWrite.All" -NoWelcome

$servicePrincipal = Get-MgServicePrincipal `
    -Filter "appId eq '$mySignInsAppId'" `
    -Property "id,appId,displayName"

if (-not $servicePrincipal) {
    $servicePrincipal = New-MgServicePrincipal -AppId $mySignInsAppId
    Write-Host "Created My Sign-ins service principal."
}
else {
    Write-Host "My Sign-ins service principal already exists."
}

$servicePrincipal |
    Select-Object DisplayName, Id, AppId |
    Format-Table

Disconnect-MgGraph
```

Expected application ID:

```text
19db86c3-b2b9-44cc-b339-36da233a3be2
```

The administrator running the script must be allowed to create enterprise applications and consent to the delegated `Application.ReadWrite.All` permission requested by Microsoft Graph PowerShell.

After the service principal exists:

1. Open **Microsoft Entra admin center**.
2. Browse to **Protection → Conditional Access → Policies**.
3. Open the policy used for the Passkey onboarding group.
4. Under **Target resources → Resources**, select **Exclude → Select resources**.
5. Add:
   - the deployed Passkey onboarding portal;
   - **My Signins**.
6. Start with **Report-only**, validate the sign-in logs, and then enable the policy.

> [!CAUTION]
> Prefer an onboarding-specific Conditional Access policy scoped to the onboarding group. Don't exclude My Sign-ins from a broad all-users policy unless the wider bypass has been explicitly reviewed and accepted. Other My Sign-ins features can depend on additional Microsoft first-party applications; use sign-in logs to identify only the resources required by your scenario rather than creating broad Microsoft application exclusions.

References:

- [Microsoft Graph PowerShell: New-MgServicePrincipal](https://learn.microsoft.com/powershell/module/microsoft.graph.applications/new-mgserviceprincipal)
- [Microsoft Entra Conditional Access target resources](https://learn.microsoft.com/entra/identity/conditional-access/concept-conditional-access-cloud-apps)
- [Background: My Sign-ins portal applications](https://office365itpros.com/2025/10/15/my-sign-ins-portal/)

## Security model

This reference implementation authorizes TAP issuance using:

- a fresh password-authenticated Microsoft Entra session;
- immutable `oid` and tenant claims;
- membership in one fixed onboarding group;
- exclusion of guests, service accounts, break-glass identities, active role holders, PIM-eligible identities, and users privileged through role-assignable groups;
- antiforgery protection, one-use action nonces, rate limiting, and secret-free auditing.

Password-only bootstrap remains a high-impact design decision. Review and formally accept that risk before production use.

The default B1 deployment uses one App Service instance and in-memory nonce/rate-limit state. Before scaling to multiple instances, move that state to a distributed store and separate the reconciler from the web process.

## Local validation

```powershell
pwsh ./scripts/test-local.ps1
```

Or:

```bash
dotnet test TapPasskeyOnboarding.slnx -c Release
```

## Public repository guidance

- Never commit tenant IDs, group IDs, deployment outputs, customer branding, or exported TAP values.
- Keep environment-specific parameters in `infra/parameters.local.json`, which is ignored by Git.
- Use GitHub Security Advisories for vulnerability reports.
- Review Graph permissions and Bicep changes before every release.

## License

Licensed under the [MIT License](LICENSE).
