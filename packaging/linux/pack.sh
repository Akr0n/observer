#!/usr/bin/env bash
# Builds Observer's .deb.
#
# No extra .NET tool is needed: dpkg-deb is already on ubuntu-latest. The .NET tools for
# building Debian packages are the worst option - one has not been updated in years, the
# other is paid.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
STAGING="$SCRIPT_DIR/root"
OUT_DIR="$SCRIPT_DIR/out"
CONFIG="${1:-Release}"

# THE version comes from Directory.Build.props, which is the only source. It used to be
# written by hand here, in control and in the .wxs: bumping it meant remembering three places.
VERSION="$(sed -n "s:.*<Version>\(.*\)</Version>.*:\1:p" "$REPO_ROOT/Directory.Build.props" | head -1)"

if [ -z "$VERSION" ]; then
    echo "Cannot find <Version> in Directory.Build.props." >&2
    exit 1
fi

# And the changelog must agree. A package whose version does not match the first changelog
# entry is a package that lies about its own history, and lintian says so; but checking it
# here costs one comparison and catches it sooner.
if ! head -1 "$SCRIPT_DIR/debian/changelog" | grep -q "($VERSION)"; then
    echo "The changelog does not describe version $VERSION:" >&2
    head -1 "$SCRIPT_DIR/debian/changelog" >&2
    exit 1
fi

# And no line may be longer than 80 columns. This is not style pedantry: lintian emits
# debian-changelog-line-too-long, the release job runs with --fail-on error,warning, and an
# 81-character line stops the release AFTER the MSI has already been built. It happened:
# v0.2.0, two lines at 81. It costs one comparison and it is caught here.
# ONLY the new entry, that is, up to the sign-off line: that is the one lintian checks, and the
# older entries stay as they were written instead of having to be reopened at every release.
# The exit comes BEFORE the check, not after: the sign-off line has a mandatory format - name
# plus address plus date - and has always been longer than 80, but lintian does not count it.
# With the two blocks in the wrong order this guard would block every single build.
LONG_LINES="$(awk '/^ -- / { exit } length($0) > 80 { print FNR ": " length($0) " columns" }' "$SCRIPT_DIR/debian/changelog")"

if [ -n "$LONG_LINES" ]; then
    echo "Lines too long in the changelog (lintian allows at most 80):" >&2
    echo "$LONG_LINES" >&2
    exit 1
fi

# And the new entry must be DATED AFTER the previous one. Lintian compares the two dates and
# rejects the package (latest-changelog-entry-without-new-date): it happened to 0.8.1, dated
# 11:30 under a 0.8.0 dated 12:00, and the release failed after the MSI had already been
# built. This too costs one comparison and is caught here.
NEW_DATE="$(grep -m1 '^ -- ' "$SCRIPT_DIR/debian/changelog" | sed 's/.*>  //')"
PREVIOUS_DATE="$(grep -m2 '^ -- ' "$SCRIPT_DIR/debian/changelog" | tail -1 | sed 's/.*>  //')"

if [ "$(date -d "$NEW_DATE" +%s)" -le "$(date -d "$PREVIOUS_DATE" +%s)" ]; then
    echo "The date of the new changelog entry ($NEW_DATE) is not later than that of the" >&2
    echo "previous one ($PREVIOUS_DATE): lintian rejects it." >&2
    exit 1
fi

echo "Version $VERSION"

rm -rf "$STAGING" "$OUT_DIR"
mkdir -p "$STAGING/DEBIAN" "$STAGING/usr/lib/observer/service" "$STAGING/usr/lib/observer/dashboard"          "$STAGING/usr/lib/observer/cli" "$STAGING/usr/bin" "$STAGING/lib/systemd/system"          "$STAGING/usr/share/doc/observer" "$STAGING/usr/share/applications"          "$STAGING/usr/share/icons/hicolor/256x256/apps" "$STAGING/usr/share/man/man1" "$STAGING/usr/share/lintian/overrides" "$STAGING/etc/ufw/applications.d" "$OUT_DIR"

