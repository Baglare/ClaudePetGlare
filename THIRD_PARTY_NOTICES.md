# Third-party components and notices

Petglare's MIT license covers its own source code, not third-party components, trademarks or community pet submissions.

- **Microsoft.ML.OnnxRuntime / Microsoft.ML.OnnxRuntime.Managed 1.20.1** — Microsoft Corporation, MIT; [upstream LICENSE](https://github.com/microsoft/onnxruntime/blob/v1.20.1/LICENSE), [ThirdPartyNotices.txt](https://github.com/microsoft/onnxruntime/blob/v1.20.1/ThirdPartyNotices.txt). The native runtime is bundled in the EXE. The release packaging script copies the license and notices shipped with the exact restored NuGet packages into the ZIP.
- **.NET 10 Desktop Runtime** — required separately, not bundled (framework-dependent build); [upstream licensing](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT).
- **U²-Net (small)** and **IS-Net (general)** — optional model weights downloaded from the [rembg release](https://github.com/danielgatis/rembg/releases/tag/v0.0.0); [U²-Net source](https://github.com/xuebinqin/U-2-Net), [IS-Net/DIS source](https://github.com/xuebinqin/DIS). The original source repositories use Apache-2.0; [rembg source](https://github.com/danielgatis/rembg) uses MIT. Model weights have their own applicable terms; consult the original model sources before redistributing or using them commercially. Models are not bundled in Petglare releases.
- **Community pets** — separate CC BY 4.0 licensing and attribution policy in the [Workshop repository](https://github.com/Baglare/Petglare-Workshop). Original contributors retain their rights.

Claude and Anthropic are their respective owners' marks. Petglare is an independent project.
