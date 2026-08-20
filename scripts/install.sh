#!/usr/bin/env bash
#
# Installe tempi comme service systemd sur un Raspberry Pi.
#
#   sudo ./scripts/install.sh                  # dernière version publiée
#   sudo ./scripts/install.sh v1.2.0           # une version précise
#   sudo ./scripts/install.sh ./tempi.tar.gz   # artefact local, sans réseau
#
# Le script est idempotent : on peut le relancer pour mettre à jour. La base de
# mesures et /etc/tempi/tempi.env ne sont jamais touchés.

set -euo pipefail

PREFIX=/opt/tempi
BIN_DIR="$PREFIX/bin"
BIN_LINK=/usr/local/bin/tempi
CONFIG_DIR=/etc/tempi
SERVICE=tempi.service
USER_NAME=tempi
REPO=Orkad/tempi
RID=linux-arm64
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m/!\\\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mErreur :\033[0m %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "ce script doit être lancé avec sudo."
command -v systemctl >/dev/null || die "systemd est requis."
command -v tar >/dev/null || die "tar est requis."

# --- Architecture -----------------------------------------------------------

# .NET ne prend pas en charge l'ARMv6, et l'artefact publié est 64 bits. Le dire
# franchement vaut mieux que de laisser découvrir un binaire qui ne s'exécute pas.
case "$(uname -m)" in
    aarch64|arm64) ;;
    armv6l)
        die "ARMv6 ($(uname -m)) n'est pas pris en charge par .NET : Raspberry Pi 1, Zero et Zero W sont exclus."
        ;;
    armv7l|armhf)
        die "système 32 bits détecté ($(uname -m)) : installez Raspberry Pi OS 64 bits, ou restez sur la version Python."
        ;;
    x86_64)
        warn "architecture $(uname -m) : l'artefact publié est arm64, l'installation va échouer."
        warn "Utilisez « sudo ./scripts/install.sh ./chemin/vers/artefact-x64.tar.gz » avec un artefact adapté."
        ;;
    *)
        die "architecture non prise en charge : $(uname -m)."
        ;;
esac

# --- 1-Wire -----------------------------------------------------------------

enable_one_wire() {
    local config
    for config in /boot/firmware/config.txt /boot/config.txt; do
        [[ -f $config ]] || continue
        if grep -qE '^\s*dtoverlay=w1-gpio' "$config"; then
            info "1-Wire déjà activé dans $config."
        else
            info "Activation du 1-Wire dans $config (GPIO 4)."
            printf '\n# Ajouté par tempi : bus 1-Wire pour le capteur DS18B20\ndtoverlay=w1-gpio\n' >> "$config"
            warn "Un redémarrage sera nécessaire pour que le capteur soit détecté."
        fi
        return 0
    done
    warn "Aucun config.txt trouvé : activez le 1-Wire manuellement (raspi-config > Interface Options > 1-Wire)."
}

enable_one_wire

# Charge les modules immédiatement : évite d'attendre le redémarrage quand
# l'overlay était déjà en place.
modprobe w1-gpio 2>/dev/null || true
modprobe w1-therm 2>/dev/null || true

# --- Récupération de l'artefact ---------------------------------------------

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

resolve_artifact() {
    local requested="${1:-}"

    # Un chemin de fichier : installation hors ligne. C'est ce qui remplace la
    # propriété qu'avait l'ancienne installation par pip, qui ne demandait aucun
    # accès réseau puisque le paquet n'avait aucune dépendance.
    if [[ -n $requested && -f $requested ]]; then
        info "Artefact local : $requested"
        printf '%s' "$requested"
        return 0
    fi

    command -v curl >/dev/null || die "curl est requis pour télécharger une release."

    local tag="$requested"
    if [[ -z $tag ]]; then
        info "Recherche de la dernière version publiée." >&2
        tag="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
               | grep -oE '"tag_name": *"[^"]+"' | head -1 | grep -oE '"[^"]+"$' | tr -d '"')" \
            || die "impossible d'interroger les releases de $REPO."
        [[ -n $tag ]] || die "aucune version publiée trouvée pour $REPO."
    fi

    local name="tempi-$tag-$RID.tar.gz"
    local base="https://github.com/$REPO/releases/download/$tag"

    info "Téléchargement de $name." >&2
    curl -fsSL -o "$WORK/$name" "$base/$name" \
        || die "téléchargement de $name impossible (la version $tag existe-t-elle ?)."
    curl -fsSL -o "$WORK/SHA256SUMS" "$base/SHA256SUMS" \
        || die "empreintes introuvables pour $tag."

    if command -v sha256sum >/dev/null; then
        info "Vérification de l'empreinte." >&2
        (cd "$WORK" && sha256sum --check --ignore-missing SHA256SUMS >/dev/null) \
            || die "l'empreinte de $name ne correspond pas : téléchargement corrompu ou altéré."
    else
        warn "sha256sum absent : empreinte non vérifiée."
    fi

    printf '%s' "$WORK/$name"
}

ARTIFACT="$(resolve_artifact "${1:-}")"

