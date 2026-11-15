# CommuteCast for Android

The native Android listener opens from its launcher icon and reads MP3s directly from OneDrive over Microsoft Graph. Use the **Ready to Listen** folder from the Windows remote queue or another folder with your finished MP3s. Once an MP3 has finished syncing, the phone can play it with the Windows PC off. The phone needs an internet connection to stream audio from OneDrive.

## Build and install

Open the `src/CommuteCast.Android` project in Android Studio, or run the Gradle wrapper with JDK 17 or newer and Android SDK 36.

```powershell
Set-Location src/CommuteCast.Android
.\gradlew.bat assembleDebug testDebugUnitTest lintDebug
adb install -r .\build\outputs\apk\debug\CommuteCast.Android-debug.apk
```

The debug APK is suitable for validating an installation with your phone. Keep the same signing certificate across APKs that update an installation. Register the certificate used to sign the APK for the phone.

## Connect your OneDrive folder

1. Open CommuteCast and tap **Settings**. Copy the Android package name (`dev.kimber.commutecast.listener`) and **Signature hash** shown there.
2. Register an app in Microsoft Entra for the target account type. Under **Authentication**, add the **Android** platform and enter the package name and signature hash. Microsoft generates a redirect URI that returns the sign-in result to the phone. Under **API permissions**, add the Microsoft Graph **delegated** permission `Files.Read`. For a work account, use the organization's rules for registration and consent. [Microsoft Android registration](https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-redirect-uri).
3. In CommuteCast, enter the **Application (client) ID** from the Android registration. For a single work tenant, add its **Directory (tenant) ID**. Leave the directory field blank when the registration supports both work and personal Microsoft accounts.
4. Enter the folder's path relative to your OneDrive root, for example `CommuteCast/Ready to Listen`. For a shared library or a folder already identified in the web studio, enter the **Drive ID** and **Folder item ID** instead. If the folder is shared with your work account and the read scope is insufficient, add the delegated `Files.Read.All` permission and select **Read files shared with this account**. This scope covers files that account can access, including shared files. [Microsoft OneDrive permissions](https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/permissions_reference).
5. Tap **Save**, then **Connect OneDrive**. Sign in as the account with access to the MP3s.

The Android app uses Microsoft sign-in and delegated read access. A CommuteCast web server, DNS entry, anonymous sharing link, and publishing secret are unnecessary for this workflow.

## Listen on the commute

Tap the CommuteCast icon and use **Resume** at the top for the most recently played unfinished episode. The player keeps each episode's exact position and rewinds 20 seconds on resume, stopping at the beginning if less than 20 seconds have played. The **In progress** and **Unplayed** sections are visible by default. Enable **Show finished** to replay a completed MP3. Bookmarks live on the phone under the current account and drive item.

Android Media3 provides playback when the app is in the background, including media controls on the lock screen and Bluetooth play and pause commands. When returning to the library or after a new MP3 finishes syncing, use **Refresh** to see the latest folder contents. [Android background playback](https://developer.android.com/media/media3/session/background-playback).

For the live commute check, sign in on the target phone and play a synced MP3 over mobile data. Lock the screen, pause with the car's Bluetooth control, then reopen CommuteCast by tapping its icon. Check the saved time and listen for the 20-second rewind. Switch between two episodes to verify each bookmark, and let one play through the end to verify the finished filter.

The debug APK builds and passes Android lint with zero errors. Six JVM tests cover folder paths and IDs, paginated MP3 filtering and titles, playback URL validation, and the 20-second rewind. Live OneDrive sign-in and the phone and car checks require the registered app and target devices.
