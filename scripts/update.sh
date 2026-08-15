#!/usr/bin/env bash
#
# Met à jour tempi depuis GitHub et redémarre le service.
#
#   ~/tempi/scripts/update.sh
#
# À lancer SANS sudo : le dépôt et les identifiants GitHub appartiennent à
# votre compte, pas à root. Le script appelle sudo lui-même pour la partie
# qui en a besoin.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31mErreur :\033[0m %s\n' "$*" >&2; exit 1; }

[[ $EUID -ne 0 ]] || die "lancez ce script sans sudo (voir l'en-tête du fichier)."
[[ -d $REPO_DIR/.git ]] || die "$REPO_DIR n'est pas un dépôt git : mise à jour impossible."

cd "$REPO_DIR"

before="$(git rev-parse HEAD)"

info "Récupération des modifications."
# --ff-only : on refuse de fusionner. Si le dépôt local a divergé, mieux vaut
# s'arrêter et laisser l'utilisateur décider que produire un commit surprise.
git pull --ff-only || die "le dépôt local a divergé : résolvez-le avant de relancer."

after="$(git rev-parse HEAD)"

if [[ $before == "$after" ]]; then
    info "Déjà à jour ($(git rev-parse --short HEAD)). Rien à faire."
    exit 0
fi

info "Mise à jour $(git rev-parse --short "$before") → $(git rev-parse --short "$after")."
git --no-pager log --oneline "$before..$after" | sed 's/^/    /'

info "Réinstallation et redémarrage du service."
sudo "$REPO_DIR/scripts/install.sh"
