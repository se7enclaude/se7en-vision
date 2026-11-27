# se7en Vision

Contrôle un PC Windows à distance, depuis ton iPhone ou depuis un autre PC, de n'importe où, gratuitement. Fluide comme un outil d'assistance à distance : capture par la carte graphique, jusqu'à 144 images par seconde, connexion directe entre les deux appareils quand c'est possible.

À l'ouverture, se7en Vision affiche un **QR code**, un **lien unique** et **ton code à 6 chiffres**. Scanne le QR code avec l'iPhone : tu es connecté tout de suite, sans rien taper. Avec le lien copié (sur un iPhone ou dans l'onglet « Se connecter à un PC » d'un autre PC), il faut entrer le code. Ensuite, tu peux :

- **voir l'écran du PC** en direct, en résolution native, jusqu'à 144 images par seconde (H.264, HEVC ou AV1 encodés par la carte graphique NVIDIA, AMD ou Intel ; le processeur prend le relais sinon) ;
- choisir la **qualité** : Rapide, Équilibré ou Netteté maximale ; le débit s'adapte ensuite tout seul à la connexion ;
- voir le **curseur du PC sans retard**, avec sa vraie forme, dessiné chez toi dès qu'il bouge ;
- **bouger la souris** avec une vraie souris (viewer PC), ou sur iPhone en mode Toucher, Trackpad ou **Joystick** ;
- **écrire au clavier** : le vrai clavier du PC qui regarde, ou celui de l'iPhone avec Ctrl, Alt, Maj, Windows, flèches et raccourcis ;
- passer d'un **écran** à l'autre, ou les afficher **tous en même temps** ;
- montrer quelque chose avec le **pointeur laser** ou dessiner au **crayon** sur l'écran du PC contrôlé ;
- **envoyer et recevoir des fichiers** par glisser-déposer, dans les deux sens ;
- **verrouiller, mettre en veille, redémarrer ou éteindre le PC**, avec une confirmation à chaque fois.

L'image passe en **direct entre les deux appareils (WebRTC)** quand les box le permettent, avec un ping de quelques millisecondes. Sinon, elle passe par un tunnel [Cloudflare](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/) gratuit (`*.trycloudflare.com`, sans compte), en Wi-Fi comme en 4G/5G. Le passage de l'un à l'autre est automatique.

## Installation

Installe-le depuis **Seventh OS**, ou télécharge `se7en-Vision.exe` dans les [releases](../../releases/latest). Il n'y a rien d'autre à installer : cloudflared et FFmpeg sont inclus dans l'exe.

Windows peut afficher un avertissement SmartScreen, parce que l'exécutable n'est pas signé. Clique sur « Informations complémentaires », puis sur « Exécuter quand même ».

## Les trois onglets

- **Mon PC** : le QR code, le lien, ton code, qui est connecté et par quel chemin, et une zone où déposer un fichier pour l'envoyer à l'appareil connecté.
- **Se connecter à un PC** : colle le lien d'un autre PC (ou reprends-le dans les récents) : son écran s'ouvre dans une fenêtre, en plein écran si tu veux.
- **Réglages** : encodeur et format vidéo, cadence maximale, économie d'énergie, connexion directe, laser et crayon sur tes écrans, dossier de réception, encodeurs testés sur ce PC.

## Sécurité

- Le lien contient un jeton aléatoire de 128 bits. Toute autre adresse renvoie une erreur 404.
- Le lien change à chaque ouverture. Le code à 6 chiffres est celui que tu choisis (« Choisir mon code ») : il est gardé d'une ouverture à l'autre, chiffré avec Windows (DPAPI). Les codes trop faciles (000000, 123456…) sont refusés. Au bout de 5 codes faux, le lien est désactivé et un nouveau lien est créé.
- Le QR code contient une clé secrète à usage unique (dans la partie `#` de l'adresse, jamais envoyée dans les journaux) : elle connecte sans code, puis un nouveau QR code s'affiche. Le lien copié ne contient pas cette clé.
- Un seul appareil peut se connecter à la fois. Le bouton « Déconnecter » coupe l'accès immédiatement.
- La connexion directe est chiffrée (DTLS-SRTP) et ne s'ouvre qu'à l'appareil déjà authentifié sur le lien. Elle révèle ton adresse IP à cet appareil (et la sienne à toi) ; tu peux la désactiver dans Réglages.
- Les fichiers reçus vont dans `Téléchargements\se7en Vision`, avec un nom nettoyé, sans jamais écraser un fichier existant, et ne sont gardés qu'une fois leur empreinte SHA-256 vérifiée. Un programme reçu (.exe, .bat…) est signalé.
- Le laser et le crayon ne font que dessiner par-dessus l'écran : ils ne cliquent sur rien. Un petit filigrane « se7en Vision » accompagne le dessin.
- Quand tu fermes l'appli, le tunnel est coupé et le lien ne fonctionne plus.

## Développement

Il faut le SDK .NET 10 sous Windows.

```powershell
dotnet run --project src/PhoneRemote        # lance l'appli (cloudflared et ffmpeg doivent être dans tools/)
dotnet test tests/PhoneRemote.Tests         # tests unitaires (dont une boucle WebRTC locale)
powershell -ExecutionPolicy Bypass -File build.ps1   # → dist/se7en-Vision.exe + SHA-256
```

Options en ligne de commande : `--probe` teste les encodeurs et écrit `encoders.json` ; `--displays` liste les écrans ; `--selftest` vérifie la connexion directe de bout en bout sans navigateur ; `--viewer <lien>` ouvre seulement la fenêtre « Se connecter à un PC ».

Organisation du code (`src/PhoneRemote`) :

- `Core` : logique pure et testée — découpage des flux H.264/HEVC/AV1, protocole, planificateur de qualité, contrôleur de débit, découpage des fichiers, noms de fichiers sûrs, disposition des écrans.
- `Media` : sonde des encodeurs, lignes de commande FFmpeg, processus FFmpeg et session vidéo (redémarrage avec recouvrement, repli d'encodeur). `Media/Capture` : écrans et sorties DXGI.
- `Rtc` : la connexion directe (SIPSorcery) et son autotest.
- `Server` : le serveur Kestrel (127.0.0.1 uniquement), la session et le code, le WebSocket, la connexion du viewer (v1 et v2).
- `Input`, `Cursor`, `Overlay`, `Files` : souris et clavier (`SendInput`), suivi du curseur, laser et crayon, transfert de fichiers.
- `Tunnel` : le lancement de cloudflared.
- `wwwroot` : l'interface du PC (`desktop`, dans WebView2), la page du viewer (`client`, iPhone et PC, modules ES) et le design system (`ds`, polices embarquées).

Pour publier une nouvelle version : change `<Version>` dans le `.csproj`, lance `build.ps1`, puis crée une release GitHub `vX.Y.Z`. Joins-y `se7en-Vision.exe` et ajoute la ligne `SHA-256 : <hash>` dans les notes de la release. Seventh Hub proposera alors la mise à jour.

## Composants inclus

cloudflared (Apache 2.0) et FFmpeg (GPLv3) sont inclus dans l'exe comme programmes séparés ; SIPSorcery (BSD 3-Clause) assure la connexion directe ; les polices Geist (OFL) et les icônes Lucide (ISC) sont embarquées. Voir [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) pour les licences et les sources.
