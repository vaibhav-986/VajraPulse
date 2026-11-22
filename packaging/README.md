# Optional Windows notification installation

The prepared package manifest is version **1.2.0.0**, matching Vajra Pulse 1.2.0.

The ordinary `Start-VajraPulse.cmd` app works without package registration. Windows notification reading is OFF by default and requires both package identity and Windows consent. The listener does not request permission during normal startup.

## Prepared package

`VajraPulse-unsigned.msix` contains the built application with `runFullTrust` and `userNotificationListener` capabilities. It is intentionally unsigned and has NOT been installed or trusted automatically.

For this laptop, the .NET 8 Desktop Runtime is already present. The package is x64 and requires Windows 10 build 19041 or later.

## Explicit local-development installation

1. Quit the ordinary app using its tray menu.
2. Read `Install-Package.ps1` before executing it.
3. If you approve its changes, run this command in this folder:

```powershell
.\Install-Package.ps1 -ApproveLocalDevelopmentCertificate
```

That flag explicitly approves these actions:

- Create a one-year local code-signing certificate in `CurrentUser\My`, if one does not already exist.
- Export only its PUBLIC certificate, then trust it in `CurrentUser\TrustedPeople` (not the Root store).
- Sign a copy of this local package and install it for the current Windows user.

The private key is not exported. No machine-wide certificate store, administrator elevation, startup registration, or security-policy bypass is used by the script. Windows can still block installation under an organization policy; do not bypass that policy.

4. Launch the installed **Vajra Pulse** entry from Start, not the ordinary `.cmd` launcher.
5. Open Settings, enable notification previews, and respond to Windows' consent dialog.
6. Choose individual apps, or explicitly turn on **Allow all apps**. Message content is hidden by default.

Only new notifications are previewed; existing notifications are not replayed. Previews remain in memory, are not uploaded or logged, and never dismiss notifications from Windows. Disable previews or remove permission in Windows Settings to stop reading them.

## Building again

```powershell
.\Build-Package.ps1
```

The build script downloads the official Microsoft Windows SDK BuildTools package into a new temporary directory, verifies MakeAppx's signature, and uses the copied Microsoft tools. It generates the unsigned package only; it neither creates certificates nor installs anything. Move an existing output package before rebuilding so it is not silently overwritten.

An installed package may need a higher manifest version for an update. Signing and trusting a local-development certificate is not a production distribution solution. Do not share private keys.

## Removal

Remove the app through Windows **Installed apps**. If you no longer need local package signing, remove the certificate with the displayed thumbprint from this user's **Personal** and **Trusted People** certificate stores. Other desktop-island features do not require the package.

## Platform references

- [Microsoft: notification listener](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)
- [Microsoft: package identity and certificate trust](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)
