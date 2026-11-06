$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!(Test-Path "$PSScriptRoot/icon-renderer/node_modules/@resvg/resvg-js")) {
 npm ci --prefix "$PSScriptRoot/icon-renderer"
 if ($LASTEXITCODE) { throw 'SVG renderer setup failed; stop rather than substitute artwork' }
}
node "$PSScriptRoot/icon-renderer/render.cjs"
if ($LASTEXITCODE) { throw 'Original SVG export failed' }
