param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\.."))
)

$ErrorActionPreference = "Stop"
$protocolDir = Join-Path $PSScriptRoot "Protocol"
$networkDir = Join-Path $RepositoryRoot "Server\DfoServer\Network"
$packetTypesPath = Join-Path $networkDir "Core\PacketTypes.cs"
$handlerPath = Join-Path $networkDir "Protocol\GameProtocolHandler.cs"

function Read-Enums([string]$path) {
    $result = @{ cmd = @(); noti = @() }
    $mode = $null
    foreach ($line in Get-Content -LiteralPath $path) {
        if ($line -match 'enum\s+NotiPacketType') { $mode = 'noti'; continue }
        if ($line -match 'enum\s+CmdPacketType') { $mode = 'cmd'; continue }
        if ($mode -and $line -match '^\s*}\s*$') { $mode = $null; continue }
        if ($mode -and $line -match '^\s*(?<name>[A-Za-z0-9_]+)\s*=\s*(?<value>0x[0-9A-Fa-f]+|\d+)\s*,?') {
            $result[$mode] += [ordered]@{ name = $Matches.name; value = $Matches.value }
        }
    }
    return $result
}

function Convert-Value([string]$text) {
    if ($text.StartsWith('0x', [StringComparison]::OrdinalIgnoreCase)) {
        return [Convert]::ToInt32($text.Substring(2), 16)
    }
    return [Convert]::ToInt32($text)
}

function Split-Arguments([string]$text) {
    $parts = [System.Collections.Generic.List[string]]::new()
    $depth = 0
    $start = 0
    $inString = $false
    $verbatim = $false
    for ($index = 0; $index -lt $text.Length; $index++) {
        $char = $text[$index]
        if ($inString) {
            if ($verbatim) {
                if ($char -eq '"') {
                    if ($index + 1 -lt $text.Length -and $text[$index + 1] -eq '"') { $index++ }
                    else { $inString = $false; $verbatim = $false }
                }
            }
            elseif ($char -eq '\\') { $index++ }
            elseif ($char -eq '"') { $inString = $false }
            continue
        }
        if ($char -eq '@' -and $index + 1 -lt $text.Length -and $text[$index + 1] -eq '"') {
            $inString = $true; $verbatim = $true; $index++; continue
        }
        if ($char -eq '"') { $inString = $true; continue }
        if ($char -eq '(' -or $char -eq '[' -or $char -eq '{') { $depth++; continue }
        if ($char -eq ')' -or $char -eq ']' -or $char -eq '}') { $depth--; continue }
        if ($char -eq ',' -and $depth -eq 0) {
            $parts.Add($text.Substring($start, $index - $start).Trim())
            $start = $index + 1
        }
    }
    $parts.Add($text.Substring($start).Trim())
    return $parts
}

function Find-Calls([string]$text, [string]$needle) {
    $calls = [System.Collections.Generic.List[object]]::new()
    $search = 0
    while (($position = $text.IndexOf($needle, $search, [StringComparison]::Ordinal)) -ge 0) {
        $open = $position + $needle.Length
        $depth = 1
        $index = $open
        $inString = $false
        $verbatim = $false
        while ($index -lt $text.Length -and $depth -gt 0) {
            $char = $text[$index]
            if ($inString) {
                if ($verbatim) {
                    if ($char -eq '"') {
                        if ($index + 1 -lt $text.Length -and $text[$index + 1] -eq '"') { $index++ }
                        else { $inString = $false; $verbatim = $false }
                    }
                }
                elseif ($char -eq '\\') { $index++ }
                elseif ($char -eq '"') { $inString = $false }
            }
            elseif ($char -eq '@' -and $index + 1 -lt $text.Length -and $text[$index + 1] -eq '"') {
                $inString = $true; $verbatim = $true; $index++
            }
            elseif ($char -eq '"') { $inString = $true }
            elseif ($char -eq '(') { $depth++ }
            elseif ($char -eq ')') { $depth-- }
            $index++
        }
        if ($depth -eq 0) {
            $body = $text.Substring($open, $index - $open - 1)
            $line = 1 + ($text.Substring(0, $position).Split("`n").Count - 1)
            $calls.Add([ordered]@{ line = $line; arguments = (Split-Arguments $body) })
        }
        $search = [Math]::Max($index, $position + $needle.Length)
    }
    return $calls
}

function Find-BodyForwardingCalls([string]$text) {
    $calls = [System.Collections.Generic.List[object]]::new()
    $seen = @{}
    foreach ($match in [regex]::Matches(
        $text,
        '(?:(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\s*\.)?\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(')) {
        $name = $match.Groups['name'].Value
        if ($name -in @('if', 'for', 'foreach', 'while', 'switch', 'catch', 'lock', 'using', 'nameof', 'sizeof')) { continue }
        $open = $match.Index + $match.Length - 1
        $depth = 1
        $index = $open + 1
        $inString = $false
        $verbatim = $false
        while ($index -lt $text.Length -and $depth -gt 0) {
            $char = $text[$index]
            if ($inString) {
                if ($verbatim) {
                    if ($char -eq '"') {
                        if ($index + 1 -lt $text.Length -and $text[$index + 1] -eq '"') { $index++ }
                        else { $inString = $false; $verbatim = $false }
                    }
                }
                elseif ($char -eq '\\') { $index++ }
                elseif ($char -eq '"') { $inString = $false }
            }
            elseif ($char -eq '@' -and $index + 1 -lt $text.Length -and $text[$index + 1] -eq '"') {
                $inString = $true; $verbatim = $true; $index++
            }
            elseif ($char -eq '"') { $inString = $true }
            elseif ($char -eq '(') { $depth++ }
            elseif ($char -eq ')') { $depth-- }
            $index++
        }
        if ($depth -ne 0) { continue }
        $arguments = $text.Substring($open + 1, $index - $open - 2)
        if ($arguments -notmatch '\b(?:body|b)\b') { continue }
        $key = "$($match.Index):$name"
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $calls.Add([pscustomobject]@{
            receiver = $match.Groups['receiver'].Value
            name = $name
            arguments = $arguments
        })
    }
    return $calls
}

