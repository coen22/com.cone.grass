# Offline shader compilation

This check compiles the package's actual HLSL functions against pinned, unmodified Unity Graphics headers. It runs the public Microsoft DirectX Shader Compiler (DXC) without installing or running Unity. It provides an executable shader compilation gate while Unity import, runtime binding, and rendered tests remain separate requirements.

## Run

Use Linux x86-64, Python 3.12 or newer, and Git. Ubuntu 24.04 is the tested environment. Run from the package root:

```sh
python3 -B -m unittest discover -s Tools~/ShaderChecks -p 'test_*.py' -v
python3 -B Tools~/ShaderChecks/check.py
```

The first compilation command downloads the compiler and sparse-checks out the public shader headers. Later runs verify and reuse `Validation~/ShaderDependencies`. The cache is owned by this check and marked with a receipt. If dependencies change, remove that directory and run again; the script refuses to replace an unmarked directory or silently use a modified cache. Download failures, compiler errors, missing outputs, unsupported extraction formats, and an empty program set fail the command.

Generated HLSL, per-invocation compiler logs, DXIL/SPIR-V binaries, and `report.json` go to `artifacts/shaders`. The report records entry points, keywords, compiler arguments, source hashes, compiler version, and dependency pins. When run in GitHub Actions, the script also writes an explicitly scoped job summary.

For CI, add an Ubuntu 24.04 job with the repository checkout, Python 3.12, and the commands above. Upload `artifacts/shaders` with `if: always()`. Unity credentials are unnecessary. This job can run alongside source checks, independently of the licensed Unity job.

## What is compiled

The script discovers every `.compute` and `.shader` file beneath `Runtime` and `Editor/GroundColor`. It compiles each declared compute kernel and each shader pass's vertex and fragment entry points. The current baseline contains **53 entry-point/keyword configurations**, compiled once to DXIL and once to SPIR-V: **106 compiler invocations**.

Coverage includes:

- All seven generation and motion-history compute kernels.
- Every grass capture, placement, contact-depth, contact-shadow, motion-copy, and motion-vector pass.
- Six grass forward-lighting configurations, covering main-light shadows, cascades, screen shadows, soft shadows, additional lights, clustered lights, ground normals, and the three fog modes.
- Four terrain-albedo configurations: baseline, mask maps, height blending, and both together.

HLSL blocks retain their original functions, includes, and source-line mapping. The extractor supports the package's single-SubShader layout with at most one shared `HLSLINCLUDE` before its passes. It rejects additional SubShaders, pass-scoped shared blocks, legacy CG blocks, and malformed entry declarations instead of silently dropping those programs. Unit tests check these failure cases, correct shared-source extraction, kernel-specific defines, archive extraction, and compiler-library integrity.

## Reproducible inputs

`dependencies.json` pins:

- [Microsoft DXC v1.9.2609](https://github.com/microsoft/DirectXShaderCompiler/releases/tag/v1.9.2609), using the official Linux archive and its published SHA-256 digest. The complete extracted compiler distribution, including shared libraries, is fingerprinted for cache reuse.
- [Unity Graphics commit `a7e4c051d256a781ab362c64316b125a1e104694`](https://github.com/Unity-Technologies/Graphics/commit/a7e4c051d256a781ab362c64316b125a1e104694). Its [URP package manifest](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.universal/package.json) declares **17.6.0**. The checkout is verified at that exact commit and must remain clean. This identifies a public source snapshot; it does not establish that every file is identical to an Editor-distributed package with the same version.

Both backends use HLSL 2018 and Shader Model 6.0 compiler profiles. DXIL selects Unity's D3D API headers; SPIR-V selects its Vulkan headers and uses Vulkan 1.1 with DirectX buffer layout. These are [documented DXC backend options](https://github.com/microsoft/DirectXShaderCompiler/blob/v1.9.2609/docs/SPIR-V.rst). `SHADER_TARGET` retains each source program's declared Unity target for preprocessor branches, and each invocation defines its actual shader stage.

The Unity version macro is `60060000`, representing 6000.6.0 in Unity 6's [documented `6MMMPPPP` format](https://docs.unity3d.com/6000.6/Documentation/Manual/shader-branching-unity-version.html). No Unity API stubs, replacement shader-library functions, or patched upstream headers are supplied.

## Scope and limits

A passing result verifies that DXC accepts and produces code for these specific HLSL entry points and keyword combinations. It can catch undeclared symbols, invalid overloads, incompatible types, missing includes, and backend code-generation errors that text checks cannot catch.

It does **not** establish Unity ShaderLab import, variant stripping, material/pass state, C# API compilation, GPU resource bindings, execution correctness, camera or RenderGraph integration, motion-vector image quality, timing improvements, or memory behavior. Shader Model 6.0 success also does not prove compatibility with the original Shader Model 3.5/4.5 targets, FXC, Metal, GLES, or every Unity-generated keyword combination. Changes to those acceptance claims still require the licensed Unity and graphics-device tests in `Documentation~/Validation.md`.
