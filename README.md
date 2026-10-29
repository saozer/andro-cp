# Andro CP

Copieur USB Android ↔ Windows, développé en C# / .NET 10 avec Avalonia. L’interface fonctionne aussi sous Linux pour le développement.

## Fonctionnalités communes

- Liste des appareils ADB, y compris les états non autorisés et hors ligne.
- Copie d’un dossier Android vers un dossier local, ou inversement.
- Sélection des dossiers locaux via une fenêtre ; saisie du chemin Android.
- Exclusions et exceptions relatives à la source, sensibles à la casse, sans jokers.
- Profil initial : exclure `Android`, conserver `Android/media/com.whatsapp`.
- Aucun dossier vide issu de la source ; les fichiers de zéro octet sont conservés.
- Préparation des noms avant le transfert : caractères Windows interdits, noms réservés et collisions, y compris la casse.
- À chaque problème : proposition modifiable, ignorer ou annuler. Application aux problèmes similaires uniquement sur demande explicite. Le choix groupé génère une proposition adaptée à chaque nom.
- Aucun écrasement automatique, aucune suppression de la source. Les dossiers existants sont réutilisés ; les collisions de fichiers demandent un nouveau nom ou l’abandon.
- Fichiers temporaires `.androcp-….part`, vérification des tailles avant validation, annulation et journal JSONL.
- Préparation des collisions avec une seule lecture de chaque dossier de destination, plutôt qu’une recherche par fichier.

## Prérequis et avertissements

Activer le débogage USB et autoriser l’ordinateur sur le téléphone. Un pilote constructeur peut être requis sous Windows. Les protections Android restent applicables : ce n’est pas une sauvegarde système ni une extraction des données privées des applications. La présence de `com.whatsapp` ne garantit pas une sauvegarde WhatsApp restaurable.

ADB est recherché dans `platform-tools/` à côté de l’exécutable, puis dans le PATH. La distribution Windows peut inclure les outils officiels Google, avec leurs notices et conditions. Aucun téléchargement silencieux n’est effectué par l’application.

## Développement Linux / Windows

Installer le SDK .NET 10 puis :

```sh
dotnet run --project tests/AndroCp.Tests
dotnet build src/AndroCp.App
dotnet run --project src/AndroCp.App
```

Les tests sont un exécutable sans framework externe. Ils ne touchent pas aux téléphones. Les fichiers de test sont créés dans un répertoire temporaire dédié et nettoyés.

## Produire l’application Windows

Depuis Windows avec le SDK .NET 10 :

```powershell
./scripts/publish-windows.ps1
```

Le résultat par défaut est `artifacts/AndroCp-win-adb-x64/AndroCp.exe` avec le runtime .NET inclus et `platform-tools/`. Distribuer le dossier entier. Le script télécharge les Platform Tools officiels via HTTPS et enregistre le SHA-256 de l’archive (traçabilité, pas une signature de confiance). Utiliser `-WithoutAdb` pour un environnement possédant déjà ADB.

| Workflow GitHub Actions | Script | Artefact | Contenu |
|---|---|---|---|
| `Build Windows Tiny sans ADB` (`windows-tiny.yml`) | `-WithoutAdb` | `AndroCp-win-tiny-x64` | Version de base, ADB du PATH, sans Python ni reprise automatique |
| `Build Windows ADB` (`windows.yml`) | sans option | `AndroCp-win-adb-x64` | Version de base avec ADB, sans Python ni reprise automatique |
| `Build Windows ARFT et reprise` (`windows-arft.yml`) | `-WithArft` | `AndroCp-win-arft-x64` | ADB + Python portable + ARFT + reprise dans les deux sens |

Chaque workflow est déclenchable via **Actions → nom du workflow → Run workflow**. Il produit son propre artefact et son propre dossier de sortie. Le runtime .NET est inclus dans les trois variantes ; « Tiny » signifie sans ADB/Python, pas sans runtime .NET. Aucun dépôt distant n’est créé automatiquement.

### Windows cible avec ADB déjà installé

```powershell
./scripts/publish-windows.ps1 -WithoutAdb
```

L’application utilise alors `adb.exe` dans le PATH Windows. Vérifier `adb version` dans un nouveau terminal et relancer l’application après toute modification du PATH. Aucune installation .NET n’est nécessaire pour la publication autonome (`--self-contained true`).

**Les variantes ADB et ARFT incluent ADB.** Le packaging vérifie `adb.exe`, ses deux DLL Windows et `NOTICE.txt`, puis exécute `adb version`. Télécharger et extraire tout l’artefact, pas seulement le `.exe`. Le copieur privilégie cet ADB intégré par rapport à celui du PATH.

### Variante ARFT et reprise persistante

Sur Windows, le packaging ARFT exige aussi Python/pip sur la machine de compilation (fourni par `setup-python` dans le workflow) :

```powershell
./scripts/publish-windows.ps1 -WithArft
```

Il embarque Python **3.14.10** au format Windows embeddable, ARFT **0.6.0**, `tqdm` et `colorama`. Les roues Python sont épinglées et leurs SHA-256 vérifiés via `--require-hashes`. Les notices Python et les licences des paquets restent dans l’artefact. Aucun Python ni pip n’est requis sur le PC cible. Le build teste l’import et la compatibilité du pont avec le vrai Python portable avant de livrer l’artefact.