function Find-MethodDefinitions([string]$text, [string]$source) {
    $definitions = [System.Collections.Generic.List[object]]::new()
    $pattern = '(?ms)(?:public|private|internal|protected)\s+(?:(?:static|async|virtual|override|sealed|new)\s+)*[A-Za-z0-9_<>\[\],.?\s]+?\b(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<parameters>.*?)\)\s*(?<tail>=>|\{)'
    foreach ($match in [regex]::Matches($text, $pattern)) {
        $methodName = $match.Groups['method'].Value
        $line = 1 + ($text.Substring(0, $match.Index).Split("`n").Count - 1)
        $prefix = $text.Substring(0, $match.Index)
        $classMatches = [regex]::Matches($prefix, '\b(?:class|struct)\s+(?<name>[A-Za-z0-9_]+)')
        $className = if ($classMatches.Count -gt 0) { $classMatches[$classMatches.Count - 1].Groups['name'].Value } else { '' }
        if ($match.Groups['tail'].Value -eq '=>') {
            $start = $match.Index + $match.Length
            $end = $text.IndexOf(';', $start)
            if ($end -lt 0) { continue }
            $definitions.Add([pscustomobject]@{
                name = $methodName
                className = $className
                body = $text.Substring($start, $end - $start)
                parameters = $match.Groups['parameters'].Value
                line = $line
                source = $source
                expressionBody = $true
            })
            continue
        }

        $brace = $match.Index + $match.Length - 1
    $depth = 1
    $index = $brace + 1
    $inString = $false
    $verbatim = $false
    while ($index -lt $text.Length -and $depth -gt 0) {
        $char = $text[$index]
        if ($inString) {
            if ($verbatim) {
                if ($char -eq '"') {
                    if ($index + 1 -lt $text.Length -and $text[$index + 1] -eq '"') { $index++ }
                    else { $inString = $false; $verbatim = $false }
                }
            }
            elseif ($char -eq '\\') { $index++ }
            elseif ($char -eq '"') { $inString = $false }
        }
        elseif ($char -eq '@' -and $index + 1 -lt $text.Length -and $text[$index + 1] -eq '"') {
            $inString = $true; $verbatim = $true; $index++
        }
        elseif ($char -eq '"') { $inString = $true }
        elseif ($char -eq '{') { $depth++ }
        elseif ($char -eq '}') { $depth-- }
        $index++
    }
        if ($depth -ne 0) { continue }
        $definitions.Add([pscustomobject]@{
            name = $methodName
            className = $className
            body = $text.Substring($brace + 1, $index - $brace - 2)
            parameters = $match.Groups['parameters'].Value
            line = $line
            source = $source
            expressionBody = $false
        })
    }
    return $definitions
}

function Add-InferredField(
    [System.Collections.Generic.List[object]]$fields,
    [string]$name,
    [string]$fieldType,
    [int]$offset,
    [bool]$optional,
    [string]$source) {
    if ($offset -lt 0) { return }
    if ($fields | Where-Object { $_.name -eq $name -and $_.fieldType -eq $fieldType -and $_.offset -eq $offset }) { return }
    $fields.Add([pscustomobject][ordered]@{
        name = $name
        fieldType = $fieldType
        offset = $offset
        optional = $optional
        source = $source
    })
}

function Extract-InferredSchema([string]$body, [string]$source, [int]$startLine) {
    $fields = [System.Collections.Generic.List[object]]::new()
    $exactLengths = [System.Collections.Generic.List[int]]::new()
    $minimumLengths = [System.Collections.Generic.List[int]]::new()
    foreach ($match in [regex]::Matches($body, '(?:body|b)\.Length\s*!=\s*(?<length>\d+)')) { $exactLengths.Add([int]$match.Groups['length'].Value) }
    foreach ($match in [regex]::Matches($body, '(?:body|b)\.Length\s*(?:<|<=)\s*(?<length>\d+)')) {
        $length = [int]$match.Groups['length'].Value
        if ($match.Value -match '<=') { $length++ }
        $minimumLengths.Add($length)
    }

    $patterns = @(
        '(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:\([A-Za-z0-9_.<>?]+\)\s*)?BitConverter\.To(?<type>UInt16|Int16|UInt32|Int32)\((?:body|b),\s*(?<offset>\d+)\)',
        '(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:\([A-Za-z0-9_.<>?]+\)\s*)?BinaryPrimitives\.Read(?<type>UInt16|Int16|UInt32|Int32)LittleEndian\(\s*(?:body|b)\.AsSpan\(\s*(?<offset>\d+)',
        '(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:\([A-Za-z0-9_.<>?]+\)\s*)?(?:body|b)\[(?<offset>\d+)\]'
    )
    $lineStarts = @(0)
    for ($index = 0; $index -lt $body.Length; $index++) { if ($body[$index] -eq "`n") { $lineStarts += ($index + 1) } }
    foreach ($pattern in $patterns) {
        foreach ($match in [regex]::Matches($body, $pattern)) {
            $name = $match.Groups['name'].Value
            if ($name -in @('offset', 'index', 'i')) { continue }
            $type = if ($match.Groups['type'].Success) { $match.Groups['type'].Value.ToLowerInvariant() } else { 'u8' }
            $fieldType = switch ($type) { 'uint16' { 'u16' } 'int16' { 'i16' } 'uint32' { 'u32' } 'int32' { 'i32' } default { 'u8' } }
            $offset = [int]$match.Groups['offset'].Value
            $relativeLine = 0
            for ($lineIndex = 0; $lineIndex -lt $lineStarts.Count; $lineIndex++) {
                if ($lineStarts[$lineIndex] -le $match.Index) { $relativeLine = $lineIndex } else { break }
            }
            Add-InferredField $fields $name $fieldType $offset $false "$source`:$($startLine + $relativeLine)"
        }
    }
    $exact = if ($exactLengths.Count -eq 1) { $exactLengths[0] } else { $null }
    $minimum = if ($minimumLengths.Count -gt 0) { ($minimumLengths | Measure-Object -Maximum).Maximum } else { $null }
    return [pscustomobject][ordered]@{
        exactLength = $exact
        minimumLength = $minimum
        bodyIgnored = $false
        fields = @($fields)
    }
}

