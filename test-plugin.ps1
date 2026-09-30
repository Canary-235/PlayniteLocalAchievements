param([string]$PlaynitePath = $env:PLAYNITE_DIR, [string]$SuccessStoryDll, [string]$OptionalGameDll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PlaynitePath)) { throw 'Set PLAYNITE_DIR or pass -PlaynitePath pointing to the Playnite installation directory.' }
$sdk = Join-Path ([IO.Path]::GetFullPath($PlaynitePath)) 'Playnite.SDK.dll'
if (!(Test-Path -LiteralPath $sdk)) { throw "Playnite.SDK.dll was not found at $sdk" }
[void][Reflection.Assembly]::LoadFrom($sdk)
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root 'LocalAchievements.dll'))
$plugin = $assembly.GetType('LocalAchievements.AutoAchievementsPlugin')
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$language = $assembly.GetType('LocalAchievements.UiLanguage')
$language.GetField('English', [Reflection.BindingFlags]'Public,Static').SetValue($null, $true)
$translate = $language.GetMethod('T', [Reflection.BindingFlags]'Public,Static')
$chineseTitle = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('5pys5Zyw5oiQ5bCx6Ieq5Yqo6Kej6ZSBIC0g5b2T5YmN5peg5rOV5by556qX'))
if ($translate.Invoke($null, [object[]]@([string]$chineseTitle)) -ne 'Automatic Local Achievements - Pop-ups unavailable') {
    throw 'English translation did not apply to a plugin dialog.'
}
$language.GetField('English', [Reflection.BindingFlags]'Public,Static').SetValue($null, $false)

function Assert-True($condition, $message) {
    if (!$condition) { throw $message }
}

if ([string]::IsNullOrWhiteSpace($SuccessStoryDll)) { $SuccessStoryDll = Join-Path $env:APPDATA 'Playnite\Extensions\playnite-successstory-plugin\SuccessStory.dll' }
$successStoryDll = $SuccessStoryDll
if (Test-Path -LiteralPath $successStoryDll) {
    $successStoryAssembly = [Reflection.Assembly]::LoadFrom($successStoryDll)
    $successStoryDatabase = $successStoryAssembly.GetType('SuccessStory.Services.SuccessStoryDatabase')
    $gameAchievementsType = $successStoryAssembly.GetType('SuccessStory.Models.GameAchievements')
    $achievementType = $successStoryAssembly.GetType('SuccessStory.Models.Achievement')
    $settingsType = $successStoryAssembly.GetType('SuccessStory.SuccessStorySettings')
    $gameType = [Reflection.Assembly]::LoadFrom($sdk).GetType('Playnite.SDK.Models.Game')
    Assert-True ($null -ne $successStoryDatabase.GetMethod('Update', [Type[]]@($gameAchievementsType))) 'Installed SuccessStory has no live database Update(GameAchievements) method.'
    Assert-True ($null -ne $successStoryDatabase.GetMethod('SetThemesResources', [Type[]]@($gameType))) 'Installed SuccessStory has no theme-resource refresh method.'
    Assert-True ($achievementType.GetProperty('DateUnlocked').CanWrite) 'Installed SuccessStory cannot set achievement unlock time in memory.'
    Assert-True ($settingsType.GetProperty('Unlocked').CanWrite) 'Installed SuccessStory does not expose a writable theme achievement count.'
    $findStatic = $plugin.GetMethod('FindStaticProperty', [Reflection.BindingFlags]'NonPublic,Static')
    $staticFlags = [Reflection.BindingFlags]'Public,NonPublic'
    $staticDatabase = $findStatic.Invoke($null, [object[]]@($successStoryAssembly.GetType('SuccessStory.SuccessStory'), [string]'PluginDatabase', $staticFlags))
    Assert-True ($null -ne $staticDatabase -and $staticDatabase.GetMethod.IsStatic -and $staticDatabase.PropertyType -eq $successStoryDatabase) 'The installed SuccessStory live database is static and must be resolved through its base class.'
    Write-Output 'PASS: Installed SuccessStory supports live database and theme counter refresh.'
}

