# Andro CP

**Android cloning program** — copieur Windows / Android développé en C# avec .NET 10 et Avalonia.

Auteur : **Michel Ekanga · King Saozer** — saozerkinghacker@gmail.com

## Une application, deux livraisons

| Livraison Windows x64 | Contenu | Prérequis sur le PC cible |
|---|---|---|
| **Full** — `AndroCp-win-full-x64` | Application, runtime .NET et ADB officiel | Autorisation USB et, si nécessaire, pilote constructeur |
| **Tiny** — `AndroCp-win-tiny-x64` | Même application, sans runtime .NET ni ADB | Runtime **.NET 10 x64** et ADB installé dans le `PATH` pour les copies Android |

Les deux livraisons offrent **exactement les mêmes fonctions**, notamment la reprise persistante et Pause / Continuer. Tiny conserve les bibliothèques de l’application et d’Avalonia ; elle n’embarque pas le runtime .NET. Aucun Python ni ARFT n’est utilisé par l’application.

Extraire et conserver **tout le dossier** de distribution, pas seulement l’exécutable. ADB est recherché dans `platform-tools/` à côté de l’application, puis dans le `PATH`. Aucun téléchargement silencieux n’est effectué par l’application. Avec Tiny, vérifier `adb version` dans un nouveau terminal et relancer l’application après modification du `PATH`. PC → PC fonctionne sans ADB.

## Source et destination

Chaque bloc possède son propre sélecteur d’appareil (**Ce PC** ou un Android) et son propre dossier, avec un bouton **Parcourir…**. Le trajet est déduit automatiquement :

- **Android → PC** et **PC → Android** ;
- **Android A → Android B** : deux processus ADB relaient le flux via le PC avec une mémoire tampon bornée, sans stocker une copie complète du fichier sur le PC ;
- **Même Android → même Android** : copie locale par fichier avec `cp -n`, sans faire transiter son contenu par le PC ;
- **PC → PC** : copie locale sans ADB.

Sur un même appareil, les dossiers source et destination doivent être distincts et **non imbriqués**, dans les deux sens. Les alias Android, notamment `/sdcard` et `/storage/emulated/0`, sont résolus avant ce contrôle. Les chemins Android peuvent être parcourus ou saisis manuellement (`/sdcard/DCIM`, `/storage/…`). Les dossiers locaux sélectionnés doivent exister.

## Fonctions et protections

- Filtres d’exclusion et exceptions relatifs à la source, sensibles à la casse, sans jokers. Le profil initial exclut `Android` et conserve `Android/media/com.whatsapp`.
- Fichiers cachés et fichiers de zéro octet inclus ; aucun dossier vide de la source n’est reproduit.
- Préparation des noms avant transfert : caractères interdits, noms réservés et collisions, notamment de casse sur Windows.
- En cas de conflit : proposition de renommage modifiable, ignorer ou annuler. Une décision n’est appliquée aux problèmes similaires que sur demande explicite.
- **Aucun écrasement silencieux et aucune suppression de la source.** Les dossiers existants peuvent être réutilisés ; les fichiers existants restent des conflits sauf s’ils appartiennent à une reprise vérifiée.
- Transfert dans un temporaire `.androcp-….part`, contrôle de **taille et SHA-256**, puis validation sans remplacement du fichier final.
- File durable, journal JSONL, contrôle de l’espace sur la destination et pause propre si l’espace manque.
- Pourcentage centré calculé sur les octets validés, temps écoulé et estimation du temps restant. La progression avance à chaque fichier terminé, pas à chaque octet transmis.
- État de connexion vérifié pour les appareils sélectionnés. Les avertissements contextuels peuvent être fermés ; leur fermeture ne désactive aucune vérification.

## Utilisation

