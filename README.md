# tempi

Enregistrement et visualisation de l'évolution de la température mesurée par un
ou plusieurs capteurs **DS18B20** sur un **Raspberry Pi**.

- collecte périodique, tolérante aux erreurs de lecture du bus 1-Wire ;
- stockage dans une base **SQLite** unique, sans serveur à administrer ;
- interface web avec graphique, plages de 1 heure à « tout l'historique »,
  statistiques et export CSV ;
- **aucune dépendance externe** : uniquement la bibliothèque standard de Python,
  donc pas de compilation ni de `pip install` d'un paquet lourd sur le Pi ;
- **HTTPS** en option, sans reverse proxy : un certificat, une clé, c'est tout ;
- service `systemd` prêt à l'emploi.

![Interface web de tempi](docs/capture.png)

---

## 1. Câblage

Le DS18B20 se branche sur le bus 1-Wire, par défaut sur le **GPIO 4**
(broche physique 7).

![Câblage du DS18B20 sur le Raspberry Pi](docs/cablage.svg)

Le même montage sur une mini-platine d'essai, trou par trou :

![Montage sur breadboard](docs/breadboard.svg)

**Une résistance de tirage de 4,7 kΩ entre DQ et 3,3 V est indispensable** :
sans elle, le bus reste muet ou renvoie des valeurs erratiques. Elle est déjà
intégrée aux modules DS18B20 vendus sur petite carte, mais pas aux composants
nus ni aux sondes étanches.

Plusieurs capteurs se branchent **en parallèle sur les mêmes trois fils**, avec
une seule résistance de tirage pour l'ensemble : chaque DS18B20 possède une
adresse unique gravée en usine, et tempi les découvre automatiquement.

> Le montage « en parasite » (VDD relié à GND) fonctionne mal au-delà de
> quelques dizaines de centimètres. Alimentez le capteur en 3,3 V.

### Précision affichée

Le DS18B20 est donné à **±0,5 °C** entre −10 et +85 °C, pour une résolution de
0,0625 °C. La résolution ne compensant pas l'incertitude, tempi affiche le
dixième de degré : le centième ferait passer du bruit pour une mesure.

Les valeurs restent enregistrées et exposées sans arrondi dans la base, dans
l'API et dans l'export CSV — seul l'affichage est arrondi.

## 2. Activation du bus 1-Wire

```bash
sudo raspi-config     # Interface Options > 1-Wire > Yes
sudo reboot
```

Équivalent manuel — ajouter à `/boot/firmware/config.txt` (Raspberry Pi OS
Bookworm ou plus récent) ou `/boot/config.txt` (versions antérieures) :

```
dtoverlay=w1-gpio
```

Après redémarrage, chaque capteur apparaît comme un répertoire dont le nom
commence par `28-` :

```bash
$ ls /sys/bus/w1/devices/
28-000005e2fdc3  w1_bus_master1
```

Si rien n'apparaît, reprenez le câblage et la résistance de tirage avant toute
autre piste : c'est la cause de loin la plus fréquente.

## 3. Installation

```bash
git clone https://github.com/Orkad/tempi.git ~/tempi
cd ~/tempi
sudo ./scripts/install.sh
```

Le script active le 1-Wire si besoin, crée l'utilisateur système `tempi`,
installe l'application dans `/opt/tempi/venv`, dépose la configuration dans
`/etc/tempi/tempi.env` et démarre le service. Il est idempotent : le relancer
met à jour l'installation sans écraser votre configuration.

