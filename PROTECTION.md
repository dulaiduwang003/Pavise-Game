# Pavise protected release builds

`build.cmd -b dev` and `dev.cmd` use the unobfuscated compiler path so local
development, stack traces, and self-tests stay useful. `build.cmd -b prod`
produces the protected release with the free ConfuserEx 1.6.0 toolchain:

```powershell
.\build.cmd -b prod
```

The production build applies Unicode symbol renaming and namespace flattening,
dynamic string/constant encoding, switch-based control-flow transformation,
strong reference proxies, ILDasm suppression, and anti-tamper protection. Each
build receives a new random seed. It then builds a second no-elevation
executable from the same sources, protects it with the same rules, and executes
both icon and full WinForms rendering smoke paths. The final artifact is only
copied into place after metadata, PE loading, and runtime checks pass.

The build tool is downloaded to `%LOCALAPPDATA%\PaviseBuildTools`, pinned to an
exact version and SHA-256, and never committed to the repository. Raw release
assemblies live only in a randomly named `%TEMP%\Pavise-ProtectedBuild`
directory and are deleted after the build. Pass `-KeepStage` only while
diagnosing a protection failure; the retained directory contains unobfuscated
code and must not be shared.

## Signing

Install the Windows SDK signing tools and place the code-signing certificate in
the current user's certificate store. Configure the release shell without
placing secrets in source control:

```powershell
$env:PAVISE_SIGNTOOL = "C:\path\to\signtool.exe"
$env:PAVISE_SIGN_CERT_SHA1 = "certificate thumbprint without spaces"
$env:PAVISE_TIMESTAMP_URL = "http://timestamp.digicert.com" # optional
.\release-protected.cmd -Output Pavise.exe -RequireSignature
```

The final `.sha256` file is written beside the executable. Signing deliberately
happens after protection because any later assembly rewrite invalidates an
Authenticode signature.

## Protection boundary

This pipeline raises the cost of decompilation and binary patching. It does not
make local executable code secret from a determined analyst. The application
remains obfuscated managed IL, so a sufficiently motivated analyst can still
adapt public tooling or inspect the program at runtime.

Do not enable compressor/packer, anti-debug, invalid-opcode, or anti-dump
protections for the normal Pavise release. Pavise already requests elevation
and manipulates other processes; those techniques materially increase
false-positive risk and make support failures harder to diagnose.

## Third-party build tool

Production builds use ConfuserEx 1.6.0 as a build-time tool. ConfuserEx is licensed
under the MIT License. Its source and license are available at:

<https://github.com/mkaring/ConfuserEx/tree/v1.6.0>

The tool itself is not redistributed with Pavise. Review whether the injected
runtime requires an accompanying third-party notice in the final distribution
before shipping a closed-source release.
