[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceDirectory,

    [Parameter(Mandatory)]
    [string]$ZigExecutable,

    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot "src/OpenShelf.App/runtimes/win-x64/native"
}

$sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
$sourcePath = Join-Path $sourceRoot "src"
$zigPath = (Resolve-Path -LiteralPath $ZigExecutable).Path
$configPath = Join-Path $projectRoot "native/libmobi/windows-config.h"
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $projectRoot "build/vendor-cache/zig-global-cache"
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $projectRoot "build/vendor-cache/zig-local-cache"

New-Item -ItemType Directory -Path @(
    $OutputDirectory,
    $env:ZIG_GLOBAL_CACHE_DIR,
    $env:ZIG_LOCAL_CACHE_DIR
) -Force | Out-Null

$sources = @(
    "buffer.c",
    "compression.c",
    "debug.c",
    "index.c",
    "memory.c",
    "meta.c",
    "parse_rawml.c",
    "read.c",
    "structure.c",
    "util.c",
    "write.c",
    "miniz.c"
) | ForEach-Object { Join-Path $sourcePath $_ }

$arguments = @(
    "cc",
    "-target", "x86_64-windows-gnu",
    "-std=c99",
    "-O2",
    "-shared",
    "-fvisibility=hidden",
    "-include", $configPath,
    "-DMINIZ_NO_STDIO",
    "-DMINIZ_NO_ZLIB_COMPATIBLE_NAMES",
    "-DMINIZ_NO_TIME",
    "-DMINIZ_NO_ARCHIVE_APIS",
    "-DMINIZ_NO_ARCHIVE_WRITING_APIS",
    "-D_POSIX_C_SOURCE=200112L",
    "-I", $sourcePath,
    "-o", (Join-Path $OutputDirectory "libmobi.dll")
) + $sources

& $zigPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "libmobi compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Built: $(Join-Path $OutputDirectory 'libmobi.dll')"
