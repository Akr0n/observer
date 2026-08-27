#!/usr/bin/env bash
# Costruisce il .deb di Observer.
#
# Non serve alcuno strumento .NET aggiuntivo: dpkg-deb c'e' gia' su ubuntu-latest. Gli
# strumenti .NET per produrre pacchetti Debian sono la strada peggiore - uno e' fermo da anni,
# l'altro e' a pagamento.
set -euo pipefail

QUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RADICE="$(cd "$QUI/../.." && pwd)"
ALBERO="$QUI/root"
USCITA="$QUI/out"
CONFIG="${1:-Release}"

rm -rf "$ALBERO" "$USCITA"
mkdir -p "$ALBERO/DEBIAN" "$ALBERO/usr/lib/observer/service" "$ALBERO/usr/lib/observer/dashboard"          "$ALBERO/usr/lib/observer/cli" "$ALBERO/usr/bin" "$ALBERO/lib/systemd/system"          "$ALBERO/usr/share/doc/observer" "$ALBERO/usr/share/applications"          "$ALBERO/usr/share/icons/hicolor/256x256/apps" "$USCITA"

pubblica() {
    local progetto="$1" destinazione="$2"
    echo "Pubblico $progetto..."
    # NON self-contained: Ubuntu 24.04 ha .NET 10 nel proprio archivio ufficiale, in main, con
    # aggiornamenti di sicurezza. Imbarcare un runtime significherebbe doverlo aggiornare noi.
    dotnet publish "$RADICE/src/$progetto" -c "$CONFIG" -r linux-x64 --self-contained false         -o "$destinazione" --nologo >/dev/null
}

pubblica Observer.Service "$ALBERO/usr/lib/observer/service"
pubblica Observer.App     "$ALBERO/usr/lib/observer/dashboard"
pubblica Observer.Cli     "$ALBERO/usr/lib/observer/cli"

# LA GUARDIA, e qui vale piu' che nell'MSI. "dotnet publish" copia nell'output anche
# appsettings.Local.json, cioe' il file dove uno sviluppatore tiene il proprio token. Un .deb
# che lo imbarcasse darebbe a OGNI macchina lo STESSO token, perche' la configurazione esplicita
# vince sul deposito: annullerebbe da sola l'intero meccanismo che genera una chiave per
# macchina. Si tolgono, e poi si CONTROLLA che non ce ne siano piu'.
find "$ALBERO" \( -name 'appsettings*.Local.json' -o -name 'credentials.json'                  -o -name 'client.json' -o -name '*.pdb'                  -o -name 'appsettings.Development.json'                  -o -name 'runtimeconfig.template.json' \) -print -delete

if find "$ALBERO" \( -name 'appsettings*.Local.json' -o -name 'credentials.json'                     -o -name 'client.json' \) | grep -q .; then
    echo "Nel pacchetto e' rimasto un file che puo' portare un segreto." >&2
    exit 1
fi

# lintian segnala shared-library-is-executable: le .so native arrivano con il bit di esecuzione.
find "$ALBERO/usr/lib/observer" -name '*.so' -exec chmod 0644 {} +

install -m 0644 "$QUI/debian/observer.service" "$ALBERO/lib/systemd/system/observer.service"
install -m 0644 "$QUI/debian/control"          "$ALBERO/DEBIAN/control"
install -m 0755 "$QUI/debian/postinst"         "$ALBERO/DEBIAN/postinst"
install -m 0755 "$QUI/debian/prerm"            "$ALBERO/DEBIAN/prerm"
install -m 0755 "$QUI/debian/postrm"           "$ALBERO/DEBIAN/postrm"
install -m 0644 "$QUI/debian/copyright"        "$ALBERO/usr/share/doc/observer/copyright"

gzip -9n -c "$QUI/debian/changelog" > "$ALBERO/usr/share/doc/observer/changelog.Debian.gz"
chmod 0644 "$ALBERO/usr/share/doc/observer/changelog.Debian.gz"

if [ -f "$RADICE/src/Observer.App/Assets/observer.png" ]; then
    install -m 0644 "$RADICE/src/Observer.App/Assets/observer.png"         "$ALBERO/usr/share/icons/hicolor/256x256/apps/observer.png"
fi

cat > "$ALBERO/usr/bin/observer" <<'AVVIO'
#!/bin/sh
exec /usr/lib/observer/cli/observer "$@"
AVVIO
chmod 0755 "$ALBERO/usr/bin/observer"

cat > "$ALBERO/usr/bin/observer-dashboard" <<'AVVIO'
#!/bin/sh
exec /usr/lib/observer/dashboard/Observer.App "$@"
AVVIO
chmod 0755 "$ALBERO/usr/bin/observer-dashboard"

cat > "$ALBERO/usr/share/applications/observer.desktop" <<'VOCE'
[Desktop Entry]
Type=Application
Name=Observer
Comment=Watch this machine
Exec=observer-dashboard
Icon=observer
Terminal=false
Categories=System;Monitor;
VOCE
chmod 0644 "$ALBERO/usr/share/applications/observer.desktop"

# Compressione predefinita, cioe' zstd su Ubuntu 24.04. Misurato: bookworm (dpkg 1.21.23) la
# installa senza storie; fallisce solo da bullseye in giu'. E li' il punto e' comunque teorico,
# perche' aspnetcore-runtime-10.0 su bookworm non esiste.
dpkg-deb --root-owner-group --build "$ALBERO" "$USCITA/observer_0.1.0_amd64.deb"

echo
dpkg-deb --info "$USCITA"/observer_*.deb | head -20
echo
ls -lh "$USCITA"/observer_*.deb
