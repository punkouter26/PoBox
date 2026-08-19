# PoBox — Azure Static Web App

The Unity WebGL player build lives in this folder. It is a plain static site:
`index.html`, `Build/`, `TemplateData/`, plus `staticwebapp.config.json`, which
Azure reads for routing, MIME types and compression headers.

Scenes in the build: `SCN_MENU` (opening menu) → `SCN_TEST_BALANCE_CONTEST` or
`SCN_TEST_WALK_CONTEST`, chosen from the menu. Training scenes are excluded.

## Deploy

### One-off, from this machine

```powershell
npm install -g @azure/static-web-apps-cli
az staticwebapp create --name pobox --resource-group pobox-rg --location eastus2
swa deploy ./WEB --deployment-token <token> --env production
```

Get the token with:

```powershell
az staticwebapp secrets list --name pobox --resource-group pobox-rg --query "properties.apiKey" -o tsv
```

### Continuous, from GitHub

`.github/workflows/azure-static-web-apps.yml` deploys this folder on every push
to `main`. It needs one repository secret, `AZURE_STATIC_WEB_APPS_API_TOKEN`,
set to the same token as above. Creating the Static Web App through the Azure
Portal with GitHub as the source adds that secret for you.

## Testing locally before you deploy

```powershell
swa start ./WEB
```

Serving `index.html` by double-clicking it will NOT work — browsers block
WebAssembly over `file://`. Always go through a server.

## Why `staticwebapp.config.json` matters

Unity compresses the build to `.br` (Brotli). The browser only knows to
decompress those if the server sends `Content-Encoding: br`. Without it you get
a black canvas and a console error about an invalid magic number — the single
most common Unity-on-static-hosting failure. The `routes` block sets that
header.

Decompression Fallback is also enabled in Player Settings, so the build still
loads even if a host ignores these headers. That costs some load time; once you
have confirmed the headers work you can switch it off in
**Player Settings → Publishing Settings** and rebuild for a faster first load.

`Cross-Origin-Opener-Policy` and `Cross-Origin-Embedder-Policy` are set because
Unity needs them for SharedArrayBuffer. Remove both if you embed this page in a
third-party iframe that breaks under COEP.

## Known limits of this build

- **ML brains run on the CPU.** Unity Inference Engine has no GPU backend on
  WebGL, and the page is single-threaded. Several ragdolls each running a
  3x512 network every physics tick is the main performance risk.
- **PhysX is single-threaded here too.** The 4-fighter walk race is a lighter
  test than the 8-fighter balance contest.
- Portrait 9:16 is preserved; the canvas letterboxes on desktop.