publish() {
    local project="$1" destination="$2"
    echo "Publishing $project..."
    # NOT self-contained: Ubuntu 24.04 has .NET 10 in its own official archive, in main, with
    # security updates. Shipping a runtime would mean having to keep it updated ourselves.
    dotnet publish "$REPO_ROOT/src/$project" -c "$CONFIG" -r linux-x64 --self-contained false         -o "$destination" --nologo >/dev/null
}

publish Observer.Service "$STAGING/usr/lib/observer/service"
publish Observer.App     "$STAGING/usr/lib/observer/dashboard"
publish Observer.Cli     "$STAGING/usr/lib/observer/cli"

# THE GUARD, and here it matters even more than in the MSI. "dotnet publish" also copies
# appsettings.Local.json into the output, which is the file where a developer keeps their own
# token. A .deb that shipped it would give EVERY machine the SAME token, because explicit
# configuration wins over the credential store: on its own it would defeat the whole mechanism
# that generates one key per machine. They are removed, and then we CHECK that none are left.
find "$STAGING" \( -name 'appsettings*.Local.json' -o -name 'credentials.json'                  -o -name 'client.json' -o -name '*.pdb'                  -o -name 'appsettings.Development.json'                  -o -name 'runtimeconfig.template.json' \) -print -delete

if find "$STAGING" \( -name 'appsettings*.Local.json' -o -name 'credentials.json'                     -o -name 'client.json' \) | grep -q .; then
    echo "A file that can carry a secret is still in the package." >&2
    exit 1
fi

# The permissions that come out of "dotnet publish" are not those of a Debian package, and
# this is not a guess: lintian, run on the real .deb, flags the managed .dlls at 0744
# (executable-not-elf-or-script plus non-standard-executable-perm) and appsettings.json at 0777.
#
# The last one is not cosmetic. A configuration file writable by ANYONE, inside a tree the
# service reads again on every start, is currently protected only by the permissions of the
# directory that holds it: that is all that stands between any local user and the contents
# of the service's Kestrel section.
#
# Everything is reset to 0644, and 0755 goes back ONLY on the three real executables. This
# also clears shared-library-is-executable on the native .so files, which should not carry
# the execute bit.
find "$STAGING/usr/lib/observer" -type f -exec chmod 0644 {} +
chmod 0755 "$STAGING/usr/lib/observer/service/Observer.Service"
chmod 0755 "$STAGING/usr/lib/observer/dashboard/Observer.App"
chmod 0755 "$STAGING/usr/lib/observer/cli/observer"

# unstripped-binary-or-object, and lintian treats it as an ERROR, not a warning: the native
# libraries that come from NuGet packages still carry their symbol tables.
find "$STAGING/usr/lib/observer" -name '*.so' -exec strip --strip-unneeded {} +

install -m 0644 "$SCRIPT_DIR/debian/observer.service" "$STAGING/lib/systemd/system/observer.service"
# control is a template: the version is filled in from outside, from Directory.Build.props.
sed "s/@VERSION@/$VERSION/" "$SCRIPT_DIR/debian/control" > "$STAGING/DEBIAN/control"
chmod 0644 "$STAGING/DEBIAN/control"
install -m 0755 "$SCRIPT_DIR/debian/postinst"         "$STAGING/DEBIAN/postinst"
install -m 0755 "$SCRIPT_DIR/debian/prerm"            "$STAGING/DEBIAN/prerm"
install -m 0755 "$SCRIPT_DIR/debian/postrm"           "$STAGING/DEBIAN/postrm"
install -m 0644 "$SCRIPT_DIR/debian/copyright"        "$STAGING/usr/share/doc/observer/copyright"

