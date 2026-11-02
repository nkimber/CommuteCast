# CommuteCast web application

## Accepted direction

Run an ASP.NET Core .NET 10 application on the Windows micro PC, reusing Core and Infrastructure. Provide a responsive narration editor, prompt builder, persistent queue, library and settings. Closing a browser must not stop generation. Only one process owns a workspace; the desktop must close before the web host opens that workspace.

Expose the management application at `commutecast.kimber.dev` through Cloudflare Tunnel and Access, restricted to the owner's verified identity. The application must verify Access JWTs at its boundary and protect state-changing browser requests against CSRF. Local setup uses loopback access. Do not expose audio, workspace directories, credentials or source-bearing diagnostics as static files.

The backend generates and validates MP3s, retains them locally, and publishes finished files to a configured OneDrive/SharePoint folder through Microsoft Graph. The destination has a **read-only Anyone link**, explicitly requested by the owner. M365 owns playback and downloads. CommuteCast's web application provides neither MP3 streaming nor MP3 download routes.

Generation and publishing have independent durable states. A publishing retry reuses the validated local MP3, never calls TTS again, and never overwrites an unrelated remote file. Publication captures its destination so later configuration edits do not silently redirect existing work. Cloud confirmation is required before showing Published.

## Graph transport checkpoint

`M365Publisher` uses application credentials and explicit drive/folder IDs. It can inspect a destination's existing read-only Anyone link, or explicitly create that link when requested during setup. Tenant and site policies still govern whether anonymous sharing is permitted. Public links do not provide write authorization.

Uploads use sequential Graph upload-session fragments and a deterministic job filename with conflict behavior `fail`. The authenticated Graph bearer token is omitted from preauthenticated upload requests. Local input is held against writers and SHA256 checked before upload. Both new and recovered existing remote files are read back by the backend and SHA256 checked before publication is acknowledged. No remote bytes pass through a browser endpoint. Provider response bodies, secrets and preauthenticated session URLs are excluded from user-facing failures.

Live Microsoft 365 upload and Anyone-link access require an actual tenant, application grant and destination. Synthetic transport tests establish protocol behavior only.

Validation: `dotnet test tests/CommuteCast.Tests -c Release --filter FullyQualifiedName~M365PublishingTests` passes 8 tests, including byte verification, lost-response recovery, remote conflict preservation, local tamper rejection, upload-host restrictions, token isolation and destination validation. Live tenant upload and public-link acceptance remain unexecuted.

## Run the web studio

On Windows with .NET 10 SDK and FFmpeg/FFprobe on PATH:

```powershell
.\scripts\Start-CommuteCast-Web.ps1
```

Open `http://127.0.0.1:5188`. Local setup authenticates only requests from loopback with a localhost/loopback Host header. Changing the listener does not enable remote access. Public hostnames and non-loopback clients are rejected until Cloudflare Access is configured. Local Kokoro/Piper require the existing approved Docker speech provisioning (`scripts/Provision-Speech.ps1 -Build`); hosted providers use API keys saved in Settings. Refresh voices to verify the selected provider/model before submitting. Hosted narration may be billed by that provider.

The default private workspace is `%LOCALAPPDATA%\CommuteCast-Web`; validated local exports are in its sibling `CommuteCast-Web-exports`. Neither directory is under the web root. The host owns the workspace lease until shutdown. Use `-Workspace <absolute-local-folder>` to select another workspace. Close the desktop before selecting its workspace. Do not run old desktop binaries against a migrated workspace. The dedicated web workspace is the recommended initial deployment.

The editor autosaves source, prompt work and voice settings together in `web/draft.json`, using an atomic write and revision checks: a stale browser cannot overwrite a newer draft silently. On first use it can import an existing desktop-format draft; later web edits remain in the web draft. Submission IDs prevent a repeated HTTP submission from creating duplicate jobs; reusing an accepted identity with different text or voice settings is rejected. Queue pause, accepted jobs, captured speech settings and M365 publication intent survive restart. Browser disconnection does not cancel accepted generation. Settings and publication records contain destination IDs, never API keys or the M365 application secret. Credentials live in Windows Credential Manager under the Windows account running the host.

