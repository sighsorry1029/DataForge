param (
    [ValidateSet("UpdateManifest", "ValidatePackage", "PromotePackages")]
    [string] $Mode = "UpdateManifest",
    [string] $ManifestFile,
    [string] $VersionString,
    [string] $ExpectedName,
    [string] $IconFile,
    [string] $ThunderstoreZip,
    [string] $NexusZip,
    [string] $DllFileName,
    [string] $ThunderstoreDestination,
    [string] $NexusDestination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require([string] $Name, [string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Parameter -$Name is required for mode '$Mode'."
    }
}

function Test-Manifest([string] $Content, [string] $DisplayPath, [string] $PackageName, [string] $PackageVersion) {
    try {
        $manifest = $Content | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Manifest '$DisplayPath' is not valid JSON: $($_.Exception.Message)"
    }

    foreach ($field in @("name", "version_number", "website_url", "description", "dependencies")) {
        if ($null -eq $manifest.PSObject.Properties[$field]) {
            throw "Manifest '$DisplayPath' is missing required property '$field'."
        }
    }
    if ($manifest.name -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest.name) -or
        $manifest.version_number -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest.version_number) -or
        $manifest.website_url -isnot [string] -or
        $manifest.description -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest.description) -or
        $manifest.dependencies -isnot [System.Array]) {
        throw "Manifest '$DisplayPath' has an invalid required field."
    }
    foreach ($dependency in $manifest.dependencies) {
        if ($dependency -isnot [string] -or [string]::IsNullOrWhiteSpace($dependency)) {
            throw "Manifest dependencies in '$DisplayPath' must be non-empty strings."
        }
    }
    if ($PackageName -and $manifest.name -cne $PackageName) {
        throw "Manifest name '$($manifest.name)' does not match '$PackageName'."
    }
    if ($PackageVersion -and $manifest.version_number -cne $PackageVersion) {
        throw "Manifest version '$($manifest.version_number)' does not match '$PackageVersion'."
    }
}

function Test-Icon([string] $Path) {
    if (-not [System.IO.File]::Exists($Path)) {
        throw "Icon '$Path' was not found."
    }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 24 -or
        ($bytes[0..7] -join ",") -cne "137,80,78,71,13,10,26,10" -or
        ($bytes[8..11] -join ",") -cne "0,0,0,13" -or
        [System.Text.Encoding]::ASCII.GetString($bytes, 12, 4) -cne "IHDR") {
        throw "Icon '$Path' is not a valid PNG."
    }
    $width = [System.BitConverter]::ToUInt32([byte[]] $bytes[19..16], 0)
    $height = [System.BitConverter]::ToUInt32([byte[]] $bytes[23..20], 0)
    if ($width -ne 256 -or $height -ne 256) {
        throw "Icon '$Path' must be 256x256 pixels, but is ${width}x${height}."
    }
}

