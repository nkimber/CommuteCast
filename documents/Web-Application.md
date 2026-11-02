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