function Get-BuilderFieldName([string]$expression, [int]$offset) {
    $value = ($expression -replace '//.*$', '').Trim()
    $value = $value -replace '^\([^)]*\)\s*', ''
    if ($value -match '^(?<name>[A-Za-z_][A-Za-z0-9_]*)$') { return $Matches.name }
    if ($value -match '(?:\.|\?)\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)$') { return $Matches.name }
    if ($offset -eq 0) { return 'status' }
    return "value_$offset"
}

function Extract-BuilderSchema([string]$body, [string]$source, [int]$startLine, [string]$writerName = '') {
    if ($body -match '\b(?:if|switch|for|foreach|while)\s*\(' -or $body -match '\.WriteBytes\s*\(') {
        return $null
    }
    $fields = [System.Collections.Generic.List[object]]::new()
    $offset = 0
    $dynamic = $false
    $lineStarts = @(0)
    for ($index = 0; $index -lt $body.Length; $index++) { if ($body[$index] -eq "`n") { $lineStarts += ($index + 1) } }
    $writePattern = '(?:(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\s*\.)?(?<method>WriteByte|WriteInt16|WriteUInt16|WriteInt32|WriteUInt32|WriteZeroBytes|WriteUtf8Dstr|WriteAsciiDstr|WriteDstr|WriteRawDstr)\s*\((?<arg>[^\r\n;]*)\)'
    foreach ($match in [regex]::Matches($body, $writePattern)) {
        if ($writerName -and $match.Groups['receiver'].Value -ne $writerName) { continue }
        $method = $match.Groups['method'].Value
        $argument = $match.Groups['arg'].Value.Trim()
        $relativeLine = 0
        for ($lineIndex = 0; $lineIndex -lt $lineStarts.Count; $lineIndex++) {
            if ($lineStarts[$lineIndex] -le $match.Index) { $relativeLine = $lineIndex } else { break }
        }
        $location = "$source`:$($startLine + $relativeLine)"
        if ($method -eq 'WriteZeroBytes') {
            if ($argument -match '^\d+$') { $offset += [int]$argument } else { $dynamic = $true }
            continue
        }
        if ($method -in @('WriteUtf8Dstr', 'WriteAsciiDstr', 'WriteDstr', 'WriteRawDstr')) {
            $name = Get-BuilderFieldName $argument $offset
            Add-InferredField $fields "${name}Length" 'i32' $offset $false $location
            $offset += 4
            $dynamic = $true
            break
        }
        $type = switch ($method) {
            'WriteByte' { 'u8' }
            'WriteInt16' { 'i16' }
            'WriteUInt16' { 'u16' }
            'WriteInt32' { 'i32' }
            'WriteUInt32' { 'u32' }
        }
        $name = Get-BuilderFieldName $argument $offset
        Add-InferredField $fields $name $type $offset $false $location
        $offset += switch ($type) { 'u8' { 1 } 'i16' { 2 } 'u16' { 2 } 'i32' { 4 } 'u32' { 4 } }
    }
    if ($fields.Count -eq 0 -and $offset -eq 0) { return $null }
    [pscustomobject][ordered]@{
        exactLength = if ($dynamic) { $null } else { $offset }
        minimumLength = if ($fields.Count -gt 0) { $offset } else { $null }
        bodyIgnored = $false
        fields = @($fields)
        sources = @([ordered]@{ location = "$source`:$startLine" })
    }
}

function Get-RelativeLine([string]$text, [int]$index) {
    if ($index -le 0) { return 0 }
    return ($text.Substring(0, [Math]::Min($index, $text.Length)).Split("`n").Count - 1)
}

function Resolve-LocalWriterSchema(
    [string]$writerName,
    [object]$enclosingMethod,
    [int]$callLine) {
    if ($null -eq $enclosingMethod -or [string]::IsNullOrWhiteSpace($writerName)) { return $null }
    $body = $enclosingMethod.body
    $relativeTargetLine = [Math]::Max(0, $callLine - $enclosingMethod.line)
    $toArrayMatches = @([regex]::Matches($body, '\b' + [regex]::Escape($writerName) + '\s*\.\s*ToArray\s*\(\s*\)'))
    if ($toArrayMatches.Count -eq 0) { return $null }
    $target = $toArrayMatches |
        Where-Object { (Get-RelativeLine $body $_.Index) -le $relativeTargetLine + 2 } |
        Select-Object -Last 1
    if ($null -eq $target) { $target = $toArrayMatches | Select-Object -First 1 }

    $declarationPattern = '(?m)\b(?:var|GamePacketWriter)\s+' + [regex]::Escape($writerName) + '\s*=\s*new\s+GamePacketWriter\s*\(\s*\)\s*;'
    $declarations = @([regex]::Matches($body, $declarationPattern) | Where-Object Index -lt $target.Index)
    if ($declarations.Count -eq 0) { return $null }
    $declaration = $declarations | Select-Object -Last 1
    $segment = $body.Substring($declaration.Index, ($target.Index + $target.Length) - $declaration.Index)
    $segmentLine = $enclosingMethod.line + (Get-RelativeLine $body $declaration.Index)
    return Extract-BuilderSchema $segment $enclosingMethod.source $segmentLine $writerName
}

