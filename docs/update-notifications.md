# Update notifications

AdTrim checks for updates once per application session after startup settles.
Recording windows share the check. Help provides a manual check. Starting with
v2.1.11, an available update offers Download update, followed by Restart and update.
Nothing is downloaded or installed without those actions. Downloads leave all
recording windows usable; restarting updates them together. The app verifies the
installer, saves every open project's edits and window preferences, exits, runs
the per-user installer silently, then reopens the recordings. Finish exports,
close export setup and wait for analysis before restarting. Reopened recording
windows stay disabled until the update helper finishes or exits, so cancelling
startup cannot discard edits made while another recording is still opening.
A save failure keeps
the windows open. Automatic installation requires the installed copy; test-folder
builds do not overwrite an installation. Settings, projects and recordings remain.

See what's new opens the release history on adtrim.github.io, with a manual
installer download. The installed and available versions travel in the URL
fragment, which the browser does not send to the server. Releases without signed
package information use that manual fallback. Notifications do not interrupt
editing or exporting. Older clients need one manual upgrade to gain this feature.

Regular announcements appear once per release. Security reminders recur at a
subsequent launch after seven days; urgent reminders recur after one day.
Dismissal, Escape, closing the dialog, and opening the release page acknowledge
the announcement. Security reminders follow advisory IDs, including when a later
routine release contains the same fix. A security-to-urgent escalation can announce
again immediately. Indicators remain until the installed version includes the fix.

## Signed feed

The client reads https://adtrim.github.io/updates.json and updates.json.sig.
HTTPS redirects are rejected. The feed is limited to 64 KiB, its signature to
8 KiB, and each request including its body has a timeout. There are no arbitrary
navigation targets or HTML in the protocol. Versions must contain three decimal
components; only the app constructs the release URL.

Schema 1 fields: schema, revision, publishedAt, latestVersion, summary,
advisories, and optional legacy expiresAt. Dates are UTC ISO 8601. Revision
increases whenever any bytes change. Starting with v1.1.6, announcements do not
expire; expiresAt is accepted for compatibility but does not impose a deadline.
Advisories contain id, affectedFrom
(inclusive), fixedIn (exclusive), importance (security or urgent), and summary.
Keep advisories that apply to supported older installations in later feeds.

Sign the exact UTF-8 bytes using RSA-3072 PSS with SHA-256. The ordinary detached
signature is 384 binary bytes. The embedded public key is the trust root; a
checksum or a public key downloaded from the website is not a trust anchor.
Duplicate fields, unsupported schemas, malformed values, future publication dates,
older revisions, and reused revisions with different bytes are rejected. A valid
cached feed can survive a network failure regardless of its age. Failed checks never
report that the installation is current.

The per-user updates-state.json stores the last verified feed/signature, revision
and bounded reminder history. A separate updates-revision.json keeps the highest
accepted revision and its digest, so a damaged or reset cache cannot re-admit an
older signed announcement. Cache revision and digest fields never override that
record. Migration or recovery derives them from the cached announcement only after
verifying its signature and checking it against the independent record. These files
resist accidental corruption only: they are not a
defense against an attacker who already controls the local account and can delete
or edit both. A first installation has no prior revision to compare;
an attacker controlling delivery could replay an older signed announcement to it.
There is no time limit on this replay risk. Previously observed revisions prevent
rollback on returning installations. Signatures do not prove freshness. Blocking
the network can prevent checks. This protocol
authenticates announcements only. The separately signed package information below
authenticates installer bytes, including when a GitHub release account is
compromised without access to the signing key. A compromised signing key or local
account is outside this protection.

## Signed installer packages

Each new release also provides `update-package.json` and its detached
`update-package.json.sig`. The package uses the same embedded trust root and
RSA-3072 PSS/SHA-256 verification, including delegated-key expiry. Schema 1 has
exactly `schema`, `version`, `installerName`, `sizeBytes` and `sha256`. The version
must match the available release, and the name must be
`AdTrim-Setup-v<version>.exe`. The installer URL is constructed under the fixed
`adtrim/adtrim` GitHub repository. HTTPS redirects are limited to GitHub's release
asset hosts. Metadata requests time out after 20 seconds, the installer after
30 minutes. The installer is limited to 1 GiB and streamed to disk.

