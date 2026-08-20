# CI/CD

Two workflows in [`.github/workflows`](../.github/workflows):

```
push to main ──► build-web.yml ──► azure-static-web-apps.yml ──► Azure Static Web App
   (Unity source only)   (Unity WebGL build)   (deploy WEB/)
```

## `build-web.yml` — Build WebGL

Spins up `unityVersion: 6000.5.6f1` in a container via
[`game-ci/unity-builder`](https://game.ci/docs/github/builder), runs
`PoBox.Editor.Build_WebGL.Build` (the entry point added by the build-web fix
earlier this session), and uploads `WEB/` as the `web-build` artifact.

Triggers:

| When | Why |
|------|-----|
| Push to `main` with changes under `Assets/`, `Packages/`, `ProjectSettings/`, `config/`, or this workflow file | Rebuilds the player |
| Manual (`workflow_dispatch`) | Verify the build before committing `WEB/` |

`WEB/**` is **deliberately excluded** from the trigger path filter — the
deploy job consumes the artifact, not the working tree, so a push that only
touches `WEB/` is a no-op for the build runner (and avoids the infinite
loop where every rebuild would re-trigger itself).

## `azure-static-web-apps.yml` — Deploy

Triggered by `workflow_run` on `build-web.yml` completing successfully on
`main`. Downloads the `web-build` artifact, sanity-checks the contents, and
hands them to `Azure/static-web-apps-deploy@v1`. Manual dispatch exists for
edge cases — prefer rerunning the build job.

## Required secrets

| Secret | Used by | How to set it |
|--------|---------|---------------|
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Deploy job | Provision the Static Web App through the Azure Portal with GitHub as the source — Azure adds the secret for you |
| `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`, `UNITY_SERIAL` | Build job | Activate Unity on a Windows machine with `unityhub --activate`, then copy the resulting `.ulf` file into a base64-encoded secret named `UNITY_LICENSE`. Email/password/serial are fallbacks for personal seats |

## First-time setup

1. Provision the Static Web App in the Azure Portal with `WEB/` as `app_location` and no `api_location`. Copy the deployment token into the GitHub secret.
2. Push to `main`. The first one will fail at the Unity license activation step — that's expected. Add the license secrets (see table) and re-run the build job.
3. From then on, every push that touches source code rebuilds and deploys in ~5-10 minutes.

## Local reproduction

```powershell
.\build-web.ps1          # builds into .\WEB using the local Unity install
swa start .\WEB          # serves the build for browser-based verification
```