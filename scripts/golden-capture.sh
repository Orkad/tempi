#!/usr/bin/env bash
#
# Capture le comportement observable de tempi dans des fichiers de référence.
# Les fichiers versionnés sous tests/golden/expected/ ont été produits par
# l'implémentation Python d'origine : les rejouer, c'est vérifier qu'aucun
# changement n'a déplacé un octet de l'API ou de la ligne de commande.
#
#   scripts/golden-capture.sh                    # -> tests/golden/actual/
#   scripts/golden-capture.sh --out /tmp/x
#
# Les réponses sont normalisées : tout ce qui dépend de l'instant ou de la machine
# (« now », chemin et taille de la base) est remplacé par un jeton. Aucune requête
# n'utilise « range= » seul, qui serait relatif à l'heure courante — les fenêtres
# sont toujours données en « from »/« to » explicites.
#
# La température extérieure reste désactivée : sans TEMPI_OUTDOOR_PROVIDER, tempi
# n'émet aucune requête réseau. Le pseudo-capteur présent dans la base de référence
# suffit à couvrir son passage dans l'API.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REFERENCE="$ROOT/tests/golden/reference.db"
PORT="${TEMPI_GOLDEN_PORT:-18471}"

# Fenêtre figée par tests/golden/make_reference.py.
FROM=1767225600
TO=1767398400

OUT=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dotnet) ;;   # accepté par compatibilité, il n'y a plus qu'une implémentation
        --out)    OUT="$2"; shift ;;
        *) echo "option inconnue : $1" >&2; exit 2 ;;
    esac
    shift
done

[[ -f $REFERENCE ]] || { echo "base de référence absente : $REFERENCE" >&2; exit 2; }
[[ -n $OUT ]] || OUT="$ROOT/tests/golden/actual"

