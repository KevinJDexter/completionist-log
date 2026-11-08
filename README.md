# Completionist Log

An Android app (Capacitor) for tracking Final Fantasy XIV completion, expansion by expansion: story, duties, jobs, collections, exchanges, world content, leves, Gold Saucer, PvP and more.

## Getting it on your phone
Every push to `main` builds a signed APK and attaches it to a new **Release**. On the phone, open the latest release, download `completionist-log.apk` and install it (allow Chrome to install apps the first time). Later versions install over the top and keep your progress.

## Layout
- `www/` – the app itself (plain HTML/JS, one page) and `appdata.json` (game data, patch 7.57)
- `android/` – the Capacitor Android project (open with `npm run open` in Android Studio)
- `assets/` – icon and splash sources (`npm run icons` regenerates the Android ones)
- `.github/workflows/android.yml` – the APK build

## Building locally
```
npm install
npm run sync          # copy www/ into the Android project
npm run open          # Android Studio, or: npm run build:debug
```
Release builds are signed with the key in the repository secrets (`KEYSTORE_BASE64`, `KEYSTORE_PASSWORD`, `KEY_ALIAS`, `KEY_PASSWORD`). Locally, put the same values in `android/keystore.properties` (git-ignored). An APK signed with a different key won't install over the existing app.

Progress is stored on the device; use **Settings → Export backup** now and then.

## Game export (Dalamud plugin)
`dalamud/` holds **Progress Exporter**, a read-only Dalamud plugin. In XIVLauncher: `/xlsettings` → Experimental → Custom Plugin Repositories, add
`https://raw.githubusercontent.com/KevinJDexter/completionist-log/main/dalamud/repo.json`, save, then install it from `/xlplugins`.
In game, `/progex` writes `Documents\Completionist Log\completionist-log-game-<date>.json` (add `name` to include your character name).
Move the file to the phone and use **Settings → Import game export** in the app.
