[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
if ($Version.Length -gt 128) { throw 'Version exceeds the Docker tag length limit.' }
$path = Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props'
$original = [IO.File]::ReadAllBytes($path)

# Let XML determine the encoding and the actual declaration, not a whole-file
# text replacement (which could also change comments or conditional attributes).
$stream = [IO.MemoryStream]::new($original, $false)
$reader = [Xml.XmlTextReader]::new($stream)
$reader.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$reader.XmlResolver = $null
try {
    $reader.MoveToContent() | Out-Null
    $encoding = $reader.Encoding
    $document = [Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.Load($reader)
}
finally {
    $reader.Dispose()
    $stream.Dispose()
}
$nodes = $document.SelectNodes('/Project/PropertyGroup/Version')
if ($nodes.Count -ne 1) { throw 'Expected exactly one application Version declaration in Directory.Build.props.' }
$oldVersion = $nodes[0].InnerText.Trim()

$preamble = $encoding.GetPreamble()
$bomLength = 0
if ($preamble.Length -gt 0 -and $original.Length -ge $preamble.Length -and
    [Convert]::ToBase64String($original, 0, $preamble.Length) -ceq [Convert]::ToBase64String($preamble)) {
    $bomLength = $preamble.Length
}
$text = $encoding.GetString($original, $bomLength, $original.Length - $bomLength)
if ([Convert]::ToBase64String($encoding.GetBytes($text)) -cne
    [Convert]::ToBase64String($original, $bomLength, $original.Length - $bomLength)) {
    throw 'XML encoding cannot be round-tripped without changing unrelated bytes.'
}

# XmlReader line information identifies the real element even when comments
# contain example Version declarations. Line positions refer to the decoded XML.
$reader = [Xml.XmlTextReader]::new([IO.StringReader]::new($text))
$reader.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$reader.XmlResolver = $null
try {
    $inPropertyGroup = $false
    while ($reader.Read()) {
        if ($reader.Depth -eq 1) {
            $inPropertyGroup = $reader.NodeType -eq [Xml.XmlNodeType]::Element -and $reader.Name -ceq 'PropertyGroup'
        }
        if ($reader.NodeType -eq [Xml.XmlNodeType]::Element -and
            $inPropertyGroup -and $reader.Depth -eq 2 -and $reader.Name -ceq 'Version') {
            $line = $reader.LineNumber
            $column = $reader.LinePosition
            break
        }
    }
}
finally { $reader.Dispose() }
$lineStarts = @(0) + @([regex]::Matches($text, '\r\n|\r|\n') | ForEach-Object { $_.Index + $_.Length })
$elementStart = $lineStarts[$line - 1] + $column - 2
$opening = [regex]::Match($text.Substring($elementStart), '^<Version\b(?:[^<>"'']|"[^"]*"|''[^'']*'')*>')
if (-not $opening.Success) { throw 'Could not locate the parsed Version opening tag.' }
$contentStart = $elementStart + $opening.Length
$closingStart = $text.IndexOf('</Version', $contentStart, [StringComparison]::Ordinal)
if ($closingStart -lt 0) { throw 'Version must have a text value and a closing tag.' }
$content = $text.Substring($contentStart, $closingStart - $contentStart)
if ($content.Contains('<')) { throw 'Version must contain only a text value.' }
$leadingLength = $content.Length - $content.TrimStart().Length
$valueStart = $contentStart + $leadingLength
$valueLength = $content.Trim().Length
$candidate = $text.Substring(0, $valueStart) + $Version + $text.Substring($valueStart + $valueLength)
$verified = [Xml.XmlDocument]::new()
$verified.XmlResolver = $null
$verified.LoadXml($candidate)
$newNodes = $verified.SelectNodes('/Project/PropertyGroup/Version')
if ($newNodes.Count -ne 1 -or $newNodes[0].InnerText.Trim() -cne $Version) {
    throw 'Replacement failed XML Version validation; no file was changed.'
}

# Splice only the value bytes; retain the original BOM, encoding and all other
# bytes verbatim. Stage beside the destination for an atomic replacement.
$byteStart = $bomLength + $encoding.GetByteCount($text.Substring(0, $valueStart))
$byteLength = $encoding.GetByteCount($text.Substring($valueStart, $valueLength))
$valueBytes = $encoding.GetBytes($Version)
$result = [byte[]]::new($original.Length - $byteLength + $valueBytes.Length)
[Array]::Copy($original, 0, $result, 0, $byteStart)
[Array]::Copy($valueBytes, 0, $result, $byteStart, $valueBytes.Length)
[Array]::Copy($original, $byteStart + $byteLength, $result, $byteStart + $valueBytes.Length,
    $original.Length - $byteStart - $byteLength)
$temporary = "$path.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllBytes($temporary, $result)
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($original)) {
        throw 'Directory.Build.props changed during preparation; no replacement was made.'
    }
    # Pass a CLR null string, not PowerShell's empty-string conversion of $null.
    [IO.File]::Replace($temporary, $path, [NullString]::Value)
}
finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($result)) {
    throw 'Release version replacement completed but read-back verification failed; inspect Directory.Build.props before retrying.'
}
Write-Host "Application Version: $oldVersion -> $($newNodes[0].InnerText.Trim()) ($path; read-back verified)"