# La commande à exercer. Surchargeable pour pointer un binaire publié.
# shellcheck disable=SC2206  # le découpage en mots est voulu : TEMPI_CMD porte une commande
TEMPI_BIN=(${TEMPI_CMD:-$ROOT/artifacts/tempi})
[[ -x ${TEMPI_BIN[0]} ]] || { echo "binaire introuvable : ${TEMPI_BIN[0]}" >&2; exit 2; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; [[ -n ${SRV:-} ]] && kill "$SRV" 2>/dev/null || true' EXIT

rm -rf "$OUT"
mkdir -p "$OUT/api" "$OUT/cli" "$OUT/machine"

# Environnement neutre : fuseau fixe, aucune source extérieure, aucun bus 1-Wire.
export TZ=UTC
export LC_ALL=C.UTF-8
export TEMPI_W1_DIR=/nonexistent
export TEMPI_SIMULATE=0
unset TEMPI_OUTDOOR_PROVIDER TEMPI_OUTDOOR_STATION TEMPI_OUTDOOR_TOKEN 2>/dev/null || true

normalise() {
    # Neutralise les champs volatils d'une réponse JSON.
    python3 -c '
import re, sys
body = sys.stdin.read()
body = re.sub(r"\"now\":\s*\d+", "\"now\":\"<now>\"", body)
body = re.sub(r"\"db_path\":\s*\"[^\"]*\"", "\"db_path\":\"<db_path>\"", body)
body = re.sub(r"\"db_bytes\":\s*\d+", "\"db_bytes\":\"<db_bytes>\"", body)
sys.stdout.write(body)
'
}

# ---------------------------------------------------------------- API HTTP ---

DB="$WORK/api.db"
cp "$REFERENCE" "$DB"

TEMPI_DB="$DB" TEMPI_PORT="$PORT" TEMPI_HOST=127.0.0.1 \
    "${TEMPI_BIN[@]}" serve >"$WORK/serve.log" 2>&1 &
SRV=$!

for _ in $(seq 1 80); do
    curl -sf -o /dev/null "http://127.0.0.1:$PORT/api/health" && break
    sleep 0.25
done
curl -sf -o /dev/null "http://127.0.0.1:$PORT/api/health" \
    || { echo "le serveur n'a pas démarré :" >&2; cat "$WORK/serve.log" >&2; exit 1; }

W="from=$FROM&to=$TO"

# nom -> chemin+requête. Le nom devient celui du fichier de référence.
declare -a CASES=(
  "health|/api/health"
  "sensors|/api/sensors"
  "latest|/api/latest"
  "series-raw|/api/series?$W&bucket=raw"
  "series-auto|/api/series?$W"
  "series-3600|/api/series?$W&bucket=3600"
  "series-10m|/api/series?$W&bucket=10m"
  "series-un-capteur|/api/series?$W&sensor=28-000005e2fdc3"
  "series-deux-capteurs|/api/series?$W&sensor=28-000005e2fdc3&sensor=outdoor-metar-LFLY"
  "series-bucket-vide|/api/series?$W&bucket="
  "series-range-vide|/api/series?$W&range="
  "series-sensor-inconnu|/api/series?$W&sensor=28-inexistant"
  "series-fenetre-vide|/api/series?from=1000&to=2000"
  "summary|/api/summary?$W"
  "summary-un-capteur|/api/summary?$W&sensor=28-000005e30a1b"
  "sensors-slash-final|/api/sensors/"
  "erreur-duree|/api/series?range=demain"
  "erreur-date|/api/series?from=hier&to=$TO"
  "erreur-bornes-inversees|/api/series?from=$TO&to=$FROM"
  "erreur-route-inconnue|/api/inexistant"
)

for entry in "${CASES[@]}"; do
    name="${entry%%|*}"
    path="${entry#*|}"
    curl -s -D "$WORK/h" -o "$WORK/b" "http://127.0.0.1:$PORT$path"
    {
        head -1 "$WORK/h" | tr -d '\r' | awk '{print $2}'
        grep -iE '^(content-type|cache-control|content-disposition):' "$WORK/h" \
            | tr -d '\r' | tr 'A-Z' 'a-z' | sort
    } > "$OUT/api/$name.headers"
    normalise < "$WORK/b" > "$OUT/api/$name.json"
done

# Export CSV : corps binaire conservé tel quel (fins de ligne comprises).
for entry in "export-complet|/api/export.csv?$W" \
             "export-un-capteur|/api/export.csv?$W&sensor=outdoor-metar-LFLY" \
             "export-alias|/api/export?$W&sensor=28-0000ffffffff"; do
    name="${entry%%|*}"; path="${entry#*|}"
    curl -s -D "$WORK/h" -o "$OUT/api/$name.csv" "http://127.0.0.1:$PORT$path"
    {
        head -1 "$WORK/h" | tr -d '\r' | awk '{print $2}'
        grep -iE '^(content-type|cache-control|content-disposition):' "$WORK/h" \
            | tr -d '\r' | tr 'A-Z' 'a-z' | sort
    } > "$OUT/api/$name.headers"
done

# HEAD doit rendre les mêmes en-têtes que GET, sans corps.
curl -s -I -o /dev/null -D "$WORK/h" "http://127.0.0.1:$PORT/api/latest"
{
    head -1 "$WORK/h" | tr -d '\r' | awk '{print $2}'
    grep -iE '^(content-type|cache-control):' "$WORK/h" | tr -d '\r' | tr 'A-Z' 'a-z' | sort
} > "$OUT/api/head-latest.headers"

# Page d'accueil : on ne fige que sa taille et son type, pas son contenu.
curl -s -D "$WORK/h" -o "$WORK/b" "http://127.0.0.1:$PORT/"
{
    head -1 "$WORK/h" | tr -d '\r' | awk '{print $2}'
    grep -iE '^(content-type|cache-control):' "$WORK/h" | tr -d '\r' | tr 'A-Z' 'a-z' | sort
    echo "octets $(wc -c < "$WORK/b")"
} > "$OUT/api/index.headers"

# Renommage : mutation, donc joué en dernier et suivi de son effet.
curl -s -o "$WORK/b" -X POST -H 'Content-Type: application/json' \
    -d '{"label":"Congélateur"}' \
    "http://127.0.0.1:$PORT/api/sensors/28-000005e30a1b/label"
normalise < "$WORK/b" > "$OUT/api/label-pose.json"
curl -s -o "$WORK/b" -X POST -H 'Content-Type: application/json' \
    -d '{"label":"Jardin"}' \
    "http://127.0.0.1:$PORT/api/sensors/28-zzzz/label"
normalise < "$WORK/b" > "$OUT/api/label-capteur-inconnu.json"
curl -s "http://127.0.0.1:$PORT/api/sensors" | normalise > "$OUT/api/sensors-apres-renommage.json"

kill "$SRV" 2>/dev/null || true
wait "$SRV" 2>/dev/null || true
SRV=""

# --------------------------------------------------------------------- CLI ---

# Retire d'une sortie ce qui varie d'une exécution à l'autre : chemins temporaires
# et horodatage des lignes de journal (format « %Y-%m-%dT%H:%M:%S » en tête de ligne).
#
# Le bloc d'usage d'argparse est également retiré. C'est une divergence assumée : le
# reproduire en C# reviendrait à coder en dur la mise en forme d'argparse — un
# littéral déguisé en sortie générée, qui se périmerait au premier ajout de
# sous-commande. Le contrat conservé est celui qui compte pour qui scripte la
# commande : le code de retour et la ligne « tempi: error: … ». La suppression ne
# s'applique qu'entre « usage: tempi » et cette ligne, pour ne pas toucher aux
# sorties indentées légitimes comme celle de « stats ».
scrub() {
    sed -E "s#$2#<db>#g; s#$WORK#<tmp>#g; s#^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}#<ts>#" "$1" \
        | awk '/^usage: tempi/ { skip = 1 } /^tempi: error:/ { skip = 0 } !skip'
}

run_cli() {
    local name="$1"; shift
    local db="$WORK/cli.db"
    cp "$REFERENCE" "$db"
    local rc=0
    TEMPI_DB="$db" "${TEMPI_BIN[@]}" "$@" >"$WORK/out" 2>"$WORK/err" || rc=$?
    {
        echo "\$ tempi $*"
        echo "--- code de retour ---"
        echo "$rc"
        echo "--- stdout ---"
        scrub "$WORK/out" "$db"
        echo "--- stderr ---"
        scrub "$WORK/err" "$db"
    } > "$OUT/cli/$name.txt"
}

run_cli version --version
run_cli stats stats
run_cli sensors sensors
run_cli read read
run_cli outdoor-sans-source outdoor
run_cli prune-sans-retention prune
run_cli export export --from "$FROM" --to "$TO" --sensor outdoor-metar-LFLY
run_cli export-tout export --from "$FROM" --to "$TO"
run_cli label label 28-000005e2fdc3 "Séjour"
run_cli label-efface label 28-000005e2fdc3
run_cli label-inconnu label 28-absent "Test"
run_cli collect-trois-cycles --simulate collect -n 3 --interval 1
run_cli erreur-intervalle collect --interval 0

# `doctor` interroge la machine (config.txt, /proc/modules, pinctrl, systemctl) :
# sa sortie dépend de l'hôte. On la capture pour comparer Python et .NET dans la
# même exécution, mais elle n'a pas de sens versionnée — d'où le dossier à part.
run_cli_machine() {
    local name="$1"; shift
    local db="$WORK/cli.db"
    cp "$REFERENCE" "$db"
    local rc=0
    TEMPI_DB="$db" "${TEMPI_BIN[@]}" "$@" >"$WORK/out" 2>"$WORK/err" || rc=$?
    {
        echo "code=$rc"
        scrub "$WORK/out" "$db"
    } > "$OUT/machine/$name.txt"
}
run_cli_machine doctor-json doctor --json
run_cli_machine doctor-texte doctor

echo "capture écrite dans $OUT"
