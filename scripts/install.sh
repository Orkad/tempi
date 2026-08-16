#!/usr/bin/env bash
#
# Installe tempi comme service systemd sur un Raspberry Pi.
#
#   sudo ./scripts/install.sh
#
# Le script est idempotent : on peut le relancer pour mettre à jour.

set -euo pipefail

PREFIX=/opt/tempi
BIN_LINK=/usr/local/bin/tempi
CONFIG_DIR=/etc/tempi
SERVICE=tempi.service
USER_NAME=tempi
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m/!\\\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mErreur :\033[0m %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "ce script doit être lancé avec sudo."
command -v systemctl >/dev/null || die "systemd est requis."
command -v python3 >/dev/null || die "python3 est requis."

python3 - <<'PY' || die "Python 3.9 ou plus récent est requis."
import sys
raise SystemExit(0 if sys.version_info >= (3, 9) else 1)
PY

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

# --- Utilisateur système ----------------------------------------------------

if id "$USER_NAME" &>/dev/null; then
    info "L'utilisateur $USER_NAME existe déjà."
else
    info "Création de l'utilisateur système $USER_NAME."
    useradd --system --no-create-home --shell /usr/sbin/nologin "$USER_NAME"
fi

# --- Installation -----------------------------------------------------------

if [[ ! -x $PREFIX/venv/bin/python ]]; then
    info "Création de l'environnement virtuel dans $PREFIX/venv."
    mkdir -p "$PREFIX"
    python3 -m venv "$PREFIX/venv" \
        || die "python3-venv est manquant : sudo apt install python3-venv"
fi

info "Installation de tempi depuis $REPO_DIR."
"$PREFIX/venv/bin/pip" install --quiet --upgrade "$REPO_DIR"

# L'environnement virtuel n'est pas dans le PATH : sans ce lien, la commande
# documentée « tempi sensors » répond « command not found ».
if [[ -e $BIN_LINK && ! -L $BIN_LINK ]]; then
    warn "$BIN_LINK existe déjà et n'est pas un lien symbolique : commande non installée."
    warn "Utilisez $PREFIX/venv/bin/tempi, ou supprimez ce fichier et relancez."
else
    ln -sfn "$PREFIX/venv/bin/tempi" "$BIN_LINK"
    info "Commande disponible : tempi ($BIN_LINK)"
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

if systemctl is-active --quiet "$SERVICE"; then was_active=yes; else was_active=no; fi

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
    port="$(grep -oP '^\s*TEMPI_PORT=\K\d+' "$CONFIG_DIR/tempi.env" 2>/dev/null || echo 8080)"
    host="$(grep -oP '^\s*TEMPI_HOST=\K\S+' "$CONFIG_DIR/tempi.env" 2>/dev/null || echo 127.0.0.1)"
    # Le service sert en TLS dès qu'un certificat est configuré : annoncer
    # http:// dans ce cas enverrait sur une adresse qui ne répond pas.
    if grep -qE '^\s*TEMPI_TLS_CERT=\S' "$CONFIG_DIR/tempi.env" 2>/dev/null; then
        scheme=https
    else
        scheme=http
    fi
    info "tempi est actif."
    if [[ $host == "0.0.0.0" || $host == "::" ]]; then
        info "Interface : $scheme://$(hostname -I | awk '{print $1}'):$port/"
    else
        info "Interface : $scheme://127.0.0.1:$port/ (accessible depuis le Raspberry Pi uniquement)."
        info "Pour l'ouvrir au réseau local : TEMPI_HOST=0.0.0.0 dans $CONFIG_DIR/tempi.env"
    fi
    if [[ $scheme == http ]]; then
        info "Pour chiffrer la connexion (HTTPS) : sudo tempi cert"
    fi
    info "Journal : journalctl -u $SERVICE -f"
else
    warn "Le service n'a pas démarré. Diagnostic : journalctl -u $SERVICE -n 50"
    exit 1
fi

if ! ls /sys/bus/w1/devices/28-* &>/dev/null; then
    warn "Aucun capteur DS18B20 détecté pour l'instant."
    warn "Vérifiez le câblage et la résistance de tirage de 4,7 kΩ, puis redémarrez."
fi