Library deletion currently removes the local narration and its local audio; any published M365 copy remains in the shared folder. The UI explains this before deletion. Use M365 to manage published copies. Publishing retries retain the originally captured destination unless an explicit change is requested through the API. Generation without a configured destination remains visible as generated/unpublished and can be published after setup.

## Configure Microsoft 365

1. Create a dedicated OneDrive for Business or SharePoint library folder for the finished MP3s. The writer needs authenticated Graph access even though readers use an Anyone link.
2. Register a single-tenant application in Microsoft Entra ID. Record its tenant ID and application/client ID, and create an application secret. Prefer a narrowly selected site/folder permission grant with write access for this application rather than tenant-wide access. Selected permissions require both administrator consent and an explicit resource assignment. Confirm the selected grant supports the folder's upload and sharing operations in your tenant. Reference: [Selected permissions](https://learn.microsoft.com/en-us/graph/permissions-selected-overview).
3. Obtain the destination drive ID and folder item ID through Microsoft Graph or your administrator. These are item identities, not a browser sharing URL. In the web studio's Settings, save those IDs and the secret. Blank secret fields preserve the saved credential; responses never return it.
4. Choose **Check folder** to verify an existing unexpired read-only Anyone link, or **Enable Anyone link** to create one explicitly. This cannot override organization/site restrictions on anonymous sharing. Reference: [Sharing policy](https://learn.microsoft.com/en-us/sharepoint/turn-external-sharing-on-or-off).
5. Enable automatic publishing. Generate a short narration, wait for **Published**, then open the folder from a signed-out browser and verify the finished MP3 can be accessed with its link. This is live acceptance; synthetic tests cannot establish tenant permissions or external access.

The backend uploads in 3.125 MiB fragments, including files larger than the simple-upload limit. An interrupted session is recovered by checking the unique final filename and bytes before another upload; partial sessions are not treated as published. A remote conflict is preserved for inspection. Retries never authorize another TTS request. [Graph upload sessions](https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0).

## Cloudflare and kimber.dev

Configure these nonsecret environment values for the web host (double underscores map to ASP.NET configuration sections):

```text
Cloudflare__TeamDomain=YOUR-TEAM.cloudflareaccess.com
Cloudflare__Audience=YOUR-ACCESS-APPLICATION-AUD-TAG
Cloudflare__AllowedEmail=YOUR-EXACT-EMAIL-ADDRESS
```

Create a Cloudflare Access self-hosted application for `commutecast.kimber.dev`, protect the entire hostname, and allow only your exact email identity. Use your configured identity provider or email one-time PIN. Create a named Cloudflare Tunnel on the micro PC and route the hostname to `http://127.0.0.1:5188`. Configure Access before making the hostname reachable. The backend independently verifies the Access JWT's RS256 signature, issuer, audience, expiry and email; it never trusts the email identity header by itself. Without the Access configuration, a public-hostname request is rejected even if a local tunnel forwards it.

The free domain setup requires Cloudflare authoritative DNS for `kimber.dev`; its current nameservers were observed as `ns33.domaincontrol.com` and `ns34.domaincontrol.com` during planning. Preserve existing website/email records when migrating DNS. No DNS or tenant changes have been applied by this implementation. [Cloudflare setup](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/self-hosted-public-app/), [JWT verification](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/).

## Deploy on the micro PC

`scripts/Publish-CommuteCast-Web.ps1` creates a self-contained Windows x64 host under ignored `artifacts/web-release`; the micro PC does not need the SDK for that package. Run `CommuteCast.Web.exe` with the package directory as its working directory. Keep private data outside that directory. Configure FFmpeg/FFprobe and provision local speech separately, or select a hosted speech provider. Run the host as the same Windows account that saved its credentials. Keep the micro PC awake.

Configure the web host and `cloudflared` to restart automatically under the intended account. A Task Scheduler logon task is compatible with Docker Desktop's interactive-user lifecycle; a pre-logon service deployment requires separately verified speech availability and account/credential access. Back up the entire stopped web workspace (including per-job `m365-publication.json` and `web/settings.json`), rather than assuming the older desktop backup manifest includes the web sidecars. Windows credentials and M365 public copies are separate from that backup.

## Verification and remaining acceptance

The initial web integration suite passes 11 tests: rendered entry point and absent audio routes; rejected remote/forged-host requests; CSRF rejection; conflicting draft protection; real prompt/text preparation; idempotent submission; real FFmpeg generation after client disconnect; queue/publication recovery after host restart; and correctly signed, wrong-owner, wrong-audience and expired Access JWTs. Browser inspection at 1440×1100 and 390×844 confirms responsive editor layouts and functional text preview. Generated screenshots and preview state remain ignored.

Public DNS/Tunnel/Access configuration, micro-PC boot behavior, live local speech quality, live M365 upload, signed-out Anyone-link access and physical phone/work-laptop acceptance remain to be observed in the actual deployment. These are not established by the local build, synthetic speech or contract tests.

Checkpoint validation: the 12-project Release solution builds with zero warnings/errors; all 11 web integration tests and 55 focused existing-backend tests pass. The focused backend selection covers M365 publishing (now 10 cases including dot-segment IDs), narration prompts, preferences and logging. JavaScript syntax and both web PowerShell scripts parse successfully. The first web test run exposed early authentication-configuration capture; deferred options configuration fixes it and the entire web suite passes afterward. The first solution rebuild also encountered a running preview's DLL lock; the owned preview was stopped before the successful build. The full existing backend/desktop regression and packaged web-host smoke test remain subsequent verification.

Operational follow-up: provision a dedicated web workspace with `.\scripts\Provision-Speech.ps1 -Build -Workspace "$env:LOCALAPPDATA\CommuteCast-Web"`. The default remains the desktop workspace for existing callers. Settings also provides **Start / repair local speech**, using the existing verified-service repair path to recover a missing image pin or start installed owned services without downloading models. New installation still requires provisioning. Repair never restarts running services. Before explicitly provisioning a new image, pause generation and wait for active speech to finish.

The expanded web suite passes **15/15** cases, adding atomic voice-draft persistence, changed-settings duplicate rejection, M365 failure/retry with frozen destination and one speech call, and an authenticated Cloudflare browser obtaining/using CSRF tokens. Host shutdown is idempotent and disposes queue/lease resources even when the host stop token is cancelled. Named Microsoft 365 HTTP logging is disabled so preauthenticated upload URLs are not written by default HttpClient logging. Invalid Cloudflare configuration is checked before the host starts.

The full existing backend run reports **1,070 passed, 5 failed, 1 skipped** out of 1,076 in 6m22s. Four failures were Windows shell-link cleanup locks; the fifth was an empty-message assertion in the durable Piper queue test. A focused rerun of the Windows registration class and both durable queue variants passes **20/20**. No claim is made that the full run was clean or that every transient failure's root cause has been established. Desktop regression passes **55**, with **2** physical/native dictation acceptance tests skipped. A self-contained Windows x64 package was published and started successfully from its package directory against an isolated workspace. Browser prompt generation and stale-prompt copy disabling were exercised. Live deployment acceptance listed above remains pending.

Final runtime follow-up: **17/17** web tests and **12/12** hosted-provider tests pass. Concurrent shutdown callers now await the same complete cleanup operation; the regression test interrupts an active synthetic narration and reacquires the released workspace lease. Data Protection uses a stable `CommuteCast.Web` application name across source/package paths. Hosted voice-name caching supports concurrent readers/writers, and the web picker uses the provider-qualified name key. A live local-service repair attempt reached its bounded Docker startup timeout; no real local-voice render was established. The API preserves actionable timeout guidance with HTTP 503, and failed provider operations update the connection status and reveal the error. A dedicated test verifies that this failure also releases the setup gate. The Piper endpoint remained unavailable in the final read-only check. No shared Docker restart was attempted.