1. Pour Android, activer le débogage USB, brancher les appareils et autoriser le PC, puis cliquer sur **Actualiser**.
2. Choisir l’appareil et le dossier de chaque bloc **Source** et **Destination**.
3. Adapter exclusions et exceptions. Elles sont relatives à la source : un filtre `Android` n’a aucun effet si la source est déjà `/sdcard/DCIM`.
4. Garder **Reprise persistante** cochée pour retrouver la copie après fermeture ou plantage.
5. Cliquer sur **Préparer et copier**, puis résoudre les éventuels problèmes de noms.

### Pause / Continuer

1. Cliquer sur **Pause**. Pendant « Mise en pause… », **ne pas débrancher** : l’arrêt des transferts et le nettoyage sont encore en cours.
2. Attendre la confirmation de pause et la barre **orange**. Aucun transfert actif ne subsiste ; les fichiers terminés sont conservés et le chronomètre est figé.
3. Il est alors possible de débrancher les téléphones. Rebrancher et autoriser **les mêmes appareils** avant de cliquer sur **Continuer**.
4. L’application vérifie leur accès avant la reprise. La barre redevient **bleue** lorsque la copie redémarre ; en cas d’échec, elle reste en pause.

Les appareils, chemins et filtres restent verrouillés pendant la session. Le fichier interrompu est **recommencé depuis zéro**, tandis que les fichiers terminés sont vérifiés puis ignorés. Le temps passé en pause n’est pas compté. Un nouvel inventaire et la vérification des fichiers peuvent prendre du temps.

**Annuler** abandonne la session ouverte en conservant les fichiers terminés. Si la reprise persistante est décochée, Pause / Continuer reste disponible dans la session, grâce à une file dédiée ; cette file n’est pas retrouvée automatiquement après fermeture.

### Reprise persistante

Disponible dans **tous les modes**, en Full comme en Tiny. Après fermeture, choisir les mêmes appareils, chemins et filtres, puis relancer **Préparer et copier**. Les décisions de renommage enregistrées dans la file sont conservées.

- La file append-only se trouve dans `%LOCALAPPDATA%/AndroCp/queues`. Elle est isolée par appareils, trajet, chemins et filtres, et verrouillée pendant son utilisation. Les identités des anciennes files PC ↔ Android sont conservées.
- Les entrées passent de `queued` à `pending`, puis à `completed`. Après interruption, les `pending` deviennent `retry`. Une dernière ligne tronquée par un plantage est récupérable ; une corruption antérieure provoque un arrêt explicite.
- Le SHA-256 attendu est persisté avant la validation finale. Une interruption après le renommage final est ainsi récupérable sans doublon ni écrasement.
- Les fichiers terminés sont également revérifiés par SHA-256. Un fichier final manquant peut être recopié ; un fichier final modifié n’est pas écrasé.
- Une source dont la taille ou la date a changé depuis l’inventaire de la file est refusée. Utiliser le téléphone pendant la pause peut déclencher ce conflit, notamment pour des données d’application.

Ne pas supprimer la file si l’on souhaite conserver la reprise et les renommages. Sans la file, les fichiers déjà présents redeviennent des collisions normales, pas des fichiers automatiquement adoptés comme terminés.

### Espace limité

Avec **Reprise persistante**, cocher **Poursuivre avec l’espace disponible** pour avancer fichier par fichier sur n’importe quelle destination. Si le prochain fichier ne tient pas, la copie passe en pause orange. Libérer de l’espace puis cliquer sur **Continuer**.

Chaque nouveau fichier exige sa taille entière plus **8 Mio de marge**. Un temporaire appartenant à l’entrée de reprise peut être nettoyé avant le nouveau contrôle. Les fichiers terminés ne nécessitent pas une deuxième copie. Sans cette option, le contrôle initial exige l’espace de tous les fichiers restant à transférer. Aucun dossier parent n’est créé pour le prochain transfert si son contrôle d’espace échoue ; une interruption antérieure peut toutefois laisser des parents vides.

## Prérequis et limites