L'interface est alors disponible sur <http://127.0.0.1:8080/>. Pour la rendre
accessible depuis le réseau local, voir la section [Accès réseau](#7-accès-réseau).

### Mettre à jour

```bash
~/tempi/scripts/update.sh
```

Récupère les modifications, réinstalle et redémarre le service. À lancer **sans**
`sudo` : le dépôt appartient à votre compte, et le script appelle `sudo` lui-même
pour la seule partie qui en a besoin. Si rien n'a changé, il s'arrête sans
toucher au service.

### Sans installation

Le dépôt fonctionne tel quel, sans être installé :

```bash
python3 -m tempi --simulate run
```

L'option `--simulate` remplace le matériel par un capteur virtuel : pratique
pour découvrir l'interface depuis un poste de développement.

## 4. Utilisation

```bash
tempi doctor                     # diagnostique le bus et nomme la panne
tempi sensors                    # capteurs détectés et capteurs connus de la base
tempi read                       # lecture immédiate, sans rien enregistrer
tempi collect                    # boucle de collecte seule
tempi serve                      # interface web seule
tempi run                        # collecte + interface dans un seul processus
tempi cert                       # certificat pour servir l'interface en HTTPS
tempi label 28-000005e2fdc3 Salon
tempi stats
tempi export --range 30d -o mesures.csv
tempi prune --retention-days 365 --vacuum
```

Exemple :

```
$ tempi sensors
2 capteur(s) détecté(s) sur le bus :
  28-000005e2fdc3    21.4 °C
  28-000005e30a1b     4.8 °C

Capteurs enregistrés dans /var/lib/tempi/tempi.db :
  28-000005e2fdc3 « Salon »  43 210 mesure(s), dernière vue 2026-08-15 10:28:00
  28-000005e30a1b « Cave »   43 210 mesure(s), dernière vue 2026-08-15 10:28:00
```

Un capteur peut aussi être renommé depuis l'interface web, en cliquant sur son
nom dans la carte correspondante.

## 5. Configuration

Toute la configuration passe par des variables d'environnement, surchargeables
par les options de la ligne de commande. Pour le service, éditez
`/etc/tempi/tempi.env` puis `sudo systemctl restart tempi`.

| Variable | Défaut | Rôle |
|---|---|---|
| `TEMPI_DB` | `/var/lib/tempi/tempi.db` | fichier SQLite |
| `TEMPI_INTERVAL` | `60` | secondes entre deux relevés (minimum 1) |
| `TEMPI_MIN_DELTA` | `0` | bande morte en °C (voir ci-dessous) |
| `TEMPI_MAX_INTERVAL` | `900` | durée maximale sans enregistrement malgré la bande morte |
| `TEMPI_RETENTION_DAYS` | `0` | purge automatique, `0` = illimité |
| `TEMPI_HOST` | `127.0.0.1` | adresse d'écoute de l'interface web |
| `TEMPI_PORT` | `8080` | port d'écoute |
| `TEMPI_TLS_CERT` | — | certificat PEM ; active HTTPS (voir [Accès réseau](#7-accès-réseau)) |
| `TEMPI_TLS_KEY` | — | clé privée du certificat |
| `TEMPI_W1_DIR` | `/sys/bus/w1/devices` | répertoire des périphériques 1-Wire |
| `TEMPI_READ_RETRIES` | `3` | tentatives par relevé avant abandon |
| `TEMPI_ALLOW_RESET_VALUE` | `0` | accepter la valeur suspecte de 85 °C |
| `TEMPI_SIMULATE` | `0` | capteur virtuel, sans matériel |

### Bande morte et usure de la carte SD

Une mesure occupe environ 20 octets, soit **10 Mio par an et par capteur** avec
un relevé chaque minute. C'est peu, mais chaque écriture use la carte SD.

`TEMPI_MIN_DELTA` n'enregistre une mesure que si elle s'écarte suffisamment de
la précédente. Avec `TEMPI_MIN_DELTA=0.2` et `TEMPI_MAX_INTERVAL=900`, une pièce
dont la température est stable ne génère qu'un point tous les quarts d'heure,
tout en conservant la pleine résolution lors des variations rapides. Le point
périodique forcé évite les trous de courbe et prouve que le capteur répond
toujours.

`TEMPI_RETENTION_DAYS` supprime en plus les mesures anciennes, une fois par jour.

## 6. API HTTP

| Méthode et route | Description |
|---|---|
| `GET /` | interface web |
| `GET /api/health` | état du service, de la base et du collecteur |
| `GET /api/sensors` | capteurs connus |
| `GET /api/latest` | dernière mesure de chaque capteur |
| `GET /api/series` | points d'une plage, éventuellement agrégés |
| `GET /api/summary` | minimum, moyenne, maximum sur une plage |
| `GET /api/export.csv` | export CSV |
| `POST /api/sensors/<adresse>/label` | renomme un capteur — corps `{"label": "Salon"}` |

Paramètres communs aux routes de consultation :

- `range` — fenêtre relative à maintenant : `30m`, `6h`, `7d`, `2w`, ou `all` ;
- `from` / `to` — bornes explicites, en epoch ou en ISO 8601 ;
- `sensor` — limite à une adresse, répétable ;
- `bucket` — agrégation : `raw` pour les points bruts, une durée (`10m`) ou un
  nombre de secondes. Par défaut, tempi choisit l'agrégation qui ramène la
  réponse à environ 800 points, quelle que soit la longueur de la plage.

Chaque point agrégé porte la moyenne (`celsius`) mais aussi le minimum et le
maximum de sa tranche, si bien que les extrêmes restent visibles même sur un
graphique couvrant plusieurs mois.

```bash
curl 'http://127.0.0.1:8080/api/series?range=24h&bucket=15m' | python3 -m json.tool
curl 'http://127.0.0.1:8080/api/export.csv?range=7d' -o semaine.csv
```

## 7. Accès réseau

Par défaut, l'interface n'écoute que sur `127.0.0.1` : elle n'est joignable que
depuis le Raspberry Pi lui-même. Pour l'ouvrir au réseau local, mettez
`TEMPI_HOST=0.0.0.0` dans `/etc/tempi/tempi.env` puis redémarrez le service.

**tempi n'a aucune authentification.** N'exposez l'interface que sur un réseau
de confiance. Pour un accès depuis l'extérieur, placez-la derrière un reverse
proxy assurant TLS et authentification, ou passez par un VPN.

### HTTPS

tempi sait servir l'interface en TLS. Il lui suffit d'un certificat et de sa
clé, quelle qu'en soit l'origine :

```ini
# /etc/tempi/tempi.env
TEMPI_TLS_CERT=/etc/tempi/tls/tempi-cert.pem
TEMPI_TLS_KEY=/etc/tempi/tls/tempi-key.pem
```

```bash
sudo systemctl restart tempi
sudo tempi doctor          # vérifie le certificat et son expiration
```

Le service bascule alors sur `https://`, sur le même port. Les options
`--tls-cert` et `--tls-key` font la même chose pour un lancement manuel.

Reste à obtenir le certificat, et c'est là que le choix se pose.

> **Le point à comprendre avant de choisir.** Aucune autorité publique
> — Let's Encrypt comprise — n'a le droit d'émettre un certificat pour un nom
> interne (`raspberrypi.local`, `r4.local`) ni pour une adresse privée
> (`192.168.x.x`) : c'est une règle du CA/Browser Forum, appliquée depuis 2015.
> Un navigateur ne fera donc jamais confiance spontanément à un site désigné par
> son nom local. Il faut renoncer à l'une des deux choses : soit installer une
> autorité sur les appareils, soit désigner le Pi par un nom de domaine public.

#### Voie A — autorité locale, on garde l'adresse `.local`

```bash
sudo tempi cert
```

La commande fabrique une petite autorité (`/etc/tempi/tls/ca.pem`) et, signé par
elle, le certificat du serveur — couvrant le nom de la machine, son équivalent
`.local` et ses adresses locales. `--host` et `--ip` permettent d'en ajouter.

Il faut ensuite installer **l'autorité** (`ca.pem`, jamais la clé) sur chaque
appareil : copiez-la par AirDrop, courriel ou clé USB, ouvrez-la, puis

- **iOS** — Réglages > Profil téléchargé > Installer, puis Réglages > Général >
  Informations > Réglages de confiance des certificats : activez
  « tempi local CA ». Les deux étapes sont nécessaires ; la première seule ne
  suffit pas, et iOS ne le signale pas.
- **Android** — Paramètres > Sécurité > Chiffrement > Installer un certificat.
- **macOS** — Trousseau d'accès > Système, double-clic, « Toujours approuver ».

C'est à faire une seule fois par appareil : le certificat du serveur, lui, se
renouvelle sans rien redemander à personne.

```bash
sudo tempi cert --force && sudo systemctl restart tempi
```

Le certificat vaut 397 jours — Safari refuse au-delà de 398. `tempi doctor`
prévient un mois avant l'échéance.

#### Voie B — Let's Encrypt, rien à installer sur les appareils

Il faut un **nom de domaine public**, mais **le site peut rester purement
local** : la validation `DNS-01` se fait par un enregistrement TXT, sans aucune
connexion entrante, et rien ne vous oblige à faire pointer ce nom ailleurs que
vers l'adresse privée du Pi. Un sous-domaine gratuit (DuckDNS) suffit ; un
domaine à vous fonctionne pareil, avec l'API DNS de votre hébergeur.

```bash
# 1. le nom public désigne l'adresse privée du Pi (à mettre dans une tâche cron
#    pour suivre les changements de bail DHCP)
curl "https://www.duckdns.org/update?domains=r4&token=JETON&ip=192.168.1.42"

# 2. certificat par validation DNS, sans ouvrir le moindre port
curl https://get.acme.sh | sh -s email=vous@example.com
export DuckDNS_Token="JETON"
~/.acme.sh/acme.sh --issue --dns dns_duckdns -d r4.duckdns.org --server letsencrypt

# 3. installation, avec renouvellement automatique tous les 60 jours
sudo mkdir -p /etc/tempi/tls
~/.acme.sh/acme.sh --install-cert -d r4.duckdns.org \
    --key-file       /etc/tempi/tls/tempi-key.pem \
    --fullchain-file /etc/tempi/tls/tempi-cert.pem \
    --reloadcmd      "systemctl restart tempi"
```

L'interface répond alors sur `https://r4.duckdns.org:8080/`, avec un cadenas
valide sur n'importe quel appareil, sans installation. L'adresse `.local`
continue de fonctionner en clair pour les usages locaux.

Deux points peuvent gêner : certaines box bloquent un nom public qui répond une
adresse privée (protection « DNS rebinding », à désactiver pour ce nom), et le
Relais privé iCloud d'un iPhone peut perturber la résolution — désactivez-le
pour votre Wi-Fi.

#### Droits de la clé privée

Le service tourne sous l'utilisateur `tempi` et doit pouvoir lire la clé.
`sudo tempi cert` s'en charge ; pour un certificat obtenu autrement :

```bash
sudo chgrp tempi /etc/tempi/tls/tempi-key.pem
sudo chmod 640   /etc/tempi/tls/tempi-key.pem
```

## 8. Exploitation

```bash
sudo systemctl status tempi
sudo journalctl -u tempi -f
curl -s http://127.0.0.1:8080/api/health | python3 -m json.tool
```

Sauvegarde à chaud, sans arrêter le service :

```bash
sudo sqlite3 /var/lib/tempi/tempi.db ".backup '/tmp/tempi-$(date +%F).db'"
```

Le fichier SQLite est autonome : le copier suffit à déplacer tout l'historique
sur une autre machine.

### Séparer la collecte de l'interface web

Le service livré exécute `tempi run`, qui réunit les deux dans un seul
processus. Pour les séparer — par exemple pour n'exposer l'interface qu'à la
demande — dupliquez l'unité en remplaçant `ExecStart` par `tempi collect` d'un
côté et `tempi serve` de l'autre. Le mode WAL de SQLite autorise sans
difficulté un rédacteur et plusieurs lecteurs simultanés.

## 9. Dépannage

Commencez toujours par :

```bash
sudo tempi doctor
```

La commande vérifie l'overlay, les modules noyau, l'état du bus, le niveau
électrique de la ligne de données, la lecture de chaque capteur, le stockage et
le service. Elle nomme la panne et donne le geste correctif, et sort avec un code
non nul en cas d'échec — utilisable dans un script. `--json` produit une sortie
exploitable.

Le `sudo` n'est pas obligatoire, mais il permet un second balayage du bus, seul
moyen de distinguer une ligne à la masse d'une ligne flottante.

### Lire les périphériques fantômes

Un bus en défaut enregistre malgré tout des périphériques, de famille `00`.
**Ce ne sont pas des capteurs** : leur forme désigne la panne.

| Contenu de `/sys/bus/w1/devices/` | Cause |
|---|---|
| `w1_bus_master1` seul | le bus fonctionne mais ne voit rien : la donnée n'atteint pas le capteur, ou il n'est pas alimenté |
| Rien du tout | 1-Wire non activé, ou modules non chargés — un redémarrage est nécessaire après l'activation |
| `00-800000000000`, constant | ligne de données **tenue à la masse** : donnée et masse dans la même rangée, résistance branchée sur GND au lieu du 3,3 V, ou capteur à l'envers |
| ROM en `00-…` qui **changent** à chaque balayage | ligne de données **flottante** : la résistance de 4,7 kΩ ne relie pas la donnée au 3,3 V |
| Un `28-…` **et** des `00-…` | le capteur répond mais le bus est bruité : raccourcissez le câble, ou descendez la résistance à 2,2 kΩ |

### Autres symptômes

| Symptôme | Cause la plus probable |
|---|---|
| `valeur de reset 85 °C` dans le journal | alimentation insuffisante ou câble trop long ; alimentez en 3,3 V plutôt qu'en parasite |
| `CRC invalide` occasionnel | normal sur un câble long ; tempi réessaie, seuls les échecs répétés sont journalisés |
| Lectures qui s'arrêtent après quelques heures | fils trop longs ou trop nombreux : réduisez la résistance de tirage à 2,2 kΩ |
| `tempi: command not found` | installation antérieure à l'ajout du lien `/usr/local/bin/tempi` : relancez `scripts/install.sh` |
| Interface inaccessible depuis un autre poste | `TEMPI_HOST` vaut `127.0.0.1` (voir section 7) |
| Courbe interrompue | collecte arrêtée sur cette période ; tempi ne relie pas artificiellement les deux bords |

## 10. Développement

```bash
pytest tests -q             # ou : python3 -m unittest discover -s tests -t .
python3 -m tempi --simulate run
```

Le mode `--simulate` génère une température suivant un cycle journalier bruité,
ce qui permet de travailler sur l'interface sans matériel.

Organisation du code :

| Fichier | Rôle |
|---|---|
| `tempi/sensor.py` | lecture du bus 1-Wire, validation des trames, capteur simulé |
| `tempi/storage.py` | schéma SQLite, écriture, requêtes, agrégation, purge |
| `tempi/collector.py` | boucle de collecte, bande morte, rétention |
| `tempi/web.py` | serveur HTTP, API JSON, export CSV |
| `tempi/diagnostics.py` | analyse de l'état du bus, sans accès système — donc testable partout |
| `tempi/cli.py` | ligne de commande |
| `tempi/static/index.html` | interface web (HTML, CSS et JavaScript sans dépendance) |

## Licence

MIT.
