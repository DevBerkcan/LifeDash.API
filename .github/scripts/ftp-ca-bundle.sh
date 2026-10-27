#!/usr/bin/env bash
# Builds ftp-ca.pem for curl: the system trust store plus the intermediates the FTP server
# leaves out. Follows each certificate's AIA "CA Issuers" URL until the chain verifies against
# the system store, so certificate checks stay on.
# Usage: ftp-ca-bundle.sh <host> [system-ca-bundle]
set -euo pipefail
host="$1"
system_ca="${2:-/etc/ssl/certs/ca-certificates.crt}"

echo | openssl s_client -starttls ftp -connect "$host:21" -servername "$host" 2>/dev/null \
  | openssl x509 > leaf.pem

: > chain.pem
current=leaf.pem
for level in 1 2 3 4; do
  if openssl verify -CAfile "$system_ca" -untrusted chain.pem leaf.pem >/dev/null 2>&1; then
    cat "$system_ca" chain.pem > ftp-ca.pem
    echo "Chain verified with $((level - 1)) downloaded intermediate(s)."
    exit 0
  fi
  url=$(openssl x509 -in "$current" -noout -ext authorityInfoAccess 2>/dev/null \
    | sed -n 's/.*CA Issuers - URI:\(.*\)/\1/p' | head -1)
  if [ -z "$url" ]; then break; fi
  echo "Fetching issuer of $(openssl x509 -in "$current" -noout -subject): $url"
  curl -fsS "$url" -o "issuer$level.der"
  openssl x509 -inform DER -in "issuer$level.der" -out "issuer$level.pem" 2>/dev/null \
    || cp "issuer$level.der" "issuer$level.pem"
  cat "issuer$level.pem" >> chain.pem
  current="issuer$level.pem"
done

echo "::error::Could not build a verified certificate chain for $host."
openssl verify -CAfile "$system_ca" -untrusted chain.pem leaf.pem || true
exit 1
