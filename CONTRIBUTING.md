# Contributing

1. Create a focused branch.
2. Do not commit tenant-specific values or secrets.
3. Run `dotnet test TapPasskeyOnboarding.slnx -c Release`.
4. Run `az bicep build --file infra/main.bicep`.
5. Document security-impacting changes.
6. Submit a pull request with the behavior, validation, and deployment impact.

Changes to authentication, TAP issuance, Microsoft Graph permissions, group transitions, or logging require security review.