The download's exact length, SHA-256 and embedded file version must match the
signed package. An installer is written under a temporary `.partial` name and gets
its real name only after those checks. The update window names each step as it
runs: getting the update information, verifying the signature, downloading,
checking the download and checking its version.

Restart and update saves every open recording, closes AdTrim and hands over to an
update helper: a copy of the running app in the update's own folder, so Setup never
has to replace a file the helper is using. Its "Updating AdTrim" window shows each
step: waiting for AdTrim to close, backing up the current version, checking the
downloaded installer, installing, checking the new version, and starting AdTrim
with the same recordings. The helper verifies the package again, waits for the
original process by PID and start time, and can only target the registered
installation folder. It cannot install a same-version or older package.

Before installing, the helper backs up the files the installation owns, as listed
in its `install-files.txt` (written by Setup). Nothing outside that list is copied,
changed or removed, including user data in installations that share a folder with
settings. If installation fails, the installer is retried once; after that the
window explains what failed and offers Retry or Cancel update. Cancel update
restores the backed-up files, removes files the failed update added, and starts the
previous version with the same recordings. The backup is kept until the installed
copy itself reports that all its windows are ready; the helper checks that the
reporting process is running from the installed `AdTrim.exe`, so a launch that only
handed its recordings to another copy cannot confirm the update. An update that
installs but does not start can also be restored: Retry waits for the same starting
copy instead of starting another, and Cancel update closes that copy before
restoring its files. If the installer runs for more than five minutes, the window
offers to stop it and restore. Closing the window after a failure means Cancel
update; the window stays until recovery has finished. An installation without
`install-files.txt` is not updated automatically; use the manual download.

