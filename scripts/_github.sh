#!/usr/bin/env bash
#
# Accès aux releases GitHub, partagé par install.sh et update.sh.
#
# Le dépôt est privé : sans jeton, l'API comme les artefacts répondent 404. Le
# jeton est cherché, dans cet ordre :
#
#   1. la variable d'environnement GITHUB_TOKEN ;
#   2. ~/.config/tempi/github-token — celui de l'utilisateur qui a appelé sudo,
#      pas celui de root, pour que les deux scripts trouvent le même fichier ;
#   3. /etc/tempi/github-token, pour une installation sans utilisateur derrière ;
#   4. celui que git conserve déjà pour github.com.
#
# Le quatrième cas est le cas normal sur le Raspberry Pi : cloner un dépôt privé
# demande déjà un jeton, et celui-là a exactement la portée qu'il faut ici. Il
# n'y a donc rien à installer de plus.
#
# Aucun jeton n'est une situation valide : les appels restent anonymes, ce qui
# suffirait si le dépôt devenait public.
#
# Le jeton n'a besoin que de la lecture du contenu : un jeton à portée fine avec
# « Contents: read » sur ce dépôt, ou un jeton classique de portée « repo ».

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
gh_curl() {
    local accept="$1" url="$2"
    shift 2

    local -a auth=()
    [[ -n ${GH_TOKEN_VALUE:-} ]] && auth=(-H "Authorization: Bearer $GH_TOKEN_VALUE")

    curl -fsSL "${auth[@]}" \
        -H "Accept: $accept" \
        -H "X-GitHub-Api-Version: 2022-11-28" \
        "$@" "$url"
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
