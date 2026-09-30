param([string]$PlaynitePath = $env:PLAYNITE_DIR)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PlaynitePath)) { throw 'Set PLAYNITE_DIR or pass -PlaynitePath pointing to the Playnite installation directory.' }
$playnite = [IO.Path]::GetFullPath($PlaynitePath)
if (!(Test-Path -LiteralPath (Join-Path $playnite 'Playnite.SDK.dll'))) { throw "Playnite.SDK.dll was not found in $playnite" }
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

$gac = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL'
$windowsBase = Get-ChildItem "$gac\WindowsBase\*\WindowsBase.dll" | Select-Object -First 1 -ExpandProperty FullName
$presentationCore = Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_64\PresentationCore\*\PresentationCore.dll' | Select-Object -First 1 -ExpandProperty FullName
if (!$presentationCore) { $presentationCore = Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_32\PresentationCore\*\PresentationCore.dll' | Select-Object -First 1 -ExpandProperty FullName }
$presentationFramework = Get-ChildItem "$gac\PresentationFramework\*\PresentationFramework.dll" | Select-Object -First 1 -ExpandProperty FullName
$systemXaml = Get-ChildItem "$gac\System.Xaml\*\System.Xaml.dll" | Select-Object -First 1 -ExpandProperty FullName
if (!$windowsBase -or !$presentationCore -or !$presentationFramework -or !$systemXaml) { throw 'Required WPF assemblies were not found in the .NET Framework GAC.' }

& $csc /nologo /target:library /out:"$root\LocalAchievements.dll" /reference:"$playnite\Playnite.SDK.dll" /reference:$windowsBase /reference:$presentationCore /reference:$presentationFramework /reference:$systemXaml "$root\LocalAchievements.cs" "$root\Localization.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$package = Join-Path (Split-Path -Parent $root) 'LocalAchievements_1.1.pext'
if (Test-Path $package) { Remove-Item -LiteralPath $package }
$zipPackage = [System.IO.Path]::ChangeExtension($package, '.zip')
if (Test-Path $zipPackage) { Remove-Item -LiteralPath $zipPackage }
Compress-Archive -Path "$root\extension.yaml", "$root\LocalAchievements.dll", "$root\rules" -DestinationPath $zipPackage
Move-Item -LiteralPath $zipPackage -Destination $package
Write-Host "Built: $package"
