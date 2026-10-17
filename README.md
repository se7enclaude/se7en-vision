# se7en Vision

Contrôle ton PC Windows depuis ton iPhone, de n'importe où, gratuitement.

À l'ouverture, se7en Vision affiche un **QR code**, un **lien unique** et **ton code à 6 chiffres**. Scanne le QR code : tu es connecté tout de suite, sans rien taper. Avec le lien copié, il faut entrer ton code. Ensuite, tu peux :

- **voir l'écran du PC** en direct, en **vidéo fluide à 30 images par seconde** (H.264, décodée par Safari depuis iOS 16.4), en plein écran en paysage, et zoomer dessus ;
- **bouger la souris** en mode Toucher (tu touches l'écran), en mode Trackpad, ou en mode **Joystick** : un joystick sous le pouce gauche et un gros bouton « Clic » sous le pouce droit (toucher = clic gauche, deux fois = clic droit, rester appuyé = glisser) ;
- faire un **clic gauche**, un **clic droit**, utiliser la **molette** et faire du **glisser-déposer** ;
- **écrire au clavier**, accents compris, avec Ctrl, Alt, Maj, Windows, les flèches et des raccourcis tout prêts.
- **verrouiller, mettre en veille, redémarrer ou éteindre le PC**, avec une confirmation à chaque fois.

Ça marche en Wi-Fi comme en 4G/5G, grâce à un tunnel [Cloudflare](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/) gratuit (`*.trycloudflare.com`, sans compte).

## Installation

Installe-le depuis **Seventh Hub**, ou télécharge `se7en-Vision.exe` dans les [releases](../../releases/latest). Il n'y a rien d'autre à installer : cloudflared est inclus dans l'exe.

Windows peut afficher un avertissement SmartScreen, parce que l'exécutable n'est pas signé. Clique sur « Informations complémentaires », puis sur « Exécuter quand même ».

## Sécurité

- Le lien contient un jeton aléatoire de 128 bits. Toute autre adresse renvoie une erreur 404.
- Le lien change à chaque ouverture. Le code à 6 chiffres est celui que tu choisis (« Choisir mon code ») : il est gardé d'une ouverture à l'autre, chiffré avec Windows (DPAPI). Les codes trop faciles (000000, 123456…) sont refusés. Au bout de 5 codes faux, le lien est désactivé et un nouveau lien est créé.
- Le QR code contient une clé secrète à usage unique (dans la partie `#` de l'adresse, jamais envoyée dans les journaux) : elle connecte sans code, puis un nouveau QR code s'affiche. Le lien copié ne contient pas cette clé.
- Un seul téléphone peut se connecter à la fois. Le bouton « Déconnecter » coupe l'accès immédiatement.
- Quand tu fermes l'appli, le tunnel est coupé et le lien ne fonctionne plus.

## Développement

Il faut le SDK .NET 10 sous Windows.

```powershell
dotnet run --project src/PhoneRemote        # lance l'appli (cloudflared doit être dans tools/)
powershell -ExecutionPolicy Bypass -File build.ps1   # → dist/se7en-Vision.exe + SHA-256
```

Organisation du code :

- `src/PhoneRemote/Server` : le serveur Kestrel (127.0.0.1 uniquement), la gestion du code PIN et de la session, et le WebSocket.
- `src/PhoneRemote/Input` : la souris et le clavier, via `SendInput`.
- `src/PhoneRemote/Capture` : la capture de l'écran, en vidéo H.264 (FFmpeg) ou en images JPEG.
- `src/PhoneRemote/Tunnel` : le lancement de cloudflared.
- `src/PhoneRemote/wwwroot` : l'interface du PC (dans WebView2) et la page iPhone. Le design system est dans `ds/`.

Pour publier une nouvelle version : change `<Version>` dans le `.csproj`, lance `build.ps1`, puis crée une release GitHub `vX.Y.Z`. Joins-y `se7en-Vision.exe` et ajoute la ligne `SHA-256 : <hash>` dans les notes de la release. Seventh Hub proposera alors la mise à jour.

## Composants inclus

cloudflared (Apache 2.0) et FFmpeg (GPLv3) sont inclus dans l'exe, comme programmes séparés. Voir [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) pour leurs licences et leurs sources.