function Resolve-LocalOutboundSchema(
    [string]$builder,
    [object]$enclosingMethod,
    [int]$callLine,
    [hashtable]$methodIndex) {
    if ($null -eq $enclosingMethod) { return $null }
    $value = $builder.Trim()
    if ($value -match '^(?<writer>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*ToArray\s*\(\s*\)$') {
        return Resolve-LocalWriterSchema $Matches.writer $enclosingMethod $callLine
    }
    if ($value -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { return $null }

    $variable = $value
    $body = $enclosingMethod.body
    $relativeTargetLine = [Math]::Max(0, $callLine - $enclosingMethod.line)
    $assignmentPattern = '(?m)\b(?:var|byte\s*\[\s*\])\s+' + [regex]::Escape($variable) + '\s*=\s*(?<value>[^;]+);'
    $assignments = @([regex]::Matches($body, $assignmentPattern) |
        Where-Object { (Get-RelativeLine $body $_.Index) -le $relativeTargetLine + 2 })
    if ($assignments.Count -eq 0) { return $null }
    $assignment = $assignments | Select-Object -Last 1
    $assignedValue = ($assignment.Groups['value'].Value -replace '\s+', ' ').Trim()
    if ($assignedValue -match '^(?<writer>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*ToArray\s*\(\s*\)$') {
        return Resolve-LocalWriterSchema $Matches.writer $enclosingMethod $callLine
    }
    return Resolve-BuilderSchema $assignedValue $methodIndex
}

function Resolve-BuilderSchema([string]$builder, [hashtable]$methodIndex) {
    if ([string]::IsNullOrWhiteSpace($builder)) { return $null }
    if ($builder -match '^new\s+byte\s*\[\s*(?<count>\d+)\s*\]$') {
        $count = [int]$Matches.count
        return [pscustomobject][ordered]@{ exactLength = $count; minimumLength = $count; bodyIgnored = $true; fields = @(); sources = @([ordered]@{ location = 'inline zeroed byte array' }) }
    }
    if ($builder -match 'CommonPacketBodyBuilder\.BuildSuccessAck\s*\(') {
        return [pscustomobject][ordered]@{ exactLength = 1; minimumLength = 1; bodyIgnored = $false; fields = @([pscustomobject][ordered]@{ name = 'status'; fieldType = 'u8'; offset = 0; optional = $false; source = 'Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:14' }); sources = @([ordered]@{ location = 'Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:14' }) }
    }
    if ($builder -match 'CommonPacketBodyBuilder\.BuildCmdError\s*\(') {
        return [pscustomobject][ordered]@{ exactLength = 2; minimumLength = 2; bodyIgnored = $false; fields = @(
            [pscustomobject][ordered]@{ name = 'status'; fieldType = 'u8'; offset = 0; optional = $false; source = 'Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:19' },
            [pscustomobject][ordered]@{ name = 'errorCode'; fieldType = 'u8'; offset = 1; optional = $false; source = 'Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:19' }
        ); sources = @([ordered]@{ location = 'Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:19' }) }
    }
    if ($builder -match 'CommonPacketBodyBuilder\.BuildInt32Value\s*\(') {
        return [pscustomobject][ordered]@{ exactLength = 4; minimumLength = 4; bodyIgnored = $false; fields = @([pscustomobject][ordered]@{ name = 'value'; fieldType = 'i32'; offset = 0; optional = $false; source = 'Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:17' }); sources = @([ordered]@{ location = 'Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:17' }) }
    }
    if ($builder -match 'CommonPacketBodyBuilder\.BuildZeroBytes\s*\(\s*(?<count>\d+)\s*\)') {
        $count = [int]$Matches.count
        return [pscustomobject][ordered]@{ exactLength = $count; minimumLength = $count; bodyIgnored = $true; fields = @(); sources = @([ordered]@{ location = 'Server/DfoServer/Network/Builders/CommonPacketBodyBuilder.cs:24' }) }
    }
    if ($builder -match '^new\s*(?:byte\s*)?\[\s*\]\s*\{(?<bytes>.*)\}') {
        $parts = @($Matches.bytes -split ',') | Where-Object { $_.Trim().Length -gt 0 }
        $fields = [System.Collections.Generic.List[object]]::new()
        for ($index = 0; $index -lt $parts.Count; $index++) {
            $name = if ($index -eq 0) { 'status' } elseif ($index -eq 1) { 'errorCode' } else { "byte_$index" }
            Add-InferredField $fields $name 'u8' $index $false 'inline byte array'
        }
        return [pscustomobject][ordered]@{ exactLength = $parts.Count; minimumLength = $parts.Count; bodyIgnored = $false; fields = @($fields); sources = @([ordered]@{ location = 'inline byte array' }) }
    }
    $call = [regex]::Match($builder, '(?:(?<class>[A-Za-z_][A-Za-z0-9_.]*)\.)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(')
    if (-not $call.Success) { return $null }
    $qualified = if ($call.Groups['class'].Success) { "$($call.Groups['class'].Value).$($call.Groups['name'].Value)" } else { $call.Groups['name'].Value }
    $definitions = @(if ($methodIndex.ContainsKey($qualified)) { @($methodIndex[$qualified]) } elseif ($methodIndex.ContainsKey($call.Groups['name'].Value)) { @($methodIndex[$call.Groups['name'].Value]) } else { @() })
    if ($definitions.Count -ne 1) { return $null }
    $definition = $definitions[0]
    $inlineReturn = [regex]::Match($definition.body, 'new\s*(?:byte\s*)?\[\s*\]\s*\{(?<bytes>[^}]*)\}')
    if ($inlineReturn.Success) {
        $parts = @($inlineReturn.Groups['bytes'].Value -split ',') | Where-Object { $_.Trim().Length -gt 0 }
        $fields = [System.Collections.Generic.List[object]]::new()
        for ($index = 0; $index -lt $parts.Count; $index++) {
            $name = if ($index -eq 0) { 'status' } elseif ($index -eq 1) { 'errorCode' } else { "byte_$index" }
            Add-InferredField $fields $name 'u8' $index $false "$($definition.source):$($definition.line)"
        }
        return [pscustomobject][ordered]@{
            exactLength = $parts.Count
            minimumLength = $parts.Count
            bodyIgnored = $false
            fields = @($fields)
            sources = @([ordered]@{ location = "$($definition.source):$($definition.line)" })
        }
    }
    return Extract-BuilderSchema $definition.body $definition.source $definition.line
}

function Merge-Schemas([object[]]$schemas) {
    $fields = @($schemas.fields | ForEach-Object { $_ } | Sort-Object offset, name -Unique)
    $exactValues = @($schemas.exactLength | Where-Object { $null -ne $_ } | Sort-Object -Unique)
    $minimumValues = @($schemas.minimumLength | Where-Object { $null -ne $_ })
    return [pscustomobject][ordered]@{
        exactLength = if ($exactValues.Count -eq 1) { $exactValues[0] } else { $null }
        minimumLength = if ($minimumValues.Count -gt 0) { ($minimumValues | Measure-Object -Maximum).Maximum } else { $null }
        bodyIgnored = (@($schemas | Where-Object bodyIgnored).Count -gt 0 -and $fields.Count -eq 0 -and $exactValues.Count -eq 0 -and $minimumValues.Count -eq 0)
        fields = $fields
    }
}

function Resolve-MethodSchemas(
    [string]$methodName,
    [hashtable]$methodIndex,
    [hashtable]$receiverTypes,
    [int]$maximumDepth = 6) {
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue([pscustomobject]@{ name = $methodName; depth = 0 })
    $visited = @{}
    $results = [System.Collections.Generic.List[object]]::new()
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        if ($current.depth -gt $maximumDepth -or $visited.ContainsKey($current.name)) { continue }
        $visited[$current.name] = $true
        if (-not $methodIndex.ContainsKey($current.name)) { continue }
        foreach ($definition in $methodIndex[$current.name]) {
            $schema = Extract-InferredSchema $definition.body $definition.source $definition.line
            $childCalls = @(Find-BodyForwardingCalls $definition.body)
            $acceptsBody = $definition.parameters -match '\bbyte\s*\[\s*\]\s+(?:body|b)\b'
            $terminalBodyIgnored = $acceptsBody -and $childCalls.Count -eq 0 -and $definition.body -notmatch '\b(?:body|b)\b'
            if ($schema.fields.Count -gt 0 -or $null -ne $schema.exactLength -or $null -ne $schema.minimumLength -or $terminalBodyIgnored) {
                $schema.bodyIgnored = $terminalBodyIgnored
                $results.Add([pscustomobject]@{
                    schema = $schema
                    source = "$($definition.source):$($definition.line)"
                    method = $definition.name
                })
            }
            foreach ($call in $childCalls) {
                $childName = $call.name
                $classQualifier = $call.receiver
                $qualified = $null
                if ($classQualifier) {
                    # A receiver such as LoginRequest.Parse can be followed only when the
                    # receiver is an actual class name. Falling back to the global `Parse`
                    # bucket mixes every parser in the project into the current packet.
                    if ($methodIndex.ContainsKey("$classQualifier.$childName")) {
                        $qualified = "$classQualifier.$childName"
                    }
                    elseif ($definition.className -and $receiverTypes.ContainsKey("$($definition.className).$classQualifier")) {
                        $receiverType = $receiverTypes["$($definition.className).$classQualifier"]
                        if ($methodIndex.ContainsKey("$receiverType.$childName")) {
                            $qualified = "$receiverType.$childName"
                        }
                    }
                }
                elseif ($definition.className -and $methodIndex.ContainsKey("$($definition.className).$childName")) {
                    $qualified = "$($definition.className).$childName"
                }
                elseif ($methodIndex.ContainsKey($childName) -and $methodIndex[$childName].Count -eq 1) {
                    # Unqualified cross-class wrappers are safe only when the method name
                    # resolves uniquely across the network source tree.
                    $qualified = $childName
                }
                if ($null -eq $qualified) { continue }
                if ($qualified -ne $current.name -and -not $visited.ContainsKey($qualified)) {
                    $queue.Enqueue([pscustomobject]@{ name = $qualified; depth = $current.depth + 1 })
                }
            }
        }
    }
    return $results
}

