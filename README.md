# Hololoco app

The Hololoco phone app, built in Unity 6.3 LTS (6000.3.28f1) for Android first. Players find drops at real
places through the camera, capture them, and keep, redeem, trade or leave them for someone else. It talks to the
same Supabase project and accounts as the website, through the database functions listed in the website's
`docs/app-api.md`.

## What's here

```
Assets/Hololoco/Scripts/   the app
  App.cs                   the screens (Nearby, Bag, Me, sign-in, redeem code) and tapping drops to capture them
  ArPlacer.cs              puts drops in the camera view: Geospatial terrain anchors, or GPS and compass
  DropView.cs              one drop's 3D model, sized to its real width
  Supabase.cs              sign-in with an emailed code, and calls to the database
  Drops.cs                 the database's answers as C#, and error codes in plain words
  Location.cs              GPS and compass
Assets/Editor/HololocoSetup.cs   project setup and builds from the command line
Assets/Resources/hololoco.uss    the look
Packages/com.google.ar.core.arfoundation.extensions/   ARCore Extensions 1.57.0, with one fix (below)
```

## Setting up

1. `Assets/Resources/hololoco-config.json` isn't in git. Create it with the website's Supabase address and
   publishable key: `{ "supabaseUrl": "https://<ref>.supabase.co", "publishableKey": "sb_publishable_…", "siteUrl": "https://www.hololoco.com" }`.
2. For precise placement, put an ARCore API key (Google Cloud, ARCore API enabled) in
   `F:\Hololoco\secrets\arcore-api-key.txt`. The build reads it from there, so it never sits in the project.
   Without it, drops are placed from GPS and the compass.

## Building

```
"C:\Program Files\Unity\Hub\Editor\6000.3.28f1\Editor\Unity.exe" -batchmode -projectPath F:\Hololoco\HololocoApp ^
  -executeMethod HololocoSetup.BuildAndroid -logFile F:\Hololoco\unity-build.log
```

That configures everything (Android settings, ARCore, Geospatial, the scene) and writes
`F:\Hololoco\Builds\Hololoco.apk`. Install it on a phone with USB debugging on:
`adb install -r F:\Hololoco\Builds\Hololoco.apk`.

## The ARCore Extensions fix

ARCore Extensions 1.57.0 always compiles an iOS build step that needs Unity's iOS Build Support, even for
Android builds. The copy in `Packages/` wraps `Editor/Scripts/Internal/IOSPostProcessBuild.cs` in `#if UNITY_IOS`.
When upgrading, copy the new version into `Packages/` and make the same change, or install iOS Build Support
and delete the copy.
