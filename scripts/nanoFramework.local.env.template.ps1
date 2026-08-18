# nanoFramework companion repositories configuration
# Copy to scripts\nanoFramework.local.env.ps1 and edit as needed.

# Branch/tag to use for nanoFramework companion repositories.
# Keep this aligned with the package generation you rely on in this repo.
$env:SMARTHOME_NANOFW_BRANCH = "main"

# Optional: override the sibling directory where companion repos are cloned.
# Default behavior clones beside SmartHome, so this normally stays empty.
# $env:SMARTHOME_NANOFW_ROOT = ""
