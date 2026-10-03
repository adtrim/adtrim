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

Schema 1 fields: schema, revision, publishedAt, expiresAt, latestVersion, summary,
advisories. Dates are UTC ISO 8601. Revision increases whenever any bytes change.
Expiry is at most 90 days after publication. Advisories contain id, affectedFrom
(inclusive), fixedIn (exclusive), importance (security or urgent), and summary.
Keep advisories that apply to supported older installations in later feeds.

Sign the exact UTF-8 bytes using RSA-3072 PSS with SHA-256. The ordinary detached
signature is 384 binary bytes. The embedded public key is the trust root; a
checksum or a public key downloaded from the website is not a trust anchor.
Duplicate fields, unsupported schemas, malformed values, expired information,
older revisions, and reused revisions with different bytes are rejected. A valid
cached feed can survive a network failure until expiration. Failed checks never
report that the installation is current.

The per-user updates-state.json stores the last verified feed/signature, revision
and bounded reminder history. It is not a defense against an attacker who already
controls the local account. A first installation has no prior revision to compare;
expiration bounds replay. Blocking the network can prevent checks. This protocol
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
release. Its weekly health workflow fails within 14 days of expiry: maintainers
must monitor failed-run notifications and renew by increasing revision, refreshing
dates, and signing again. Replace only the signed announcement assets on the
current release and redeploy the website; never replace installer assets.
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
in already-installed offline clients; expiration and revision checks limit some
replay, not all key-compromise attacks. This is not a full TUF implementation.
