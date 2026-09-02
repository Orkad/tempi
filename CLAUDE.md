# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

tempi enregistre et visualise la température mesurée par des capteurs DS18B20
sur un Raspberry Pi. C'est un portage en C# / .NET 10 d'une implémentation
Python d'origine (jusqu'à la v1.0.0) : le comportement observable (fichier
SQLite, API JSON, ligne de commande) est un **contrat figé**, vérifié par les
tests golden master (voir plus bas). La documentation utilisateur complète
est dans `README.md` — ce fichier ne la répète pas.

Le code, les commentaires et les noms de méthode/test sont en **français**.

## Commandes

```bash
dotnet restore src/Tempi.slnx
dotnet build src/Tempi.slnx --configuration Release

dotnet test --solution src/Tempi.slnx                                  # toute la suite
dotnet test --solution src/Tempi.slnx --filter "FullyQualifiedName~StorageTests"   # une classe/un test

dotnet run --project src/Tempi -- --simulate run                       # lancer l'app, capteur virtuel
```

Le SDK est fixé par `global.json` (.NET 10, runner de test
`Microsoft.Testing.Platform` — VSTest a été retiré, `dotnet test` prend
`--solution` et non un chemin de `.csproj`/`.sln` classique).

Golden master (comportement identique à l'implémentation Python d'origine) :

```bash
scripts/golden-capture.sh
diff -r tests/golden/expected/api tests/golden/actual/api
diff -r tests/golden/expected/cli tests/golden/actual/cli
```

Vérification shellcheck des scripts de déploiement :

```bash
shellcheck -x -s bash --severity=warning scripts/*.sh
```

Ces mêmes étapes tournent dans `.github/workflows/ci.yml`, avec en plus une
publication trimmée `linux-arm64` et le démarrage réel du binaire trimmé
(le trimming .NET supprime silencieusement du code atteint dynamiquement ;
seule l'exécution le révèle — voir les commentaires du workflow).

## Architecture

Point d'entrée : `src/Tempi/Program.cs`, qui déclare toutes les
sous-commandes (`System.CommandLine`) et les fait passer par `Run`/`RunAsync`
pour un traitement d'erreurs uniforme.

| Répertoire | Rôle |
|---|---|
| `Sensors/` | lecture du bus 1-Wire, parsing des trames, capteur simulé (`SimulatedBus`) |
| `Storage/` | schéma SQLite, écriture, requêtes, agrégation par bucket, purge |
| `Collect/` | boucle de collecte : bande morte (`TEMPI_MIN_DELTA`), rétention |
| `Outdoor/` | température extérieure (METAR / Infoclimat / Open-Meteo), traitée comme un capteur de plus |
| `Web/` | serveur Kestrel, endpoints JSON, export CSV |
| `Diagnostics/` | analyse de l'état du bus sans accès système — testable sans matériel ; consommé par `doctor` |
| `Cli/` | définitions des commandes, liaison des options vers `TempiConfig` |
| `Hosting/` | assemble les hôtes de `run`/`serve`/`collect`, intégration systemd |
| `Configuration/` | lecture des variables d'environnement, validation, chemins par défaut |
| `wwwroot/index.html` | UI web (HTML/CSS/JS sans dépendance), embarquée dans le binaire |

Points structurants à connaître avant de modifier le code :

- **`TempiVersion.Value`** (`src/Tempi/TempiVersion.cs`) est la seule source
  de vérité pour la version ; elle doit rester synchronisée avec `Version`
  dans `Directory.Build.props` (`VersionTests.cs` échoue sinon). Voir la
  remarque XML doc du fichier pour la procédure de release complète.
- **Golden master** (`tests/golden/`) : toute modification qui change un
  octet de sortie HTTP ou CLI casse ces tests par construction — c'est
  voulu. `tests/golden/reference.db` est une base figée et versionnée (pas
  régénérable : le capteur simulé Python d'origine dépendait du Mersenne
  Twister).
- **Trimming** : le binaire publié est self-contained et trimmé
  (`PublishTrimmed=true`). Un changement qui ajoute de la réflexion ou du
  chargement dynamique peut compiler et planter seulement à l'exécution du
  binaire trimmé — d'où l'étape CI dédiée qui démarre le binaire et
  l'interroge.
- **`InvariantGlobalization`** est activé (`Directory.Build.props`) pour
  économiser l'ICU embarqué ; les fuseaux horaires viennent de
  `/usr/share/zoneinfo`, pas d'ICU.
- Les avertissements sont traités comme des erreurs
  (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`).
