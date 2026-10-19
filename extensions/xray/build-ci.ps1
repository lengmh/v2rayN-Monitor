param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$ProbeRegressionSource,
    [string]$SourceArchive
)

$ErrorActionPreference = 'Stop'
$revision = '5ca6f4b7d4dc20a881d4330e498892697627ec0c'
$archiveSha256 = '45de3ead5186fea442b04c662c554a1ffd1d7bd9093a83410f2edabe74fb766e'
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'OutputRoot must be a new directory.' }
if (-not (Test-Path -LiteralPath $ProbeRegressionSource -PathType Leaf)) { throw 'Native probe regressions are required.' }
New-Item -ItemType Directory -Path $output | Out-Null
$artifacts = Join-Path $output 'artifacts'
New-Item -ItemType Directory -Path $artifacts | Out-Null
$archive = Join-Path $output 'xray-fixed-source.tar.gz'
$sourceUrl = "https://codeload.github.com/XTLS/Xray-core/tar.gz/$revision"
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
$source = Join-Path $output "Xray-core-$revision"
$env:GOTOOLCHAIN = 'local'
$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
$goVersion = (& go version) -join "`n"
if ($LASTEXITCODE -ne 0 -or $goVersion -ne 'go version go1.26.8 windows/amd64') {
    throw "Unexpected Go toolchain: $goVersion"
}
$manifests = @{}
foreach ($name in @('go.mod', 'go.sum')) {
    $manifests[$name] = (Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash
}
$package = Join-Path $source 'app/observatory/probestats'
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probe-stats.go') -Destination (Join-Path $package 'probe_stats.go')
Copy-Item -LiteralPath $ProbeRegressionSource -Destination (Join-Path $package 'probe_stats_test.go')
$patch = Join-Path $PSScriptRoot 'probe-stats.patch'
$flags = '-s -w -X github.com/xtls/xray-core/core.build=monitor-test.1'
Push-Location -LiteralPath $source
try {
    & git apply --check --unidiff-zero $patch
    if ($LASTEXITCODE -ne 0) { throw 'Probe patch check failed.' }
    & git apply --unidiff-zero $patch
    if ($LASTEXITCODE -ne 0) { throw 'Probe patch application failed.' }
    & go test -mod=readonly -count=1 -v ./app/observatory/probestats ./app/observatory ./app/observatory/burst ./app/metrics 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'go-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Native regression failed.' }
    $required = @('TestMonitorProbeCountersKeepConnectionTagAndPartialErrors',
        'TestMonitorProbeExcludedUnknownConcurrentAndDisabled')
    $testLog = Get-Content -LiteralPath (Join-Path $output 'go-tests.log') -Raw -Encoding utf8
    foreach ($name in $required) {
        if ($testLog -notmatch "(?m)^--- PASS: $name ") { throw "Required regression did not pass: $name" }
    }
    & go build -mod=readonly -buildvcs=false -trimpath -ldflags $flags -o (Join-Path $artifacts 'xray.exe') ./main
    if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed.' }
    foreach ($name in @('go.mod', 'go.sum')) {
        if ((Get-FileHash -LiteralPath $name -Algorithm SHA256).Hash -ne $manifests[$name]) {
            throw "Dependency manifest changed: $name"
        }
    }
    $version = (& (Join-Path $artifacts 'xray.exe') version) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^Xray 26\.7\.28 .*monitor-test\.1 ') { throw 'Candidate version mismatch.' }
    $hash = (Get-FileHash -LiteralPath (Join-Path $artifacts 'xray.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $artifacts 'LICENSE-Xray-core')
    Copy-Item -LiteralPath $patch -Destination $artifacts
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probe-stats.go') -Destination $artifacts
    Copy-Item -LiteralPath $ProbeRegressionSource -Destination (Join-Path $artifacts 'probe-stats_test.go')
    $hash | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256.txt') -Encoding utf8NoBOM
    [ordered]@{
        sourceRevision = $revision
        sourceUrl = $sourceUrl
        sourceArchiveSha256 = $archiveSha256
        sourceIdentityMatchesInstalledCore = $false
        patchSha256 = (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant()
        probeSourceSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'probe-stats.go') -Algorithm SHA256).Hash.ToLowerInvariant()
        probeRegressionSha256 = (Get-FileHash -LiteralPath $ProbeRegressionSource -Algorithm SHA256).Hash.ToLowerInvariant()
        goVersion = $goVersion
        buildFlags = $flags
        version = $version
        coreSha256 = $hash
        dependencyManifestsUnchanged = $true
        testsPassed = $true
        requiredTests = $required
        ciHeadCommit = $env:GITHUB_SHA
        ciRunId = $env:GITHUB_RUN_ID
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifacts 'provenance.json') -Encoding utf8NoBOM
} finally {
    Pop-Location
}
