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

# LA versione viene da Directory.Build.props, che e' l'unica fonte. Prima era scritta a mano
# qui, nel control e nel .wxs: alzarla voleva dire ricordarsi di tre posti.
VERSIONE="$(sed -n "s:.*<Version>\(.*\)</Version>.*:\1:p" "$RADICE/Directory.Build.props" | head -1)"

if [ -z "$VERSIONE" ]; then
    echo "Non trovo <Version> in Directory.Build.props." >&2
    exit 1
fi

# E il changelog deve concordare. Un pacchetto la cui versione non corrisponde alla prima voce
# del changelog e' un pacchetto che mente sulla propria storia, e lintian lo dice; ma dirlo qui
# costa un confronto e si scopre prima.
if ! head -1 "$QUI/debian/changelog" | grep -q "($VERSIONE)"; then
    echo "Il changelog non parla della versione $VERSIONE:" >&2
    head -1 "$QUI/debian/changelog" >&2
    exit 1
fi

# E nessuna riga puo' superare le 80 colonne. Non e' pignoleria di stile: lintian emette
# debian-changelog-line-too-long, il job di release gira con --fail-on error,warning, e una
# riga di 81 caratteri ferma la pubblicazione DOPO che l'MSI e' gia' stato costruito. E'
# successo: v0.2.0, due righe a 81. Costa un confronto e si scopre qui.
# SOLO la voce nuova, cioe' fino alla riga di firma: lintian guarda quella, e le voci
# storiche restano come sono state scritte invece di dover essere riaperte a ogni release.
# L'uscita PRIMA del controllo, e non dopo: la riga di firma e' a formato obbligato - nome
# piu' indirizzo piu' data - e supera gli 80 da sempre, ma lintian non la conta. Con i due
# blocchi nell'ordine sbagliato questa guardia bloccherebbe ogni singola build.
LUNGHE="$(awk '/^ -- / { exit } length($0) > 80 { print FNR ": " length($0) " colonne" }' "$QUI/debian/changelog")"

if [ -n "$LUNGHE" ]; then
    echo "Righe troppo lunghe nel changelog (lintian ne ammette 80):" >&2
    echo "$LUNGHE" >&2
    exit 1
fi

echo "Versione $VERSIONE"

rm -rf "$ALBERO" "$USCITA"
mkdir -p "$ALBERO/DEBIAN" "$ALBERO/usr/lib/observer/service" "$ALBERO/usr/lib/observer/dashboard"          "$ALBERO/usr/lib/observer/cli" "$ALBERO/usr/bin" "$ALBERO/lib/systemd/system"          "$ALBERO/usr/share/doc/observer" "$ALBERO/usr/share/applications"          "$ALBERO/usr/share/icons/hicolor/256x256/apps" "$ALBERO/usr/share/man/man1" "$ALBERO/usr/share/lintian/overrides" "$ALBERO/etc/ufw/applications.d" "$USCITA"

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

# I permessi che escono da "dotnet publish" non sono quelli di un pacchetto Debian, e non e'
# un'ipotesi: lintian, girato sul .deb vero, segnala le .dll gestite a 0744
# (executable-not-elf-or-script piu' non-standard-executable-perm) e appsettings.json a 0777.
#
# L'ultimo non e' una rifinitura. Un file di configurazione scrivibile da CHIUNQUE, dentro un
# albero che il servizio rilegge a ogni avvio, oggi e' tappato soltanto dal permesso della
# cartella che lo contiene: e' l'unica cosa fra un utente qualsiasi e il contenuto della
# sezione Kestrel del servizio.
#
# Si azzera tutto a 0644 e si rimette 0755 SOLO sui tre eseguibili veri. Cosi' cadono anche
# shared-library-is-executable sulle .so native, che il bit di esecuzione non lo vogliono.
find "$ALBERO/usr/lib/observer" -type f -exec chmod 0644 {} +
chmod 0755 "$ALBERO/usr/lib/observer/service/Observer.Service"
chmod 0755 "$ALBERO/usr/lib/observer/dashboard/Observer.App"
chmod 0755 "$ALBERO/usr/lib/observer/cli/observer"

