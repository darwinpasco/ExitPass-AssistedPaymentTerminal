# ExitPass APT Governed Windows Device Enrollment Runbook v1.0

## Purpose

This runbook governs credential rotation for an existing Windows Assisted Payment Terminal (APT). It uses the Central PMS Device Administration lifecycle and preserves the registered device ID, service identity, terminal, Site, and Site Group. It does not register, replace, revoke, or reassign a device.

The reviewed utility is:

`tools/AssistedPaymentTerminal.DeviceAdministration`

The utility accepts only `ROTATION` provisioning packages. It requires HTTPS with normal Windows server-certificate validation, generates a non-exportable CurrentUser client certificate, enrolls that certificate through Central PMS, and persists schema-v1 DPAPI CurrentUser trust state only after exact response validation.

## Security boundary

- Run the utility as the same Windows user that runs the APT desktop.
- Store the one-time provisioning package in an operator-protected local directory. Never place the provisioning credential on the command line, in a transcript, or in Git.
- Do not copy UAT trust state, certificates, or private keys into Demo.
- Do not use HTTP, a certificate-validation bypass, or header-only identity.
- Do not continue after an uncertain enrollment result. Reconcile Device Administration state before another action.
- Do not delete the previous certificate during Demo rotation. UAT may still depend on that credential until its own governed rotation is separately authorized.

## Preconditions

1. Confirm Central PMS, the target Site, and Device Administration are healthy.
2. Confirm the exact existing device is the intended target and remains assigned to the expected service identity, terminal, Site, and Site Group.
3. Confirm there is no active cashier session, shift/cash custody operation, pending cash command, payment finalization, or fiscal issuance on the target environment.
4. Confirm the physical BIOS serial with:

   ```powershell
   (Get-CimInstance Win32_BIOS).SerialNumber.Trim()
   ```

5. Confirm the Windows CurrentUser trusts the approved Central PMS server chain and that the HTTPS certificate covers the requested host.
6. Record the active credential version and registered SHA-256 certificate fingerprint through authorized Device Administration readback.
7. Back up the current APT trust metadata and local APT state through the approved environment backup procedure. Do not expose DPAPI plaintext or private key material.

## Issue the rotation package

An authorized System Administrator uses the existing Management Platform Device Administration action or its approved authenticated endpoint:

`POST /v1/management-platform/devices/{deviceId}/rotate`

The request must include the approved rotation reason, management authentication, antiforgery proof, and correlation ID. Save the returned `DeviceProvisioningCredentialDto` JSON value directly as the protected package file. Do not transform field names or copy the credential into another command.

Issuing rotation moves the existing device into the Central PMS credential-rotation lifecycle and interrupts its previous runtime authority. Do not issue a package until the physical terminal and operator are ready to finish enrollment before expiry.

## Demo rotation command

Build or publish the reviewed tool from the merged commit. Run it under the APT Windows user with exact values read from Device Administration. For the approved Demo device, the stable identifiers are shown below; the credential version and prior fingerprint must be read again immediately before execution.

```powershell
dotnet run --project .\tools\AssistedPaymentTerminal.DeviceAdministration\AssistedPaymentTerminal.DeviceAdministration.csproj --configuration Release -- `
  enroll-rotation `
  --central-pms-base-url "https://localhost:57264" `
  --package "D:\Protected\ExitPass\Demo\rotation-package.json" `
  --state "D:\SourceCodes\ExitPass.local\demo-rc1-20261112\apt\DeviceTrust\state.bin" `
  --result "D:\Protected\ExitPass\Demo\enrollment-result.json" `
  --unreadable-state-backup "D:\Protected\ExitPass\Demo\state.bin.pre-rotation-corrupt" `
  --expected-device-id "e84cf0bf-e100-4beb-9d42-787b449bf0cc" `
  --expected-service-identity-id "337609f0-efe7-46a0-a7e9-3fe59feca401" `
  --expected-site-id "2d1dcdf8-f563-537c-8542-0bde7cc9da97" `
  --expected-site-group-id "a6dbadf6-68b5-5bed-a7e0-a75faee70841" `
  --expected-terminal-id "PITX-APT-01" `
  --physical-terminal-serial "ACE4_2E00_653A_D952" `
  --expected-credential-version "<CURRENT_VERSION_PLUS_ONE>" `
  --prior-certificate-sha256 "<CURRENT_REGISTERED_SHA256>"
```

If the package includes a terminal serial, it must match the explicit physical serial. The utility always sends the verified physical serial because the APT enrollment contract requires it.

## Post-enrollment validation

Inspect the local trust state and certificate without disclosing protected content:

```powershell
dotnet run --project .\tools\AssistedPaymentTerminal.DeviceAdministration\AssistedPaymentTerminal.DeviceAdministration.csproj --configuration Release -- `
  inspect `
  --state "D:\SourceCodes\ExitPass.local\demo-rc1-20261112\apt\DeviceTrust\state.bin" `
  --result "D:\Protected\ExitPass\Demo\trust-inspection.json"
```

Prove Demo accepts the enrolled certificate and service identity:

```powershell
dotnet run --project .\tools\AssistedPaymentTerminal.DeviceAdministration\AssistedPaymentTerminal.DeviceAdministration.csproj --configuration Release -- `
  probe `
  --central-pms-base-url "https://localhost:57264" `
  --state "D:\SourceCodes\ExitPass.local\demo-rc1-20261112\apt\DeviceTrust\state.bin" `
  --result "D:\Protected\ExitPass\Demo\demo-authority-probe.json"
```

Prove the new Demo credential is rejected by UAT before changing the Demo launcher:

```powershell
dotnet run --project .\tools\AssistedPaymentTerminal.DeviceAdministration\AssistedPaymentTerminal.DeviceAdministration.csproj --configuration Release -- `
  probe-isolation `
  --central-pms-base-url "https://localhost:57164" `
  --state "D:\SourceCodes\ExitPass.local\demo-rc1-20261112\apt\DeviceTrust\state.bin" `
  --result "D:\Protected\ExitPass\Demo\uat-isolation-probe.json"
```

`probe` succeeds only when the intended authority accepts mTLS and returns the canonical not-found result for a random human-session ID. `probe-isolation` succeeds only when the other authority returns `401` or `403`. TLS errors, redirects, or ambiguous responses fail closed.

After both probes pass, separately deploy the reviewed APT build and point only the Demo launcher at the verified Demo state. Launcher changes and live credential rotation require their own operational authorization.

## Failure and recovery limits

- Before Central PMS accepts enrollment, the utility removes only the newly generated certificate and leaves the package and prior trust state intact.
- After Central PMS returns success, any response mismatch or local state-write failure is treated as a potentially committed enrollment. The new certificate and package are preserved for reconciliation. Do not replay the package.
- A consumed or expired one-time token cannot be recovered locally. Obtain authoritative Device Administration status before any new rotation.
- Restoring an old DPAPI state file does not restore an old Central PMS credential. Central PMS credential status remains authoritative.
- The utility never deletes the previous certificate. Removal requires separate proof that no environment relies on it and separate authorization.
- Cancelling an outstanding provisioning operation or issuing another rotation changes device lifecycle state and is not an automatic rollback.
- If DPAPI state is unreadable, an explicit nonexisting backup path is required. The original encrypted bytes and SHA-256 are preserved before enrollment proceeds.

## Audit evidence

Retain the safe result files, package-consumption status, Device Administration correlation IDs, credential version, certificate thumbprint/fingerprint, pre-rotation evidence hashes, and both authority-probe results. Never retain or publish the provisioning credential, private key, cashier password, or decrypted DPAPI payload.