$enums = Read-Enums $packetTypesPath
$cmdByName = @{}
$notiByName = @{}
foreach ($entry in $enums.cmd) { $cmdByName[$entry.name] = Convert-Value $entry.value }
foreach ($entry in $enums.noti) { $notiByName[$entry.name] = Convert-Value $entry.value }

$constants = @{}
foreach ($file in Get-ChildItem -LiteralPath $networkDir -Recurse -File -Filter *.cs) {
    $className = $null
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        if (-not $className -and $line -match '\bclass\s+(?<name>[A-Za-z0-9_]+)') { $className = $Matches.name }
        if ($className -and $line -match '\bconst\s+(?:byte|ushort|int|uint)\s+(?<name>[A-Za-z0-9_]+)\s*=\s*(?<value>0x[0-9A-Fa-f]+|\d+)') {
            $value = Convert-Value $Matches.value
            if ($value -ge 0 -and $value -le 65535) {
                $constants["$className.$($Matches.name)"] = $value
            }
        }
    }
}

function Resolve-Type([string]$expression, [hashtable]$enumMap) {
    $value = $expression.Trim()
    $value = $value -replace '^\((?:byte|ushort|int)\)', ''
    $value = $value.Trim()
    if ($value -match '^0x(?<hex>[0-9A-Fa-f]+)$') { return [Convert]::ToInt32($Matches.hex, 16) }
    if ($value -match '^\d+$') { return [int]$value }
    if ($value -match '(?:CmdPacketType|NotiPacketType)\.(?<name>[A-Za-z0-9_]+)') {
        if ($enumMap.ContainsKey($Matches.name)) { return $enumMap[$Matches.name] }
    }
    if ($constants.ContainsKey($value)) { return $constants[$value] }
    return $null
}

