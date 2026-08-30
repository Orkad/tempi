#!/usr/bin/env bash
#
# Met à jour tempi vers la dernière version publiée.
#
#   ~/tempi/scripts/update.sh                  # dernière version publiée
#   ~/tempi/scripts/update.sh v1.2.0           # une version précise
#   ~/tempi/scripts/update.sh ./tempi.tar.gz   # artefact local, sans réseau
#   ~/tempi/scripts/update.sh --force          # réinstalle même si à jour
#
# À lancer SANS sudo : le script appelle sudo lui-même pour la partie qui en a
# besoin. Il ne touche ni à la base de mesures ni à /etc/tempi/tempi.env.
#
# La comparaison de version interroge l'API publique des releases.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN=/opt/tempi/bin/tempi

# shellcheck source=scripts/_github.sh
. "$(dirname "${BASH_SOURCE[0]}")/_github.sh"
GH_TOKEN_VALUE="$(github_token)"

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m/!\\\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mErreur :\033[0m %s\n' "$*" >&2; exit 1; }

FORCE=no
REQUESTED=""
for arg in "$@"; do
    case "$arg" in
        --force) FORCE=yes ;;
        -h|--help) sed -n '2,11p' "${BASH_SOURCE[0]}" | sed 's/^# \?//'; exit 0 ;;
        -*) die "option inconnue : $arg" ;;
        *)  [[ -z $REQUESTED ]] || die "un seul argument de version ou d'artefact attendu."
            REQUESTED="$arg" ;;
    esac
done

[[ $EUID -ne 0 ]] || die "lancez ce script sans sudo (voir l'en-tête du fichier)."

# sudo repart d'un environnement vide : un jeton donné par l'environnement doit
# être transmis explicitement, sans passer par la ligne de commande où « ps » le
# rendrait visible. Un jeton déposé dans un fichier n'a besoin de rien : install.sh
# le relit lui-même, dans le dossier de l'utilisateur qui a appelé sudo.
run_install() {
    if [[ -n ${GITHUB_TOKEN:-} ]]; then
        exec sudo --preserve-env=GITHUB_TOKEN "$REPO_DIR/scripts/install.sh" "$@"
    fi
    exec sudo "$REPO_DIR/scripts/install.sh" "$@"
}

# --- Version installée ------------------------------------------------------

# Le lien /usr/local/bin/tempi peut manquer (voir install.sh) sans que
# l'installation soit cassée : on interroge le binaire à sa place réelle.
installed=""
if [[ -x $BIN ]]; then
    installed="$("$BIN" --version 2>/dev/null | awk '{print $NF}')" || true
fi

if [[ -z $installed ]]; then
    warn "Aucune installation détectée dans $BIN."
    info "Première installation : sudo $REPO_DIR/scripts/install.sh"
    exit 1
fi

info "Version installée : $installed"

# --- Version visée ----------------------------------------------------------

# Un artefact local court-circuite la comparaison : on ne sait pas quelle version
# il contient sans l'extraire, et l'installation hors ligne doit rester possible
# quand l'API GitHub est injoignable.
if [[ -n $REQUESTED && -f $REQUESTED ]]; then
    info "Artefact local : $REQUESTED"
    info "Installation."
    run_install "$REQUESTED"
fi

target="$REQUESTED"
if [[ -z $target ]]; then
    command -v curl >/dev/null || die "curl est requis pour interroger les releases."
    info "Recherche de la dernière version publiée."
    release="$(gh_release_json "")" \
        || die "impossible d'interroger les releases (voir la ligne ci-dessus)."
    target="$(gh_json_string "$release" tag_name)"
    [[ -n $target ]] || die "aucune version publiée trouvée pour $TEMPI_REPO."
fi

# Le tag porte un « v », la sortie de « tempi --version » non.
info "Dernière version publiée : $target"

if [[ ${target#v} == "$installed" && $FORCE == no ]]; then
    info "Déjà à jour. Rien à faire."
    exit 0
fi

if [[ ${target#v} == "$installed" ]]; then
    info "Réinstallation de $target (--force)."
else
    info "Mise à jour $installed → ${target#v}."
fi

# install.sh fait le reste : téléchargement, vérification de l'empreinte,
# remplacement atomique du binaire et redémarrage du service.
run_install "$target"
