[CmdletBinding()]
param(
    [ValidateSet("win-x64", "osx-x64", "osx-arm64", "linux-x64")]
    [string]$Runtime = "win-x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot "OpenShelfReader.slnx"
$appProject = Join-Path $projectRoot "src/OpenShelf.App/OpenShelf.App.csproj"
$speechBrokerProject = Join-Path $projectRoot "src/OpenShelf.SpeechBroker/OpenShelf.SpeechBroker.csproj"
$artifactRoot = Join-Path $projectRoot "artifacts"
$publishRoot = Join-Path $artifactRoot "publish/$Runtime"
$archiveRoot = Join-Path $artifactRoot "packages"
$packageStage = Join-Path $artifactRoot "package-staging/$Runtime"
$speechBrokerRoot = Join-Path $artifactRoot "speech-brokers"
$buildProperties = [xml](Get-Content -LiteralPath (Join-Path $projectRoot "Directory.Build.props") -Raw)
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version) -or $version.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw "Directory.Build.props does not contain a package-safe Version value."
}

$fullArtifactRoot = [IO.Path]::GetFullPath($artifactRoot)
$fullPublishRoot = [IO.Path]::GetFullPath($publishRoot)
$fullPackageStage = [IO.Path]::GetFullPath($packageStage)
if (-not $fullPublishRoot.StartsWith(
        $fullArtifactRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside the project artifact directory."
}
if (-not $fullPackageStage.StartsWith(
        $fullArtifactRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage a package outside the project artifact directory."
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null

if (-not $SkipRestore) {
    dotnet restore $solution
    if ($LASTEXITCODE -ne 0) {
        throw "Package restore failed."
    }
}

dotnet build $solution --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

if (-not $SkipTests) {
    dotnet test $solution --configuration $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed."
    }
}

if ($Runtime -eq "win-x64") {
    foreach ($brokerRuntime in @("win-x64", "win-x86")) {
        $brokerPublishRoot = Join-Path $speechBrokerRoot $brokerRuntime
        if (Test-Path -LiteralPath $brokerPublishRoot) {
            Remove-Item -LiteralPath $brokerPublishRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $brokerPublishRoot -Force | Out-Null

        if (-not $SkipRestore) {
            dotnet restore $speechBrokerProject --runtime $brokerRuntime
            if ($LASTEXITCODE -ne 0) {
                throw "Speech broker restore failed for $brokerRuntime."
            }
        }

        dotnet publish `
            $speechBrokerProject `
            --configuration $Configuration `
            --runtime $brokerRuntime `
            --self-contained true `
            --no-restore `
            --output $brokerPublishRoot `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:PublishTrimmed=false `
            -p:PublishReadyToRun=false `
            -p:DebugSymbols=false `
            -p:DebugType=None
        if ($LASTEXITCODE -ne 0) {
            throw "Speech broker publish failed for $brokerRuntime."
        }

        $brokerExecutable = Join-Path $brokerPublishRoot "OpenShelf.SpeechBroker.exe"
        if (-not (Test-Path -LiteralPath $brokerExecutable -PathType Leaf)) {
            throw "Speech broker executable was not found for $brokerRuntime."
        }
    }
}

if (-not $SkipRestore) {
    dotnet restore $appProject --runtime $Runtime
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime-specific package restore failed."
    }
}

$publishArguments = @(
    "publish"
    $appProject
    "--configuration"
    $Configuration
    "--runtime"
    $Runtime
    "--self-contained"
    "true"
    "--no-restore"
    "--output"
    $publishRoot
    "-p:PublishTrimmed=false"
    "-p:PublishReadyToRun=false"
    "-p:DebugSymbols=false"
    "-p:DebugType=None"
    "-p:SpeechBrokerRoot=$speechBrokerRoot"
)

if ($Runtime -eq "win-x64") {
    $publishArguments += @(
        "-p:PublishSingleFile=true"
        "-p:IncludeNativeLibrariesForSelfExtract=true"
        "-p:IncludeAllContentForSelfExtract=true"
        "-p:EnableCompressionInSingleFile=true"
    )
} else {
    $publishArguments += "-p:PublishSingleFile=false"
}

& dotnet @publishArguments

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed."
}

$executableName = if ($Runtime.StartsWith("win-", [StringComparison]::Ordinal)) {
    "OpenShelfReader.exe"
} else {
    "OpenShelfReader"
}

$executablePath = Join-Path $publishRoot $executableName
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Published executable was not found at $executablePath."
}