$registrations = [System.Collections.Generic.List[object]]::new()
$handlerLines = Get-Content -LiteralPath $handlerPath
for ($index = 0; $index -lt $handlerLines.Count; $index++) {
    if ($handlerLines[$index] -notmatch '^\s*d\[(?<expression>[^\]]+)\]\s*=') { continue }
    $startLine = $index + 1
    $expression = $Matches.expression.Trim()
    $statementParts = [System.Collections.Generic.List[string]]::new()
    $braceDepth = 0
    $parenDepth = 0
    $bracketDepth = 0
    $finished = $false
    while (-not $finished -and $index -lt $handlerLines.Count) {
        $lineText = $handlerLines[$index]
        $statementParts.Add($lineText.Trim())
        $code = ($lineText -replace '//.*$', '') -replace '@?"(?:""|\\.|[^"])*"', '""'
        foreach ($char in $code.ToCharArray()) {
            switch ($char) {
                '{' { $braceDepth++ }
                '}' { $braceDepth-- }
                '(' { $parenDepth++ }
                ')' { $parenDepth-- }
                '[' { $bracketDepth++ }
                ']' { $bracketDepth-- }
            }
        }
        $finished = $braceDepth -eq 0 -and $parenDepth -eq 0 -and $bracketDepth -eq 0 -and $code -match ';\s*$'
        if (-not $finished) { $index++ }
    }
    $statement = ($statementParts -join ' ')
    $type = Resolve-Type $expression $cmdByName
    if ($null -eq $type) { continue }
    $registrations.Add([pscustomobject][ordered]@{
        type = $type
        statement = $statement
        sources = @([ordered]@{
            location = "Server/DfoServer/Network/Protocol/GameProtocolHandler.cs:$startLine"
            expression = $expression
        })
    })
}
$registered = $registrations | Group-Object -Property type | ForEach-Object {
    [pscustomobject][ordered]@{ type = [int]$_.Name; sources = @($_.Group.sources | ForEach-Object { $_ }) }
} | Sort-Object type

