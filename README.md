# se7en Relais

Contrôle ton PC Windows depuis ton iPhone, de n'importe où, gratuitement.

À l'ouverture, se7en Relais affiche un **QR code**, un **lien unique** et un **code à 6 chiffres**. Le lien et le code changent à chaque ouverture. Scanne le QR code, entre le code dans Safari, et tu peux :

- **voir l'écran du PC** en direct, et zoomer dessus ;
- **bouger la souris** en mode Toucher (tu touches l'écran) ou en mode Trackpad ;
- faire un **clic gauche**, un **clic droit**, utiliser la **molette** et faire du **glisser-déposer** ;
- **écrire au clavier**, accents compris, avec Ctrl, Alt, Maj, Windows, les flèches et des raccourcis tout prêts.

Ça marche en Wi-Fi comme en 4G/5G, grâce à un tunnel [Cloudflare](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/) gratuit (`*.trycloudflare.com`, sans compte).

## Installation

Installe-le depuis **Seventh Hub**, ou télécharge `se7en-Relais.exe` dans les [releases](../../releases/latest). Il n'y a rien d'autre à installer : cloudflared est inclus dans l'exe.

Windows peut afficher un avertissement SmartScreen, parce que l'exécutable n'est pas signé. Clique sur « Informations complémentaires », puis sur « Exécuter quand même ».

## Sécurité

- Le lien contient un jeton aléatoire de 128 bits. Toute autre adresse renvoie une erreur 404.
- Le code PIN s'affiche uniquement sur l'écran du PC. Au bout de 5 codes faux, le lien est désactivé et un nouveau lien est créé.
- Un seul téléphone peut se connecter à la fois. Le bouton « Déconnecter » coupe l'accès immédiatement.
- Quand tu fermes l'appli, le tunnel est coupé et le lien ne fonctionne plus.

## Développement

Il faut le SDK .NET 10 sous Windows.

```powershell
dotnet run --project src/PhoneRemote        # lance l'appli (cloudflared doit être dans tools/)
powershell -ExecutionPolicy Bypass -File build.ps1   # → dist/se7en-Relais.exe + SHA-256
```

Organisation du code :

- `src/PhoneRemote/Server` : le serveur Kestrel (127.0.0.1 uniquement), la gestion du code PIN et de la session, et le WebSocket.
- `src/PhoneRemote/Input` : la souris et le clavier, via `SendInput`.
- `src/PhoneRemote/Capture` : la capture de l'écran en JPEG.
- `src/PhoneRemote/Tunnel` : le lancement de cloudflared.
- `src/PhoneRemote/wwwroot` : l'interface du PC (dans WebView2) et la page iPhone. Le design system est dans `ds/`.

Pour publier une nouvelle version : change `<Version>` dans le `.csproj`, lance `build.ps1`, puis crée une release GitHub `vX.Y.Z`. Joins-y `se7en-Relais.exe` et ajoute la ligne `SHA-256 : <hash>` dans les notes de la release. Seventh Hub proposera alors la mise à jour.
