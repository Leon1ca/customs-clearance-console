param([switch]$CheckOnly)

# Run in the account's own PowerShell so gh can use Windows Credential Manager.
# No token is requested, displayed, exported, or copied into this repository.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$repo = 'Leon1ca/customs-clearance-console'
$tag = 'v1.4.0'
$oldTag = 'v1.6.0'
$testedCommit = '4dc8d6b'
$expectedZipHash = 'd33da4bc42e57390617b469080c404fb3ac594c74c7d5f01c0be0c74287c2673'
$expectedOldTagObject = 'c9cb8c933b429ca6d9629dbffb63c27d2610670f'
$expectedOldCommit = '2eaf5fa1e106cacf8507d9dc6278cf8c10bcae10'
$outputRoot = Join-Path $repoRoot 'artifacts\releases\v1.4.0'
$notes = Join-Path $repoRoot 'docs\release-notes-v1.4.0.md'

function Invoke-Gh {
    $result = & gh @args
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed (exit $LASTEXITCODE). No further actions performed." }
    return $result
}

function Invoke-Git {
    $gitArguments = @($args)
    $operation = [string]$gitArguments[0]
    $networkOperation = $operation -in @('ls-remote', 'fetch', 'push')
    # Read/fetch and replaying the same non-force push are safe to retry.
    # Do not blindly replay deletion after an ambiguous network failure.
    $attemptLimit = if ($networkOperation -and $gitArguments -notcontains '--delete') { 4 } else { 1 }
    $transientFailure = '(?i)connection (was )?reset|recv failure|connection timed out|operation timed out|failed to connect|could not resolve host|remote end hung up|early eof|ssl_error_syscall|tls connection was non-properly terminated|requested url returned error: (429|502|503|504)'
    $logRoot = Join-Path $repoRoot 'artifacts\dev-state'
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    $errorFile = Join-Path $logRoot ('publish-git-' + [Guid]::NewGuid().ToString('N') + '.stderr')
    # PowerShell 5 treats redirected native stderr as ErrorRecord objects.
    # Inspect the exit code ourselves without losing the command's stdout.
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        for ($attempt = 1; $attempt -le $attemptLimit; $attempt++) {
            if ($networkOperation) { Write-Host "Git $operation (attempt $attempt/$attemptLimit, HTTP/1.1)..." }
            $result = & git -c http.sslBackend=openssl -c http.version=HTTP/1.1 -c http.lowSpeedLimit=1 -c http.lowSpeedTime=30 -c 'credential.helper=' -c 'credential.helper=!gh auth git-credential' @gitArguments 2> $errorFile
            $exitCode = $LASTEXITCODE
            $errorText = if (Test-Path -LiteralPath $errorFile) { [IO.File]::ReadAllText($errorFile) } else { '' }
            if ($exitCode -eq 0) {
                # Successful Git progress is written to stderr. Do not display
                # PowerShell 5's NativeCommandError formatting for exit code 0.
                if ($networkOperation) { Write-Host "Git $operation completed." }
                return $result
            }
            if ($errorText.Trim()) { Write-Host $errorText.Trim() -ForegroundColor Yellow }
            if ($attempt -lt $attemptLimit -and $errorText -match $transientFailure) {
                $delay = [int][Math]::Pow(2, $attempt)
                Write-Host "Temporary network failure. Retrying in $delay seconds; no force-push."
                Start-Sleep -Seconds $delay
                continue
            }
            [IO.File]::WriteAllText((Join-Path $logRoot 'publish-network-last-error.txt'),
                "git $operation; exit $exitCode; attempt $attempt/$attemptLimit`r`n$errorText", [Text.UTF8Encoding]::new($false))
            throw "Git $operation failed (exit $exitCode, attempt $attempt/$attemptLimit). Publication stopped; no later cleanup steps were run. See artifacts/dev-state/publish-network-last-error.txt."
        }
    } finally {
        if (Test-Path -LiteralPath $errorFile) { Remove-Item -LiteralPath $errorFile -Force }
    }
}

function Get-Releases {
    $pages = (Invoke-Gh api "repos/$repo/releases?per_page=100" --paginate --slurp) -join "`n"
    $result = @()
    foreach ($page in ($pages | ConvertFrom-Json)) { $result += @($page) }
    return $result
}