Dans cette variante, la case **Reprise persistante** est activée par défaut. Pour reprendre, sélectionner le même téléphone, le même sens, les mêmes chemins et les mêmes filtres, puis cliquer à nouveau sur **Préparer et copier**. Les décisions de renommage sont conservées. Décocher cette option utilise la copie classique.

- **Android → PC** : le pont `python/bridge.py` utilise la primitive `pull_one_file` d’ARFT pour transférer et vérifier le fichier dans un staging dédié. Le moteur C# garde le contrôle des exclusions, des noms et de la validation finale sans écrasement. La CLI ARFT complète n’est volontairement pas appelée, car elle peut remplacer les fichiers finaux. La reprise est gérée par notre file durable, pas par les fichiers `.arft-*` de la CLI.
- **PC → Android** : chaque fichier commence en `queued`. Au redémarrage de la copie, les `pending` deviennent `retry`. Avant un transfert, `queued/retry` passe en `pending` et cet état est synchronisé sur disque. Après vérification et validation du fichier final, il devient `completed`. En cas d’échec, il reste `pending` pour une prochaine relance.
- Un fichier interrompu est **recommencé depuis zéro**, jamais repris au dernier octet.
- La file est un journal append-only dans `%LOCALAPPDATA%/AndroCp/queues`. Elle est isolée par téléphone, sens, chemins et filtres, et verrouillée pendant son utilisation. Une dernière ligne tronquée par un plantage est récupérable ; une corruption antérieure provoque un arrêt explicite.
- Avant la validation finale, le SHA-256 du contenu attendu est persisté. Si une interruption survient après le renommage final mais avant `completed`, la relance reconnaît le fichier par son SHA-256, sans le retransférer ni l’écraser.
- Les fichiers déjà `completed` sont également revérifiés par SHA-256 à la reprise. Cela peut prendre du temps sur une grosse sauvegarde ; il ne s’agit pas du mode rapide sans vérification de la CLI ARFT.
- La source est comparée à l’inventaire précédent par taille et date de modification. Une source modifiée bloque la reprise concernée : choisir une nouvelle destination. Une destination de reprise modifiée est signalée et n’est pas écrasée.

Ne pas supprimer la file si vous souhaitez conserver la reprise et les renommages. Les files et journaux contiennent des chemins potentiellement sensibles. Une perte de la file ne transforme pas les fichiers existants en fichiers appartenant à la copie : ils redeviennent des collisions normales.

Tests du pont, sans téléphone, dans un environnement Python de développement :

```sh
python -m pip install --require-hashes --only-binary=:all: -r python/requirements.txt
python -m unittest discover -s python -p 'test_*.py' -v
python python/bridge.py --check
```

Une compilation croisée sans ADB est possible depuis Linux :

```sh
dotnet publish src/AndroCp.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/AndroCp-win-tiny-x64
```

L’exécutable n’est pas signé. Windows SmartScreen peut afficher un avertissement. Un test sur une vraie machine Windows et plusieurs téléphones reste nécessaire avant distribution.

## Utilisation

1. Brancher le téléphone, accepter son autorisation, puis cliquer sur **Actualiser**.
2. Choisir l’appareil et le sens de copie.
3. Saisir le dossier Android (`/sdcard`, `/sdcard/DCIM`, etc.) et choisir un dossier local existant.
4. Adapter les filtres. Ils sont relatifs au dossier source : si la source est `/sdcard/DCIM`, un filtre `Android` n’a aucun effet.
5. Cliquer sur **Préparer et copier**, puis résoudre les éventuels problèmes de noms.

Les journaux sont dans `%LOCALAPPDATA%/AndroCp/journals` sous Windows et dans le dossier de données locales correspondant sous Linux. Ils contiennent les chemins, les renommages et les erreurs : attention à leur confidentialité.

## Limites de cette version

- Pas encore de navigateur de dossiers Android, copie Android ↔ Android, ni conservation générale des métadonnées/horodatages. La reprise n’est activée dans l’interface que pour la variante ARFT.
- La progression avance par fichier terminé, pas en continu à l’intérieur d’un gros fichier. La vitesse est une moyenne depuis le lancement, préparation comprise.
- Les transferts eux-mêmes sont encore séquentiels et utilisent des commandes ADB par fichier ; les transferts groupés restent à implémenter avant de viser des collections de plusieurs centaines de milliers de fichiers.
- L’espace disponible est précontrôlé pour les disques Windows ; ailleurs une saturation provoque une erreur de transfert.
- Copie classique : vérification des tailles uniquement. Variante reprise : SHA-256 pour reconnaître les fichiers déjà validés. Ne pas modifier les sources pendant la copie ; les fichiers modifiés en conservant volontairement taille et date ne sont pas détectés par l’inventaire seul.
- Les liens/jonctions sont refusés plutôt que suivis. Les noms sont traités comme UTF-8 côté Android.
- Les chemins Windows très longs peuvent encore échouer selon la configuration Windows et les limites du système de fichiers. Ils sont signalés dans le journal ; aucun fichier source n’est modifié.
- Une interruption peut laisser des dossiers parents vides ou, en cas de déconnexion Android, un fichier `.part`. Aucun dossier utilisateur n’est supprimé pour les nettoyer.
- Les erreurs de fichiers sont consignées et le transfert continue ; un inventaire incomplet bloque la copie pour éviter un faux succès.

Ne pas lancer deux copies vers la même destination. Les contrôles de liens et d’existence ne constituent pas une protection contre un autre processus malveillant modifiant simultanément l’arborescence.
