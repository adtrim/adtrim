# Update notifications

AdTrim checks for updates once per application session after startup settles.
Recording windows share the check. Help provides a manual check. The app never
downloads or installs an update; the release button opens the matching AdTrim
GitHub release page. Notifications do not interrupt editing or exporting.

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
and bounded reminder history. It is not a defense against an attacker who already
controls the local account. A first installation has no prior revision to compare;
an attacker controlling delivery could replay an older signed announcement to it.
There is no time limit on this replay risk. Previously observed revisions prevent
rollback on returning installations. Signatures do not prove freshness. Blocking
the network can prevent checks. This protocol
does not authenticate installers or protect a compromised GitHub release account.

## Preparing a feed

Use PowerShell 7 and tools/sign-update-feed.ps1. Create the protected key outside
the repositories using -CreateKey -KeyPath <private-path> -PublicKeyPath <public-path>.
Private material is encrypted for the current Windows account. Never commit it,
publish it, or place it in website deployment credentials. Keep an independently
encrypted recovery backup in separate storage before production deployment.

Sign with -KeyPath <private-path> -FeedPath <updates.json>, then validate:

```powershell
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

For a replacement operational key, use the original root key with
-AuthorizePublicKeyPath <replacement-public.pem> -AuthorizationPath <authorization.json>.
The authorization expires after one year. Sign subsequent feeds using the new
private key and -AuthorizationPath <authorization.json>.

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
preserves an existing choice during upgrades. Help > Automatically check for
updates changes the same setting for every open window. Turning it off cancels
a pending automatic check and prevents future startup checks, including security
alerts. Manual checks remain available. Re-enabling takes effect at the next app
launch; a manual check can be run immediately.

The installer and app share `automatic-updates.txt` in the user's AdTrim settings
directory: `1` enables checks and `0` disables them. This file is independent of
window preferences and is retained on uninstall. An unreadable or invalid setting
does not enable network access.

Checks download release information and its signature from GitHub Pages. They do
not upload recordings, filenames, hardware information, usage events, installation
identifiers, or the installed version. GitHub Pages logs request IP addresses for
security; see https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages#data-collection.