function Find-VerifiedAsset {
    param($Release, [System.IO.FileInfo]$File)
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $named = @($Release.assets | Where-Object { $_.name -eq $File.Name })
    if ($named.Count -gt 0) {
        # A conflicting same-name upload must not be silently ignored.
        $candidates = $named
    } else {
        # GitHub can sanitize non-ASCII upload filenames. Match content, not
        # the local Chinese filename, and never weaken size/SHA-256 checks.
        $candidates = @($Release.assets | Where-Object { $_.digest -eq $digest -and $_.size -eq $File.Length })
    }
    if ($candidates.Count -eq 0) { return $null }
    if ($candidates.Count -ne 1 -or $candidates[0].state -ne 'uploaded' -or
        $candidates[0].size -ne $File.Length -or $candidates[0].digest -ne $digest) {
        throw "Conflicting/incomplete asset: $($File.Name). No existing asset was overwritten."
    }
    Write-Host "Verified asset: $($candidates[0].name) ($($File.Length) bytes, SHA-256 matches)."
    return $candidates[0]
}

function Assert-OldTagUnchanged {
    $refs = @(Invoke-Git ls-remote origin "refs/tags/$oldTag" "refs/tags/$oldTag^{}")
    if ($refs.Count -eq 0) { return $false }
    $object = @($refs | Where-Object { $_ -match "\srefs/tags/$oldTag`$" })
    $peeled = @($refs | Where-Object { $_ -match "\srefs/tags/$oldTag\^\{\}`$" })
    if ($object.Count -ne 1 -or ($object[0] -split '\s+')[0] -ne $expectedOldTagObject -or
        $peeled.Count -ne 1 -or ($peeled[0] -split '\s+')[0] -ne $expectedOldCommit) {
        throw 'Remote v1.6.0 tag changed since review. Refusing to delete it.'
    }
    return $true
}

