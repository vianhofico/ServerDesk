# Install ServerDesk on Windows

**English** | [Tiếng Việt](INSTALL.vi.md)

ServerDesk ships in two Windows x64 formats.

## Recommended — Windows installer

Download `ServerDesk-v1.0.3-win-x64-setup.exe` from the GitHub Release and run it once.

The installer is per-user and does not require administrator elevation for the normal installation path. By default it:

- installs ServerDesk under `%LOCALAPPDATA%\Programs\ServerDesk`;
- creates a **ServerDesk** shortcut on the current user's Desktop;
- creates a **ServerDesk** shortcut in the Start Menu;
- uses the official ServerDesk icon for the app, setup program and shortcuts;
- registers ServerDesk under Windows **Settings → Apps → Installed apps**, with an uninstaller.

After installation, launch ServerDesk from the Desktop or Start Menu like any other Windows desktop application. You do not need to browse to the installation folder each time.

To uninstall, use **Settings → Apps → Installed apps → ServerDesk → Uninstall**, or run the uninstaller from the installed ServerDesk directory.

> Current public builds are not Authenticode/code-signed, so Windows SmartScreen may show an unknown-publisher warning. Verify the release checksum before running the installer when required by your environment.

## Portable ZIP

`ServerDesk-v1.0.3-win-x64.zip` remains available for users who intentionally want a portable copy.

1. Extract the ZIP to a folder.
2. Run `ServerDesk.App.exe` from that folder.

The portable package does **not** install the app, create Desktop/Start Menu shortcuts, or register an uninstaller automatically.

## Verify SHA-256

Download `SHA256SUMS.txt` from the same GitHub Release. In PowerShell:

```powershell
Get-FileHash .\ServerDesk-v1.0.3-win-x64-setup.exe -Algorithm SHA256
Get-FileHash .\ServerDesk-v1.0.3-win-x64.zip -Algorithm SHA256
```

Compare the output with the corresponding entries in `SHA256SUMS.txt`.
