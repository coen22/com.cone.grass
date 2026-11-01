# Developer tools

Scripts for working on the package itself. Users of the package do not need them; Unity ignores this folder.
Run everything from the repository root.

## Checks without Unity

```sh
python3 Tools~/ci/check_package.py                                   # release shape: package.json, docs, .meta files
python3 Tools~/ci/check_source.py                                    # shader includes, Unity GUIDs, JSON metadata
python3 -B -m unittest discover -s Tools~/ShaderChecks -p 'test_*.py'
python3 -B Tools~/ShaderChecks/check.py                              # DXC: every kernel and pass to DXIL and SPIR-V
dotnet format whitespace --folder --verify-no-changes .              # layout against .editorconfig
```

`ShaderChecks/` compiles every compute kernel and shader pass with Microsoft DXC against pinned Unity Graphics headers.
It needs Linux x86-64, Python 3.12+ and Git; output goes to `artifacts/shaders`. See
[ShaderChecks/README.md](ShaderChecks/README.md). It catches HLSL errors only, not Unity import or rendering.

## Unity tests

Create an empty Unity 6000.6 project with the Universal Render Pipeline, add the package from disk, and list it as
testable in `Packages/manifest.json`:

```json
"testables": ["com.cone.grass"]
```

Run the `Cone.Grass.Tests.Editor` (EditMode) and `Cone.Grass.Tests.Runtime` (PlayMode) assemblies from the Test Runner
or from the command line. GPU cases need a graphics device, so do not pass `-nographics`:

```sh
Unity -batchmode -projectPath <project> -runTests -testPlatform EditMode -testResults editmode.xml -logFile editmode.log
Unity -batchmode -projectPath <project> -runTests -testPlatform PlayMode -testResults playmode.xml -logFile playmode.log
```

## Continuous integration

- `.github/workflows/package-ci.yml` is the shared Cone package workflow: package shape, the `Tools~/ci/check_*.py`
  hooks, `dotnet format whitespace`, and EditMode and PlayMode tests in a generated project. The Unity jobs run only
  when the repository has a `UNITY_LICENSE` secret, and say so when they skip.
- `.github/workflows/shader-check.yml` runs the DXC shader compile above.
