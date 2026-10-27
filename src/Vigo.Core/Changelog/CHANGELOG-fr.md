## 0.31.0 — 2026-10-27
- Français : « Checkout » devient « Basculer », « issue » devient « ticket », la case Amend s'appelle « Modifier le dernier commit » et les modes de fusion des pull requests sont traduits. Les noms de touches des raccourcis (Entrée / Enter, Maj / Shift…) suivent la langue choisie.
- Formulaire de commit : la case « Pousser après la validation » se décoche une fois le push fait.
- Français : vocabulaire git traduit dans toute l'interface (Tirer, Pousser, Récupérer, Remiser / Réappliquer, arbres de travail, éléments de travail, Inverser, Réinitialiser) ; les boutons de la barre du haut sont traduits et un peu plus larges.
- Barre du haut : des badges sur Tirer et Pousser indiquent le nombre de commits à récupérer et à pousser.

## 0.30.0 — 2026-10-27
- Formulaire de commit : un loader tourne sur le bouton pendant la validation puis l'envoi (« Validation en cours… », « Envoi en cours… »).
- Formulaire de commit : la ligne Amend / profil / signature passe au-dessus du message, une case « Pousser après la validation » est ajoutée, et le bouton s'intitule « Valider X fichiers » ou « Valider X fichiers et pousser ».
- Profils Git : un profil porte désormais toute la signature des commits et des tags ; la config Git globale n'est utilisée que sans profil. Un profil sans clé ne signe plus, même si la config globale l'impose (fin de l'erreur « gpg failed to sign the data »). Si vos commits étaient signés par la config globale, activez la signature dans votre profil.
- Préférences › Profils : le bouton « Créer depuis la config Git » crée un profil à partir de votre config globale (identité, clé et format de signature).

## 0.29.4 — 2026-10-26
- Barre d'état : un bouton en bas à droite pour rechercher les mises à jour à la demande (son icône tourne pendant la recherche).
- Terminal intégré : mise à jour de LoomTerminal en 0.1.0-alpha.2.
- Préférences : correction d'un plantage en passant à l'onglet d'un autre dépôt avec la page « Dépôt » ouverte.

## 0.29.3 — 2026-10-26
- Graphe : le menu clic droit d'une branche (badge ou ligne) reprend les actions du panneau gauche : extraire, fusionner, rebaser, pousser, renommer, supprimer, copier le nom.
- Graphe : la touche Suppr supprime la branche (locale ou distante) de la ligne sélectionnée (un choix est proposé s'il y en a plusieurs).

## 0.29.2 — 2026-10-26
- Rebase interactif : avec uniquement des « pick » vers une cible qui a avancé, le rebase est maintenant lancé au lieu d'afficher « Rien à modifier ».

## 0.29.1 — 2026-10-25
- Mises à jour : la fenêtre « Nouveautés » liste toutes les versions depuis la dernière installée, et plus seulement la dernière.
- Onglets : clic droit sur un onglet de dépôt › « Changer l'icône… » pour choisir son icône parmi toutes les icônes Fluent, avec recherche par nom en anglais (mémorisée par dépôt).
- Profils Git : chaque profil peut avoir une icône (choisie parmi toutes les icônes Fluent), affichée dans la liste des profils et dans la barre d'état.
- Préférences › Icônes : la liste de toutes les icônes Fluent avec leur nom, à affecter à un profil Git ou à un dépôt (icône de son onglet). Les menus de profils affichent l'icône du profil.
- Barre d'état : « Gérer les profils » du menu du profil ouvre directement la section Profils Git des préférences.
- Préférences : sections réordonnées (réglages courants en haut) ; « Dépôt en cours » est à part, en bas de la liste, sous un séparateur.

## 0.28.1 — 2026-10-24
- GitHub : la connexion par navigateur demande aussi l'autorisation « workflow », nécessaire pour pousser des fichiers de `.github/workflows/` ; reconnectez le compte dans Préférences › Intégrations pour en profiter.
- Les identifiants OAuth des hébergeurs sont désormais fournis à la compilation au lieu d'être codés en dur dans le code source.
- Résumé, pull requests, tickets : une image dans un lien (badge de README) ne fait plus planter l'application ; les badges SVG sont dessinés avec leurs couleurs et restent cliquables.

## 0.28.0 — 2026-10-23
- Terminal intégré : nouveau moteur natif (sans WebView2), plus léger ; mêmes fonctions (shells, thème, police, copier-coller, liens, raccourcis), avec une petite marge autour du texte.

## 0.27.1 — 2026-10-23
- Préférences › À propos : nouvelle option « Recevoir les versions bêta » pour recevoir aussi les pré-releases comme mises à jour (désactivée par défaut : seulement les versions stables).
- Les versions sont désormais construites et publiées automatiquement depuis le dépôt public github.com/vigo-project/vigo ; aucun changement fonctionnel.

## 0.27.0 — 2026-10-22
- VIGO est désormais open source (licence MIT) : le code et les versions sont publiés sur github.com/vigo-project/vigo, et les mises à jour automatiques viennent de ce dépôt à partir de cette version.
- GitHub : la connexion par navigateur utilise une nouvelle application OAuth ; si l'autorisation est refusée, reconnectez le compte dans Préférences › Intégrations.
- Azure DevOps : la connexion par navigateur n'est plus proposée par défaut ; utilisez un jeton d'accès personnel.

## 0.26.1 — 2026-10-22
- Création, renommage de branche, tag, remote, worktree et Git Flow : une espace tapée ou collée dans le nom devient « - » au lieu d'afficher une erreur.

## 0.26.0 — 2026-10-22
- Confirmations : le bouton « Annuler » est neutre (plus bleu) à côté du bouton d'action.
- Diff : la mise en page unifiée / côte à côte (diff, historique de fichier, pull request) se choisit avec un commutateur à deux icônes ; les noms sont dans les infobulles.
- Clonage : le dossier de destination proposé décode les espaces (`%20`) de l'URL au lieu de les garder encodés.
- Restauration de session : un dépôt introuvable au premier lancement est signalé ; s'il l'est encore au lancement suivant, il est retiré de la session sans alerte.
- Fichiers modifiés : « Rejeter les modifications » s'applique à tous les fichiers sélectionnés, et plus seulement à celui sous le clic.
- Mises à jour : une mise à jour téléchargée s'installe au lancement suivant de VIGO (avec une fenêtre de progression) et non plus à la fermeture.
- Préférences › Dépôt en cours : modification des branches et préfixes Git Flow du dépôt (ou initialisation de Git Flow).
- Préférences › Dépôt en cours : dossier des hooks Git (core.hooksPath), activation ou désactivation de chaque hook, et interrupteur « Ignorer les hooks » par dépôt pour les commits et push lancés par VIGO.
- Préférences › Dépôt en cours : encodage par défaut des fichiers et des diffs du dépôt (clé gui.encoding de sa configuration).
- Comptes GitHub et Azure DevOps : fetch, pull, push et clone utilisent le jeton du compte connecté (sans fenêtre de Git Credential Manager) ; s'il est refusé, git retombe sur vos identifiants habituels et un message l'indique.
- Préférences › Éditeur et outils : option « Ligatures » pour l'éditeur (Cascadia Code, Fira Code, JetBrains Mono…), désactivée par défaut.
- Pull requests GitHub : bouton « Supprimer la branche » sur une pull request fusionnée ou fermée (avec option pour supprimer aussi la branche locale).
- Graphe : les flèches ↑ / ↓ (et PageHaut, PageBas, Début, Fin) changent le commit sélectionné après un clic sur un commit.

## 0.25.0 — 2026-10-21
- Préférences › Apparence › Mise en forme du texte : choix de la police et de la taille du texte de l'interface (appliqués au prochain lancement).
- Préférences › Éditeur et outils : police, taille, tabulation, fin de ligne, coloration, numéros de ligne et retour à la ligne de l'éditeur ; police, taille, hauteur de ligne, curseur et atténuation sans focus du terminal intégré.

## 0.24.0 — 2026-10-21
- Indexer (ou désindexer) le fichier affiché dans l'aperçu ouvre le fichier suivant de la même liste au lieu de revenir au graphe.
- Le bouton Stash utilise le titre et la description saisis pour le commit ; un Pop les remet dans le formulaire de commit.
- Le fetch automatique (dont celui du lancement) affiche la barre de progression de la barre d'outils.

## 0.23.0 — 2026-10-20
- Dans la barre d'état, l'ancienneté du dernier fetch utilise le même format que le reste de l'application (« hier », « il y a 2 sem. »…).
- La taille des pièces jointes d'un work item Azure DevOps s'affiche dans la langue de l'application (Ko, Mo).
- Les images privées d'une issue ou d'une pull request ne restent plus affichées après un changement de profil ou une déconnexion.
- Les images volumineuses (plus de 25 Mo) d'un work item Azure DevOps ne sont plus téléchargées en entier.
- Les images des issues et pull requests ne sont plus chargées en http simple : un lien vers l'image s'affiche à la place.
- Appliquer beaucoup de fichiers d'un stash à la fois n'échoue plus à cause de la longueur de la ligne de commande.
- Un build qui écrit dans bin/, obj/ ou node_modules/ ne relance plus un statut git complet toutes les 2 secondes.
- Les onglets et projets sont enregistrés quelques secondes après chaque changement, pas seulement à la fermeture normale.
- Indexer une ligne ajoutée après une dernière ligne sans saut de ligne ne colle plus les deux lignes.
- Dans un worktree lié, les branches, distants et tags se rafraîchissent maintenant d'eux-mêmes.
- Ouvrir un très gros fichier ne le charge plus en mémoire avant de le refuser.
- Azure DevOps ne renouvelle plus le jeton une fois par image d'un work item : un seul renouvellement est partagé.
- Un jeton GitHub renouvelé n'est plus perdu si la requête est annulée, et une erreur du Gestionnaire d'informations d'identification est affichée.
- Un dépôt inaccessible au démarrage (partage réseau, VPN) reste dans la session au lieu d'en disparaître.
- Rejeter un dépôt git imbriqué non suivi affiche une erreur au lieu d'annoncer un succès sans rien supprimer.
- Le blame fonctionne sur les chemins avec accents, et un réglage git `diff.suppressBlankEmpty` ne décale plus les numéros de ligne des diffs.
- Les nouveautés d'une version ne sont plus perdues quand un autre dialogue s'ouvre au démarrage : elles s'affichent au lancement suivant.
- Quitter VIGO pendant la réouverture des onglets ne perd plus ceux qui n'étaient pas encore rouverts.
- Les fichiers qui ne sont pas en UTF-8 ne peuvent plus être indexés ou rejetés ligne par ligne (leur encodage était corrompu).
- Le diff d'un commit affiche le bon fichier quand son nom contient [, * ou ?.
- Un second lancement de VIGO ouvre son dépôt dans la fenêtre déjà ouverte au lieu de démarrer une autre instance.
- Un fichier de réglages illisible est conservé en .bad au lieu d'être écrasé en silence, et la session des autres profils n'est plus effacée.
- Fermer un onglet pendant une écriture git ne l'interrompt plus (plus de index.lock résiduel).
- Le push forcé n'efface plus les commits d'un collègue récupérés par le fetch automatique (--force-if-includes).
- Préférence pour charger les commits du graphe au fil du défilement.
- Préférence pour le nombre de commits affichés en premier dans le graphe.
- Préférence pour garder les submodules à jour après un pull ou un checkout.
- Préférence pour élaguer à chaque fetch les branches distantes supprimées sur le serveur (activée par défaut).
- Stash avec un titre et une description facultative.

## 0.22.1 — 2026-10-19
- Journal des modifications dans Préférences › À propos.
- Fenêtre « Nouveautés » au premier lancement de chaque nouvelle version.

## 0.21.3 — 2026-10-19
- La liste déroulante de commit est désactivée quand aucun commit n'est possible.

## 0.21.2 — 2026-10-19
- Menu contextuel de dossier dans l'arborescence du répertoire de travail.

## 0.21.1 — 2026-10-19
- Build Native AOT corrigé sous .NET 11 RC2.

## 0.21.0 — 2026-10-19
- Indexer et désindexer plusieurs fichiers ou un dossier entier en une fois.
- Les projets acceptent n'importe quelle couleur, choisie au sélecteur de couleur.

## 0.20.0 — 2026-10-19
- Suppression de clés SSH et génération de clés GPG depuis les préférences.

## 0.19.1 — 2026-10-16
- Masquer des branches, tags, remises et worktrees dans le graphe.

## 0.19.0 — 2026-10-16
- Remises (stash) affichées dans le graphe, avec un menu dédié ; application des seuls fichiers sélectionnés.
- Écran en lecture seule des work items Azure DevOps.

## 0.18.3 — 2026-10-16
- Les refs de points de contrôle des agents IA sont masquées dans le graphe.

## 0.18.2 — 2026-10-16
- Commande d'aide en ligne ouvrant le site de documentation.

## 0.18.1 — 2026-10-16
- Pages Résumé et Statistiques dans le panneau gauche.
- Clonage depuis un compte d'hébergeur connecté ; nouvelle mise en page du dialogue de clonage.
- Prévention des conflits avec la branche distante par défaut.
- Vérifications détaillées des pull requests (GitHub et Azure DevOps).

## 0.17.0 — 2026-10-15
- Toute l'application est disponible en français et en anglais.

## 0.16.0 — 2026-10-15
- Première localisation français / anglais des vues principales.

## 0.15.0 — 2026-10-14
- Sélecteurs de dépôt et de branche et recherche de commits dans la barre d'outils.
- Revue de pull request dans l'éditeur ; fichiers de la PR en arborescence redimensionnable.
- Pull requests Azure DevOps : votes, fils, commentaires, brouillon, fusion.
- Création de PR enrichie : relecteurs, étiquettes, assignés, issues liées.
- Push forcé proposé après un push refusé.
- Pull request proposée quand une branche est déposée sur un autre badge.

## 0.14.1 — 2026-10-13
- Écran en lecture seule des pull requests Azure DevOps, avec diff des fichiers.

## 0.14.0 — 2026-10-13
- Terminal intégré et journal des commandes Git dans le panneau bas.

## 0.13.0 — 2026-10-13
- Messages de commit générés par IA (Claude, Copilot en CLI ou API).
- Git Flow (init, start, finish).
- Format et clé de signature par profil Git.
- Correctif : création de tag depuis le menu du graphe.

## 0.12.0 — 2026-10-13
- Créer, modifier, fermer et rouvrir des issues GitHub.
- Branches distantes supprimées élaguées au fetch ; branches distantes groupées en dossiers.

## 0.11.0 — 2026-10-13
- Filtrer, paginer et commenter les issues GitHub.
- Thème clair : diffs plus lisibles ; fichiers non UTF-8 décodés.
- Correctif : graphe vide après restauration de session.

## 0.10.0 — 2026-10-13
- Renouvellement automatique des jetons GitHub qui expirent.

## 0.9.0 — 2026-10-12
- Double clic sur un badge de branche : checkout et pull.
- Modifications locales emportées automatiquement au checkout.

## 0.8.0 — 2026-10-12
- Revue de pull request en ligne, fusion automatique, réactions et mise à jour de la branche.

## 0.7.0 — 2026-10-12
- Bouton de redémarrage dans la barre d'état quand une mise à jour est prête.

## 0.6.0 — 2026-10-12
- Connexion Azure DevOps avec un simple jeton, à partir de son URL.

## 0.5.0 — 2026-10-12
- Onglets de dépôts regroupés en projets colorés.
- Écran de pull request GitHub avec Markdown et actions d'écriture.

## 0.4.0 — 2026-10-10
- Section Git LFS : suivre, ne plus suivre, pull.

## 0.3.0 — 2026-10-10
- Couleurs des voies du graphe personnalisables.

## 0.2.0-beta.1 — 2026-10-10
- Vérification de la signature des commits dans les détails.
- Raccourcis clavier configurables.
- Section À propos dans les préférences.

## 0.1.0-beta.3 — 2026-10-09
- Recherche de mises à jour toutes les heures.

## 0.1.0-beta.2 — 2026-10-09
- Version de l'application affichée dans la barre d'état.
- Correctif : indicateur de chargement bloqué dans les détails du commit.

## 0.1.0-beta.1 — 2026-10-09
Première bêta.
- Graphe des commits, détails, diffs, indexation par fichier, bloc ou ligne, commit et amend.
- Fetch, pull, push ; branches, tags, distants, fusion, rebase (interactif aussi), cherry-pick, revert, reset, remise.
- Éditeur de code pour les diffs, éditeur de conflits, diff d'images, annuler / rétablir.
- Onglets, fenêtres multiples, restauration de session, palette de commandes, thèmes et densité.
- Comptes GitHub et Azure DevOps, pull requests et issues.
- Worktrees, submodules, profils Git multiples.
- Installateur et mises à jour automatiques.