- Ce programme copie les fichiers accessibles, **pas une image système Android**. Les données privées et certains dossiers protégés restent inaccessibles. Copier `com.whatsapp` ne garantit pas une sauvegarde WhatsApp restaurable.
- Ne pas modifier les sources pendant la copie. Les vérifications taille/date ne détectent pas une modification volontaire conservant ces deux valeurs ; le SHA-256 vérifie le contenu transféré, pas un instantané global de l’appareil.
- Pas de conservation générale des métadonnées, permissions et horodatages. Les liens symboliques/jonctions sont refusés plutôt que suivis, hors alias racine Android résolus pour la sélection.
- Transferts séquentiels et commandes ADB par fichier : les très grandes collections peuvent être lentes. L’estimation restante est approximative, se base sur les transferts effectifs et peut rester « calcul en cours ».
- Une interruption peut laisser des parents vides ou un `.part`, notamment si Android est déconnecté. Aucun dossier utilisateur n’est supprimé pour les nettoyer.
- Les erreurs de fichiers sont consignées et le transfert continue ; un inventaire incomplet bloque la copie. Les chemins Windows très longs peuvent échouer selon le système de fichiers et la configuration.
- Les files et journaux contiennent des chemins potentiellement sensibles. Les journaux se trouvent dans `%LOCALAPPDATA%/AndroCp/journals` ; sous Linux, dans le dossier de données locales correspondant. Éviter de copier les propres dossiers d’état actifs d’Andro CP.
- Ne pas lancer deux copies vers la même destination. Les contrôles d’existence et de liens ne protègent pas contre un autre processus modifiant simultanément l’arborescence.
- L’exécutable n’est pas signé : Windows SmartScreen peut afficher un avertissement. Des essais sur une vraie machine Windows et plusieurs téléphones restent nécessaires avant distribution.

## Développement et tests

Installer le SDK .NET 10 :

```sh
dotnet run --project tests/AndroCp.Tests -c Release
dotnet build AndroCp.slnx -c Release
dotnet run --project src/AndroCp.App
```

L’interface fonctionne aussi sous Linux pour le développement. Les tests sont un exécutable sans framework externe. Ils utilisent des fichiers temporaires et des processus ADB simulés, **jamais de vrais téléphones**. Les scénarios d’intégration Android sont exécutés sous Linux, notamment le relais binaire, `cp` local, les alias et les pauses en cours de transfert.

Le logo original est `logo-vector.svg`. Les PNG/ICO pour l’en-tête, la fenêtre et l’exécutable sont dans `src/AndroCp.App/Assets/`. La commande facultative `python3 scripts/generate-icons.py` exige Inkscape et Pillow pour les régénérer ; elle n’est pas nécessaire pour compiler ni utiliser l’application. Les images générées sont versionnées.

## Produire les livraisons Windows

Depuis Windows avec le SDK .NET 10 :

```powershell
# Full : runtime .NET et ADB intégrés
./scripts/publish-windows.ps1

# Tiny : runtime .NET et ADB externes
./scripts/publish-windows.ps1 -Tiny
```

Les résultats sont `artifacts/AndroCp-win-full-x64/` et `artifacts/AndroCp-win-tiny-x64/`. Full télécharge les Platform Tools officiels via HTTPS, conserve leurs notices, vérifie les fichiers ADB et lance `adb version`. Le SHA-256 de l’archive est enregistré pour la traçabilité, pas comme signature de confiance.

| Workflow GitHub Actions | Script | Artefact |
|---|---|---|
| `Build Windows Full` — `windows-full.yml` | sans option | `AndroCp-win-full-x64` |
| `Build Windows Tiny` — `windows-tiny.yml` | `-Tiny` | `AndroCp-win-tiny-x64` |

Chaque workflow exécute les tests simulés sous Linux avant le packaging Windows. Déclenchement sur `main`, `master`, `stable`, pull request ou manuellement via **Actions → Run workflow**. Aucun dépôt distant n’est créé automatiquement.

Compilation croisée Tiny depuis Linux (sans packaging ADB) :

```sh
dotnet publish src/AndroCp.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/AndroCp-win-tiny-x64
```