# changelog.gz and NOT changelog.Debian.gz: this is a NATIVE package - the version has no
# Debian revision - and for a native package the second name is wrong. lintian says so
# (wrong-name-for-changelog-of-native-package), and it is right.
gzip -9n -c "$SCRIPT_DIR/debian/changelog" > "$STAGING/usr/share/doc/observer/changelog.gz"
chmod 0644 "$STAGING/usr/share/doc/observer/changelog.gz"

# The man pages. They are not a formality: both commands end up in /usr/bin, and on Debian
# whatever lives in /usr/bin is documented by "man", not by "--help" alone.
gzip -9n -c "$SCRIPT_DIR/debian/observer.1"           > "$STAGING/usr/share/man/man1/observer.1.gz"
gzip -9n -c "$SCRIPT_DIR/debian/observer-dashboard.1" > "$STAGING/usr/share/man/man1/observer-dashboard.1.gz"
chmod 0644 "$STAGING/usr/share/man/man1/observer.1.gz"            "$STAGING/usr/share/man/man1/observer-dashboard.1.gz"

# The only tag left, and it cannot be fixed: the libraries SkiaSharp bundles inside itself.
# The file explains why, and it is deliberately short - a long list of exceptions is how
# people stop looking at them.
install -m 0644 "$SCRIPT_DIR/debian/lintian-overrides" "$STAGING/usr/share/lintian/overrides/observer"

# The ufw profile. It does NOT open anything by itself - a Debian package does not touch the
# firewall of whoever installs it - but it lets an administrator type "sudo ufw allow Observer"
# instead of the port number. It lives in /etc, so it is a conffile: dpkg treats it as
# configuration and does not overwrite an administrator's change on upgrade.
install -m 0644 "$SCRIPT_DIR/debian/observer.ufw" "$STAGING/etc/ufw/applications.d/observer"
echo /etc/ufw/applications.d/observer > "$STAGING/DEBIAN/conffiles"
chmod 0644 "$STAGING/DEBIAN/conffiles"

if [ -f "$REPO_ROOT/src/Observer.App/Assets/observer.png" ]; then
    install -m 0644 "$REPO_ROOT/src/Observer.App/Assets/observer.png"         "$STAGING/usr/share/icons/hicolor/256x256/apps/observer.png"
fi

cat > "$STAGING/usr/bin/observer" <<'LAUNCHER'
#!/bin/sh
exec /usr/lib/observer/cli/observer "$@"
LAUNCHER
chmod 0755 "$STAGING/usr/bin/observer"

cat > "$STAGING/usr/bin/observer-dashboard" <<'LAUNCHER'
#!/bin/sh
exec /usr/lib/observer/dashboard/Observer.App "$@"
LAUNCHER
chmod 0755 "$STAGING/usr/bin/observer-dashboard"

cat > "$STAGING/usr/share/applications/observer.desktop" <<'DESKTOP_ENTRY'
[Desktop Entry]
Type=Application
Name=Observer
Comment=Watch this machine
Exec=observer-dashboard
Icon=observer
Terminal=false
Categories=System;Monitor;
DESKTOP_ENTRY
chmod 0644 "$STAGING/usr/share/applications/observer.desktop"

# Default compression, which is zstd on Ubuntu 24.04. Measured: bookworm (dpkg 1.21.23)
# installs it without complaint; it fails only on bullseye and older. And there the point is
# academic anyway, because aspnetcore-runtime-10.0 does not exist on bookworm.
dpkg-deb --root-owner-group --build "$STAGING" "$OUT_DIR/observer_${VERSION}_amd64.deb"

echo
# sed and NOT head: with "set -o pipefail", head closes the pipe after twenty lines and
# dpkg-deb's SIGPIPE becomes an exit code - that is, a correctly built package and a script
# that says it failed. It is a race: in CI it was won for weeks, in a Debian container it was
# lost on 2026-09-03, and lintian never ran. sed reads to the end.
dpkg-deb --info "$OUT_DIR"/observer_*.deb | sed -n '1,20p'
echo
ls -lh "$OUT_DIR"/observer_*.deb
