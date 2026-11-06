# Running LiteDM

This folder contains the self-contained Windows x64 desktop app, the browser extension, setup scripts, and study notes. Keep the `LiteDM` folder together.

Run `LiteDM\LiteDM.App.exe`. Paste a direct HTTP/HTTPS file link, choose **Add download**, review the filename and destination, and add it to the queue. Settings controls concurrency and appearance. Closing the window keeps downloads running; use the tray menu's **Exit** command to stop the app.

For browser integration:

1. Open `edge://extensions` or `chrome://extensions`, enable Developer mode, and load the `extension` folder unpacked.
2. Copy the extension's ID.
3. Open PowerShell in this folder and run the matching command:

```powershell
./setup/register-browser.ps1 -Browser Edge -PublishDirectory ./LiteDM -EdgeExtensionId YOUR_EXTENSION_ID
./setup/register-browser.ps1 -Browser Chrome -PublishDirectory ./LiteDM -ChromeExtensionId YOUR_EXTENSION_ID
```

Right-click a direct file link and select **Download with LiteDM**. The app asks you to review it before downloading.

To remove the browser host registration:

```powershell
./setup/unregister-browser.ps1 -Browser Both -PublishDirectory ./LiteDM
```

Remove registration before moving the folder. After moving it, register again with the new location and current extension IDs.

`STUDY-NOTES.md` explains the repository's architecture, concurrency, HTTP correctness, crash recovery, IPC, UI threading, and C# 14 examples. Its source paths refer to the development repository.
