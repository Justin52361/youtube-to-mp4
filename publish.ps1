# Builds the small framework-dependent single-file exe (bin\publish\YoutubeToMp4.exe).
# Needs the .NET 10 Desktop Runtime on the target PC; Windows offers a download link if it's missing.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish YoutubeToMp4.csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o bin\publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-Item bin\publish\YoutubeToMp4.exe | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}}
