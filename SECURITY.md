# Security Policy

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability.

Use the repository's private GitHub Security Advisory reporting channel and include:

- affected version or commit;
- reproduction steps;
- expected and observed behavior;
- potential impact;
- suggested mitigation, if available.

Do not include real Temporary Access Pass values, access tokens, tenant data, user identifiers, or customer information.

## Supported versions

Security fixes are applied to the latest published release.

## Deployment responsibility

This project can create privileged Microsoft Graph assignments. Deployers must review the infrastructure code, use least privilege, protect administrative accounts, and validate all Conditional Access and authentication-method policies in a nonproduction tenant before rollout.