# unstripped-binary-or-object, e per lintian e' un ERRORE, non un avvertimento: le librerie
# native che arrivano dai pacchetti NuGet portano dentro la tabella dei simboli.
find "$ALBERO/usr/lib/observer" -name '*.so' -exec strip --strip-unneeded {} +

install -m 0644 "$QUI/debian/observer.service" "$ALBERO/lib/systemd/system/observer.service"
# Il control e' un modello: la versione ci entra da fuori, da Directory.Build.props.
sed "s/@VERSIONE@/$VERSIONE/" "$QUI/debian/control" > "$ALBERO/DEBIAN/control"
chmod 0644 "$ALBERO/DEBIAN/control"
install -m 0755 "$QUI/debian/postinst"         "$ALBERO/DEBIAN/postinst"
install -m 0755 "$QUI/debian/prerm"            "$ALBERO/DEBIAN/prerm"
install -m 0755 "$QUI/debian/postrm"           "$ALBERO/DEBIAN/postrm"
install -m 0644 "$QUI/debian/copyright"        "$ALBERO/usr/share/doc/observer/copyright"

# changelog.gz e NON changelog.Debian.gz: questo e' un pacchetto NATIVO - la versione non ha
# revisione Debian - e per un pacchetto nativo il secondo nome e' sbagliato. Lo dice lintian
# (wrong-name-for-changelog-of-native-package), e ha ragione.
gzip -9n -c "$QUI/debian/changelog" > "$ALBERO/usr/share/doc/observer/changelog.gz"
chmod 0644 "$ALBERO/usr/share/doc/observer/changelog.gz"

# Le pagine di manuale. Non e' una formalita': i due comandi finiscono in /usr/bin, e su
# Debian cio' che sta in /usr/bin si spiega con "man", non con "--help" e basta.
gzip -9n -c "$QUI/debian/observer.1"           > "$ALBERO/usr/share/man/man1/observer.1.gz"
gzip -9n -c "$QUI/debian/observer-dashboard.1" > "$ALBERO/usr/share/man/man1/observer-dashboard.1.gz"
chmod 0644 "$ALBERO/usr/share/man/man1/observer.1.gz"            "$ALBERO/usr/share/man/man1/observer-dashboard.1.gz"

# L'unico tag che resta e non si puo' correggere: le librerie che SkiaSharp porta dentro di
# se'. Il file spiega perche', ed e' volutamente corto - un elenco lungo di eccezioni sarebbe
# il modo di smettere di guardarle.
install -m 0644 "$QUI/debian/lintian-overrides" "$ALBERO/usr/share/lintian/overrides/observer"

# Il profilo per ufw. NON apre niente da solo - un pacchetto Debian non tocca il firewall di
# chi lo installa - ma rende possibile "sudo ufw allow Observer" al posto del numero della
# porta. Sta in /etc, quindi e' un conffile: cosi' dpkg lo tratta da configurazione e a un
# aggiornamento non sovrascrive una modifica dell'amministratore.
install -m 0644 "$QUI/debian/observer.ufw" "$ALBERO/etc/ufw/applications.d/observer"
echo /etc/ufw/applications.d/observer > "$ALBERO/DEBIAN/conffiles"
chmod 0644 "$ALBERO/DEBIAN/conffiles"

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
dpkg-deb --root-owner-group --build "$ALBERO" "$USCITA/observer_${VERSIONE}_amd64.deb"

echo
# sed e NON head: con "set -o pipefail", head chiude la pipe dopo venti righe e il SIGPIPE
# di dpkg-deb diventa un codice d'uscita - cioe' un pacchetto costruito bene e uno script che
# dice di aver fallito. E' una corsa: in CI e' stata vinta per settimane, in un container
# Debian e' stata persa il 2026-09-03, e lintian non e' mai partito. sed legge fino in fondo.
dpkg-deb --info "$USCITA"/observer_*.deb | sed -n '1,20p'
echo
ls -lh "$USCITA"/observer_*.deb