A per-user named gate marks the update. AdTrim launched during it (for example
from a recording's Edit with AdTrim command) hands its recording to the update
window and exits at once, so it never holds files open that Setup is replacing;
the recording opens when the update finishes. If the helper itself stops, Windows
releases the gate and AdTrim starts normally.

When a step fails, Copy details places the versions, step, error and Windows
version on the clipboard, with personal folders replaced by `%LOCALAPPDATA%`,
`%APPDATA%` or `%USERPROFILE%`. Report a problem opens a new issue at
https://github.com/adtrim/adtrim/issues/new. The error is also written to
`%LOCALAPPDATA%\AdTrim\crash-log.txt`.

Downloads and restart state use dedicated GUID folders under the user's AdTrim
`updates` directory. Closing the update window removes a finished download that was
not installed. Each startup removes every update folder no running update uses,
including partial downloads; a folder holding a backup that could not be restored
is kept for 30 days for manual recovery. Successful restarts remove their job,
including its temporary recording paths. Cleanup refuses links and paths outside
that directory. Windows may still block an unsigned application; these signatures
do not replace Windows code signing.

The update window does not belong to any recording window: closing a recording
does not cancel or discard a download, and asking for updates again brings the
open update window forward. See what's new opens the release notes without closing
it. The manual route stays available: download the installer from the release
page, close AdTrim and run it.

After building a release installer, prepare its immutable package information:

```powershell
powershell -File tools/prepare-update-package.ps1 -InstallerPath <installer.exe> -OutputDirectory <release-folder>
dotnet run --project tools/AdTrim.UpdateFeed -- --sign <release-folder>/update-package.json <private-key>
dotnet run --project tools/AdTrim.UpdateFeed -- --package <release-folder>/update-package.json src/AdTrim/Services/update-public-key.pem <version>
```

Keep the matching installer in the release folder during verification. Sign the
final bytes only; never sign synthetic test installers with a production key.
Attach both package files to the release together with the matching installer,
media sources and signed announcement. Keep the existing schema-1 announcement
unchanged in shape so older clients continue to understand it. GitHub Releases
hosts the downloads; no paid updater service is required.

## Preparing a feed

Signing, verification and key management are all commands of tools/AdTrim.UpdateFeed.
They need only the .NET SDK. Run it without arguments for the list.

Create the protected key outside the repositories:

```powershell
dotnet run --project tools/AdTrim.UpdateFeed -- --create-key <private-path> <public-path>
```

Private material is encrypted for the current Windows account, so signing on that
computer needs no passphrase. Never commit it, publish it, or place it in website
deployment credentials. Keep an independently encrypted recovery backup in separate
storage before production deployment:

```powershell
dotnet run --project tools/AdTrim.UpdateFeed -- --backup-key <private-path> <backup-file>
dotnet run --project tools/AdTrim.UpdateFeed -- --check-backup <backup-file> src/AdTrim/Services/update-public-key.pem
```

The backup asks for a passphrase twice. The check confirms that the backup opens with
the passphrase and holds the private half of the public key the app trusts, and writes
nothing. Repeat it from time to time: a backup that cannot be opened is the same as no
backup.

To restore on a new computer or Windows account, copy the backup file there:

```powershell
dotnet run --project tools/AdTrim.UpdateFeed -- --restore-key <backup-file> src/AdTrim/Services/update-public-key.pem <private-path>
```

The key is written encrypted for that Windows account and verified. An existing key
file is never overwritten. Without the backup or its passphrase the root key cannot be
recovered; see Key rotation below for what that means. --check-key <private-path>
<public-path> confirms that a key file opens for this account and matches a public key.

tools/test-update-signing.ps1 exercises every command with throwaway keys in a
temporary folder.

Sign and validate an announcement:

```powershell
dotnet run --project tools/AdTrim.UpdateFeed -- --sign updates.json <private-path>
dotnet run --project tools/AdTrim.UpdateFeed -- updates.json src/AdTrim/Services/update-public-key.pem
```

Attach the reviewed, signed updates.json and updates.json.sig to the draft release
alongside the installer. Publish the release first; the website workflow retrieves
and verifies those assets before deploying them together. Prereleases do not
trigger website publication. The bootstrap v1.1.0 feed lives in the website source
because that release predates the notification feature.
The website deployment verifies signatures and the referenced public stable
release. There is no scheduled expiration check or periodic announcement renewal.
Publish a new signed announcement when release information changes; never replace
installer assets.

For the v1.1.6 transition, retain expiresAt within 90 days of publishedAt so
v1.1.1 and v1.1.2 can discover the upgrade while their deadline is valid. Those
older clients still enforce expiration and require a manual download if they miss
that window. Future announcements may omit expiresAt. Existing signed feeds and
caches are accepted by v1.1.6 even after their legacy deadline.
No private signing key is kept by the website workflow.
Do not classify a dependency refresh as a security emergency without reviewing
whether the vulnerability affects this application's build and enabled features.

## Key rotation

Rotation is optional and has never been used. The root key has no expiry, so a
project that never rotates has nothing to renew.

For a replacement operational key, use the original root key with
--authorize <root-private-path> <replacement-public.pem> <authorization.json>.
The authorization expires after one year: renew it and sign the current announcement
again before then, or announcements signed with the replacement stop verifying.
Sign subsequent feeds with --sign <file> <replacement-private-path> <authorization.json>.

The signature file then contains signature, authorization, and
authorizationSignature, each base64. Authorization decodes to UTF-8 JSON with
publicKey and expiresAt. The root signs the UTF-8 prefix
"AdTrim update signing key v1\n" (a real newline) followed by the exact authorization
bytes. This authorizes the replacement without changing installed clients.

Losing or compromising the root requires a new app trust root and a separately
trusted distribution/recovery process. Rotation does not revoke a stolen old key
in already-installed offline clients. Revision checks limit rollback, not all
key-compromise attacks. Delegated-key authorization expiry is still enforced;
it is separate from announcement expiration. The current root-signed feed has
no delegated authorization and needs no periodic key renewal. This is not a full TUF implementation.
# Automatic check preference

Automatic checks default to on. Setup offers a checkbox before installation and
preserves an existing choice during upgrades. Edit > Settings > Updates
changes the same setting for every open window. Turning it off cancels
a pending automatic check and prevents future startup checks, including security
alerts. Manual checks remain available. Re-enabling takes effect at the next app
launch; a manual check can be run immediately.

The installer and app share `automatic-updates.txt` in the user's AdTrim settings
directory: `1` enables checks and `0` disables them. This file is independent of
window preferences and is retained on uninstall unless the user chooses to delete
AdTrim's settings and caches. An unreadable or invalid setting
does not enable network access.

Checks download release information and its signature from GitHub Pages. They do
not upload recordings, filenames, hardware information, usage events, installation
identifiers, or the installed version. GitHub Pages logs request IP addresses for
security; see https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages#data-collection.
