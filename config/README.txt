BRIDGE DES COMPTES
==================

1. Copier src\BridgeWorkerAccount.cs dans le dossier src du bridge.
2. Copier build-account.bat à la racine du bridge.
3. Copier config\config.accounts.json.dist vers config\config.accounts.json.
4. Renseigner le chemin GCM et les identifiants Sage dans config.accounts.json.
5. Lancer build-account.bat sur Windows.
6. Lancer bin\BridgeWorkerAccount.exe.

Le worker utilise exclusivement les dossiers sous sage-bridge\accounts.
Le worker des commandes et sa configuration actuelle ne sont pas modifiés.

Les champs source et platform sont conservés dans le JSON et les logs.
La description est copiée dans CT_Commentaire et limitée à 35 caractères.
Le champ tariffCategory est conservé dans le JSON mais ne sera utilisé
qu'après validation de la catégorie tarifaire Sage avec LMDC.

Ne pas enregistrer config.accounts.json avec les identifiants réels dans Git.
