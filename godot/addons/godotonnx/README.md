# godotonnx addon — Godot 4 (.NET)

Godot-side glue for the [`GodotOnnx`](https://github.com/i-icc/godotonnx) core library:
local AIVMX (AivisSpeech-compatible) Japanese text-to-speech, running fully in-process —
no external TTS server and no Python runtime.

```text
addons/godotonnx/
├── plugin.cfg / plugin.gd    plugin metadata (optional to enable; runtime nodes need no activation)
├── icon.svg
├── LICENSE                   MIT (same as the repository)
├── README.md
└── scripts/
    ├── TtsPaths.cs           model/dictionary path resolution (incl. user:// and res://)
    ├── TtsSpeaker.cs         LocalTtsEngine → AudioStreamPlayer adapter node (queue, cancel, 16-bit WAV)
    └── TtsModelManager.cs    one-shot asset download (SHA-256 verified, progress/cancel signals)
```

## Requirements

- Godot 4.7 **.NET edition** (the standard Godot build cannot load C# scripts) and .NET SDK 8.0+
- The core engine as a NuGet reference (`GodotOnnx`)
- Voice model + dictionary files, **not bundled** — sources and licenses:
  the main repository README ("Models you need") and `THIRD-PARTY-LICENSES.md`

## Install

1. Copy this folder into your project:

   ```bash
   mkdir -p <your-project>/addons
   cp -R addons/godotonnx <your-project>/addons/
   ```

2. Reference the core library from your `.csproj`:

   ```xml
   <ItemGroup>
       <PackageReference Include="GodotOnnx" Version="0.1.0" />
   </ItemGroup>
   ```

3. Use it from any C# script:

   ```csharp
   var paths = GodotOnnx.Godot.TtsPaths.Resolve();
   if (paths is null)
   {
       GD.PushError(GodotOnnx.Godot.TtsPaths.DescribeMissing()); // prints what it searched
       return;
   }

   var speaker = new GodotOnnx.Godot.TtsSpeaker();
   AddChild(speaker);
   await speaker.InitializeAsync(paths.Aivmx, paths.BertModel, paths.Tokenizer, paths.NaistJdic);

   speaker.Speak("こんにちは、世界", styleId: 0, styleWeight: 1f, speed: 1f);
   // speaker.StopAll()  stops synthesis + playback
   ```

## Node behavior

- `TtsSpeaker : Node` serializes synthesis jobs and streams decoded 16-bit PCM into an
  `AudioStreamPlayer`; overlapped requests queue up instead of starting twice.
- Signals: `StateChanged(state, message)`, `PlaybackStarted(text, styleName, duration)`,
  `QueueChanged(synthPending, playPending)`.
- `SynthesisOptions` (style id/weight, speed, sdp/noise scales) reach the engine unchanged —
  values default to the Style-Bert-VITS2 reference parameters.
- Path resolution order: `GODOTONNX_ROOT` (env) → repo root (walk-up to `GodotOnnx.sln`) →
  `res://` (PCK in exported games) → `user://`; each base prefers `models/`, with the legacy
  development `research/` layout still supported as a fallback.

## Fetching the assets (optional)

`TtsModelManager : Node` wraps the engine-less `GodotOnnx.Setup.ModelDownloader`: it checks a base
directory (`TtsModelManager.Inspect(base)`, SHA-256 comparison, no network), then downloads whatever
is missing there (`user://` by default) after **your** consent UI —

```csharp
var manager = new GodotOnnx.Godot.TtsModelManager();
AddChild(manager);
manager.ProgressChanged += (message, fraction) => GD.Print($"{fraction:P0} {message}");
manager.Finished += (ok, message) => GD.Print($"{(ok ? "done" : "failed")}: {message}");

if (!GodotOnnx.Godot.TtsModelManager.Inspect(GodotOnnx.Godot.TtsPaths.UserBaseDir()).Complete)
{
    // Show your license screen, then:
    await manager.DownloadAsync(GodotOnnx.Godot.TtsPaths.UserBaseDir());
    // onlyNames optional: ["voice"], ["bert"], ["dictionary"] subsets
}
// manager.Cancel() aborts mid-download; finished files are kept, retried later.
```

Signals: `ProgressChanged(message, fraction)` (0..1), `Finished(ok, message)`. Downloads land in
`base/models/` using pinned URLs and hashes from `ModelCatalog`; already-valid files are skipped, so
re-running or resuming is safe. The demo project shows a complete consent-first first-launch flow.

The in-repository demo (`godot/`) consumes exactly this addon folder — it doubles as the
integration sample.
