#!/bin/bash
# Creates the Mac app's code-signing identity: a self-signed certificate ("display-toggle code
# signing") whose key signs every build, local and released. macOS then sees each new build as the
# same app: Local Network access follows the certificate, not one build's hash, which an ad-hoc
# signature can't do.
#
#   mac/scripts/new-signing-identity.sh            # login keychain + GitHub release secrets
#
# It imports the identity into your login keychain (for `make install`) and stores it as the
# MACOS_SIGNING_P12 / MACOS_SIGNING_P12_PASSWORD secrets of the `release` environment (for the
# release workflow). Run it once, and again only to replace a lost or expiring certificate: a new
# certificate is a new app identity to macOS.
set -euo pipefail

NAME="display-toggle code signing"
REPO="RoggerFabri/display-toggle"
DAYS=3650

if security find-identity -p codesigning | grep -qF "\"$NAME\""; then
  echo "error: \"$NAME\" is already in a keychain; delete it first (Keychain Access) to replace it." >&2
  exit 1
fi
# The p12 must use the legacy algorithms `security import` reads: `-legacy`, OpenSSL 3 or later
# (macOS's own /usr/bin/openssl is LibreSSL, which lacks it).
OPENSSL=/opt/homebrew/bin/openssl
"$OPENSSL" version 2>/dev/null | grep -Eq '^OpenSSL ([3-9]|[1-9][0-9])\.' \
  || { echo "error: needs Homebrew's OpenSSL 3 or later (brew install openssl)" >&2; exit 1; }
[ "$(gh api user -q .login)" = RoggerFabri ] || { echo "error: gh is not logged in as RoggerFabri" >&2; exit 1; }

work=$(mktemp -d)
trap 'rm -rf "${work:?}"' EXIT
cat > "$work/cert.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
EOF
password=$("$OPENSSL" rand -hex 24)
"$OPENSSL" req -x509 -newkey rsa:2048 -nodes -keyout "$work/key.pem" -out "$work/cert.pem" \
  -days "$DAYS" -config "$work/cert.cnf" 2>/dev/null
"$OPENSSL" pkcs12 -export -legacy -inkey "$work/key.pem" -in "$work/cert.pem" -name "$NAME" \
  -out "$work/identity.p12" -passout "pass:$password"

# -T: codesign may use the key without asking.
security import "$work/identity.p12" -k "$HOME/Library/Keychains/login.keychain-db" \
  -P "$password" -T /usr/bin/codesign
base64 -i "$work/identity.p12" | gh secret set MACOS_SIGNING_P12 -R "$REPO" -e release
printf '%s' "$password" | gh secret set MACOS_SIGNING_P12_PASSWORD -R "$REPO" -e release

echo "→ \"$NAME\": login keychain + $REPO release secrets (valid $DAYS days)"
echo "→ certificate SHA-1 (in every build's designated requirement): $("$OPENSSL" x509 -in "$work/cert.pem" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d :)"