function Test-Zip([string] $Path, [string[]] $ExpectedEntries, [string] $PackageName, [string] $PackageVersion, [switch] $HasManifest) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    } catch {
        throw "Package '$Path' is not a readable ZIP: $($_.Exception.Message)"
    }
    try {
        $actual = @($archive.Entries | ForEach-Object FullName | Sort-Object)
        $expected = @($ExpectedEntries | Sort-Object)
        if (($actual -join "|") -cne ($expected -join "|")) {
            throw "Package '$Path' entries are '$($actual -join ",")'; expected '$($expected -join ",")'."
        }
        foreach ($entry in $archive.Entries) {
            $stream = $entry.Open()
            try {
                $stream.CopyTo([System.IO.Stream]::Null)
            } finally {
                $stream.Dispose()
            }
        }
        if ($HasManifest) {
            $reader = New-Object System.IO.StreamReader($archive.GetEntry("manifest.json").Open())
            try {
                Test-Manifest $reader.ReadToEnd() "$Path!/manifest.json" $PackageName $PackageVersion
            } finally {
                $reader.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }
}

function Promote-Pair([string] $ThunderstoreSource, [string] $NexusSource, [string] $ThunderstoreTarget, [string] $NexusTarget) {
    $token = [System.Guid]::NewGuid().ToString("N")
    $items = @(
        @{ Name = "Thunderstore"; Source = [System.IO.Path]::GetFullPath($ThunderstoreSource); Target = [System.IO.Path]::GetFullPath($ThunderstoreTarget) },
        @{ Name = "Nexus"; Source = [System.IO.Path]::GetFullPath($NexusSource); Target = [System.IO.Path]::GetFullPath($NexusTarget) })
    if ([string]::Equals($items[0].Target, $items[1].Target, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Thunderstore and Nexus destinations must be different."
    }
    foreach ($item in $items) {
        if (-not [System.IO.File]::Exists($item.Source) -or
            [string]::Equals($item.Source, $item.Target, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$($item.Name) staged package is missing or is also the final destination."
        }
        $item.Temp = $item.Target + ".incoming." + $token
        $item.Backup = $item.Target + ".backup." + $token
        $item.HadOriginal = [System.IO.File]::Exists($item.Target)
        $item.BackedUp = $false
        $item.Promoted = $false
    }

    try {
        foreach ($item in $items) {
            [void] [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($item.Target))
            [System.IO.File]::Copy($item.Source, $item.Temp)
        }
        foreach ($item in $items) {
            if ($item.HadOriginal) {
                [System.IO.File]::Move($item.Target, $item.Backup)
                $item.BackedUp = $true
            }
        }
        foreach ($item in $items) {
            [System.IO.File]::Move($item.Temp, $item.Target)
            $item.Promoted = $true
        }
    } catch {
        $failure = $_.Exception.Message
        $rollbackErrors = @()
        foreach ($item in @($items[1], $items[0])) {
            try {
                if ($item.Promoted -and [System.IO.File]::Exists($item.Target)) {
                    [System.IO.File]::Delete($item.Target)
                }
                if ($item.BackedUp -and [System.IO.File]::Exists($item.Backup)) {
                    [System.IO.File]::Move($item.Backup, $item.Target)
                }
                if ([System.IO.File]::Exists($item.Temp)) {
                    [System.IO.File]::Delete($item.Temp)
                }
            } catch {
                $rollbackErrors += "$($item.Name): $($_.Exception.Message)"
            }
        }
        if ($rollbackErrors.Count) {
            throw "Package promotion failed: $failure Rollback incomplete: $($rollbackErrors -join " ")"
        }
        throw "Package promotion failed; previous outputs were restored: $failure"
    }

    foreach ($item in $items) {
        if ([System.IO.File]::Exists($item.Backup)) {
            try {
                [System.IO.File]::Delete($item.Backup)
            } catch {
                Write-Warning "Package was promoted, but backup '$($item.Backup)' could not be removed."
            }
        }
    }
}

try {
    switch ($Mode) {
        "UpdateManifest" {
            Require "ManifestFile" $ManifestFile
            Require "VersionString" $VersionString
            $content = [System.IO.File]::ReadAllText($ManifestFile)
            Test-Manifest $content $ManifestFile $ExpectedName ""
            $pattern = '"version_number"\s*:\s*"[^"]*"'
            if ([regex]::Matches($content, $pattern).Count -ne 1) {
                throw "Manifest '$ManifestFile' must contain exactly one version_number property."
            }
            $updated = [regex]::Replace($content, $pattern, "`"version_number`": `"$VersionString`"", 1)
            Test-Manifest $updated $ManifestFile $ExpectedName $VersionString
            [System.IO.File]::WriteAllText($ManifestFile, $updated, (New-Object System.Text.UTF8Encoding($false)))
        }
        "ValidatePackage" {
            foreach ($argument in @("ManifestFile", "VersionString", "ExpectedName", "IconFile", "ThunderstoreZip", "NexusZip", "DllFileName")) {
                Require $argument (Get-Variable $argument -ValueOnly)
            }
            Test-Manifest ([System.IO.File]::ReadAllText($ManifestFile)) $ManifestFile $ExpectedName $VersionString
            Test-Icon $IconFile
            Test-Zip $ThunderstoreZip @($DllFileName, "manifest.json", "icon.png", "README.md", "CHANGELOG.md") $ExpectedName $VersionString -HasManifest
            Test-Zip $NexusZip @($DllFileName) $ExpectedName $VersionString
        }
        "PromotePackages" {
            foreach ($argument in @("ThunderstoreZip", "NexusZip", "ThunderstoreDestination", "NexusDestination")) {
                Require $argument (Get-Variable $argument -ValueOnly)
            }
            Promote-Pair $ThunderstoreZip $NexusZip $ThunderstoreDestination $NexusDestination
        }
    }
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