Add-Type -ReferencedAssemblies $sdk -TypeDefinition @'
public class ThemeRefreshProbe
{
    public Playnite.SDK.Models.Game GameContext { get; set; }
    public int Refreshes { get; private set; }
    public void SetThemesResources(Playnite.SDK.Models.Game game) { Refreshes++; }
}
'@
$probeGame = New-Object Playnite.SDK.Models.Game
$probeGame.Id = [Guid]::NewGuid()
$probe = New-Object ThemeRefreshProbe
$probe.GameContext = $probeGame
$probePlugin = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($plugin)
$refreshTheme = $plugin.GetMethod('RefreshSuccessStoryTheme', [Reflection.BindingFlags]'NonPublic,Instance')
$refreshArguments = New-Object object[] 2
$refreshArguments[0] = $probe.PSObject.BaseObject
$refreshArguments[1] = $probeGame.PSObject.BaseObject
[void]$refreshTheme.Invoke($probePlugin, $refreshArguments)
Assert-True ($probe.Refreshes -eq 1) 'Unlock did not recalculate the selected game theme values.'

$fixture = Join-Path $env:TEMP ('local-achievements-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $settings = Join-Path $fixture 'steam_settings'
    New-Item -ItemType Directory -Path $settings -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture 'steam_api64.dll'), 'test fixture')
    [IO.File]::WriteAllText((Join-Path $settings 'steam_appid.txt'), '480')
    [IO.File]::WriteAllText((Join-Path $settings 'configs.user.ini'), "[user::general]`nlocal_save_path=portable-saves`nsaves_folder_name=Alternate GSE`n")

    $find = $plugin.GetMethod('FindGseSchemaTarget', $flags)
    $target = $find.Invoke($null, [object[]]@([string]$fixture, [string]'480'))
    Assert-True ($target -eq (Join-Path $settings 'achievements.json')) 'Schema target was not placed beside the Steam API DLL.'

    $create = $plugin.GetMethod('CreateGseRules', $flags)
    $rules = $create.Invoke($null, [object[]]@([string]'Fixture', [string]'480'))
    $rules.settingsDirectory = [string]$settings
    $add = $plugin.GetMethod('AddGseRuleSources', $flags)
    [void]$add.Invoke($null, [object[]]@($rules, [string]$fixture))
    $paths = @($rules.sources | ForEach-Object { $_.path })
    Assert-True (($paths | Where-Object { $_ -like '*portable-saves\achievements.json' }).Count -eq 1) 'Portable save path not monitored.'
    Assert-True (($paths | Where-Object { $_ -like '*Alternate GSE\480\achievements.json' }).Count -eq 1) 'Custom GSE folder not monitored.'
    Assert-True (($paths | Where-Object { $_ -like '*Goldberg SteamEmu Saves\480\achievements.json' }).Count -eq 1) 'Legacy save folder not monitored.'

    $fetch = $plugin.GetMethod('FetchSteamAchievementSchema', $flags)
    $steam = $fetch.Invoke($null, [object[]]@([string]'480'))
    Assert-True ($steam.Count -ge 1) 'Steam returned no schema.'
    Assert-True ($steam[0]['internal_name'] -eq 'ACH_WIN_ONE_GAME') 'Unexpected Steam API achievement ID.'

    $autoGse = $plugin.GetMethod('CreateMissingGseSchema', $flags)
    [void]$autoGse.Invoke($null, [object[]]@([string]$fixture, [string]'480'))
    Assert-True (Test-Path -LiteralPath $target) 'Automatic GSE completion did not create the missing definitions.'
    $createdSchema = [IO.File]::ReadAllText($target)
    Assert-True ($createdSchema.Contains('ACH_WIN_ONE_GAME')) 'Created GSE definitions lack the Steam API ID.'
    [void]$autoGse.Invoke($null, [object[]]@([string]$fixture, [string]'480'))
    Assert-True ([IO.File]::ReadAllText($target) -eq $createdSchema) 'Automatic GSE completion overwrote an existing file.'
    $wrongGse = $false
    try { [void]$autoGse.Invoke($null, [object[]]@([string]$fixture, [string]'481')) }
    catch { $wrongGse = $true }
    Assert-True $wrongGse 'Automatic GSE completion accepted a mismatched App ID.'

    [IO.File]::WriteAllText($target, '[{"name":"ACH_WIN_ONE_GAME","displayName":"Custom title","statistic":"keep me"}]')
    $merge = $plugin.GetMethod('MergeGseSchema', $flags)
    $parameters = [object[]]@([string]$target, $steam, 0, 0, $false)
    $merged = $merge.Invoke($null, $parameters)
    Assert-True ($parameters[2] -eq 1) 'Existing schema count incorrect.'
    Assert-True ($parameters[3] -eq ($steam.Count - 1)) 'Missing achievements were not added.'
    Assert-True ($merged[0]['displayName'] -eq 'Custom title') 'Existing title was overwritten.'
    Assert-True ($merged[0]['statistic'] -eq 'keep me') 'Existing custom metadata was lost.'

    [IO.File]::WriteAllText($target, '{broken json')
    $parameters = [object[]]@([string]$target, $steam, 0, 0, $false)
    $repaired = $merge.Invoke($null, $parameters)
    Assert-True ($parameters[4] -eq $true -and $repaired.Count -eq $steam.Count) 'Malformed schema was not classified for backed-up rebuild.'

    $second = Join-Path $fixture 'second'
    $secondSettings = Join-Path $second 'steam_settings'
    New-Item -ItemType Directory -Path $secondSettings -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $second 'steam_api64.dll'), 'test fixture')
    [IO.File]::WriteAllText((Join-Path $secondSettings 'steam_appid.txt'), '480')
    $ambiguous = $false
    try { [void]$find.Invoke($null, [object[]]@([string]$fixture, [string]'480')) }
    catch { $ambiguous = $true }
    Assert-True $ambiguous 'Ambiguous Steam API directories must not be written automatically.'
    [IO.File]::WriteAllText((Join-Path $fixture 'Game-Win64-Shipping.exe'), 'test fixture')
    $resolved = $find.Invoke($null, [object[]]@([string]$fixture, [string]'480'))
    Assert-True ($resolved -eq $target) 'Unique game executable directory was not selected.'

    $runeDir = Join-Path $fixture 'codex'
    New-Item -ItemType Directory -Path $runeDir | Out-Null
    $runeIni = Join-Path $runeDir 'steam_emu.ini'
    [byte[]]$iniBytes = ([byte[]](0x80,0x81) + [Text.Encoding]::ASCII.GetBytes("`r`n### Game data is stored at C:\Users\Public\Documents\Steam\CODEX\1332010`r`n[Settings]`r`nAppId=1332010`r`n[Interfaces]`r`nSteamUser=SteamUser020`r`n"))
    [IO.File]::WriteAllBytes($runeIni, $iniBytes)
    [IO.File]::WriteAllBytes((Join-Path $runeDir 'steam_api64.cdx'), [Text.Encoding]::ASCII.GetBytes('STEAMUSERSTATS_INTERFACE_VERSION012'))
    $latin = [Text.Encoding]::GetEncoding(28591).GetString($iniBytes)
    $providerMethod = $plugin.GetMethod('GetRuneProvider', $flags)
    $provider = $providerMethod.Invoke($null, [object[]]@([string]$runeIni,[string]$latin,[string]'1332010'))
    Assert-True ($provider -eq 'CODEX') 'CODEX save location was not recognized.'
    $versionMethod = $plugin.GetMethod('DiscoverSteamUserStatsVersion', $flags)
    $version = $versionMethod.Invoke($null, [object[]]@([string]$runeIni))
    Assert-True ($version -eq 'STEAMUSERSTATS_INTERFACE_VERSION012') 'Original Steam interface version was not detected.'
    $insert = $plugin.GetMethod('InsertSteamUserStats', $flags)
    $insertArgs = New-Object object[] 2
    $insertArgs[0] = $iniBytes
    $insertArgs[1] = [string]$version
    $newBytes = [byte[]]$insert.Invoke($null, $insertArgs)
    Assert-True ($newBytes[0] -eq 0x80 -and $newBytes[1] -eq 0x81) 'Original config bytes were not preserved.'
    Assert-True ([Text.Encoding]::ASCII.GetString($newBytes).Contains("[Interfaces]`r`nSteamUserStats=STEAMUSERSTATS_INTERFACE_VERSION012`r`n")) 'Interface was not inserted under [Interfaces].'
    $autoRune = $plugin.GetMethod('CreateMissingRuneInterface', $flags)
    [void]$autoRune.Invoke($null, [object[]]@([string]$runeDir, [string]'1332010'))
    Assert-True ([IO.File]::ReadAllText($runeIni).Contains('SteamUserStats=STEAMUSERSTATS_INTERFACE_VERSION012')) 'Automatic RUNE/CODEX completion did not insert the interface.'
    Assert-True (@(Get-ChildItem -LiteralPath $runeDir -Filter 'steam_emu.ini.local-achievements-*.bak').Count -eq 1) 'Automatic RUNE/CODEX completion did not back up the original config.'
    $runeAfter = [IO.File]::ReadAllBytes($runeIni)
    [void]$autoRune.Invoke($null, [object[]]@([string]$runeDir, [string]'1332010'))
    Assert-True ([Linq.Enumerable]::SequenceEqual([byte[]]$runeAfter, [byte[]][IO.File]::ReadAllBytes($runeIni))) 'Automatic RUNE/CODEX completion changed an already-configured file.'
    $wrongRune = $false
    try { [void]$autoRune.Invoke($null, [object[]]@([string]$runeDir, [string]'1332011')) }
    catch { $wrongRune = $true }
    Assert-True $wrongRune 'Automatic RUNE/CODEX completion accepted a mismatched App ID.'
    $makeRune = $plugin.GetMethod('CreateRuneRules', $flags)
    $codexRules = $makeRune.Invoke($null, [object[]]@([string]'Stray',[string]'1332010',[string]'CODEX'))
    Assert-True ($codexRules.sources.Count -eq 1 -and $codexRules.sources[0].path -like '*\CODEX\1332010\achievements.ini') 'CODEX rule watches the wrong save path.'
    $oldRules = $makeRune.Invoke($null, [object[]]@([string]'Stray',[string]'1332010',[string]'RUNE'))
    $upgrade = $plugin.GetMethod('UpgradeRuneRuleSources', $flags)
    $upgraded = $upgrade.Invoke($null, [object[]]@($oldRules,[string]$runeDir))
    Assert-True $upgraded ("Old RUNE rule was not upgraded: name={0}; path={1}" -f $oldRules.name,$oldRules.sources[0].path)
    Assert-True ($oldRules.sources[0].path -like '*\CODEX\1332010\achievements.ini') 'Migration did not correct the path.'

    $replacedDir = Join-Path $fixture 'replaced-rune'
    New-Item -ItemType Directory -Path $replacedDir | Out-Null
    [IO.File]::WriteAllText((Join-Path $replacedDir 'steam_emu.ini'), "[Settings]`r`nAppId=3447040`r`n[Interfaces]`r`nSteamUserStats=STEAMUSERSTATS_INTERFACE_VERSION012`r`n")
    [IO.File]::WriteAllText((Join-Path $replacedDir 'steam_api64.dll'), 'GSE Saves steam_settings')
    [IO.File]::WriteAllText((Join-Path $replacedDir 'steam_api64.rne'), 'original Steam API')
    $replacedTarget = $find.Invoke($null, [object[]]@([string]$replacedDir, [string]'3447040'))
    Assert-True ($replacedTarget -eq (Join-Path $replacedDir 'steam_settings\achievements.json')) 'GSE replacement without steam_appid.txt was not recognized.'
    $replacedRules = $makeRune.Invoke($null, [object[]]@([string]'Sora no Kiseki the 1st',[string]'3447040',[string]'RUNE'))
    $replaceRule = $plugin.GetMethod('UpgradeReplacedRuneRule', $flags)
    Assert-True ($replaceRule.Invoke($null,[object[]]@($replacedRules,[string]$replacedDir))) 'Legacy RUNE rule was not migrated to GSE.'
    Assert-True ($replacedRules.name -like '*GSE 3447040*') 'Migrated rule did not identify GSE.'
    Assert-True (($replacedRules.sources | Where-Object { $_.path -like '*GSE Saves\3447040\achievements.json' }).Count -eq 1) 'Migrated rule missed the GSE save.'
    $instance = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($plugin)
    $detect = $plugin.GetMethod('DetectEmulatorRules', [Reflection.BindingFlags]'NonPublic,Instance')
    $detectArgs = [object[]]@([string]$replacedDir,[string]'Sora no Kiseki the 1st',$null,$null)
    $detected = $detect.Invoke($instance,$detectArgs)
    Assert-True ($detectArgs[2] -eq '3447040' -and $detectArgs[3] -eq 'Goldberg / GSE') 'DLL-based detection did not take precedence over stale RUNE ini.'
    Assert-True ($detected.settingsDirectory -eq (Join-Path $replacedDir 'steam_settings')) 'Detected GSE settings directory was wrong.'

    $realSoraDll = $OptionalGameDll
    if (![string]::IsNullOrWhiteSpace($realSoraDll) -and (Test-Path -LiteralPath $realSoraDll)) {
        $isGse = $plugin.GetMethod('IsGseLibrary', $flags)
        Assert-True ($isGse.Invoke($null,[object[]]@([string]$realSoraDll))) 'The installed Sora no Kiseki DLL should be detected as GSE.'
        $realDirectory = [IO.Path]::GetDirectoryName($realSoraDll)
        $realArgs = [object[]]@([string]$realDirectory,[string]'Sora no Kiseki the 1st',$null,$null)
        $realRules = $detect.Invoke($instance,$realArgs)
        Assert-True ($realArgs[2] -eq '3447040' -and $realArgs[3] -eq 'Goldberg / GSE') 'Installed Sora no Kiseki was not detected as GSE.'
        Assert-True ($realRules.settingsDirectory -eq (Join-Path $realDirectory 'steam_settings')) 'Installed Sora no Kiseki settings target was wrong.'
        $realTarget = $find.Invoke($null,[object[]]@([string]$realDirectory,[string]'3447040'))
        Assert-True ($realTarget -eq (Join-Path $realDirectory 'steam_settings\achievements.json')) 'Installed Sora no Kiseki schema target was wrong.'
        $realSchema = $fetch.Invoke($null,[object[]]@([string]'3447040'))
        Assert-True ($realSchema.Count -ge 1) 'Steam returned no Sora no Kiseki achievement definitions.'
        Write-Output ("PASS: Sora no Kiseki Steam definitions {0}; GSE target {1}" -f $realSchema.Count,$realTarget)
    }
    Write-Output ("PASS: Steam schema {0}; merged {1}; monitored paths {2}" -f $steam.Count, $merged.Count, $paths.Count)
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('local-achievements-test-', [StringComparison]::Ordinal) -and
        (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