if ($Runtime -eq "win-x64") {
    # These package-supplied native debug symbols are not needed at runtime and
    # are intentionally kept out of the standalone distribution directory.
    foreach ($nativePdbName in @("libHarfBuzzSharp.pdb", "libSkiaSharp.pdb")) {
        $nativePdbPath = Join-Path $publishRoot $nativePdbName
        if (Test-Path -LiteralPath $nativePdbPath -PathType Leaf) {
            Remove-Item -LiteralPath $nativePdbPath -Force
        }
    }

    $standaloneExecutable = Get-Item -LiteralPath $executablePath
    $minimumStandaloneSize = 50MB
    if ($standaloneExecutable.Length -lt $minimumStandaloneSize) {
        throw (
            "Published executable is too small to contain the self-contained runtime " +
            "and bundled native resources: $($standaloneExecutable.Length) bytes."
        )
    }

    $publishEntries = @(Get-ChildItem -LiteralPath $publishRoot -Force)
    $isStandaloneOutput = $false
    if ($publishEntries.Count -eq 1) {
        $isStandaloneOutput = (-not $publishEntries[0].PSIsContainer) -and (
            [string]::Equals(
                $publishEntries[0].Name,
                $executableName,
                [StringComparison]::OrdinalIgnoreCase)
        )
    }
    if (-not $isStandaloneOutput) {
        $entryNames = ($publishEntries | ForEach-Object { $_.Name }) -join ", "
        throw (
            "The win-x64 publish directory must contain only the standalone executable. " +
            "Found: $entryNames"
        )
    }

    $smokeStartInfo = [Diagnostics.ProcessStartInfo]::new()
    $smokeStartInfo.FileName = $executablePath
    $smokeStartInfo.Arguments = "--smoke-test"
    $smokeStartInfo.WorkingDirectory = $publishRoot
    $smokeStartInfo.UseShellExecute = $false
    $smokeStartInfo.CreateNoWindow = $true
    $smokeProcess = [Diagnostics.Process]::Start($smokeStartInfo)
    if ($null -eq $smokeProcess) {
        throw "Published executable smoke test could not be started."
    }
    try {
        if (-not $smokeProcess.WaitForExit(90000)) {
            $smokeProcessId = $smokeProcess.Id
            taskkill.exe /PID $smokeProcessId /T /F 2>&1 | Out-Null
            throw "Published executable smoke test timed out after 90 seconds."
        }
        if ($smokeProcess.ExitCode -ne 0) {
            throw "Published executable smoke test failed with exit code $($smokeProcess.ExitCode)."
        }
    } finally {
        $smokeProcess.Dispose()
    }
}

$archiveName = if ($Runtime.StartsWith("win-", [StringComparison]::Ordinal)) {
    "OpenShelfReader-$version-$Runtime.zip"
} else {
    "OpenShelfReader-$version-$Runtime.tar.gz"
}
$archivePath = Join-Path $archiveRoot $archiveName
$checksumPath = "$archivePath.sha256"
$fullArchiveRoot = [IO.Path]::GetFullPath($archiveRoot)
$fullArchivePath = [IO.Path]::GetFullPath($archivePath)
if (-not $fullArchivePath.StartsWith(
        $fullArchiveRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create a package outside the project artifact directory."
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

if (Test-Path -LiteralPath $packageStage) {
    Remove-Item -LiteralPath $packageStage -Recurse -Force
}
New-Item -ItemType Directory -Path $packageStage -Force | Out-Null

$archiveHash = $null
try {
    if ($Runtime -eq "win-x64") {
        Copy-Item -LiteralPath $executablePath -Destination $packageStage -Force
    } else {
        Copy-Item `
            -Path (Join-Path $publishRoot "*") `
            -Destination $packageStage `
            -Recurse `
            -Force
    }

    foreach ($legalDocument in @(
        "LICENSE",
        "THIRD_PARTY_NOTICES.md",
        "README.md",
        "SECURITY.md")) {
        Copy-Item `
            -LiteralPath (Join-Path $projectRoot $legalDocument) `
            -Destination $packageStage `
            -Force
    }

    if ($Runtime.StartsWith("win-", [StringComparison]::Ordinal)) {
        Compress-Archive `
            -Path (Join-Path $packageStage "*") `
            -DestinationPath $archivePath `
            -CompressionLevel Optimal
    } else {
        tar -czf $archivePath -C $packageStage .
        if ($LASTEXITCODE -ne 0) {
            throw "Portable archive creation failed."
        }
    }

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $checksumPath -Value "$archiveHash  $archiveName" -Encoding ascii
} finally {
    if (Test-Path -LiteralPath $packageStage) {
        Remove-Item -LiteralPath $packageStage -Recurse -Force
    }
}

Write-Host "Published executable: $executablePath"
Write-Host "Portable package: $archivePath"
Write-Host "SHA-256: $archiveHash"