$previousConsoleEncoding = [Console]::OutputEncoding
$previousOutputEncoding = $OutputEncoding
Push-Location $repoRoot
try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $OutputEncoding = [Console]::OutputEncoding
    $zipFiles = @(Get-ChildItem -LiteralPath $outputRoot -File | Where-Object { $_.Name -like '*-Windows-x64-v1.4.0.zip' })
    if ($zipFiles.Count -ne 1) { throw 'Expected exactly one v1.4.0 portable ZIP.' }
    $zip = $zipFiles[0]
    if ((Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedZipHash) {
        throw 'Portable ZIP differs from the verified build. Stop and rebuild/review.'
    }
    if (-not (Test-Path -LiteralPath $notes)) { throw 'Release notes are missing.' }
    if ((Invoke-Git remote get-url origin) -ne "https://github.com/$repo.git") { throw 'Unexpected Git remote.' }
    if ((Invoke-Git branch --show-current) -ne 'main') { throw 'Please use the main branch.' }
    if (Invoke-Git status --porcelain) { throw 'Working tree has uncommitted changes. Stop and review them first.' }
    Invoke-Git merge-base --is-ancestor $testedCommit HEAD
    Invoke-Git diff --exit-code $testedCommit HEAD -- src docs/release-notes-v1.4.0.md
    if ($CheckOnly) {
        Write-Host 'LOCAL_CHECKS_OK: verified ZIP, matching source, correct remote, clean main branch.'
        return
    }

    Invoke-Gh auth status
    $login = ((Invoke-Gh api user --jq .login) -join '').Trim()
    if ($login -ne 'Leon1ca') { throw "Unexpected signed-in account: $login" }
    $repoInfo = ((Invoke-Gh api "repos/$repo") -join "`n") | ConvertFrom-Json
    if ($repoInfo.private -or -not $repoInfo.permissions.push) { throw 'Expected the existing public repository with push permission.' }
    $releases = @(Get-Releases)
    $oldRelease = @($releases | Where-Object { $_.tag_name -eq $oldTag })
    $oldReleaseId = if ($oldRelease.Count -eq 1) { $oldRelease[0].id } else { $null }
    $null = Assert-OldTagUnchanged

    Write-Host '[1/5] Checking branch ancestry and publishing source (no force-push)...'
    Invoke-Git fetch origin main
    Invoke-Git merge-base --is-ancestor origin/main HEAD
    $head = ((Invoke-Git rev-parse HEAD) -join '').Trim()
    $localTag = @(Invoke-Git tag --list $tag)
    $releaseCommit = $head
    if ($localTag.Count -gt 0) {
        # Resuming after a script-only fix must preserve the published tag.
        $releaseCommit = ((Invoke-Git rev-parse "$tag^{}") -join '').Trim()
        Invoke-Git merge-base --is-ancestor $testedCommit $releaseCommit
        Invoke-Git merge-base --is-ancestor $releaseCommit HEAD
        Invoke-Git diff --exit-code $testedCommit $releaseCommit -- src docs/release-notes-v1.4.0.md
    }
    $remoteTag = @(Invoke-Git ls-remote origin "refs/tags/$tag" "refs/tags/$tag^{}")
    if ($remoteTag.Count -gt 0) {
        $target = @($remoteTag | Where-Object { $_ -match '\^\{\}$' })
        if ($target.Count -eq 0) { $target = $remoteTag }
        if (($target[0] -split '\s+')[0] -ne $releaseCommit) { throw 'Remote v1.4.0 tag differs from the verified local release tag; refusing to replace it.' }
    }
    if ($localTag.Count -eq 0) {
        Invoke-Git tag -a $tag -m 'Consolidated v1.4.0 release' $head
    }
    Invoke-Git push --atomic origin 'HEAD:refs/heads/main' "refs/tags/$tag"

    Write-Host '[2/5] Uploading the portable ZIP and checksum to a draft release...'
    $release = @(Get-Releases | Where-Object { $_.tag_name -eq $tag })
    if ($release.Count -eq 0) {
        Invoke-Gh release create $tag --repo $repo --verify-tag --draft --title 'Customs Clearance Console v1.4.0' --notes-file $notes
        $release = @(Get-Releases | Where-Object { $_.tag_name -eq $tag })
    }
    if ($release.Count -ne 1) { throw 'Could not uniquely resolve the v1.4.0 release.' }
    $releaseId = $release[0].id
    foreach ($file in @($zip, (Get-Item -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt')))) {
        $info = ((Invoke-Gh api "repos/$repo/releases/$releaseId") -join "`n") | ConvertFrom-Json
        $existing = Find-VerifiedAsset -Release $info -File $file
        if ($null -eq $existing) {
            Invoke-Gh release upload $tag $file.FullName --repo $repo
        }
    }

    Write-Host '[3/5] Verifying uploaded SHA-256, then publishing v1.4.0 as Latest...'
    $info = ((Invoke-Gh api "repos/$repo/releases/$releaseId") -join "`n") | ConvertFrom-Json
    foreach ($file in @($zip, (Get-Item -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt')))) {
        $asset = Find-VerifiedAsset -Release $info -File $file
        if ($null -eq $asset) {
            $details = ($info.assets | Select-Object name,size,state,digest | ConvertTo-Json -Compress) -join ''
            throw "Uploaded file not found by name or content: $($file.Name). Assets: $details. v1.6.0 is untouched."
        }
    }
    Invoke-Gh release edit $tag --repo $repo --draft=false --prerelease=false --latest --notes-file $notes
    $latest = ((Invoke-Gh api "repos/$repo/releases/latest") -join "`n") | ConvertFrom-Json
    if ($latest.id -ne $releaseId -or $latest.draft -or $latest.tag_name -ne $tag) { throw 'Latest release verification failed; old release retained.' }

    Write-Host '[4/5] Removing ONLY the reviewed v1.6.0 release, its assets, and tag...'
    $hasOldTag = Assert-OldTagUnchanged
    $currentOld = @(Get-Releases | Where-Object { $_.tag_name -eq $oldTag })
    if ($currentOld.Count -gt 0) {
        if ($currentOld.Count -ne 1 -or -not $oldReleaseId -or $currentOld[0].id -ne $oldReleaseId) {
            throw 'The old release changed during publication. Refusing deletion.'
        }
        Invoke-Gh release delete $oldTag --repo $repo --cleanup-tag --yes
    } elseif ($hasOldTag) {
        Invoke-Git push origin --delete "refs/tags/$oldTag"
    }

    Write-Host '[5/5] Final verification...'
    if (@(Get-Releases | Where-Object { $_.tag_name -eq $oldTag }).Count -gt 0 -or
        @(Invoke-Git ls-remote origin "refs/tags/$oldTag").Count -gt 0) { throw 'Old release/tag still exists; inspect before retry.' }
    $mainRef = @(Invoke-Git ls-remote origin refs/heads/main)
    if ($mainRef.Count -ne 1 -or ($mainRef[0] -split '\s+')[0] -ne $head) { throw 'Remote main changed; inspect its status.' }
    Write-Host 'PUBLISH_OK: v1.4.0 is Latest. v1.6.0 release/assets/tag removed. Commit history preserved.' -ForegroundColor Green
    Write-Host "https://github.com/$repo/releases/tag/$tag"
} finally {
    Pop-Location
    [Console]::OutputEncoding = $previousConsoleEncoding
    $OutputEncoding = $previousOutputEncoding
}
