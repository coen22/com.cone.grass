# Developer tools

Scripts for working on the package itself. Users of the package do not need them; Unity ignores this folder.

Run everything from the repository root.

## Source and shader checks (no Unity licence)

```sh
python3 Tools~/validate_source.py .
python3 -B -m unittest discover -s Tools~/tests -v
python3 -B -m unittest discover -s Tools~/ShaderChecks -p 'test_*.py' -v
python3 -B Tools~/ShaderChecks/check.py
dotnet run --project Tools~/SourceChecks -- .
```

| Tool | What it checks |
|---|---|
| `validate_source.py` | Package metadata and source layout. |
| `tests/` | Unit tests for these Python tools. |
| `ShaderChecks/` | Compiles every compute kernel and shader pass with Microsoft DXC to DXIL and SPIR-V against pinned Unity Graphics headers. Needs Linux x86-64, Python 3.12+ and Git; output goes to `artifacts/shaders`. See [ShaderChecks/README.md](ShaderChecks/README.md). |
| `SourceChecks/` | Parses the C# sources with Roslyn (C# 9) for Editor, Release and Checked player configurations. Needs .NET 8. It does not bind Unity APIs. |

These catch syntax and HLSL compile errors only. Unity import, runtime behaviour and rendering need the Editor tests below.

## Validation project

```sh
python3 Tools~/create_validation_project.py --unity 6000.6.0f1 --urp 17.6.0
```

Creates a disposable project in `Validation~/Project` (ignored by Git) with the package as a local dependency, the Unity Test Framework, package test discovery, the MicroVerse bridge sample and a native URP setup. Rerunning it updates the template files and keeps existing `.meta` files. It refuses to overwrite an unrelated project. Pass `--output` to choose another folder.

Open the project in that Unity version and run the EditMode and PlayMode tests. GPU tests need a graphics device; do not use `-nographics` for them.

```sh
Unity -batchmode -projectPath Validation~/Project -runTests -testPlatform EditMode \
  -testResults results/editmode.xml -logFile results/editmode.log
python3 Tools~/report_unity_results.py results --require-gpu
```

Do not pass `-quit` to a Test Runner invocation. `report_unity_results.py` fails if GPU cases are missing or skipped when `--require-gpu` is given.

## Standalone builds

In the validation project, **Tools > Cone > Grass Validation > Create Rendering Scene** creates a test scene: a URP terrain, a saved synthetic mask used through the MicroVerse bridge, the renderer feature, a light, an obstacle and a camera.

```sh
Unity -batchmode -nographics -quit -buildTarget StandaloneLinux64 -projectPath Validation~/Project \
  -executeMethod GrassValidationBuild.BuildCurrent -logFile results/build.log
python3 Tools~/report_build_results.py Validation~/Project/Builds
```

`BuildCurrent` makes a clean and then an incremental development player with Managed Code Variant set to Checked (restored afterwards). Windows and macOS targets work when installed.

Launch the player with `-grassSmoke -grassSmokeOutput results/player` to run its rendering probe, which captures grass on/off, wind, a camera pan, contact shadows, render scales, MSAA and empty sources. Check the result with:

```sh
python3 Tools~/report_player_results.py results/player
python3 Tools~/report_player_results.py results/player --require-msaa 2 4 8
```

Add `-grassSmokeMotion` to the player and `--require-motion` to the reporter for the optional motion-vector stage.

## Other scripts

| Script | Use |
|---|---|
| `probe_unity_cli.py` | Records how a local Unity install behaves from the command line: `--editor <Unity> --project <path> --output <dir>`. |

## Continuous integration

The GitHub workflow runs the source and shader checks on every pull request. The Unity test and build jobs run only when Unity licence secrets are configured (`UNITY_LICENSE_SERVER`, or `UNITY_EMAIL` and `UNITY_PASSWORD` with `UNITY_LICENSE` or `UNITY_SERIAL`).
