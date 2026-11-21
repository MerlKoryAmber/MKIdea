# graphify-gate.ps1 — PreToolUse hook: blocks BROAD code exploration.
# Project rule (CLAUDE.md + memory use-graphify-first): broad code navigation must go
# through `graphify query/explain/path`, not raw grep/glob over the whole src tree.
#
# Mode: ALWAYS block broad exploration (does not rely on the agent remembering the graph).
# Targeted ops (Read of one file, grep of a single file / narrow path) pass through.
# graphify itself and utility commands (git, dotnet, build) pass through.
#
# Output: exit 0 + JSON permissionDecision=deny on block; exit 0 with no output = allow.
# Reason text is ASCII/English to survive Windows PowerShell 5.1 console encoding.

$ErrorActionPreference = 'Stop'

function Allow { exit 0 }  # print nothing => allowed

$hint = "BLOCKED: broad code exploration. MKADIdea rule: navigate code via the graph first. Run: graphify query ""<what you look for>"" (or: graphify explain ""<node>"" / graphify path ""A"" ""B""). The graph returns a scoped subgraph, cheaper than grepping all of src. After orienting, a targeted Read of a specific file or grep over a single file / narrow path is fine (not blocked)."

function Deny($reason) {
  $obj = @{
    hookSpecificOutput = @{
      hookEventName            = 'PreToolUse'
      permissionDecision       = 'deny'
      permissionDecisionReason = $reason
    }
  }
  ($obj | ConvertTo-Json -Depth 6 -Compress)
  exit 0
}

# --- read stdin ---
$raw = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($raw)) { Allow }

try { $in = $raw | ConvertFrom-Json } catch { Allow }  # unparseable => don't interfere

$tool = [string]$in.tool_name
$ti   = $in.tool_input

# Is this path "narrow" (a concrete file, or one specific deep subdir; no ** and not the src root)?
function Is-NarrowPath([string]$p) {
  if ([string]::IsNullOrWhiteSpace($p)) { return $false }   # empty = whole project = broad
  $p = $p.Trim().Trim('"').Trim("'")
  if ($p -match '\*\*') { return $false }                    # recursive glob = broad
  if ($p -in @('.','./','src','src/','./src','./src/','*','**')) { return $false }
  if ($p -match '(^|[\\/])src[\\/]?\*') { return $false }     # src/* etc.
  # concrete file with extension => narrow
  if ($p -match '\.[A-Za-z0-9]+$' -and $p -notmatch '\*') { return $true }
  # one specific subdir deeper than src (e.g. src/AdManager.Web/Components/Pages) without * => narrow
  if ($p -notmatch '\*' -and ($p -split '[\\/]').Count -ge 3) { return $true }
  return $false
}

switch ($tool) {
  'Grep' {
    $path = [string]$ti.path
    $glob = [string]$ti.glob
    if (Is-NarrowPath $path) { Allow }
    # no narrow path => code-wide search
    if ($glob -match '\*\*') { Deny $hint }
    Deny $hint
  }
  'Glob' {
    $pattern = [string]$ti.pattern
    $path    = [string]$ti.path
    if (Is-NarrowPath $path) { Allow }
    if ($pattern -match '\*\*') { Deny $hint }
    if ([string]::IsNullOrWhiteSpace($path) -and $pattern -match '[\\/]' -and $pattern -match '\*') { Deny $hint }
    Allow
  }
  'Bash' {
    $cmd = [string]$ti.command
    if ([string]::IsNullOrWhiteSpace($cmd)) { Allow }
    if ($cmd -match '(^|[\s;&|])graphify(\s|$)') { Allow }   # graphify always allowed
    # code-search utilities
    $searchTool = $cmd -match '(^|[\s;&|(])(grep|rg|ripgrep|ag|ack|findstr|Select-String|sls)(\s|$)' `
                  -or $cmd -match 'Get-ChildItem[^\n|;]*-[rR]ec' `
                  -or $cmd -match '(^|[\s;&|(])gci[^\n|;]*-[rR]'
    if (-not $searchTool) { Allow }
    # it's a search. Narrow? recursion / ** / wildcard / src-root => broad.
    $recursive = $cmd -match '\s-[rR](\s|$)' -or $cmd -match '\s-[rR]ec' -or $cmd -match '--recursive' -or $cmd -match '\*\*'
    if ($recursive) { Deny $hint }
    if ($cmd -match '\*\.(cs|razor|csproj|json|ps1)\b') { Deny $hint }   # wildcard over file types
    # extract path-like tokens (contain a slash or a dotted extension); if any is narrow-file => allow
    $tokens = [regex]::Matches($cmd, '[^\s"'']+') | ForEach-Object { $_.Value }
    $hasNarrowFile = $false
    $hasBroadTarget = $false
    foreach ($t in $tokens) {
      if ($t -match '^-') { continue }                       # flags
      if ($t -notmatch '[\\/]' -and $t -notmatch '\.[A-Za-z0-9]+$') { continue }  # not a path
      if ($t -match '\*') { $hasBroadTarget = $true; continue }
      if ($t -in @('.','src','src/','./src','./src/')) { $hasBroadTarget = $true; continue }
      if (Is-NarrowPath $t) { $hasNarrowFile = $true }
      elseif ($t -match '[\\/]') { $hasBroadTarget = $true }  # a dir path that isn't a narrow subdir
    }
    if ($hasNarrowFile -and -not $hasBroadTarget) { Allow }
    Deny $hint
  }
  default { Allow }
}
Allow