# --- Utilisateur système ----------------------------------------------------

if id "$USER_NAME" &>/dev/null; then
    info "L'utilisateur $USER_NAME existe déjà."
else
    info "Création de l'utilisateur système $USER_NAME."
    useradd --system --no-create-home --shell /usr/sbin/nologin "$USER_NAME"
fi

# --- Bascule depuis l'installation Python -----------------------------------

if systemctl is-active --quiet "$SERVICE"; then was_active=yes; else was_active=no; fi

if [[ -d $PREFIX/venv ]]; then
    info "Installation Python détectée : arrêt du service avant remplacement."
    systemctl stop "$SERVICE" 2>/dev/null || true
    info "Suppression de $PREFIX/venv."
    # La base de mesures est dans /var/lib/tempi et la configuration dans
    # /etc/tempi : ni l'une ni l'autre n'est touchée. Le format du fichier SQLite
    # est identique, il n'y a rien à convertir.
    rm -rf "$PREFIX/venv"
fi

# --- Installation -----------------------------------------------------------

info "Installation du binaire dans $BIN_DIR."
rm -rf "$PREFIX/bin.new"
install -d "$PREFIX/bin.new"
tar -C "$PREFIX/bin.new" -xzf "$ARTIFACT" || die "archive illisible : $ARTIFACT"
[[ -f $PREFIX/bin.new/tempi ]] || die "l'archive ne contient pas d'exécutable « tempi »."
chmod +x "$PREFIX/bin.new/tempi"

# Remplacement en deux temps : à aucun moment $BIN_DIR n'est dans un état
# partiel, et l'ancienne version reste disponible jusqu'au dernier instant.
rm -rf "$PREFIX/bin.old"
[[ -d $BIN_DIR ]] && mv "$BIN_DIR" "$PREFIX/bin.old"
mv "$PREFIX/bin.new" "$BIN_DIR"
rm -rf "$PREFIX/bin.old"

# Sans ce lien, la commande documentée « tempi sensors » répond
# « command not found ».
if [[ -e $BIN_LINK && ! -L $BIN_LINK ]]; then
    warn "$BIN_LINK existe déjà et n'est pas un lien symbolique : commande non installée."
    warn "Utilisez $BIN_DIR/tempi, ou supprimez ce fichier et relancez."
else
    ln -sfn "$BIN_DIR/tempi" "$BIN_LINK"
    info "Commande disponible : tempi ($("$BIN_DIR/tempi" --version))"
fi

# --- Configuration ----------------------------------------------------------

mkdir -p "$CONFIG_DIR"
if [[ -f $CONFIG_DIR/tempi.env ]]; then
    info "Configuration existante conservée : $CONFIG_DIR/tempi.env"
else
    info "Installation de la configuration par défaut dans $CONFIG_DIR/tempi.env."
    install -m 0644 "$REPO_DIR/deploy/tempi.env.example" "$CONFIG_DIR/tempi.env"
fi

# --- Service ----------------------------------------------------------------

info "Installation de $SERVICE."
install -m 0644 "$REPO_DIR/deploy/$SERVICE" "/etc/systemd/system/$SERVICE"
systemctl daemon-reload
systemctl enable --quiet "$SERVICE"

if [[ $was_active == yes ]]; then
    # Redémarrage explicite : « enable --now » ne fait rien sur une unité déjà
    # active, si bien qu'une mise à jour laisserait tourner l'ancien code.
    info "Redémarrage du service pour charger la nouvelle version."
    systemctl restart "$SERVICE"
else
    systemctl start "$SERVICE"
fi

sleep 2
if systemctl is-active --quiet "$SERVICE"; then
    port="$(grep -oE '^[[:space:]]*TEMPI_PORT=[0-9]+' "$CONFIG_DIR/tempi.env" 2>/dev/null | grep -oE '[0-9]+$' || echo 8080)"
    host="$(grep -oE '^[[:space:]]*TEMPI_HOST=[^[:space:]]+' "$CONFIG_DIR/tempi.env" 2>/dev/null | cut -d= -f2 || echo 127.0.0.1)"
    info "tempi est actif."
    if [[ $host == "0.0.0.0" || $host == "::" ]]; then
        info "Interface : http://$(hostname -I | awk '{print $1}'):$port/"
    else
        info "Interface : http://127.0.0.1:$port/ (accessible depuis le Raspberry Pi uniquement)."
        info "Pour l'ouvrir au réseau local : TEMPI_HOST=0.0.0.0 dans $CONFIG_DIR/tempi.env"
    fi
    info "Journal : journalctl -u $SERVICE -f"
else
    warn "Le service n'a pas démarré. Diagnostic : journalctl -u $SERVICE -n 50"
    warn "Puis : sudo -u $USER_NAME $BIN_DIR/tempi doctor"
    exit 1
fi

if ! ls /sys/bus/w1/devices/28-* &>/dev/null; then
    warn "Aucun capteur DS18B20 détecté pour l'instant."
    warn "Vérifiez le câblage et la résistance de tirage de 4,7 kΩ, puis redémarrez."
fi
