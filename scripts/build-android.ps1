#Requires -Version 7.0
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$android = Join-Path $root "src/android"

if (-not $env:JAVA_HOME) {
    Write-Host "JAVA_HOME is not set. Use JDK 17 or newer." -ForegroundColor Yellow
}
if (-not $env:ANDROID_HOME -and -not (Test-Path (Join-Path $android "local.properties"))) {
    throw "Set ANDROID_HOME or create src/android/local.properties with sdk.dir=..."
}

Push-Location $android
try {
    Write-Host "Building Android release..." -ForegroundColor Cyan
    & ".\gradlew.bat" --no-daemon assembleRelease
    if ($LASTEXITCODE -ne 0) { throw "Gradle build failed." }
} finally {
    Pop-Location
}

$out = Join-Path $root "dist/android"
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $android "app/build/outputs/apk/release/app-release.apk") (Join-Path $out "SameVoice-1.1.13.apk") -Force
Copy-Item (Join-Path $android "app/build/outputs/apk/debug/app-debug.apk") (Join-Path $out "SameVoice-1.1.13-debug.apk") -Force
Write-Host "Done: dist/android/SameVoice-1.1.13.apk" -ForegroundColor Green
