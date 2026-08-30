#!/usr/bin/env bash
#
# Accès aux releases GitHub, partagé par install.sh et update.sh.
#
# Le dépôt est public : les appels sont anonymes. Un jeton reste repris s'il y en
# a un — GITHUB_TOKEN, ~/.config/tempi/github-token, /etc/tempi/github-token, ou
# celui que git conserve pour github.com — ce qui couvre un retour au privé et
# relève au passage le quota de l'API.

GITHUB_API=https://api.github.com
TEMPI_REPO=Orkad/tempi

# Le jeton, ou une chaîne vide. Les scripts le mettent dans GH_TOKEN_VALUE.
github_token() {
    if [[ -n ${GITHUB_TOKEN:-} ]]; then
        printf '%s' "$GITHUB_TOKEN"
        return 0
    fi

    local home candidate
    home="${HOME:-/root}"
    if [[ -n ${SUDO_USER:-} ]]; then
        home="$(getent passwd "$SUDO_USER" | cut -d: -f6)"
        [[ -n $home ]] || home="/home/$SUDO_USER"
    fi

    for candidate in "$home/.config/tempi/github-token" /etc/tempi/github-token; do
        if [[ -r $candidate ]]; then
            tr -d ' \t\n\r' < "$candidate"
            return 0
        fi
    done

    # Le jeton que git garde pour github.com — celui du clonage. GIT_TERMINAL_PROMPT
    # et GIT_ASKPASS coupent toute interaction : sans identifiant enregistré, git
    # doit échouer, pas poser une question au milieu d'un script lancé par sudo.
    command -v git >/dev/null || return 0
    local answer
    answer="$(printf 'protocol=https\nhost=github.com\n\n' \
        | HOME="$home" GIT_TERMINAL_PROMPT=0 GIT_ASKPASS=/bin/true \
          git credential fill 2>/dev/null)" || return 0
    printf '%s' "$answer" | sed -n 's/^password=//p' | head -1
}

# gh_curl <type accepté> <url> [arguments curl…]
#
# curl moderne retire l'en-tête Authorization quand une redirection change
# d'hôte. C'est exactement ce qu'il faut ici : l'API redirige vers une URL signée
# qui refuse un second mécanisme d'authentification.
#
# Le code HTTP est signalé sur la sortie d'erreur avant de rendre la main : un
# script qui dit seulement « impossible d'interroger l'API » n'apprend rien,
# alors que 401, 404 ou 403 disent chacun quoi corriger. Le message est écrit
# ici, et pas remonté dans une variable, parce que les appelants travaillent en
# substitution de commande — un sous-shell, d'où aucune variable ne ressort.
gh_curl() {
    local accept="$1" url="$2"
    shift 2

    local -a auth=()
    [[ -n ${GH_TOKEN_VALUE:-} ]] && auth=(-H "Authorization: Bearer $GH_TOKEN_VALUE")

    # -w ajoute le code en dernière ligne. Avec « -o », le corps part dans le
    # fichier et il ne reste que ce code : les deux usages se traitent pareil.
    local out status
    out="$(curl -sSL -w '\n%{http_code}' "${auth[@]}" \
        -H "Accept: $accept" \
        -H "X-GitHub-Api-Version: 2022-11-28" \
        "$@" "$url")" || { gh_explain "" "$url" >&2; return 1; }

    status="${out##*$'\n'}"
    if [[ $status != 2* ]]; then
        gh_explain "$status" "$url" >&2
        return 1
    fi

    printf '%s' "${out%$'\n'*}"
}

# Ce que le code veut dire, en clair.
gh_explain() {
    local message
    case "$1" in
        401) message="jeton refusé (expiré, ou mal recopié)" ;;
        403) message="accès interdit : quota dépassé, ou jeton sans droit de lecture sur ce dépôt" ;;
        404) message="introuvable : jeton absent, dépôt hors de portée du jeton, ou version inexistante" ;;
        "")  message="pas de réponse : réseau ou résolution de noms" ;;
        *)   message="code HTTP $1" ;;
    esac
    printf 'GitHub : %s\n  %s\n' "$message" "$2"
}

# gh_release_json [tag] — la dernière release publiée, ou celle d'un tag donné.
gh_release_json() {
    local tag="${1:-}"
    if [[ -n $tag ]]; then
        gh_curl application/vnd.github+json "$GITHUB_API/repos/$TEMPI_REPO/releases/tags/$tag"
    else
        gh_curl application/vnd.github+json "$GITHUB_API/repos/$TEMPI_REPO/releases/latest"
    fi
}

# gh_json_string <json> <clé> — la première valeur de type chaîne portant cette clé.
gh_json_string() {
    printf '%s' "$1" \
        | grep -oE "\"$2\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
        | head -1 \
        | sed -E 's/.*"([^"]*)"$/\1/'
}

# gh_asset_id <json de la liste des artefacts> <nom> — l'identifiant numérique.
#
# On passe par la liste des artefacts plutôt que par la release elle-même : elle
# ne contient aucun texte libre — pas de notes de version — donc la découper
# objet par objet est sûr. L'identifiant du téléverseur suit celui de l'artefact
# dans chaque objet, d'où le « head -1 ».
gh_asset_id() {
    printf '%s' "$1" \
        | tr -d ' \n\r' \
        | sed 's/},{/}\n{/g' \
        | grep -F "\"name\":\"$2\"" \
        | grep -oE '"id":[0-9]+' \
        | head -1 \
        | cut -d: -f2
}

# gh_download_asset <url de la liste des artefacts> <nom> <destination>
#
# L'URL « releases/download/… » ne s'authentifie pas : sur un dépôt privé, un
# artefact se télécharge par son identifiant, en demandant l'octet-stream.
gh_download_asset() {
    local assets id
    assets="$(gh_curl application/vnd.github+json "$1")" || return 1
    id="$(gh_asset_id "$assets" "$2")"
    [[ -n $id ]] || return 1
    gh_curl application/octet-stream "$GITHUB_API/repos/$TEMPI_REPO/releases/assets/$id" -o "$3"
}