$methodIndex = @{}
$receiverTypes = @{}
foreach ($file in Get-ChildItem -LiteralPath $networkDir -Recurse -File -Filter *.cs) {
    $relative = $file.FullName.Substring($RepositoryRoot.Length).TrimStart('\').Replace('\', '/')
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $classMatches = @([regex]::Matches($text, '\b(?:class|struct)\s+(?<name>[A-Za-z0-9_]+)'))
    foreach ($classMatch in $classMatches) {
        $className = $classMatch.Groups['name'].Value
        $nextClass = $classMatches | Where-Object Index -gt $classMatch.Index | Select-Object -First 1
        $classEnd = if ($null -ne $nextClass) { $nextClass.Index } else { $text.Length }
        $classText = $text.Substring($classMatch.Index, $classEnd - $classMatch.Index)
        foreach ($fieldMatch in [regex]::Matches(
            $classText,
            '\b(?:private|internal|protected|public)\s+(?:(?:static|readonly|volatile)\s+)*(?<type>[A-Za-z_][A-Za-z0-9_.]*(?:<[^;=]+?>)?(?:\[\])?\??)\s+(?<name>_[A-Za-z0-9_]+)\s*(?:[;=])')) {
            $receiverType = $fieldMatch.Groups['type'].Value
            if ($receiverType.Contains('.')) { $receiverType = $receiverType.Split('.')[-1] }
            if ($receiverType.Contains('<')) { $receiverType = $receiverType.Substring(0, $receiverType.IndexOf('<')) }
            $receiverTypes["$className.$($fieldMatch.Groups['name'].Value)"] = $receiverType.TrimEnd('?')
        }
    }
    foreach ($definition in Find-MethodDefinitions $text $relative) {
        $name = $definition.name
        if (-not $methodIndex.ContainsKey($name)) { $methodIndex[$name] = [System.Collections.Generic.List[object]]::new() }
        $methodIndex[$name].Add($definition)
        if ($definition.className) {
            $qualifiedName = "$($definition.className).$name"
            if (-not $methodIndex.ContainsKey($qualifiedName)) { $methodIndex[$qualifiedName] = [System.Collections.Generic.List[object]]::new() }
            $methodIndex[$qualifiedName].Add($definition)
        }
    }
}

$inferredSchemas = [System.Collections.Generic.List[object]]::new()
foreach ($registration in $registrations) {
    $handlerMatches = @([regex]::Matches(
        $registration.statement,
        '(?:(?<receiver>_[A-Za-z_][A-Za-z0-9_]*)\s*\.)?\s*(?<name>(?:Handle|TryHandle)[A-Za-z0-9_]+)\b'))
    $inlineSchema = Extract-InferredSchema $registration.statement 'Server/DfoServer/Network/Protocol/GameProtocolHandler.cs' ([int]($registration.sources[0].location.Split(':')[-1]))
    $hasInlineSchema = $inlineSchema.fields.Count -gt 0 -or $null -ne $inlineSchema.exactLength -or $null -ne $inlineSchema.minimumLength
    $addedVariant = $false
    $seenRoots = @{}
    foreach ($handlerMatch in $handlerMatches) {
        $receiver = $handlerMatch.Groups['receiver'].Value
        $methodName = $handlerMatch.Groups['name'].Value
        $rootName = $methodName
        if ($receiver -and $receiverTypes.ContainsKey("GameProtocolHandler.$receiver")) {
            $receiverType = $receiverTypes["GameProtocolHandler.$receiver"]
            if ($methodIndex.ContainsKey("$receiverType.$methodName")) {
                $rootName = "$receiverType.$methodName"
            }
        }
        elseif ($methodIndex.ContainsKey("GameProtocolHandler.$methodName")) {
            $rootName = "GameProtocolHandler.$methodName"
        }
        if ($seenRoots.ContainsKey($rootName)) { continue }
        $seenRoots[$rootName] = $true
        $resolved = @(Resolve-MethodSchemas $rootName $methodIndex $receiverTypes)
        if ($resolved.Count -eq 0) { continue }
        $schemas = @($resolved.schema)
        if ($hasInlineSchema) { $schemas += $inlineSchema }
        $merged = Merge-Schemas $schemas
        $variantName = ($rootName -replace '[^A-Za-z0-9_.-]', '-')
        $inferredSchemas.Add([pscustomobject][ordered]@{
            type = $registration.type
            name = $variantName
            discriminator = 'server handler/context; use length when unique, otherwise select variant explicitly'
            exactLength = $merged.exactLength
            minimumLength = $merged.minimumLength
            bodyIgnored = $merged.bodyIgnored
            fields = $merged.fields
            sources = @(
                @($resolved.source) + @($registration.sources[0].location) |
                    Sort-Object -Unique |
                    ForEach-Object { [ordered]@{ location = $_ } }
            )
        })
        $addedVariant = $true
    }
    if (-not $addedVariant -and $hasInlineSchema) {
        $inferredSchemas.Add([pscustomobject][ordered]@{
            type = $registration.type
            name = 'registration-lambda'
            discriminator = 'registration lambda layout'
            exactLength = $inlineSchema.exactLength
            minimumLength = $inlineSchema.minimumLength
            bodyIgnored = $inlineSchema.bodyIgnored
            fields = $inlineSchema.fields
            sources = @([ordered]@{ location = $registration.sources[0].location })
        })
        $addedVariant = $true
    }
    if (-not $addedVariant -and ($registration.statement -match '=>\s*Task\.CompletedTask' -or $registration.statement -match '=>\s*\{\s*return\s+Task\.CompletedTask')) {
        $inferredSchemas.Add([pscustomobject][ordered]@{
            type = $registration.type
            name = 'registration-noop'
            discriminator = 'registered no-op handler'
            exactLength = $null
            minimumLength = $null
            bodyIgnored = $true
            fields = @()
            sources = @([ordered]@{ location = $registration.sources[0].location })
        })
    }
}

$registrationAudit = $registrations | ForEach-Object {
    [pscustomobject][ordered]@{
        line = [int]($_.sources[0].location.Split(':')[-1])
        type = $_.type
        statement = $_.statement
    }
}
$inferredMerged = $inferredSchemas | Group-Object -Property type | ForEach-Object {
    [pscustomobject][ordered]@{
        type = [int]$_.Name
        variants = @($_.Group | ForEach-Object {
            [pscustomobject][ordered]@{
                name = $_.name
                discriminator = $_.discriminator
                exactLength = $_.exactLength
                minimumLength = $_.minimumLength
                bodyIgnored = $_.bodyIgnored
                fields = @($_.fields)
                sources = @($_.sources)
            }
        })
        sources = @($_.Group.sources | ForEach-Object { $_ } | Sort-Object location -Unique)
    }
} | Sort-Object type
$inferredMerged = @($inferredMerged | Where-Object {
    $_.variants.Count -gt 0
})

# Map registered handler methods back to their wire command type. This resolves
# response builders that use header.type/wireType instead of repeating a literal.
$methodTypeMap = @{}
foreach ($registration in $registrations) {
    $handlerMatches = @([regex]::Matches(
        $registration.statement,
        '(?:(?<receiver>_[A-Za-z_][A-Za-z0-9_]*)\s*\.)?\s*(?<name>(?:Handle|TryHandle)[A-Za-z0-9_]+)\b'))
    foreach ($handlerMatch in $handlerMatches) {
        $handlerName = $handlerMatch.Groups['name'].Value
        $receiver = $handlerMatch.Groups['receiver'].Value
        $rootName = $handlerName
        if ($receiver -and $receiverTypes.ContainsKey("GameProtocolHandler.$receiver")) {
            $receiverType = $receiverTypes["GameProtocolHandler.$receiver"]
            if ($methodIndex.ContainsKey("$receiverType.$handlerName")) { $rootName = "$receiverType.$handlerName" }
        }
        $methodTypeMap[$handlerName] = $registration.type
        $methodTypeMap[$rootName] = $registration.type
    }
}
for ($pass = 0; $pass -lt 4; $pass++) {
    foreach ($file in Get-ChildItem -LiteralPath $networkDir -Recurse -File -Filter *.cs) {
        $relative = $file.FullName.Substring($RepositoryRoot.Length).TrimStart('\').Replace('\', '/')
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($definition in Find-MethodDefinitions $text $relative) {
            $knownType = if ($methodTypeMap.ContainsKey("$($definition.className).$($definition.name)")) {
                $methodTypeMap["$($definition.className).$($definition.name)"]
            } elseif ($methodTypeMap.ContainsKey($definition.name)) { $methodTypeMap[$definition.name] } else { $null }
            if ($null -eq $knownType) { continue }
            foreach ($call in Find-BodyForwardingCalls $definition.body) {
                if (-not $call.receiver) {
                    if (-not $methodTypeMap.ContainsKey($call.name)) { $methodTypeMap[$call.name] = $knownType }
                } elseif ($receiverTypes.ContainsKey("$($definition.className).$($call.receiver)")) {
                    $receiverType = $receiverTypes["$($definition.className).$($call.receiver)"]
                    $methodTypeMap["$receiverType.$($call.name)"] = $knownType
                }
            }
        }
    }
}

function Resolve-DynamicOutboundType(
    [string]$expression,
    [hashtable]$enumMap,
    [object]$enclosingMethod,
    [hashtable]$methodTypeMap) {
    $resolved = Resolve-Type $expression $enumMap
    if ($null -ne $resolved) { return $resolved }
    $expr = $expression.Trim()
    if ($enclosingMethod -ne $null) {
        $methodKey = "$($enclosingMethod.className).$($enclosingMethod.name)"
        $methodType = if ($methodTypeMap.ContainsKey($methodKey)) { $methodTypeMap[$methodKey] } elseif ($methodTypeMap.ContainsKey($enclosingMethod.name)) { $methodTypeMap[$enclosingMethod.name] } else { $null }
        if ($expr -match '^(?:header|h)\.type$' -and $null -ne $methodType) { return $methodType }
        if ($expr -match '^(?:wireType|commandType|responseType|ackType|type|packetType|CommandType)$') {
            $pattern = '(?m)(?:const\s+)?(?:byte|ushort|int|uint)?\s*' + [regex]::Escape($expr) + '\s*=\s*(?<value>[^;]+);'
            $assignment = [regex]::Match($enclosingMethod.body, $pattern)
            if ($assignment.Success) {
                $candidate = Resolve-Type $assignment.Groups['value'].Value $enumMap
                if ($null -ne $candidate) { return $candidate }
            }
            if ($null -ne $methodType) { return $methodType }
        }
    }
    return $null
}

$outbound = @{}
$unresolved = [System.Collections.Generic.List[object]]::new()
foreach ($file in Get-ChildItem -LiteralPath $networkDir -Recurse -File -Filter *.cs) {
    $relative = $file.FullName.Substring($RepositoryRoot.Length).TrimStart('\').Replace('\', '/')
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $fileMethods = @(Find-MethodDefinitions $text $relative)
    foreach ($call in Find-Calls $text 'GamePacketEnvelopeBuilder.Build(') {
        if ($call.arguments.Count -lt 3) { continue }
        $enclosingMethod = $fileMethods | Where-Object {
            $_.line -le $call.line -and ($fileMethods | Where-Object line -gt $_.line | Select-Object -First 1).line -gt $call.line
        } | Select-Object -Last 1
        if ($null -eq $enclosingMethod) {
            $enclosingMethod = $fileMethods | Where-Object { $_.line -le $call.line } | Select-Object -Last 1
        }
        $command = Resolve-DynamicOutboundType $call.arguments[0] @{} $enclosingMethod $methodTypeMap
        if ($command -ne 0 -and $command -ne 1) {
            $unresolved.Add([ordered]@{ location = "$relative`:$($call.line)"; reason = 'dynamic-command'; arguments = $call.arguments })
            continue
        }
        $kind = if ($command -eq 0) { 'noti' } else { 'cmd' }
        $map = if ($kind -eq 'noti') { $notiByName } else { $cmdByName }
        $type = Resolve-DynamicOutboundType $call.arguments[1] $map $enclosingMethod $methodTypeMap
        if ($null -eq $type) {
            $unresolved.Add([ordered]@{ location = "$relative`:$($call.line)"; reason = 'dynamic-type'; arguments = $call.arguments })
            continue
        }
        $key = "$kind`:$type"
        if (-not $outbound.ContainsKey($key)) {
            $outbound[$key] = [ordered]@{ kind = $kind; type = $type; sources = [System.Collections.Generic.List[object]]::new(); variants = [System.Collections.Generic.List[object]]::new() }
        }
        $location = "$relative`:$($call.line)"
        if (-not ($outbound[$key].sources | Where-Object location -eq $location)) {
            $outbound[$key].sources.Add([ordered]@{ location = $location })
        }
        $builder = ($call.arguments[2] -replace '\s+', ' ').Trim()
        $variantName = if ($builder -match '(?<name>[A-Za-z0-9_]+)\s*\(') { $Matches.name } elseif ($builder -match '^new\s') { 'inline-bytes' } else { 'default' }
        $builderSchema = Resolve-BuilderSchema $builder $methodIndex
        if ($null -eq $builderSchema) {
            $builderSchema = Resolve-LocalOutboundSchema $builder $enclosingMethod $call.line $methodIndex
        }
        $variant = [ordered]@{ name = $variantName; bodyBuilder = $builder; sources = @([ordered]@{ location = $location }) }
        if ($null -ne $builderSchema) {
            $variant.discriminator = if ($builderSchema.exactLength -ne $null) { "exact body length $($builderSchema.exactLength)" } else { 'builder write sequence; dynamic tail retained' }
            $variant.confidence = 'confirmed-from-builder-write-sequence'
            $variant.exactLength = $builderSchema.exactLength
            $variant.minimumLength = $builderSchema.minimumLength
            $variant.bodyIgnored = $builderSchema.bodyIgnored
            $variant.fields = @($builderSchema.fields)
        }
        if ($builder -match '^new\s+byte\s*\[\s*\]\s*\{(?<bytes>.*)\}') {
            $fixed = @($Matches.bytes -split ',' | ForEach-Object {
                $token = $_.Trim()
                if ($token -match '^0x[0-9A-Fa-f]+$') { '{0:X2}' -f [Convert]::ToInt32($token.Substring(2), 16) }
                elseif ($token -match '^\d+$') { '{0:X2}' -f [int]$token }
                else { $null }
            })
            if ($fixed.Count -gt 0 -and -not ($fixed -contains $null)) {
                $variant.fixedBodyHex = ($fixed -join '')
            }
        }
        if (-not ($outbound[$key].variants | Where-Object { $_.bodyBuilder -eq $builder })) {
            $outbound[$key].variants.Add($variant)
        }
    }
}

$enums.cmd | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $protocolDir 'cmd-enum.json') -Encoding utf8
$enums.noti | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $protocolDir 'noti-enum.json') -Encoding utf8
ConvertTo-Json -InputObject @($registered) -Depth 8 | Set-Content -LiteralPath (Join-Path $protocolDir 'cmd-supported.json') -Encoding utf8
ConvertTo-Json -InputObject @($inferredMerged) -Depth 10 | Set-Content -LiteralPath (Join-Path $protocolDir 'cmd-inferred-schemas.json') -Encoding utf8
ConvertTo-Json -InputObject @($registrationAudit) -Depth 8 | Set-Content -LiteralPath (Join-Path $protocolDir 'cmd-registration-statements.json') -Encoding utf8
ConvertTo-Json -InputObject @($outbound.Values | Sort-Object kind, type) -Depth 10 | Set-Content -LiteralPath (Join-Path $protocolDir 'outbound-supported.json') -Encoding utf8
ConvertTo-Json -InputObject @($unresolved) -Depth 8 | Set-Content -LiteralPath (Join-Path $protocolDir 'outbound-unresolved.json') -Encoding utf8

[ordered]@{
    cmdEnumCount = $enums.cmd.Count
    notiEnumCount = $enums.noti.Count
    inboundCmdTypeCount = @($registered).Count
    inboundInferredSchemaCount = @($inferredMerged).Count
    outboundResolvedTypeCount = $outbound.Count
    outboundUnresolvedSiteCount = $unresolved.Count
} | ConvertTo-Json
