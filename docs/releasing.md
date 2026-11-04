# Publishing a release

First merge a PR whose **build** and **hosted desktop E2E** checks both passed on
its latest commit. For a broad modernization PR, describe the full scope: settings
and localization, tab rendering and DPI, input, workspaces, runtime ownership,
native resources, build tooling, tests and documentation.

Record the chosen version and release date in [CHANGELOG.md](../CHANGELOG.md),
keeping the user-facing changes. Check that [README.md](../README.md) describes
the version being released.

Tag the merged commit and push the tag, substituting the intended version below:

```powershell
git switch main
git pull --ff-only
git tag v2026.10.07
git push origin v2026.10.07
```

A `vMAJOR.MINOR.PATCH` tag, optionally with a suffix such as `-beta.1`, triggers
[the release workflow](../.github/workflows/release.yml). It:

1. Builds the standalone Release exe, embedding the tag version.
2. Runs all 13 regression suites in three parallel groups on separate Windows
   runners, without coverage instrumentation. Suites stay serial within each group.
3. Runs the isolated Release smoke test, then starts Full desktop E2E against the
   built exe as soon as the build job succeeds, without waiting for regressions.
4. Creates a **draft** GitHub Release with `WindowTabs.exe` and generated notes
   only after all preceding checks pass. A suffixed tag creates a prerelease draft.

Review the draft, include the changelog's user-facing highlights, and publish it on
GitHub. Pushing a tag prepares a draft; it does not publish the release or update
users' installed copies. Users download and replace the exe themselves.

## Local Windows copy

After a new versioned Release build, copy only the standalone executable into the
local green-software folder. Date versions use the local `yyyy.MM.dd` date. The
command checks the embedded app/informational version and compares SHA256 hashes;
it keeps older version folders and their settings unchanged.

```powershell
$ErrorActionPreference = 'Stop'
$version = '2026.11.04'
dotnet build WindowTabs.sln -c Release -p:Version=$version
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

$source = (Resolve-Path 'WtProgram\bin\Release\WindowTabs.exe').Path
$assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($source)
$attributes = @([Reflection.CustomAttributeData]::GetCustomAttributes($assembly) |
    Where-Object { $_.AttributeType.FullName -ceq 'System.Reflection.AssemblyInformationalVersionAttribute' })
if ($attributes.Count -ne 1 -or
    [string]$attributes[0].ConstructorArguments[0].Value -cne $version) {
    throw "WindowTabs.exe does not carry informational version $version."
}

$expectedFileVersion = [version](($version -split '-', 2)[0] + '.0')
$actualFileVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($source).FileVersion
if ($actualFileVersion -ne $expectedFileVersion) {
    throw "WindowTabs.exe file version is $actualFileVersion, expected $expectedFileVersion."
}

$destinationDirectory = Join-Path 'D:\Software Green' ('WindowTabs-' + $version)
$destination = Join-Path $destinationDirectory 'WindowTabs.exe'
New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $destination -Force

$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
if ($sourceHash -ne $destinationHash) {
    throw 'The copied WindowTabs.exe does not match the Release build.'
}
Write-Host "Copied WindowTabs $version to $destinationDirectory"
```

This step runs on the local Windows machine. Hosted CI cannot access `D:`, and a
local Release build does not authorize creating a tag, pushing it, or publishing
the GitHub release.
