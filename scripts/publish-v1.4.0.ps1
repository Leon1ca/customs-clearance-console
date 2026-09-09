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
    $result = & git -c http.sslBackend=openssl -c 'credential.helper=' -c 'credential.helper=!gh auth git-credential' @args
    if ($LASTEXITCODE -ne 0) { throw "Git command failed (exit $LASTEXITCODE). No force-push will be attempted." }
    return $result
}

function Get-Releases {
    $pages = (Invoke-Gh api "repos/$repo/releases?per_page=100" --paginate --slurp) -join "`n"
    $result = @()
    foreach ($page in ($pages | ConvertFrom-Json)) { $result += @($page) }
    return $result
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
    $remoteTag = @(Invoke-Git ls-remote origin "refs/tags/$tag" "refs/tags/$tag^{}")
    if ($remoteTag.Count -gt 0) {
        $target = @($remoteTag | Where-Object { $_ -match '\^\{\}$' })
        if ($target.Count -eq 0) { $target = $remoteTag }
        if (($target[0] -split '\s+')[0] -ne $head) { throw 'Remote v1.4.0 already points to different code; refusing to replace it.' }
    }
    $localTag = @(Invoke-Git tag --list $tag)
    if ($localTag.Count -gt 0) {
        if (((Invoke-Git rev-parse "$tag^{}") -join '').Trim() -ne $head) { throw 'Local v1.4.0 tag points to different code.' }
    } else {
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
        $existing = @($info.assets | Where-Object { $_.name -eq $file.Name })
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existing.Count -eq 0) {
            Invoke-Gh release upload $tag $file.FullName --repo $repo
        } elseif ($existing.Count -ne 1 -or $existing[0].state -ne 'uploaded' -or
                  $existing[0].size -ne $file.Length -or $existing[0].digest -ne $digest) {
            throw "An existing asset differs or is incomplete: $($file.Name). It has not been overwritten."
        }
    }

    Write-Host '[3/5] Verifying uploaded SHA-256, then publishing v1.4.0 as Latest...'
    $info = ((Invoke-Gh api "repos/$repo/releases/$releaseId") -join "`n") | ConvertFrom-Json
    foreach ($file in @($zip, (Get-Item -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt')))) {
        $asset = @($info.assets | Where-Object { $_.name -eq $file.Name })
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($asset.Count -ne 1 -or $asset[0].state -ne 'uploaded' -or $asset[0].size -ne $file.Length -or $asset[0].digest -ne $digest) {
            throw 'Remote file verification failed. v1.6.0 is untouched.'
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
