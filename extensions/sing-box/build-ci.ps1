param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$SourceArchive
)

$ErrorActionPreference = 'Stop'
$revision = '1ac1a339cb1223e9c70eae14c44411c75033c02d'
$archiveSha256 = '8420c7723828a8d9d062c3fafee28c7b7e0d20a4c904fa7ff283f6e884edd537'
$version = '1.14.1-monitor-test.3'
$sourceUrl = "https://codeload.github.com/SagerNet/sing-box/tar.gz/$revision"
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'OutputRoot must be a new directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$artifacts = Join-Path $output 'artifacts'
New-Item -ItemType Directory -Path $artifacts | Out-Null
$archive = Join-Path $artifacts 'sing-box-fixed-source.tar.gz'
if ($SourceArchive) {
    Copy-Item -LiteralPath $SourceArchive -Destination $archive
} else {
    Invoke-WebRequest -Uri $sourceUrl -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveSha256) {
    throw 'Fixed upstream source archive SHA256 mismatch.'
}
& tar -xzf $archive -C $output
if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
$source = Join-Path $output "sing-box-$revision"
$env:GOTOOLCHAIN = 'local'
$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
$goVersion = (& go version) -join "`n"
if ($LASTEXITCODE -ne 0 -or $goVersion -ne 'go version go1.26.8 windows/amd64') {
    throw "Unexpected Go toolchain: $goVersion"
}
$monitorCommit = (& git -C (Join-Path $PSScriptRoot '../..') rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Monitor checkout identity unavailable.' }
$manifestHashes = @{}
foreach ($name in @('go.mod', 'go.sum')) {
    $manifestHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash
}
$patchHashes = @{}
Push-Location -LiteralPath $source
try {
    foreach ($name in @('monitor.patch', 'member-stats.patch')) {
        $patch = Join-Path $PSScriptRoot $name
        $patchHashes[$name] = (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant()
        & git apply --check --unidiff-zero $patch
        if ($LASTEXITCODE -ne 0) { throw "Patch check failed: $name" }
        & git apply --unidiff-zero $patch
        if ($LASTEXITCODE -ne 0) { throw "Patch application failed: $name" }
        Copy-Item -LiteralPath $patch -Destination $artifacts
    }
    $tags = (Get-Content -LiteralPath 'release/DEFAULT_BUILD_TAGS_WINDOWS' -Raw -Encoding utf8).Trim() + ',with_v2ray_api'
    $flags = (Get-Content -LiteralPath 'release/LDFLAGS' -Raw -Encoding utf8).Trim()
    & go test -mod=readonly -ldflags $flags -tags $tags -run '^TestMonitor' -count=1 -v ./cmd/sing-box 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'go-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Core regression failed.' }
    $requiredTests = @('TestMonitorStatsEndpoint', 'TestMonitorMemberBinding',
        'TestMonitorMemberConcurrentIndependentConnections', 'TestMonitorMembersDisabledPreservesOriginalCounters')
    $testLog = Get-Content -LiteralPath (Join-Path $output 'go-tests.log') -Raw -Encoding utf8
    foreach ($name in $requiredTests) {
        if ($testLog -notmatch "(?m)^--- PASS: $name ") { throw "Required test did not pass: $name" }
    }
    $binary = Join-Path $artifacts 'sing-box-client.exe'
    & go build -mod=readonly -tags $tags -trimpath -ldflags ($flags + " -s -w -X github.com/sagernet/sing-box/constant.Version=$version") -o $binary ./cmd/sing-box 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'go-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed.' }
    foreach ($name in @('go.mod', 'go.sum')) {
        if ((Get-FileHash -LiteralPath $name -Algorithm SHA256).Hash -ne $manifestHashes[$name]) {
            throw "Dependency manifest changed: $name"
        }
    }
    $binaryVersion = (& $binary version) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $binaryVersion -notmatch "^sing-box version $([regex]::Escape($version))(\r?\n|$)") {
        throw 'Candidate version mismatch.'
    }
    $binaryVersion | Set-Content -LiteralPath (Join-Path $output 'candidate-version.log') -Encoding utf8NoBOM
    [ordered]@{
        monitorCommit = $monitorCommit
        ciHeadCommit = $env:GITHUB_SHA
        ciRunId = $env:GITHUB_RUN_ID
        sourceRevision = $revision
        sourceUrl = $sourceUrl
        sourceArchiveSha256 = $archiveSha256
        patchSha256 = $patchHashes
        goVersion = $goVersion
        tags = $tags
        buildFlags = $flags + " -s -w -X github.com/sagernet/sing-box/constant.Version=$version"
        version = $version
        coreSha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
        dependencyManifestsUnchanged = $true
        testsPassed = $true
        requiredTests = $requiredTests
        deployed = $false
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $artifacts 'build-provenance.json') -Encoding utf8NoBOM
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE'), (Join-Path $PSScriptRoot 'README.md'), $PSCommandPath -Destination $artifacts
} finally {
    Pop-Location
}
