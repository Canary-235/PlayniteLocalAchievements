using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;
using System.Media;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace LocalAchievements
{
    // Rule files intentionally contain only local, game-readable conditions. They do not
    // contain Steam credentials, and this plugin never writes Steam achievement data.
    public class RuleSet
    {
        public int schemaVersion { get; set; }
        public string name { get; set; }
        public string settingsDirectory { get; set; }
        public List<RuleSource> sources { get; set; }
    }

    public class RuleSource
    {
        public string type { get; set; } // "log" (append only) or "snapshot" (whole file)
        public string path { get; set; }
        public string encoding { get; set; }
        public string match { get; set; }
        public string achievementGroup { get; set; }
    }

    public class AutoAchievementsPlugin : GenericPlugin
    {
        private static readonly Guid PluginId = new Guid("6d1a3eae-5c61-4f1f-bb72-9c355dc0e5d9");
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly List<ActiveSource> activeSources = new List<ActiveSource>();
        private readonly Dictionary<string, DateTime> unlocked = new Dictionary<string, DateTime>();
        private Game activeGame;
        private DispatcherTimer initialSyncTimer;
        private DispatcherTimer deferredSnapshotTimer;
        private DispatcherTimer logPollTimer;
        private AutoAchievementsSettings pluginSettings;
        // Older TENOKE releases used `id = true`; newer releases persist a TOML-like
        // entry such as `"id" = {unlocked = true, time = ...}`. Support both forms.
        private const string TenokeAchievementMatch = "(?mi)(?:^\\s*\\[ACHIEVEMENTS\\]\\s*$\\r?\\n(?:(?!^\\[).)*?^\\s*(?<achievementId>[^\\s=;]+)\\s*=\\s*(?:1|true)\\s*$|^\\s*\\\"?(?<achievementId>[^\\\"\\s=;]+)\\\"?\\s*=\\s*\\{\\s*unlocked\\s*=\\s*true\\b)";
        private const string RpgMakerObserverMarker = "/* LocalAchievements observer v1 */";
        private const string RpgMakerObserverFile = "local-achievements-events.log";
        private const string RpgMakerAchievementMatch = "(?m)^\\{\\\"id\\\":\\\"(?<achievementId>[A-Za-z0-9_]+)\\\",\\\"time\\\":\\\"(?<unlockedAt>[^\\\"]+)\\\"\\}\\s*$";
        private const string RpgMakerHookAnchor = "Game_System.prototype.unlockAchievement = function(achievement,callback,errorcallback) {";

        public override Guid Id { get { return PluginId; } }

        public AutoAchievementsPlugin(IPlayniteAPI api) : base(api)
        {
            // A small settings page makes the extension discoverable in Playnite's
            // Extensions settings list; actual configuration remains per-game.
            Properties = new GenericPluginProperties { HasSettings = true };
            pluginSettings = new AutoAchievementsSettings(Path.Combine(UserDataPath, "language.txt"), PromptRestartAfterLanguageChange);
            UiLanguage.English = pluginSettings.Language == "en";
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return pluginSettings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = UiLanguage.English ? "Automatic Local Achievements 1.1" : "本地成就自动解锁 1.1", FontWeight = FontWeights.Bold, FontSize = 16, Margin = new Thickness(0, 0, 0, 10) });
            panel.Children.Add(new TextBlock { Text = UiLanguage.English ? "Language (takes effect after restarting Playnite)" : "界面语言（重启 Playnite 后生效）", Margin = new Thickness(0, 0, 0, 5) });
            var languageBox = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            languageBox.Items.Add(new ComboBoxItem { Content = "中文", Tag = "zh" });
            languageBox.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
            languageBox.SelectedIndex = pluginSettings.Language == "en" ? 1 : 0;
            languageBox.SelectionChanged += delegate { var selected = languageBox.SelectedItem as ComboBoxItem; if (selected != null) { pluginSettings.Language = selected.Tag as string; } };
            panel.Children.Add(languageBox);
            panel.Children.Add(new TextBlock { Text = UiLanguage.English
                ? "Configure rules separately for each game from its right-click menu. Automatic detection can create missing GSE achievement definitions or safely complete a RUNE/CODEX SteamUserStats interface, then report the result. Neither action creates unlock events; the game must write a local achievement state. SuccessStory lists are imported separately."
                : "此扩展按游戏分别配置。请在游戏右键菜单中导入规则或自动识别。自动识别可补全缺失的 GSE 成就定义，或安全补写 RUNE/CODEX 的 SteamUserStats 接口，并报告结果。两者都不会产生解锁事件；游戏仍须写入本地成就状态。SuccessStory 成就列表需另行导入。", TextWrapping = TextWrapping.Wrap, MaxWidth = 550 });
            return new UserControl { Content = panel };
        }

        private MessageBoxResult ShowLocalizedMessage(string message, string title)
        {
            return PlayniteApi.Dialogs.ShowMessage(UiLanguage.T(message), UiLanguage.T(title));
        }
        private MessageBoxResult ShowLocalizedMessage(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
        {
            return PlayniteApi.Dialogs.ShowMessage(UiLanguage.T(message), UiLanguage.T(title), buttons, icon);
        }
        private void ShowLocalizedError(string message, string title)
        {
            PlayniteApi.Dialogs.ShowErrorMessage(UiLanguage.T(message), UiLanguage.T(title));
        }
        private void AddLocalizedNotification(string id, string message, NotificationType type)
        {
            PlayniteApi.Notifications.Add(id, UiLanguage.T(message), type);
        }

        private void PromptRestartAfterLanguageChange()
        {
            if (ShowLocalizedMessage("界面语言已更改。现在重启 Playnite 以应用新语言吗？", "重启 Playnite？", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { return; }
            // Playnite does not expose Restart through IPlayniteAPI. Its own
            // application has a restart method; defer until settings save exits.
            Application.Current.Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    Type applicationType = null;
                    foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (String.Equals(assembly.GetName().Name, "Playnite", StringComparison.OrdinalIgnoreCase))
                        {
                            applicationType = assembly.GetType("Playnite.PlayniteApplication", false);
                            if (applicationType != null) { break; }
                        }
                    }
                    if (applicationType == null) { throw new InvalidOperationException("当前 Playnite 版本未提供可调用的重启功能。"); }
                    var currentProperty = applicationType.GetProperty("Current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    var restart = applicationType.GetMethod("Restart", new[] { typeof(bool) });
                    object current = currentProperty == null ? null : currentProperty.GetValue(null, null);
                    if (current == null || restart == null) { throw new InvalidOperationException("当前 Playnite 版本未提供可调用的重启功能。"); }
                    restart.Invoke(current, new object[] { true });
                }
                catch (Exception exception) { ShowLocalizedError("自动重启失败，请手动重启 Playnite：" + exception.GetBaseException().Message, "本地成就自动解锁"); }
            }), DispatcherPriority.ApplicationIdle);
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var items = new List<GameMenuItem>();
            if (args.Games == null || args.Games.Count != 1) { return items; }
            Game game = args.Games[0];
            items.Add(new GameMenuItem
            {
                MenuSection = UiLanguage.T("本地成就自动解锁"),
                Description = UiLanguage.T("导入自动成就规则文件…"),
                Action = delegate(GameMenuItemActionArgs _) { ImportRules(game); }
            });
            items.Add(new GameMenuItem
            {
                MenuSection = UiLanguage.T("本地成就自动解锁"),
                Description = UiLanguage.T("自动识别模拟器并配置规则"),
                Action = delegate(GameMenuItemActionArgs _) { AutoDetectAndImportRules(game); }
            });
            items.Add(new GameMenuItem
            {
                MenuSection = UiLanguage.T("本地成就自动解锁|文件补全"),
                Description = UiLanguage.T("GSE 成就定义…"),
                Action = delegate(GameMenuItemActionArgs _) { CompleteGseSchema(game); }
            });
            items.Add(new GameMenuItem
            {
                MenuSection = UiLanguage.T("本地成就自动解锁|文件补全"),
                Description = UiLanguage.T("RUNE/CODEX 成就接口…"),
                Action = delegate(GameMenuItemActionArgs _) { CompleteRuneInterface(game); }
            });
            items.Add(new GameMenuItem
            {
                MenuSection = UiLanguage.T("本地成就自动解锁"),
                Description = UiLanguage.T("查看成就来源健康状态"),
                Action = delegate(GameMenuItemActionArgs _) { ShowStatus(game); }
            });
            items.Add(new GameMenuItem
            {
                MenuSection = UiLanguage.T("本地成就自动解锁"),
                Description = UiLanguage.T("测试成就弹窗"),
                // This checks only that a toast can render on Playnite's current
                // monitor. It uses a real existing achievement from this game's
                // SuccessStory list, without marking anything unlocked.
                Action = delegate(GameMenuItemActionArgs _)
                {
                    SuccessStoryAchievement preview = GetPreviewAchievement(game);
                    ShowToast(preview == null ? UiLanguage.T("测试成就") : preview.Title, preview == null ? UiLanguage.T("用于预览成就通知的位置和音效。") : preview.Description, 1, false, preview == null ? null : preview.IconUrl, game.Name);
                }
            });
            if (File.Exists(RulesPath(game)))
            {
                items.Add(new GameMenuItem
                {
                    MenuSection = UiLanguage.T("本地成就自动解锁"),
                    Description = UiLanguage.T("立即补扫并同步本地成就"),
                    // This is not a manual unlock: it only reads the game's existing
                    // local achievement state and recovers events missed while a
                    // launcher process ended unusually quickly.
                    Action = delegate(GameMenuItemActionArgs _) { RecoverLocalAchievements(game); }
                });
                items.Add(new GameMenuItem
                {
                    MenuSection = UiLanguage.T("本地成就自动解锁"),
                    Description = UiLanguage.T("移除自动成就规则"),
                    Action = delegate(GameMenuItemActionArgs _) { RemoveRules(game); }
                });
            }
            return items;
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            base.OnGameStarted(args);
            StopMonitoring();
            if (args.Game == null || !File.Exists(RulesPath(args.Game))) { return; }
            activeGame = args.Game;
            LoadProgress(activeGame);
            // Start watchers first. A number of lightweight launchers exit before the
            // old two-second deferred scan can run; immediately scan only small local
            // state files so their already-persisted achievements are not missed.
            StartMonitoring(activeGame, false, false, false);
            SyncSmallSnapshots(true);
            StartLogPolling();
            var startedGameId = activeGame.Id;
            if (deferredSnapshotTimer != null) { deferredSnapshotTimer.Stop(); }
            deferredSnapshotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            deferredSnapshotTimer.Tick += delegate
            {
                deferredSnapshotTimer.Stop();
                deferredSnapshotTimer = null;
                if (activeGame != null && activeGame.Id == startedGameId) { SyncSnapshots(true); }
            };
            deferredSnapshotTimer.Start();
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (activeGame != null && args.Game != null && activeGame.Id == args.Game.Id)
            {
                // A final snapshot scan catches saves written while the game closes.
                foreach (ActiveSource source in activeSources)
                {
                    if (source.Definition.type == "log") { ProcessSource(source); }
                }
                SyncSnapshots(true);
                StopMonitoring();
                // The fullscreen game-details page can become active only after
                // Playnite finishes its stop transition. Recompute its theme-bound
                // achievement count once that transition has settled.
                Game stoppedGame = args.Game;
                var themeRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                themeRefreshTimer.Tick += delegate
                {
                    themeRefreshTimer.Stop();
                    RefreshSuccessStoryThemeForGame(stoppedGame);
                };
                themeRefreshTimer.Start();
            }
            base.OnGameStopped(args);
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);
            // Give SuccessStory time to load its user database, then synchronize historical
            // local progress silently. Future game events create the notification.
            initialSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            initialSyncTimer.Tick += delegate
            {
                initialSyncTimer.Stop(); initialSyncTimer = null;
                foreach (Game game in PlayniteApi.Database.Games)
                {
                    if (!File.Exists(RulesPath(game))) { continue; }
                    try
                    {
                        RuleSet existing = ReadRules(RulesPath(game));
                        bool upgraded = UpgradeKnownRuleFormat(existing);
                        upgraded |= UpgradeReplacedRuneRule(existing, game.InstallDirectory);
                        upgraded |= UpgradeRuneRuleSources(existing, game.InstallDirectory);
                        if (upgraded) { File.WriteAllText(RulesPath(game), serializer.Serialize(existing), new UTF8Encoding(false)); }
                    }
                    catch { }
                    // Do not inspect save snapshots at application startup. Doing so
                    // silently consumed newly discovered achievements before the game
                    // session could show its notification. Existing local history may
                    // still be reconciled with SuccessStory without reading the game.
                    LoadProgress(game);
                    SyncAllToSuccessStory(game);
                }
                activeGame = null;
            };
            initialSyncTimer.Start();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            StopMonitoring();
            if (initialSyncTimer != null) { initialSyncTimer.Stop(); initialSyncTimer = null; }
            if (deferredSnapshotTimer != null) { deferredSnapshotTimer.Stop(); deferredSnapshotTimer = null; }
            if (logPollTimer != null) { logPollTimer.Stop(); logPollTimer = null; }
            base.OnApplicationStopped(args);
        }

        private string UserDataPath
        {
            get { string path = GetPluginUserDataPath(); Directory.CreateDirectory(path); return path; }
        }
        private string RulesDirectory
        {
            get { string path = Path.Combine(UserDataPath, "rules"); Directory.CreateDirectory(path); return path; }
        }
        private string RulesPath(Game game) { return Path.Combine(RulesDirectory, game.Id.ToString("N") + ".json"); }
        private string ProgressPath(Game game) { return Path.Combine(UserDataPath, "progress-" + game.Id.ToString("N") + ".tsv"); }
        private string NotificationPath(Game game) { return Path.Combine(UserDataPath, "notified-" + game.Id.ToString("N") + ".txt"); }

        private void ImportRules(Game game)
        {
            string file = PlayniteApi.Dialogs.SelectFile(UiLanguage.T("自动成就规则文件 (*.json)|*.json"));
            if (String.IsNullOrEmpty(file)) { return; }
            try
            {
                RuleSet rules = ReadRules(file);
                ValidateRules(rules);
                File.Copy(file, RulesPath(game), true);
                activeGame = game;
                LoadProgress(game);
                StartMonitoring(game, false, true, true);
                SyncSnapshots(true);
                StopMonitoring();
                SyncAllToSuccessStory(game);
                ShowLocalizedMessage("已为“" + game.Name + "”导入规则。下次通过 Playnite 启动此游戏时会自动运行。\n\n如需同步至 SuccessStory，请先在 SuccessStory 中手动添加或导入该游戏的成就列表。规则中的成就 ID 必须与 SuccessStory 的 ApiName 相同。", "本地成就自动解锁");
                activeGame = null;
            }
            catch (Exception exception)
            {
                ShowLocalizedError("规则文件无效：\n" + exception.Message, "本地成就自动解锁");
            }
        }

        // Creates a rule only from the local Steam-emulator configuration.  It never
        // changes a game executable, save file, emulator setting, or Steam account.
        private void AutoDetectAndImportRules(Game game)
        {
            // A valid existing rule must remain usable even when an emulator's
            // temporary detection evidence (for example Goldberg's shared "0"
            // folder) has aged out. Do not replace that working rule with a later
            // "not detected" message.
            if (File.Exists(RulesPath(game)))
            {
                try
                {
                    RuleSet existing = ReadRules(RulesPath(game));
                    ValidateRules(existing);
                    bool upgraded = UpgradeKnownRuleFormat(existing);
                    upgraded |= UpgradeReplacedRuneRule(existing, game.InstallDirectory);
                    upgraded |= AddGseRuleSources(existing, game.InstallDirectory);
                    upgraded |= UpgradeRuneRuleSources(existing, game.InstallDirectory);
                    if (upgraded) { File.WriteAllText(RulesPath(game), serializer.Serialize(existing), new UTF8Encoding(false)); }
                    var paths = new List<string>();
                    foreach (RuleSource source in existing.sources)
                    {
                        paths.Add(Environment.ExpandEnvironmentVariables((source.path ?? String.Empty).Replace('/', '\\')));
                    }
                    string existingDirectory = game.InstallDirectory;
                    string existingEmulator = existing.name ?? String.Empty;
                    Action<string> showExisting = delegate(string completion)
                    {
                        bool existingCannotPopup = false;
                        string existingPreflight = !String.IsNullOrWhiteSpace(existingDirectory) && Directory.Exists(existingDirectory)
                            ? BuildAchievementPreflight(existingDirectory, existingEmulator, existing, out existingCannotPopup)
                            : "ℹ 未保存有效安装目录，无法重新执行兼容性预检。";
                        string existingTitle = existingCannotPopup ? "本地成就自动解锁 - 当前无法弹窗" : "本地成就自动解锁";
                        ShowLocalizedMessage("“" + game.Name + "”已经配置了自动成就规则，无需重复识别。" + (upgraded ? "\n\n✓ 已更新内置规则格式或模拟器路径。" : String.Empty) +
                            (String.IsNullOrEmpty(completion) ? String.Empty : "\n\n" + completion) + "\n\n正在监听：\n" + String.Join("\n", paths) + "\n\n兼容性预检：\n" + existingPreflight + "\n\n若要更换规则，请先使用“移除自动成就规则”，再重新识别或导入规则文件。", existingTitle);
                    };
                    Match existingId = Regex.Match(existingEmulator, "\\(GSE (?<id>\\d+)\\)", RegexOptions.IgnoreCase);
                    if (existingId.Success && !String.IsNullOrWhiteSpace(existingDirectory) && Directory.Exists(existingDirectory))
                    {
                        AutoCompleteMissingGseSchema(game, existingDirectory, existingId.Groups["id"].Value, showExisting);
                    }
                    else if ((existingEmulator.IndexOf("RUNE", StringComparison.OrdinalIgnoreCase) >= 0 || existingEmulator.IndexOf("CODEX", StringComparison.OrdinalIgnoreCase) >= 0) &&
                             !String.IsNullOrWhiteSpace(existingDirectory) && Directory.Exists(existingDirectory))
                    {
                        Match runeId = Regex.Match(existingEmulator, "\\((?:RUNE|CODEX|RUNE / CODEX) (?<id>\\d+)\\)", RegexOptions.IgnoreCase);
                        AutoCompleteMissingRuneInterface(game, existingDirectory, runeId.Success ? runeId.Groups["id"].Value : null, showExisting);
                    }
                    else { showExisting(String.Empty); }
                    return;
                }
                catch { /* An invalid legacy rule should fall through to fresh detection. */ }
            }
            string gameDirectory = ResolveGameDirectory(game);
            if (String.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            {
                gameDirectory = PlayniteApi.Dialogs.SelectFolder();
                if (String.IsNullOrWhiteSpace(gameDirectory)) { return; }
                game.InstallDirectory = gameDirectory;
                PlayniteApi.Database.Games.Update(game);
            }

            try
            {
                string appId;
                string emulator;
                RuleSet rules = DetectEmulatorRules(gameDirectory, game.Name, out appId, out emulator);
                if (rules == null)
                {
                    ShowLocalizedMessage("在以下目录未发现受支持的本地成就来源：\n" + gameDirectory + "\n\n目前可自动识别：\n• RUNE / CODEX 的 steam_emu.ini\n• Goldberg / GSE 的 steam_settings\\steam_appid.txt\n• 旧版 Goldberg SteamEmu（已知 App ID 的无配置发布）\n• TENOKE 的 tenoke.ini\n• RELOADED 的 steam_api.ini\n• RPG Maker MV 的 Archeia / Greenworks 成就脚本\n\n你仍可导入自定义 JSON 规则文件。", "本地成就自动解锁");
                    return;
                }

                ValidateRules(rules);
                bool isRpgMakerObserver = String.Equals(emulator, "RPG Maker MV / Greenworks", StringComparison.Ordinal);
                if (isRpgMakerObserver)
                {
                    string observerScript = FindRpgMakerObserverScript(gameDirectory);
                    if (String.IsNullOrEmpty(observerScript)) { throw new InvalidDataException("未找到可安装观察器的 RPG Maker 成就脚本。"); }
                    if (!File.ReadAllText(observerScript, Encoding.UTF8).Contains(RpgMakerObserverMarker) &&
                        ShowLocalizedMessage("此游戏使用 RPG Maker MV / Greenworks 成就脚本。为记录游戏实际发出的成就事件，插件需要先备份并修改该脚本：\n" + observerScript + "\n\n是否继续？", "本地成就自动解锁", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    { return; }
                    InstallRpgMakerObserver(observerScript);
                }
                File.WriteAllText(RulesPath(game), serializer.Serialize(rules), new UTF8Encoding(false));
                activeGame = game;
                LoadProgress(game);
                StartMonitoring(game, false, true, true);
                SyncSnapshots(true);
                StopMonitoring();
                SyncAllToSuccessStory(game);
                activeGame = null;
                Action<string> showDetected = delegate(string completion)
                {
                    bool cannotPopup;
                    string preflight = BuildAchievementPreflight(gameDirectory, emulator, rules, out cannotPopup);
                    string identifier = isRpgMakerObserver ? String.Empty : "\n" + (appId == "0" ? "本地模拟器标识（非 Steam 库）" : "Steam App ID") + "：" + appId;
                    string monitoringDescription = cannotPopup
                        ? "规则已保存，但当前没有可读取的本地成就来源，扩展无法自动弹窗或同步解锁。补全游戏的成就定义后，请重新检查本地状态；仅导入 SuccessStory 成就列表不能解决此问题。\n\n"
                        : isRpgMakerObserver
                            ? "下次通过 Playnite 启动此游戏时，扩展会记录游戏脚本实际发出的成就事件，并弹出通知。"
                            : "下次通过 Playnite 启动此游戏时，扩展会监听模拟器的本地成就文件。";
                    string heading = cannotPopup ? "已识别模拟器，但当前无法自动弹出成就。" : "已为“" + game.Name + "”自动配置规则。";
                    string title = cannotPopup ? "本地成就自动解锁 - 当前无法弹窗" : "本地成就自动解锁";
                    ShowLocalizedMessage(heading + "\n\n游戏：“" + game.Name + "”\n识别结果：" + emulator + identifier +
                        (String.IsNullOrEmpty(completion) ? String.Empty : "\n\n" + completion) + "\n\n" + preflight + "\n\n" + monitoringDescription +
                        "请在 SuccessStory 中手动导入该游戏的 Steam 成就列表，以显示中文名称、图标并记录解锁时间。", title);
                };
                if (String.Equals(emulator, "Goldberg / GSE", StringComparison.Ordinal))
                {
                    AutoCompleteMissingGseSchema(game, gameDirectory, appId, showDetected);
                }
                else if (emulator != null && (emulator.IndexOf("RUNE", StringComparison.OrdinalIgnoreCase) >= 0 || emulator.IndexOf("CODEX", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    AutoCompleteMissingRuneInterface(game, gameDirectory, appId, showDetected);
                }
                else { showDetected(String.Empty); }
            }
            catch (Exception exception)
            {
                StopMonitoring(); activeGame = null;
                ShowLocalizedError("自动配置失败：\n" + exception.Message, "本地成就自动解锁");
            }
        }

        // Automatic detection completes only a *missing* GSE definition. It never
        // rewrites an existing file, and a non-unique DLL/configuration target fails
        // closed. The Steam request runs off the UI thread; the normal result dialog
        // then reports what was (or was not) written.
        private void AutoCompleteMissingGseSchema(Game game, string gameDirectory, string appId, Action<string> completed)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                try
                {
                    if (activeGame != null && activeGame.Id == game.Id) { throw new InvalidOperationException("游戏正在运行，未自动修改成就定义。请退出游戏后重新扫描。"); }
                    result = CreateMissingGseSchema(gameDirectory, appId);
                }
                catch (Exception exception) { result = "⚠ 自动补全 GSE 成就定义未完成：" + exception.Message + "\n请检查目标目录是否已有部分新文件；现有成就定义不会被覆盖。"; }
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate { completed(result); }));
            });
        }

        private static string CreateMissingGseSchema(string gameDirectory, string appId)
        {
            if (String.IsNullOrWhiteSpace(appId) || appId == "0") { throw new InvalidDataException("缺少明确的 Steam App ID，无法安全补全成就定义。"); }
            string target = FindGseSchemaTarget(gameDirectory, appId);
            if (File.Exists(target)) { return String.Empty; }
            string appIdTarget = Path.Combine(Path.GetDirectoryName(target), "steam_appid.txt");
            if (File.Exists(appIdTarget) && !String.Equals(File.ReadAllText(appIdTarget, Encoding.UTF8).Trim(), appId, StringComparison.Ordinal))
            { throw new InvalidDataException("目标目录的 Steam App ID 不一致，未写入文件：" + appIdTarget); }
            List<Dictionary<string, object>> downloaded = FetchSteamAchievementSchema(appId);
            if (downloaded.Count == 0) { throw new InvalidDataException("Steam 未返回此游戏的成就定义，未创建文件。"); }
            int existingCount, addedCount; bool malformed;
            List<object> merged = MergeGseSchema(target, downloaded, out existingCount, out addedCount, out malformed);
            if (File.Exists(target)) { return "ℹ 扫描期间成就定义已由其他程序创建，未覆盖：" + target; }
            string serialized = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(merged);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temporary = target + ".local-achievements-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, serialized, new UTF8Encoding(false));
                File.Move(temporary, target); // fails rather than overwriting a concurrent writer
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
            if (!File.Exists(appIdTarget))
            {
                try
                {
                    using (var stream = new FileStream(appIdTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(appId); }
                }
                catch (IOException)
                {
                    if (!File.Exists(appIdTarget) || !String.Equals(File.ReadAllText(appIdTarget, Encoding.UTF8).Trim(), appId, StringComparison.Ordinal)) { throw; }
                }
            }
            return "✓ 已自动补全缺失的 GSE 成就定义：" + target + "\nSteam App ID：" + appId + "；新增定义：" + addedCount + " 项。\n仅补全定义，不会伪造已解锁状态；游戏仍须写入本地解锁记录。";
        }

        private void AutoCompleteMissingRuneInterface(Game game, string gameDirectory, string appId, Action<string> completed)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                try
                {
                    if (activeGame != null && activeGame.Id == game.Id) { throw new InvalidOperationException("游戏正在运行，未自动修改成就接口。请退出游戏后重新扫描。"); }
                    result = CreateMissingRuneInterface(gameDirectory, appId);
                }
                catch (Exception exception) { result = "⚠ 自动补全 RUNE/CODEX 成就接口未完成：" + exception.Message + "\n不会猜测接口版本，也不会创建虚假的 achievements.ini。"; }
                Application.Current.Dispatcher.BeginInvoke(new Action(delegate { completed(result); }));
            });
        }

        private static string CreateMissingRuneInterface(string gameDirectory, string expectedAppId)
        {
            if (String.IsNullOrWhiteSpace(expectedAppId) || expectedAppId == "0") { throw new InvalidDataException("缺少明确的 Steam App ID，未修改 RUNE/CODEX 配置。"); }
            string ini = FindUniqueConfigurationFile(gameDirectory, "steam_emu.ini");
            if (String.IsNullOrWhiteSpace(ini)) { throw new InvalidDataException("未找到 steam_emu.ini；不能补全 RUNE/CODEX 接口。"); }
            if (!String.IsNullOrWhiteSpace(FindGseLibraryBesideRuneConfig(ini))) { throw new InvalidDataException("相邻 Steam API DLL 实际为 GSE；未修改旧 RUNE/CODEX 配置。"); }
            string content = File.ReadAllText(ini, Encoding.GetEncoding(28591));
            Match id = Regex.Match(content, "(?mi)^\\s*AppId\\s*=\\s*(?<id>\\d+)\\s*$");
            if (!id.Success || !String.Equals(id.Groups["id"].Value, expectedAppId, StringComparison.Ordinal))
            { throw new InvalidDataException("steam_emu.ini 中的 App ID 与规则不一致；未修改文件。"); }
            if (Regex.IsMatch(content, "(?mi)^\\s*SteamUserStats\\s*=\\s*[^;#\\r\\n]+")) { return String.Empty; }
            string version = DiscoverSteamUserStatsVersion(ini);
            if (String.IsNullOrWhiteSpace(version)) { throw new InvalidDataException("无法从相邻原版 Steam API 文件唯一确定 SteamUserStats 版本。"); }
            byte[] updated = InsertSteamUserStats(File.ReadAllBytes(ini), version);
            string backup = ini + ".local-achievements-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            if (File.Exists(backup)) { backup += "." + Guid.NewGuid().ToString("N"); }
            File.Copy(ini, backup, false);
            string temporary = ini + ".local-achievements-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, updated);
                File.Copy(temporary, ini, true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
            return "✓ 已自动补全 RUNE/CODEX 的 SteamUserStats 接口：" + version + "\n配置：" + ini + "\n原文件备份：" + backup + "\n游戏仍须自行写入 achievements.ini 中的 Achieved=1，才能触发弹窗。";
        }

        // Generate only the GSE achievement *definitions*. This never changes an
        // emulator DLL, a game executable, the game's save, or an unlock state.
        // Steam's keyless GetGameAchievements endpoint supplies the same API names
        // needed by generate_emu_config without bundling or logging into its CLI.
        private void CompleteGseSchema(Game game)
        {
            if (activeGame != null && activeGame.Id == game.Id)
            {
                ShowLocalizedMessage("请先退出此游戏，再补全它的 GSE 成就定义。游戏运行时可能已把旧定义加载到内存。", "本地成就自动解锁");
                return;
            }
            string gameDirectory = ResolveGameDirectory(game);
            if (String.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            {
                ShowLocalizedMessage("请先在 Playnite 中设置此游戏的安装目录。", "本地成就自动解锁");
                return;
            }
            AddLocalizedNotification("local-achievements-schema-" + game.Id, "正在读取 Steam 成就定义；完成后会显示写入前预览。", NotificationType.Info);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string appId, emulator;
                    RuleSet detected = DetectEmulatorRules(gameDirectory, game.Name, out appId, out emulator);
                    if (detected == null || !String.Equals(emulator, "Goldberg / GSE", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("此游戏未识别为 Goldberg / GSE。为避免写错游戏文件，成就定义补全只支持已经确认的 GSE 配置。");
                    }
                    string target = FindGseSchemaTarget(gameDirectory, appId);
                    string appIdTarget = Path.Combine(Path.GetDirectoryName(target), "steam_appid.txt");
                    bool createAppId = !File.Exists(appIdTarget);
                    if (!createAppId && !String.Equals(File.ReadAllText(appIdTarget, Encoding.UTF8).Trim(), appId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("目标 steam_settings 中的 App ID 与游戏不一致；未写入任何文件：" + appIdTarget);
                    }
                    List<Dictionary<string, object>> downloaded = FetchSteamAchievementSchema(appId);
                    if (downloaded.Count == 0) { throw new InvalidDataException("Steam 未返回任何成就定义；未修改游戏文件。"); }
                    int progressAchievements = 0;
                    foreach (Dictionary<string, object> item in downloaded)
                    {
                        int progressType;
                        if (Int32.TryParse(GetDictionaryString(item, "progress_type"), out progressType) && progressType != 0) { progressAchievements++; }
                    }
                    int existingCount, addedCount;
                    bool malformed;
                    List<object> merged = MergeGseSchema(target, downloaded, out existingCount, out addedCount, out malformed);
                    string serialized = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(merged);
                    Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            string summary = "游戏：“" + game.Name + "”\nSteam App ID：" + appId + "\n目标：" + target +
                                (createAppId ? "\n同时创建：" + appIdTarget : String.Empty) +
                                "\nSteam 返回：" + downloaded.Count + " 项；现有：" + existingCount + " 项；拟新增：" + addedCount + " 项。" +
                                (malformed ? "\n⚠ 现有定义无效，将在备份后重建。" : String.Empty) +
                                (progressAchievements > 0 ? "\n⚠ Steam 列表包含 " + progressAchievements + " 项进度型成就；只补定义可能不足以让缺失统计上报的游戏解锁这些成就。" : String.Empty) +
                                "\n\n只补全成就定义，不会伪造已解锁状态。游戏仍须通过模拟器实际写入解锁记录，插件才能自动弹窗。\n\n确认写入吗？";
                            if (ShowLocalizedMessage(summary, "本地成就自动解锁 - 写入前确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { return; }
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            string backup = null;
                            if (File.Exists(target))
                            {
                                backup = target + ".local-achievements-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                                if (File.Exists(backup)) { backup += "." + Guid.NewGuid().ToString("N"); }
                                File.Copy(target, backup, false);
                            }
                            string temporary = target + ".local-achievements.tmp";
                            File.WriteAllText(temporary, serialized, new UTF8Encoding(false));
                            File.Copy(temporary, target, true);
                            File.Delete(temporary);
                            if (createAppId) { File.WriteAllText(appIdTarget, appId, new UTF8Encoding(false)); }
                            string ruleWarning = String.Empty;
                            try
                            {
                                RuleSet currentRules = File.Exists(RulesPath(game)) ? ReadRules(RulesPath(game)) : detected;
                                if (currentRules != null)
                                {
                                    UpgradeReplacedRuneRule(currentRules, gameDirectory);
                                    if (!String.IsNullOrWhiteSpace(currentRules.name) && currentRules.name.IndexOf("(GSE " + appId + ")", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        currentRules.settingsDirectory = Path.GetDirectoryName(target);
                                        AddGseRuleSources(currentRules, gameDirectory);
                                        File.WriteAllText(RulesPath(game), serializer.Serialize(currentRules), new UTF8Encoding(false));
                                    }
                                    else { ruleWarning = "\n⚠ 已保留此游戏现有的非 GSE 自定义规则；请检查成就规则来源。"; }
                                }
                            }
                            catch (Exception exception) { ruleWarning = "\n⚠ 成就定义已写入，但规则路径更新失败：" + exception.Message; }
                            ShowLocalizedMessage("已补全成就定义：" + target +
                                (backup == null ? String.Empty : "\n原文件备份：" + backup) +
                                ruleWarning + "\n\n这不会使已经达成但从未写入本地状态的成就自动解锁。请通过 Playnite 启动游戏，之后用“查看成就来源健康状态”确认运行时文件是否出现 earned=true。", "本地成就自动解锁");
                        }
                        catch (Exception exception) { ShowLocalizedError("写入成就定义失败：\n" + exception.Message, "本地成就自动解锁"); }
                    }));
                }
                catch (Exception exception)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(delegate { ShowLocalizedError("补全 GSE 成就定义失败：\n" + exception.Message, "本地成就自动解锁"); }));
                }
            });
        }

        // RUNE/CODEX do not consume GSE's steam_settings/achievements.json. Their
        // steam_emu.ini lists Steam interfaces; the emulator creates achievements.ini
        // only if the game actually reports achievements. Never invent an interface
        // version: read one exact version from the adjacent original Steam library.
        private void CompleteRuneInterface(Game game)
        {
            if (activeGame != null && activeGame.Id == game.Id)
            {
                ShowLocalizedMessage("请先退出游戏，再检查或补全成就接口。", "本地成就自动解锁");
                return;
            }
            string directory = ResolveGameDirectory(game);
            if (String.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                ShowLocalizedMessage("请先在 Playnite 中设置此游戏的安装目录。", "本地成就自动解锁");
                return;
            }
            try
            {
                string ini = FindConfigurationFile(directory, "steam_emu.ini");
                if (String.IsNullOrWhiteSpace(ini)) { throw new InvalidDataException("未找到 steam_emu.ini；不能生成 RUNE/CODEX 配置。"); }
                string content = File.ReadAllText(ini, Encoding.GetEncoding(28591));
                Match id = Regex.Match(content, "(?mi)^\\s*AppId\\s*=\\s*(?<id>\\d+)\\s*$");
                if (!id.Success) { throw new InvalidDataException("steam_emu.ini 没有有效 AppId；未修改文件。"); }
                string appId = id.Groups["id"].Value;
                if (!String.IsNullOrWhiteSpace(FindGseLibraryBesideRuneConfig(ini)))
                {
                    ShowLocalizedMessage("此游戏虽然留有 RUNE/CODEX 的 steam_emu.ini，但相邻的当前 Steam API DLL 是 GSE。请使用“检查并补全 GSE 成就定义”，不要修改已失效的 RUNE 配置。", "本地成就自动解锁");
                    return;
                }
                string provider = GetRuneProvider(ini, content, appId);
                RuleSet rules = CreateRuneRules(game.Name, appId, provider);
                if (Regex.IsMatch(content, "(?mi)^\\s*SteamUserStats\\s*=\\s*[^;#\\r\\n]+"))
                {
                    SaveRuneRules(game, rules);
                    var paths = new List<string>();
                    foreach (RuleSource source in rules.sources) { paths.Add(ExpandPath(source.path)); }
                    ShowLocalizedMessage("已找到 SteamUserStats 配置，无需补写。\n\n模拟器：" + (provider ?? "RUNE/CODEX（未能区分）") + "\n监听路径：\n" + String.Join("\n", paths) + "\n\n只有游戏实际写入 Achieved=1，才会自动弹窗。", "本地成就自动解锁");
                    return;
                }
                string version = DiscoverSteamUserStatsVersion(ini);
                if (String.IsNullOrWhiteSpace(version))
                {
                    SaveRuneRules(game, rules);
                    ShowLocalizedMessage("已校正成就监听路径，但无法从相邻原版 Steam API 文件唯一确定 SteamUserStats 接口版本。\n\n配置：" + ini + "\n模拟器：" + (provider ?? "RUNE/CODEX（未能区分）") + "\n\n为避免猜错版本导致游戏异常，未修改 steam_emu.ini；也没有生成虚假的 achievements.ini。", "本地成就自动解锁");
                    return;
                }
                string preview = "发现缺少 SteamUserStats 接口。\n\n游戏：" + game.Name + "\n模拟器：" + (provider ?? "RUNE/CODEX（未能区分）") + "\nSteam App ID：" + appId + "\n配置：" + ini + "\n拟补入：[Interfaces] 下的 SteamUserStats=" + version + "\n\n会先备份原文件，保留原有编码与其他设置；不会改 DLL、存档或已解锁记录。补全接口不保证游戏会调用成就 API，也不能回放过去未上报的成就。\n\n确认补全吗？";
                if (ShowLocalizedMessage(preview, "本地成就自动解锁 - 写入前确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { return; }
                byte[] original = File.ReadAllBytes(ini);
                byte[] updated = InsertSteamUserStats(original, version);
                string backup = ini + ".local-achievements-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                if (File.Exists(backup)) { backup += "." + Guid.NewGuid().ToString("N"); }
                File.Copy(ini, backup, false);
                string temporary = ini + ".local-achievements.tmp";
                try
                {
                    File.WriteAllBytes(temporary, updated);
                    File.Copy(temporary, ini, true);
                }
                finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
                string ruleWarning = String.Empty;
                try { SaveRuneRules(game, rules); }
                catch (Exception exception) { ruleWarning = "\n⚠ 监听规则更新失败：" + exception.Message; }
                ShowLocalizedMessage("已补全 SteamUserStats 接口。\n备份：" + backup + ruleWarning + "\n\n下次通过 Playnite 启动游戏后，请在达成一个新成就时检查 achievements.ini 是否写入 Achieved=1。没有该记录就仍无法自动弹窗。", "本地成就自动解锁");
            }
            catch (Exception exception) { ShowLocalizedError("RUNE/CODEX 配置检查失败：\n" + exception.Message, "本地成就自动解锁"); }
        }

        private void SaveRuneRules(Game game, RuleSet rules)
        {
            string path = RulesPath(game);
            if (File.Exists(path))
            {
                RuleSet existing = ReadRules(path);
                if (existing == null || String.IsNullOrWhiteSpace(existing.name) ||
                    !Regex.IsMatch(existing.name, "\\((?:RUNE|CODEX|RUNE / CODEX) \\d+\\)", RegexOptions.IgnoreCase) ||
                    existing.sources == null || existing.sources.Count == 0)
                {
                    throw new InvalidOperationException("该游戏已有非 RUNE/CODEX 规则。请先检查或移除旧规则；未覆盖它。");
                }
                Match namedId = Regex.Match(existing.name, "\\((?:RUNE|CODEX|RUNE / CODEX) (?<id>\\d+)\\)", RegexOptions.IgnoreCase);
                Match newId = Regex.Match(rules.name, "\\((?:RUNE|CODEX|RUNE / CODEX) (?<id>\\d+)\\)", RegexOptions.IgnoreCase);
                if (!namedId.Success || !newId.Success || namedId.Groups["id"].Value != newId.Groups["id"].Value) { throw new InvalidOperationException("现有规则的 App ID 不同；未覆盖它。"); }
                foreach (RuleSource source in existing.sources)
                {
                    string expectedSuffix = "\\" + namedId.Groups["id"].Value + "\\achievements.ini";
                    if (source == null || !String.Equals(source.type, "snapshot", StringComparison.Ordinal) || String.IsNullOrWhiteSpace(source.path) ||
                        !source.path.StartsWith("%PUBLIC%\\Documents\\Steam\\", StringComparison.OrdinalIgnoreCase) ||
                        !source.path.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("现有规则含自定义来源；为避免丢失它，未覆盖规则。");
                    }
                }
            }
            ValidateRules(rules);
            File.WriteAllText(path, serializer.Serialize(rules), new UTF8Encoding(false));
        }

        private static string DiscoverSteamUserStatsVersion(string ini)
        {
            string directory = Path.GetDirectoryName(ini);
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string fileName in new[] { "steam_api64.cdx", "steam_api64.rne", "steam_api.cdx", "steam_api.rne", "steam_api64_o.dll", "steam_api_o.dll" })
            {
                string path = Path.Combine(directory, fileName);
                if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) { continue; }
                string bytes = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));
                foreach (Match match in Regex.Matches(bytes, "STEAMUSERSTATS_INTERFACE_VERSION\\d{3}|SteamUserStats\\d{3}")) { versions.Add(match.Value); }
            }
            if (versions.Count == 1) { foreach (string version in versions) { return version; } }
            return null;
        }

        private static byte[] InsertSteamUserStats(byte[] original, string version)
        {
            string content = Encoding.GetEncoding(28591).GetString(original);
            if (Regex.IsMatch(content, "(?mi)^\\s*SteamUserStats\\s*=")) { throw new InvalidOperationException("配置已包含 SteamUserStats；未修改文件。"); }
            Match section = Regex.Match(content, "(?mi)^\\[Interfaces\\][ \\t]*(?:\\r\\n|\\n|\\r)");
            if (!section.Success) { throw new InvalidDataException("未找到完整的 [Interfaces] 段；未修改文件。"); }
            string lineEnding = section.Value.EndsWith("\r\n") ? "\r\n" : (section.Value.EndsWith("\r") ? "\r" : "\n");
            string updated = content.Insert(section.Index + section.Length, "SteamUserStats=" + version + lineEnding);
            return Encoding.GetEncoding(28591).GetBytes(updated);
        }

        private static string FindGseSchemaTarget(string gameDirectory, string appId)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var appIdFiles = new List<string>();
            string rootAppId = Path.Combine(gameDirectory, "steam_appid.txt");
            if (File.Exists(rootAppId)) { appIdFiles.Add(rootAppId); }
            foreach (string path in Directory.GetFiles(gameDirectory, "steam_appid.txt", SearchOption.AllDirectories))
            {
                if (!appIdFiles.Contains(path)) { appIdFiles.Add(path); }
            }
            foreach (string appIdFile in appIdFiles)
            {
                string recorded = File.ReadAllText(appIdFile, Encoding.UTF8).Trim();
                if (!String.Equals(recorded, appId, StringComparison.Ordinal)) { continue; }
                string parent = Path.GetDirectoryName(appIdFile);
                string dllDirectory = String.Equals(Path.GetFileName(parent), "steam_settings", StringComparison.OrdinalIgnoreCase)
                    ? Directory.GetParent(parent).FullName : parent;
                if (!File.Exists(Path.Combine(dllDirectory, "steam_api64.dll")) && !File.Exists(Path.Combine(dllDirectory, "steam_api.dll"))) { continue; }
                candidates.Add(Path.Combine(dllDirectory, "steam_settings", "achievements.json"));
            }
            // A GSE replacement can retain a RUNE/CODEX steam_emu.ini while having
            // no steam_settings or steam_appid.txt yet. Accept it only if the DLL
            // beside that ini identifies itself as GSE and the App ID agrees.
            foreach (string ini in Directory.GetFiles(gameDirectory, "steam_emu.ini", SearchOption.AllDirectories))
            {
                string content = File.ReadAllText(ini, Encoding.GetEncoding(28591));
                Match configuredId = Regex.Match(content, "(?mi)^\\s*AppId\\s*=\\s*(?<id>\\d+)\\s*$");
                if (!configuredId.Success || configuredId.Groups["id"].Value != appId) { continue; }
                string dll = FindGseLibraryBesideRuneConfig(ini);
                if (!String.IsNullOrWhiteSpace(dll)) { candidates.Add(Path.Combine(Path.GetDirectoryName(dll), "steam_settings", "achievements.json")); }
            }
            if (candidates.Count == 1) { foreach (string candidate in candidates) { return candidate; } }
            if (candidates.Count > 1)
            {
                // Unreal releases can ship the same GSE DLL both beside the actual
                // shipping executable and under Engine/ThirdParty. Only prefer the
                // former when precisely one candidate has an EXE next to its DLL.
                // The caller still previews the exact target and asks before writing.
                var besideExecutable = new List<string>();
                foreach (string candidate in candidates)
                {
                    string dllDirectory = Directory.GetParent(Path.GetDirectoryName(candidate)).FullName;
                    if (Directory.GetFiles(dllDirectory, "*.exe", SearchOption.TopDirectoryOnly).Length > 0) { besideExecutable.Add(candidate); }
                }
                if (besideExecutable.Count == 1) { return besideExecutable[0]; }
                throw new InvalidOperationException("找到多个 App ID 相同的 GSE DLL 目录，且无法唯一确定紧邻游戏主程序的目录。未写入任何文件：\n" + String.Join("\n", candidates));
            }
            throw new InvalidOperationException("未找到 App ID 为 " + appId + "、且与 steam_api(64).dll 对应的配置目录。请检查游戏实际加载的 DLL 与 steam_appid.txt；未写入任何文件。");
        }

        private static List<Dictionary<string, object>> FetchSteamAchievementSchema(string appId)
        {
            string[] languages = new string[] { "schinese", "english" };
            foreach (string language in languages)
            {
                string url = "https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/?appid=" + Uri.EscapeDataString(appId) + "&language=" + language;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.UserAgent = "PlayniteLocalAchievements/1.1";
                string content;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) { content = reader.ReadToEnd(); }
                var root = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(content) as Dictionary<string, object>;
                var responseObject = root == null || !root.ContainsKey("response") ? null : root["response"] as Dictionary<string, object>;
                object[] achievements = responseObject == null || !responseObject.ContainsKey("achievements") ? null : responseObject["achievements"] as object[];
                var result = new List<Dictionary<string, object>>();
                if (achievements != null)
                {
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (object value in achievements)
                    {
                        var item = value as Dictionary<string, object>;
                        string id = GetDictionaryString(item, "internal_name");
                        if (String.IsNullOrWhiteSpace(id) || !ids.Add(id)) { continue; }
                        result.Add(item);
                    }
                }
                if (result.Count > 0) { return result; }
            }
            return new List<Dictionary<string, object>>();
        }

        private static string GetDictionaryString(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : String.Empty;
        }

        private static List<object> MergeGseSchema(string target, List<Dictionary<string, object>> downloaded, out int existingCount, out int addedCount, out bool malformed)
        {
            var merged = new List<object>();
            var existingById = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            malformed = false;
            if (File.Exists(target))
            {
                try
                {
                    if (new FileInfo(target).Length > 16 * 1024 * 1024) { throw new InvalidDataException("现有定义文件过大。"); }
                    object[] existing = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(File.ReadAllText(target, Encoding.UTF8)) as object[];
                    if (existing == null) { throw new InvalidDataException("现有定义不是 JSON 数组。"); }
                    foreach (object value in existing)
                    {
                        var item = value as Dictionary<string, object>;
                        string id = GetDictionaryString(item, "name");
                        if (String.IsNullOrWhiteSpace(id) || existingById.ContainsKey(id)) { throw new InvalidDataException("现有定义包含空 ID 或重复 ID。"); }
                        existingById.Add(id, item);
                        merged.Add(item);
                    }
                }
                catch (ArgumentException) { malformed = true; merged.Clear(); existingById.Clear(); }
                catch (InvalidDataException) { malformed = true; merged.Clear(); existingById.Clear(); }
                catch (InvalidOperationException) { malformed = true; merged.Clear(); existingById.Clear(); }
            }
            existingCount = existingById.Count;
            addedCount = 0;
            foreach (Dictionary<string, object> steam in downloaded)
            {
                string id = GetDictionaryString(steam, "internal_name");
                Dictionary<string, object> current;
                if (existingById.TryGetValue(id, out current))
                {
                    if (String.IsNullOrWhiteSpace(GetDictionaryString(current, "displayName"))) { current["displayName"] = GetDictionaryString(steam, "localized_name"); }
                    if (String.IsNullOrWhiteSpace(GetDictionaryString(current, "description"))) { current["description"] = GetDictionaryString(steam, "localized_desc"); }
                    continue;
                }
                var added = new Dictionary<string, object>();
                added["name"] = id;
                added["displayName"] = GetDictionaryString(steam, "localized_name");
                added["description"] = GetDictionaryString(steam, "localized_desc");
                object hidden;
                added["hidden"] = steam.TryGetValue("hidden", out hidden) && hidden is bool && (bool)hidden ? "1" : "0";
                // Missing images do not affect unlock reporting. SuccessStory owns the
                // Playnite toast artwork, and we do not overwrite curated GSE icons.
                added["icon"] = "";
                added["icongray"] = "";
                merged.Add(added);
                addedCount++;
            }
            return merged;
        }

        // Many manually added games have a valid executable on the Actions tab but an
        // empty InstallDirectory.  Prefer that trusted local path before interrupting
        // the user with a folder picker, then retain it for later Playnite actions.
        private string ResolveGameDirectory(Game game)
        {
            if (!String.IsNullOrWhiteSpace(game.InstallDirectory) && Directory.Exists(game.InstallDirectory))
            {
                return game.InstallDirectory;
            }

            // A game may move to another local drive while Playnite retains the
            // former drive letter. Probe only the same relative path on other drives.
            string remappedDirectory = RemapMovedInstallDirectory(game.InstallDirectory);
            if (!String.IsNullOrWhiteSpace(remappedDirectory))
            {
                game.InstallDirectory = remappedDirectory;
                PlayniteApi.Database.Games.Update(game);
                return remappedDirectory;
            }

            if (game.GameActions != null)
            {
                foreach (GameAction action in game.GameActions)
                {
                    if (action == null || !action.IsPlayAction) { continue; }
                    string candidate = ResolveActionDirectory(action);
                    if (!String.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                    {
                        game.InstallDirectory = candidate;
                        PlayniteApi.Database.Games.Update(game);
                        return candidate;
                    }
                }
                foreach (GameAction action in game.GameActions)
                {
                    if (action == null) { continue; }
                    string candidate = ResolveActionDirectory(action);
                    if (!String.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                    {
                        game.InstallDirectory = candidate;
                        PlayniteApi.Database.Games.Update(game);
                        return candidate;
                    }
                }
            }

            // Optional, user-configured roots support old entries with only a
            // launcher script and no usable File action.
            string discovered = FindExecutableDirectoryByGameName(game.Name);
            if (!String.IsNullOrWhiteSpace(discovered))
            {
                game.InstallDirectory = discovered;
                PlayniteApi.Database.Games.Update(game);
                return discovered;
            }
            return null;
        }

        private static string RemapMovedInstallDirectory(string staleDirectory)
        {
            if (String.IsNullOrWhiteSpace(staleDirectory)) { return null; }
            string originalRoot;
            try { originalRoot = Path.GetPathRoot(Path.GetFullPath(staleDirectory)); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            if (String.IsNullOrWhiteSpace(originalRoot)) { return null; }
            string relative = staleDirectory.Substring(originalRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative.Length == 0) { return null; }
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) ||
                        String.Equals(drive.RootDirectory.FullName, originalRoot, StringComparison.OrdinalIgnoreCase)) { continue; }
                    string candidate = Path.Combine(drive.RootDirectory.FullName, relative);
                    if (Directory.Exists(candidate)) { return candidate; }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        private static string ResolveActionDirectory(GameAction action)
        {
            if (!String.IsNullOrWhiteSpace(action.WorkingDir) && Directory.Exists(action.WorkingDir))
            {
                return action.WorkingDir;
            }
            if (action.Type == GameActionType.File && !String.IsNullOrWhiteSpace(action.Path))
            {
                string executable = ExtractExecutablePath(Environment.ExpandEnvironmentVariables(action.Path));
                if (File.Exists(executable)) { return Path.GetDirectoryName(executable); }
            }
            return null;
        }

        // Some manually-created Playnite actions contain a fully quoted executable
        // plus command-line arguments. File.Exists cannot resolve that raw command.
        private static string ExtractExecutablePath(string actionPath)
        {
            if (String.IsNullOrWhiteSpace(actionPath)) { return actionPath; }
            string candidate = actionPath.Trim();
            if (candidate.StartsWith("\"", StringComparison.Ordinal))
            {
                int closingQuote = candidate.IndexOf('"', 1);
                if (closingQuote > 1) { return candidate.Substring(1, closingQuote - 1); }
            }
            int executableEnd = candidate.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0)
            {
                return candidate.Substring(0, executableEnd + 4).Trim().Trim('"');
            }
            return candidate.Trim('"');
        }

        private static string FindExecutableDirectoryByGameName(string gameName)
        {
            List<string> nameKeys = GetExecutableSearchKeys(gameName);
            if (nameKeys.Count == 0) { return null; }
            string configuredRoots = Environment.GetEnvironmentVariable("PLAYNITE_LOCAL_ACHIEVEMENTS_GAME_ROOTS");
            if (String.IsNullOrWhiteSpace(configuredRoots)) { return null; }
            foreach (string configuredRoot in configuredRoots.Split(Path.PathSeparator))
            {
                string root = Environment.ExpandEnvironmentVariables(configuredRoot.Trim().Trim('"'));
                if (!Directory.Exists(root)) { continue; }
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories))
                    {
                        string executableKey = NormalizeGameName(Path.GetFileNameWithoutExtension(file));
                        foreach (string nameKey in nameKeys)
                        {
                            if (executableKey == nameKey || executableKey.StartsWith(nameKey) || nameKey.StartsWith(executableKey))
                            {
                                return Path.GetDirectoryName(file);
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return null;
        }

        // A few manually added games have a localized library name while every local
        // filename is its official English title.  These aliases keep automatic setup
        // non-interactive; the install path itself is still discovered rather than
        // stored as a machine-specific hard-coded path.
        private static List<string> GetExecutableSearchKeys(string gameName)
        {
            var keys = new List<string>();
            string primaryKey = NormalizeGameName(gameName);
            if (primaryKey.Length >= 4) { keys.Add(primaryKey); }
            // Library metadata uses all of these simplified, traditional and Japanese
            // spellings for SHUTEN ORDER. Treat the shared title prefix as the alias.
            if (gameName != null && (gameName.IndexOf("终天", StringComparison.OrdinalIgnoreCase) >= 0 || gameName.IndexOf("終天", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                keys.Add("shutenorder");
                keys.Add("shuten");
            }
            return keys;
        }

        private static string NormalizeGameName(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) { return String.Empty; }
            var normalized = new StringBuilder();
            foreach (char character in value.ToLowerInvariant())
            {
                if (Char.IsLetterOrDigit(character)) { normalized.Append(character); }
            }
            return normalized.ToString();
        }

        private RuleSet DetectEmulatorRules(string gameDirectory, string gameName, out string appId, out string emulator)
        {
            appId = null; emulator = null;
            string[] reloadedFiles = Directory.GetFiles(gameDirectory, "steam_api.ini", SearchOption.AllDirectories);
            if (reloadedFiles.Length > 0)
            {
                string content = File.ReadAllText(reloadedFiles[0], Encoding.UTF8);
                Match appIdMatch = Regex.Match(content, "(?mi)^\\s*AppId\\s*=\\s*(?<id>\\d+)\\s*$");
                Match userMatch = Regex.Match(content, "(?mi)^\\s*UserName\\s*=\\s*(?<name>.+?)\\s*$");
                if (!appIdMatch.Success) { throw new InvalidDataException("RELOADED steam_api.ini does not contain a valid AppId."); }
                appId = appIdMatch.Groups["id"].Value;
                emulator = "RELOADED";
                string userName = userMatch.Success ? userMatch.Groups["name"].Value.Trim() : "RLD!";
                return CreateReloadedRules(gameName, appId, userName);
            }
            string runeIni = FindConfigurationFile(gameDirectory, "steam_emu.ini");
            if (!String.IsNullOrWhiteSpace(runeIni))
            {
                string iniContent = File.ReadAllText(runeIni, Encoding.GetEncoding(28591));
                Match match = Regex.Match(iniContent, "(?mi)^\\s*AppId\\s*=\\s*(?<id>\\d+)\\s*$");
                if (!match.Success) { throw new InvalidDataException("RUNE steam_emu.ini does not contain a valid AppId."); }
                appId = match.Groups["id"].Value;
                string gseDll = FindGseLibraryBesideRuneConfig(runeIni);
                if (!String.IsNullOrWhiteSpace(gseDll))
                {
                    // Some repacks replace the RUNE API DLL with GSE but leave the
                    // old steam_emu.ini behind. The loaded DLL is stronger evidence
                    // than that stale configuration file.
                    emulator = "Goldberg / GSE";
                    RuleSet replaced = CreateGseRules(gameName, appId);
                    replaced.settingsDirectory = Path.Combine(Path.GetDirectoryName(gseDll), "steam_settings");
                    AddGseRuleSources(replaced, gameDirectory);
                    return replaced;
                }
                string provider = GetRuneProvider(runeIni, iniContent, appId);
                emulator = provider ?? "RUNE / CODEX";
                return CreateRuneRules(gameName, appId, provider);
            }

            // Goldberg/GSE settings are often next to the game executable, but Unity
            // games can put them under e.g. <game>_Data\Plugins\x86_64\steam_settings.
            // Locate the settings folder anywhere below the configured game directory.
            string settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
            string settingsAppId = Path.Combine(settingsDirectory, "steam_appid.txt");
            string rootAppId = Path.Combine(gameDirectory, "steam_appid.txt");
            string appIdFile = File.Exists(settingsAppId) ? settingsAppId : (File.Exists(rootAppId) ? rootAppId : null);
            if (appIdFile == null)
            {
                string[] nestedAppIdFiles = Directory.GetFiles(gameDirectory, "steam_appid.txt", SearchOption.AllDirectories);
                for (int index = 0; index < nestedAppIdFiles.Length; index++)
                {
                    DirectoryInfo parentDirectory = Directory.GetParent(nestedAppIdFiles[index]);
                    if (parentDirectory != null && String.Equals(parentDirectory.Name, "steam_settings", StringComparison.OrdinalIgnoreCase))
                    {
                        settingsDirectory = parentDirectory.FullName;
                        appIdFile = nestedAppIdFiles[index];
                        break;
                    }
                }
            }
            if (appIdFile != null && (Directory.Exists(settingsDirectory) || File.Exists(Path.Combine(gameDirectory, "steam_api64.dll"))))
            {
                Match match = Regex.Match(File.ReadAllText(appIdFile, Encoding.UTF8), "(?<id>\\d+)");
                if (!match.Success) { throw new InvalidDataException("steam_appid.txt does not contain a valid App ID."); }
                appId = match.Groups["id"].Value;
                emulator = "Goldberg / GSE";
                RuleSet gse = CreateGseRules(gameName, appId);
                gse.settingsDirectory = settingsDirectory;
                AddGseRuleSources(gse, gameDirectory);
                return gse;
            }

            string[] tenokeFiles = Directory.GetFiles(gameDirectory, "tenoke.ini", SearchOption.AllDirectories);
            if (tenokeFiles.Length > 0)
            {
                Match match = Regex.Match(File.ReadAllText(tenokeFiles[0], Encoding.UTF8), "(?mi)^\\s*id\\s*=\\s*(?<id>\\d+)\\b");
                if (!match.Success) { throw new InvalidDataException("TENOKE tenoke.ini does not contain a valid app id."); }
                appId = match.Groups["id"].Value;
                emulator = "TENOKE";
                return CreateTenokeRules(gameName, appId, gameDirectory);
            }

            RuleSet legacyGoldbergRules = TryCreateLegacyGoldbergRules(gameDirectory, gameName, out appId, out emulator);
            if (legacyGoldbergRules != null) { return legacyGoldbergRules; }
            RuleSet unboundGoldbergRules = TryCreateRecentlyUsedUnboundGoldbergRules(gameDirectory, gameName, out appId, out emulator);
            if (unboundGoldbergRules != null) { return unboundGoldbergRules; }
            string rpgMakerScript = FindRpgMakerObserverScript(gameDirectory);
            if (!String.IsNullOrEmpty(rpgMakerScript))
            {
                appId = String.Empty;
                emulator = "RPG Maker MV / Greenworks";
                return CreateRpgMakerObserverRules(gameName, rpgMakerScript);
            }
            return null;
        }

        // Unreal Engine games commonly keep the Steam API beside the engine's
        // Steamworks runtime instead of the installation root.  Prefer the root
        // when present, then use the first readable nested configuration.
        private static string FindConfigurationFile(string gameDirectory, string fileName)
        {
            string rootFile = Path.Combine(gameDirectory, fileName);
            if (File.Exists(rootFile)) { return rootFile; }
            try
            {
                string[] nestedFiles = Directory.GetFiles(gameDirectory, fileName, SearchOption.AllDirectories);
                foreach (string file in nestedFiles)
                {
                    if (File.Exists(file)) { return file; }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return null;
        }

        // Auto-editing must not silently choose the first of several emulator
        // configurations. Read-only detection may still use the legacy helper.
        private static string FindUniqueConfigurationFile(string gameDirectory, string fileName)
        {
            string[] matches;
            try { matches = Directory.GetFiles(gameDirectory, fileName, SearchOption.AllDirectories); }
            catch (UnauthorizedAccessException) { throw new InvalidOperationException("无法完整扫描安装目录，未修改任何配置。"); }
            catch (IOException) { throw new InvalidOperationException("无法完整扫描安装目录，未修改任何配置。"); }
            if (matches.Length > 1) { throw new InvalidOperationException("发现多个 steam_emu.ini，无法确定游戏实际使用的配置，未修改任何文件。"); }
            return matches.Length == 1 ? matches[0] : null;
        }

        // Archeia's RPG Maker MV bridge emits exact Steam achievement IDs but does
        // not persist them when Steam is unavailable. Observe the game's own call
        // without changing its Steam API behavior or inferring flags from saves.
        private static string FindRpgMakerObserverScript(string gameDirectory)
        {
            string[] scripts;
            try { scripts = Directory.GetFiles(gameDirectory, "JsScript15Set.js", SearchOption.AllDirectories); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            foreach (string script in scripts)
            {
                if (new FileInfo(script).Length > 1024 * 1024) { continue; }
                string content = File.ReadAllText(script, Encoding.UTF8);
                if (content.Contains(RpgMakerHookAnchor) && content.Contains("steamworks.activateAchievement") &&
                    script.IndexOf("\\www\\js\\plugins\\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return script;
                }
            }
            return null;
        }

        private static RuleSet CreateRpgMakerObserverRules(string gameName, string scriptPath)
        {
            string saveDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(scriptPath), "..", "..", "save"));
            return new RuleSet
            {
                schemaVersion = 1,
                name = gameName + " (RPG Maker MV / Greenworks)",
                sources = new List<RuleSource> { new RuleSource
                {
                    type = "log", path = Path.Combine(saveDirectory, RpgMakerObserverFile), encoding = "utf-8",
                    match = RpgMakerAchievementMatch, achievementGroup = "achievementId"
                } }
            };
        }

        private static string InsertRpgMakerObserver(string content)
        {
            int anchor = content.IndexOf(RpgMakerHookAnchor, StringComparison.Ordinal);
            if (anchor < 0) { throw new InvalidDataException("RPG Maker 成就脚本结构已改变，无法安全添加观察器。"); }
            int insertion = anchor + RpgMakerHookAnchor.Length;
            string newline = content.Contains("\r\n") ? "\r\n" : "\n";
            int originalLineBreak = content.Substring(insertion).StartsWith("\r\n", StringComparison.Ordinal) ? 2 :
                (content.Substring(insertion).StartsWith("\n", StringComparison.Ordinal) ? 1 : 0);
            if (originalLineBreak == 0) { throw new InvalidDataException("RPG Maker 成就脚本的函数格式不符合预期。"); }
            const string observer = "      /* LocalAchievements observer v1 */\n" +
                "      try {\n" +
                "          if (StorageManager.isLocalMode() && typeof achievement === 'string' && /^[A-Za-z0-9_]+$/.test(achievement)) {\n" +
                "              var localAchievementFs = require('fs');\n" +
                "              var localAchievementPath = require('path');\n" +
                "              var localAchievementDirectory = StorageManager.localFileDirectoryPath();\n" +
                "              if (!localAchievementFs.existsSync(localAchievementDirectory)) localAchievementFs.mkdirSync(localAchievementDirectory);\n" +
                "              localAchievementFs.appendFileSync(localAchievementPath.join(localAchievementDirectory, 'local-achievements-events.log'), JSON.stringify({id:achievement,time:new Date().toISOString()}) + '\\n', 'utf8');\n" +
                "          }\n" +
                "      } catch (localAchievementError) { /* Achievement delivery continues unchanged. */ }\n";
            return content.Substring(0, insertion) + newline + observer.Replace("\n", newline) + content.Substring(insertion + originalLineBreak);
        }

        private static void InstallRpgMakerObserver(string scriptPath)
        {
            string current = File.ReadAllText(scriptPath, Encoding.UTF8);
            if (current.Contains(RpgMakerObserverMarker)) { return; }
            string backup = scriptPath + ".local-achievements.bak";
            if (File.Exists(backup)) { throw new IOException("已存在成就脚本备份，未覆盖：" + backup); }
            string patched = InsertRpgMakerObserver(current);
            File.Copy(scriptPath, backup, false);
            try
            {
                byte[] originalBytes = File.ReadAllBytes(scriptPath);
                bool hasBom = originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF;
                File.WriteAllText(scriptPath, patched, new UTF8Encoding(hasBom));
            }
            catch
            {
                File.Copy(backup, scriptPath, true);
                throw;
            }
        }

        // Keep user-authored rules untouched. Only migrate the old built-in TENOKE
        // snapshot format that cannot read current releases' quoted TOML-like entries.
        private static bool UpgradeKnownRuleFormat(RuleSet rules)
        {
            if (rules == null || rules.sources == null || String.IsNullOrWhiteSpace(rules.name) || rules.name.IndexOf("TENOKE", StringComparison.OrdinalIgnoreCase) < 0) { return false; }
            bool changed = false;
            foreach (RuleSource source in rules.sources)
            {
                if (source == null || String.IsNullOrWhiteSpace(source.path) || !source.path.EndsWith("SteamData\\user_stats.ini", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (String.Equals(source.match, TenokeAchievementMatch, StringComparison.Ordinal)) { continue; }
                if (!String.IsNullOrWhiteSpace(source.match) && source.match.IndexOf("\\[ACHIEVEMENTS\\]", StringComparison.OrdinalIgnoreCase) >= 0 && source.match.IndexOf("unlocked\\s*=", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    source.match = TenokeAchievementMatch;
                    changed = true;
                }
            }
            return changed;
        }

        // Older Goldberg releases may replace steam_api64.dll but omit the usual
        // steam_settings folder entirely. They save unlock state in the legacy AppData folder.
        private static RuleSet TryCreateLegacyGoldbergRules(string gameDirectory, string gameName, out string appId, out string emulator)
        {
            appId = null;
            emulator = null;
            string steamApi = FindUnitySteamApiLibrary(gameDirectory);
            if (String.IsNullOrWhiteSpace(steamApi) || !FileContainsAsciiText(steamApi, "Goldberg SteamEmu")) { return null; }

            string identity = (gameName ?? String.Empty) + " " + gameDirectory;
            if (identity.IndexOf("CyberManhunt", StringComparison.OrdinalIgnoreCase) >= 0 || identity.IndexOf("全网公敌", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                appId = "1216710";
                emulator = "Goldberg SteamEmu（旧版）";
                return CreateLegacyGoldbergRules(gameName, appId);
            }
            return null;
        }

        // A few older Goldberg releases do not include an App ID or an identifying
        // string in steam_api64.dll. They instead use the generic "0" save folder.
        // Bind that folder only when its remote save has been written very recently
        // and the selected game has a Steam API library, preventing stale folders
        // from being assigned to unrelated games.
        private static RuleSet TryCreateRecentlyUsedUnboundGoldbergRules(string gameDirectory, string gameName, out string appId, out string emulator)
        {
            appId = null;
            emulator = null;
            if (String.IsNullOrWhiteSpace(FindUnitySteamApiLibrary(gameDirectory))) { return null; }
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Goldberg SteamEmu Saves", "0");
            string remote = Path.Combine(root, "remote");
            if (!Directory.Exists(remote)) { return null; }
            DateTime newestWrite = DateTime.MinValue;
            try
            {
                foreach (string file in Directory.GetFiles(remote, "*", SearchOption.AllDirectories))
                {
                    DateTime write = File.GetLastWriteTime(file);
                    if (write > newestWrite) { newestWrite = write; }
                }
            }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
            if (newestWrite < DateTime.Now.AddMinutes(-45)) { return null; }
            appId = "0";
            emulator = "Goldberg SteamEmu（未绑定本地存档）";
            return CreateLegacyGoldbergRules(gameName, appId);
        }

        private static string FindUnitySteamApiLibrary(string gameDirectory)
        {
            string direct = Path.Combine(gameDirectory, "steam_api64.dll");
            if (File.Exists(direct)) { return direct; }
            try
            {
                foreach (string dataDirectory in Directory.GetDirectories(gameDirectory, "*_Data", SearchOption.TopDirectoryOnly))
                {
                    string candidate = Path.Combine(dataDirectory, "Plugins", "steam_api64.dll");
                    if (File.Exists(candidate)) { return candidate; }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return null;
        }

        private static bool FileContainsAsciiText(string path, string text)
        {
            try
            {
                return Encoding.ASCII.GetString(File.ReadAllBytes(path)).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static RuleSet CreateGseRules(string gameName, string appId)
        {
            return new RuleSet
            {
                schemaVersion = 1,
                name = gameName + " (GSE " + appId + ")",
                sources = new List<RuleSource>
                {
                    new RuleSource
                    {
                        type = "snapshot",
                        path = "%APPDATA%\\GSE Saves\\" + appId + "\\achievements.json",
                        encoding = "utf-8",
                        match = "\\\"(?<achievementId>[^\\\"\\r\\n]+)\\\"\\s*:\\s*\\{[^}]*\\\"earned\\\"\\s*:\\s*true",
                        achievementGroup = "achievementId"
                    }
                }
            };
        }

        private static bool AddGseRuleSources(RuleSet rules, string gameDirectory)
        {
            if (rules == null || rules.sources == null || String.IsNullOrWhiteSpace(rules.name)) { return false; }
            Match match = Regex.Match(rules.name, "\\(GSE (?<id>\\d+)\\)", RegexOptions.IgnoreCase);
            if (!match.Success) { return false; }
            string appId = match.Groups["id"].Value;
            bool changed = false;
            changed |= AddGseRuleSource(rules, "%APPDATA%\\GSE Saves\\" + appId + "\\achievements.json");
            changed |= AddGseRuleSource(rules, "%APPDATA%\\Goldberg SteamEmu Saves\\" + appId + "\\achievements.json");
            string settings = rules.settingsDirectory;
            if ((String.IsNullOrWhiteSpace(settings) || !Directory.Exists(settings)) && !String.IsNullOrWhiteSpace(gameDirectory))
            {
                string direct = Path.Combine(gameDirectory, "steam_settings");
                if (Directory.Exists(direct)) { settings = direct; }
            }
            if (String.IsNullOrWhiteSpace(settings) || !Directory.Exists(settings)) { return changed; }
            string dllDirectory = Directory.GetParent(settings).FullName;
            string userConfig = Path.Combine(settings, "configs.user.ini");
            if (File.Exists(userConfig))
            {
                try
                {
                    string ini = File.ReadAllText(userConfig, Encoding.UTF8);
                    Match local = Regex.Match(ini, "(?mi)^\\s*local_save_path\\s*=\\s*(?<path>[^;#\\r\\n]+)");
                    if (local.Success)
                    {
                        string value = local.Groups["path"].Value.Trim().Trim('"', '\'');
                        if (!String.IsNullOrWhiteSpace(value))
                        {
                            string root = Environment.ExpandEnvironmentVariables(value);
                            if (!Path.IsPathRooted(root)) { root = Path.Combine(dllDirectory, root); }
                            root = Path.GetFullPath(root);
                            changed |= AddGseRuleSource(rules, Path.Combine(root, "achievements.json"));
                            changed |= AddGseRuleSource(rules, Path.Combine(root, appId, "achievements.json"));
                        }
                    }
                    Match folderName = Regex.Match(ini, "(?mi)^\\s*saves_folder_name\\s*=\\s*(?<name>[^;#\\r\\n]+)");
                    if (folderName.Success)
                    {
                        string value = folderName.Groups["name"].Value.Trim().Trim('"', '\'');
                        if (!String.IsNullOrWhiteSpace(value) && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && value.IndexOfAny(new char[] { '\\', '/' }) < 0)
                        {
                            changed |= AddGseRuleSource(rules, "%APPDATA%\\" + value + "\\" + appId + "\\achievements.json");
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (System.Security.SecurityException) { }
            }
            string classicLocal = Path.Combine(dllDirectory, "local_save.txt");
            if (File.Exists(classicLocal))
            {
                try
                {
                    string value = File.ReadAllText(classicLocal, Encoding.UTF8).Trim().Trim('"', '\'');
                    if (!String.IsNullOrWhiteSpace(value))
                    {
                        string root = Environment.ExpandEnvironmentVariables(value);
                        if (!Path.IsPathRooted(root)) { root = Path.Combine(dllDirectory, root); }
                        root = Path.GetFullPath(root);
                        changed |= AddGseRuleSource(rules, Path.Combine(root, "achievements.json"));
                        changed |= AddGseRuleSource(rules, Path.Combine(root, appId, "achievements.json"));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (System.Security.SecurityException) { }
            }
            return changed;
        }

        private static bool AddGseRuleSource(RuleSet rules, string path)
        {
            foreach (RuleSource existing in rules.sources)
            {
                if (String.Equals(existing.path, path, StringComparison.OrdinalIgnoreCase)) { return false; }
            }
            rules.sources.Add(new RuleSource
            {
                type = "snapshot", path = path, encoding = "utf-8",
                match = "\\\"(?<achievementId>[^\\\"\\r\\n]+)\\\"\\s*:\\s*\\{[^}]*\\\"earned\\\"\\s*:\\s*true",
                achievementGroup = "achievementId"
            });
            return true;
        }

        // The rule engine intentionally never creates or changes emulator files. This
        // preflight makes a common packaging problem visible before the player spends
        // time waiting for a popup: GSE/Goldberg needs an achievement definition file
        // in steam_settings before it can report achievement IDs to the local state file.
        private static string BuildAchievementPreflight(string gameDirectory, string emulator, RuleSet rules)
        {
            bool cannotPopup;
            return BuildAchievementPreflight(gameDirectory, emulator, rules, out cannotPopup);
        }

        private static string BuildAchievementPreflight(string gameDirectory, string emulator, RuleSet rules, out bool cannotPopup)
        {
            cannotPopup = false;
            var messages = new List<string>();
            // Auto-detection passes the emulator label, while saved rules pass a
            // name such as "Rivage (GSE 4094660)" during later health checks.
            bool isGse = !String.IsNullOrWhiteSpace(emulator) &&
                (emulator.IndexOf("Goldberg / GSE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 emulator.IndexOf("(GSE ", StringComparison.OrdinalIgnoreCase) >= 0);
            bool isRune = !String.IsNullOrWhiteSpace(emulator) && (emulator.IndexOf("RUNE", StringComparison.OrdinalIgnoreCase) >= 0 || emulator.IndexOf("CODEX", StringComparison.OrdinalIgnoreCase) >= 0);
            bool anyStateFile = false;
            if (rules != null && rules.sources != null)
            {
                foreach (RuleSource source in rules.sources)
                {
                    string statePath = Environment.ExpandEnvironmentVariables((source.path ?? String.Empty).Replace('/', '\\'));
                    if (File.Exists(statePath))
                    {
                        anyStateFile = true;
                        break;
                    }
                }
            }
            if (String.Equals(emulator, "RPG Maker MV / Greenworks", StringComparison.Ordinal) ||
                (!String.IsNullOrEmpty(emulator) && emulator.IndexOf("RPG Maker MV / Greenworks", StringComparison.Ordinal) >= 0))
            {
                string script = FindRpgMakerObserverScript(gameDirectory);
                bool installed = !String.IsNullOrEmpty(script) && File.ReadAllText(script, Encoding.UTF8).Contains(RpgMakerObserverMarker);
                messages.Add(installed
                    ? "✓ 已备份原有成就脚本，并添加只记录游戏真实成就事件的观察器。不会修改 Steam 接口调用。"
                    : "⚠ 尚未安装 RPG Maker 成就事件观察器；请重新执行自动配置。");
                messages.Add("ℹ 事件在游戏触发时写入本地日志。已有存档不会被推测为已解锁；游戏未调用的成就也不会自动解锁。");
            }
            if (isGse)
            {
                bool hasDefinition = false;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(gameDirectory, "achievements.json", SearchOption.AllDirectories))
                    {
                        if (file.IndexOf("\\steam_settings\\", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hasDefinition = true;
                            break;
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                if (!hasDefinition)
                {
                    if (!anyStateFile)
                    {
                        cannotPopup = true;
                        messages.Add("⛔ 当前无法自动弹窗：未发现 steam_settings\\achievements.json，也未发现规则监听的本地成就状态文件。只导入 Steam 成就列表不会产生弹窗。可使用“检查并补全 GSE 成就定义”；扩展会预览目标，并在确认后备份或创建定义，但仍需游戏实际写入解锁记录。");
                    }
                    else
                    {
                        messages.Add("⚠ 未发现 steam_settings\\achievements.json，但已有可读取的本地成就状态文件。扩展仍可监听现有记录；未来新成就是否会写入，需要在游戏中验证。可使用“检查并补全 GSE 成就定义”预览并补全缺失定义。");
                    }
                }
                else
                {
                    messages.Add(anyStateFile
                        ? "ℹ 已发现 GSE 成就定义和本地状态文件；仍须确认状态文件在游戏解锁时写入 earned=true，才能证明自动弹窗链路有效。"
                        : "⚠ 已发现 GSE 成就定义，但尚未发现本地解锁状态。这只说明成就列表可用，不证明游戏会调用成就接口或模拟器会写入解锁记录。首次实际解锁后请检查来源健康状态。");
                }
            }

            if (isRune)
            {
                string runeIni = FindConfigurationFile(gameDirectory, "steam_emu.ini");
                bool hasUserStats = false;
                try
                {
                    hasUserStats = File.Exists(runeIni) && Regex.IsMatch(File.ReadAllText(runeIni, Encoding.UTF8), "(?mi)^\\s*SteamUserStats\\s*=\\s*.+$");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                if (!hasUserStats)
                {
                    string version = String.IsNullOrWhiteSpace(runeIni) ? null : DiscoverSteamUserStatsVersion(runeIni);
                    messages.Add("⚠ RUNE/CODEX 配置未声明 SteamUserStats 接口；成就上报存在风险，但不能仅凭这一项断定一定失败。" +
                        (String.IsNullOrWhiteSpace(version) ? "相邻原版 Steam API 未提供唯一接口版本，扩展不会猜测。" : "检测到可补全的接口版本 " + version + "；可使用右键菜单“检查并补全 RUNE/CODEX 成就接口”。"));
                }
                else
                {
                    messages.Add("✓ RUNE/CODEX 配置声明了 SteamUserStats 接口；仍需实际解锁记录验证。");
                }

                string runeState = null;
                if (rules != null && rules.sources != null)
                {
                    foreach (RuleSource source in rules.sources)
                    {
                        string candidate = Environment.ExpandEnvironmentVariables((source.path ?? String.Empty).Replace('/', '\\'));
                        if (candidate.EndsWith("achievements.ini", StringComparison.OrdinalIgnoreCase))
                        {
                            runeState = candidate;
                            break;
                        }
                    }
                }
                if (!String.IsNullOrWhiteSpace(runeState) && File.Exists(runeState))
                {
                    string stateText = String.Empty;
                    try { stateText = File.ReadAllText(runeState, Encoding.UTF8); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    int declared = 0;
                    Match count = Regex.Match(stateText, "(?mi)^\\s*Count\\s*=\\s*(?<count>\\d+)\\s*$");
                    if (count.Success) { Int32.TryParse(count.Groups["count"].Value, out declared); }
                    bool hasUnlocked = Regex.IsMatch(stateText, "(?mi)^\\s*Achieved\\s*=\\s*1\\s*$");
                    if (declared == 0 && !hasUnlocked)
                    {
                        messages.Add("⚠ RUNE 已创建成就状态文件，但其中只有 Count=0，尚未上报任何成就。若游戏已经通过明确的剧情成就条件后仍保持此状态，说明该发布的成就回调不可用，扩展无法自动解锁。");
                    }
                    else if (hasUnlocked)
                    {
                        messages.Add("✓ RUNE 本地状态文件已经包含已上报的成就，自动监听可用。");
                    }
                    else
                    {
                        messages.Add("ℹ RUNE 本地状态文件已初始化，但尚未发现已解锁的成就。");
                    }
                }

                string runeDll = Path.Combine(gameDirectory, "steam_api64.dll");
                if (File.Exists(runeDll))
                {
                    try
                    {
                        DateTime newestExe = DateTime.MinValue;
                        foreach (string executable in Directory.GetFiles(gameDirectory, "*.exe", SearchOption.TopDirectoryOnly))
                        {
                            DateTime modified = File.GetLastWriteTime(executable);
                            if (modified > newestExe) { newestExe = modified; }
                        }
                        DateTime dllModified = File.GetLastWriteTime(runeDll);
                        if (newestExe != DateTime.MinValue && newestExe > dllModified.AddDays(45))
                        {
                            messages.Add("⚠ 风险提示：本地 Steam 接口 DLL 的时间明显早于游戏主程序。该发布可能混用了不同版本，成就接口虽能初始化却可能无法接收回调；这不是确定结论，但建议在出现 Count=0 时优先判定为发布兼容性问题。");
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            if (!anyStateFile && !cannotPopup)
            {
                messages.Add("ℹ 尚未生成本地解锁状态文件；对刚开始游玩的游戏这通常正常，但若已完成明确成就条件后仍不存在，则说明该发布没有可供本扩展读取的成就上报。 ");
            }
            return messages.Count == 0 ? "✓ 预检完成：已找到可监听的本地成就状态。" : String.Join("\n\n", messages);
        }

        private static RuleSet CreateLegacyGoldbergRules(string gameName, string appId)
        {
            return new RuleSet
            {
                schemaVersion = 1,
                name = gameName + " (Goldberg " + appId + ")",
                sources = new List<RuleSource>
                {
                    new RuleSource
                    {
                        type = "snapshot",
                        path = "%APPDATA%\\Goldberg SteamEmu Saves\\" + appId + "\\achievements.json",
                        encoding = "utf-8",
                        match = "\\\"(?<achievementId>[^\\\"\\r\\n]+)\\\"\\s*:\\s*\\{[^}]*\\\"earned\\\"\\s*:\\s*true",
                        achievementGroup = "achievementId"
                    }
                }
            };
        }

        private static bool IsGseLibrary(string path)
        {
            if (!File.Exists(path)) { return false; }
            try
            {
                var file = new FileInfo(path);
                if (file.Length > 64 * 1024 * 1024) { return false; }
                string content = Encoding.ASCII.GetString(File.ReadAllBytes(path));
                return content.IndexOf("GSE Saves", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       content.IndexOf("steam_settings", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static string FindGseLibraryBesideRuneConfig(string ini)
        {
            if (String.IsNullOrWhiteSpace(ini)) { return null; }
            string directory = Path.GetDirectoryName(ini);
            foreach (string name in new[] { "steam_api64.dll", "steam_api.dll" })
            {
                string candidate = Path.Combine(directory, name);
                if (IsGseLibrary(candidate)) { return candidate; }
            }
            return null;
        }

        // Migrate only a built-in RUNE/CODEX snapshot rule when the adjacent API
        // library has actually been replaced by GSE. Custom rules are untouched.
        private static bool UpgradeReplacedRuneRule(RuleSet rules, string gameDirectory)
        {
            if (rules == null || rules.sources == null || String.IsNullOrWhiteSpace(rules.name) ||
                String.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) { return false; }
            Match namedId = Regex.Match(rules.name, "\\((?:RUNE|CODEX|RUNE / CODEX) (?<id>\\d+)\\)", RegexOptions.IgnoreCase);
            if (!namedId.Success) { return false; }
            string appId = namedId.Groups["id"].Value;
            foreach (RuleSource source in rules.sources)
            {
                string suffix = "\\" + appId + "\\achievements.ini";
                if (source == null || source.type != "snapshot" || String.IsNullOrWhiteSpace(source.path) ||
                    !source.path.StartsWith("%PUBLIC%\\Documents\\Steam\\", StringComparison.OrdinalIgnoreCase) ||
                    !source.path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { return false; }
            }
            string ini = FindConfigurationFile(gameDirectory, "steam_emu.ini");
            if (String.IsNullOrWhiteSpace(ini)) { return false; }
            string content = File.ReadAllText(ini, Encoding.GetEncoding(28591));
            Match configuredId = Regex.Match(content, "(?mi)^\\s*AppId\\s*=\\s*(?<id>\\d+)\\s*$");
            if (!configuredId.Success || configuredId.Groups["id"].Value != appId) { return false; }
            string dll = FindGseLibraryBesideRuneConfig(ini);
            if (String.IsNullOrWhiteSpace(dll)) { return false; }
            string gameName = rules.name.Substring(0, namedId.Index).TrimEnd();
            RuleSet gse = CreateGseRules(gameName, appId);
            rules.name = gse.name;
            rules.settingsDirectory = Path.Combine(Path.GetDirectoryName(dll), "steam_settings");
            rules.sources = gse.sources;
            AddGseRuleSources(rules, gameDirectory);
            return true;
        }

        private static string GetRuneProvider(string ini, string content, string appId)
        {
            Match location = Regex.Match(content ?? String.Empty, "(?i)Steam[\\\\/](?<provider>CODEX|RUNE)[\\\\/]" + Regex.Escape(appId) + "(?:\\D|$)");
            if (location.Success) { return location.Groups["provider"].Value.ToUpperInvariant(); }
            string directory = Path.GetDirectoryName(ini);
            if (File.Exists(Path.Combine(directory, "steam_api64.cdx")) || File.Exists(Path.Combine(directory, "steam_api.cdx"))) { return "CODEX"; }
            if (File.Exists(Path.Combine(directory, "steam_api64.rne")) || File.Exists(Path.Combine(directory, "steam_api.rne"))) { return "RUNE"; }
            return null;
        }

        // Migrate only the exact old built-in path, never a user-authored source.
        private static bool UpgradeRuneRuleSources(RuleSet rules, string gameDirectory)
        {
            if (rules == null || rules.sources == null || String.IsNullOrWhiteSpace(rules.name) || String.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) { return false; }
            Match namedId = Regex.Match(rules.name, "\\((?:RUNE|CODEX|RUNE / CODEX) (?<id>\\d+)\\)", RegexOptions.IgnoreCase);
            if (!namedId.Success) { return false; }
            string ini = FindConfigurationFile(gameDirectory, "steam_emu.ini");
            if (String.IsNullOrWhiteSpace(ini)) { return false; }
            string content = File.ReadAllText(ini, Encoding.GetEncoding(28591));
            Match configuredId = Regex.Match(content, "(?mi)^\\s*AppId\\s*=\\s*(?<id>\\d+)\\s*$");
            if (!configuredId.Success || configuredId.Groups["id"].Value != namedId.Groups["id"].Value) { return false; }
            string provider = GetRuneProvider(ini, content, namedId.Groups["id"].Value);
            if (String.IsNullOrWhiteSpace(provider)) { return false; }
            string oldRune = "%PUBLIC%\\Documents\\Steam\\RUNE\\" + namedId.Groups["id"].Value + "\\achievements.ini";
            string oldCodex = "%PUBLIC%\\Documents\\Steam\\CODEX\\" + namedId.Groups["id"].Value + "\\achievements.ini";
            string replacement = provider == "CODEX" ? oldCodex : oldRune;
            bool changed = false;
            foreach (RuleSource source in rules.sources)
            {
                if (source == null || (!String.Equals(source.path, oldRune, StringComparison.OrdinalIgnoreCase) && !String.Equals(source.path, oldCodex, StringComparison.OrdinalIgnoreCase))) { continue; }
                if (!String.Equals(source.path, replacement, StringComparison.OrdinalIgnoreCase)) { source.path = replacement; changed = true; }
            }
            string oldName = rules.name;
            rules.name = Regex.Replace(rules.name, "\\((?:RUNE|CODEX|RUNE / CODEX) " + Regex.Escape(namedId.Groups["id"].Value) + "\\)", "(" + provider + " " + namedId.Groups["id"].Value + ")", RegexOptions.IgnoreCase);
            return changed || !String.Equals(oldName, rules.name, StringComparison.Ordinal);
        }

        private static RuleSet CreateRuneRules(string gameName, string appId, string provider)
        {
            var sources = new List<RuleSource>();
            string[] providers = String.IsNullOrWhiteSpace(provider) ? new[] { "RUNE", "CODEX" } : new[] { provider };
            foreach (string name in providers)
            {
                sources.Add(new RuleSource
                {
                    type = "snapshot",
                    path = "%PUBLIC%\\Documents\\Steam\\" + name + "\\" + appId + "\\achievements.ini",
                    encoding = "utf-8",
                    match = "(?ms)^\\[(?<achievementId>[^\\]\\r\\n]+)\\]\\r?\\n(?:(?!^\\[).)*?^Achieved\\s*=\\s*1\\s*$",
                    achievementGroup = "achievementId"
                });
            }
            return new RuleSet
            {
                schemaVersion = 1,
                name = gameName + " (" + (provider ?? "RUNE / CODEX") + " " + appId + ")",
                sources = sources
            };
        }

        private static RuleSet CreateTenokeRules(string gameName, string appId, string gameDirectory)
        {
            // TENOKE stores the unlock flags next to the game, rather than in an AppData
            // folder.  Some releases create the file only after the first achievement.
            string statsPath = Path.Combine(gameDirectory, "SteamData", "user_stats.ini");
            return new RuleSet
            {
                schemaVersion = 1,
                name = gameName + " (TENOKE " + appId + ")",
                sources = new List<RuleSource>
                {
                    new RuleSource
                    {
                        type = "snapshot",
                        path = statsPath,
                        encoding = "utf-8",
                        match = TenokeAchievementMatch,
                        achievementGroup = "achievementId"
                    }
                }
            };
        }

        private static RuleSet CreateReloadedRules(string gameName, string appId, string userName)
        {
            return new RuleSet
            {
                schemaVersion = 1,
                name = gameName + " (RELOADED " + appId + ")",
                sources = new List<RuleSource>
                {
                    new RuleSource
                    {
                        type = "snapshot",
                        path = "%ProgramData%\\Steam\\" + userName + "\\" + appId + "\\stats\\achievements.ini",
                        encoding = "utf-8",
                        match = "(?ms)^\\[(?<achievementId>[^\\]\\r\\n]+)\\]\\r?\\n(?:(?!^\\[).)*?^(?:Achieved|Unlocked)\\s*=\\s*(?:1|true)\\s*$",
                        achievementGroup = "achievementId"
                    }
                }
            };
        }

        private void RemoveRules(Game game)
        {
            if (ShowLocalizedMessage("要移除“" + game.Name + "”的自动成就规则吗？现有的 SuccessStory 记录和本地成就历史不会被删除。", "本地成就自动解锁", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    RuleSet rules = ReadRules(RulesPath(game));
                    if (rules != null && !String.IsNullOrEmpty(rules.name) && rules.name.IndexOf("RPG Maker MV / Greenworks", StringComparison.Ordinal) >= 0)
                    {
                        string script = FindRpgMakerObserverScript(ResolveGameDirectory(game));
                        if (!String.IsNullOrEmpty(script))
                        {
                            string backup = script + ".local-achievements.bak";
                            if (File.Exists(backup))
                            {
                                string original = File.ReadAllText(backup, Encoding.UTF8);
                                string current = File.ReadAllText(script, Encoding.UTF8);
                                if (String.Equals(current.Replace("\r\n", "\n"), InsertRpgMakerObserver(original).Replace("\r\n", "\n"), StringComparison.Ordinal))
                                {
                                    File.Copy(backup, script, true);
                                }
                                else if (current.Contains(RpgMakerObserverMarker))
                                {
                                    ShowLocalizedMessage("规则已移除，但游戏脚本在安装观察器后又发生变化，因此未自动覆盖它。原始备份仍在：\n" + backup, "本地成就自动解锁");
                                }
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    ShowLocalizedMessage("恢复游戏原始成就脚本时遇到问题，请保留备份并检查：\n" + exception.Message, "本地成就自动解锁");
                }
                File.Delete(RulesPath(game));
                if (activeGame != null && activeGame.Id == game.Id) { StopMonitoring(); }
            }
        }

        private RuleSet ReadRules(string path)
        {
            RuleSet rules = serializer.Deserialize<RuleSet>(File.ReadAllText(path, Encoding.UTF8));
            return rules;
        }

        private void ValidateRules(RuleSet rules)
        {
            if (rules == null || rules.schemaVersion != 1 || rules.sources == null || rules.sources.Count == 0)
            {
                throw new InvalidDataException("Expected schemaVersion 1 and at least one source.");
            }
            foreach (RuleSource source in rules.sources)
            {
                if (source == null || (source.type != "log" && source.type != "snapshot") || String.IsNullOrWhiteSpace(source.path) || String.IsNullOrWhiteSpace(source.match))
                {
                    throw new InvalidDataException("Each source needs type (log or snapshot), path, and match.");
                }
                new Regex(source.match);
            }
        }

        private void StartMonitoring(Game game, bool processExistingLog, bool notifySnapshots, bool scanSnapshotsImmediately)
        {
            RuleSet rules;
            try { rules = ReadRules(RulesPath(game)); ValidateRules(rules); }
            catch (Exception exception) { AddLocalizedNotification("local-achievements-rules-" + game.Id, "无法加载成就规则：" + exception.Message, NotificationType.Error); return; }
            bool upgraded = UpgradeReplacedRuneRule(rules, game.InstallDirectory);
            upgraded |= UpgradeRuneRuleSources(rules, game.InstallDirectory);
            if (upgraded) { File.WriteAllText(RulesPath(game), serializer.Serialize(rules), new UTF8Encoding(false)); }
            AddGseRuleSources(rules, game.InstallDirectory);
            foreach (RuleSource definition in rules.sources)
            {
                string path = ExpandPath(definition.path);
                var source = new ActiveSource { Definition = definition, Path = path, Regex = new Regex(definition.match, RegexOptions.Multiline), Position = 0, Pending = String.Empty };
                bool isObserverLog = definition.type == "log" && String.Equals(Path.GetFileName(path), RpgMakerObserverFile, StringComparison.OrdinalIgnoreCase);
                if (definition.type == "log" && File.Exists(path) && !processExistingLog && !isObserverLog) { source.Position = new FileInfo(path).Length; }
                activeSources.Add(source);
                StartWatcher(source);
                if (isObserverLog && File.Exists(path)) { ProcessSource(source); }
            }
            if (scanSnapshotsImmediately) { SyncSnapshots(notifySnapshots); }
        }

        private void SyncSmallSnapshots(bool notify)
        {
            const long maxImmediateSnapshotBytes = 256 * 1024;
            foreach (ActiveSource source in activeSources)
            {
                if (source.Definition.type != "snapshot" || !File.Exists(source.Path)) { continue; }
                try
                {
                    if (new FileInfo(source.Path).Length > maxImmediateSnapshotBytes) { continue; }
                    ProcessMatches(source, ReadText(source.Path, source.Definition.encoding), notify);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void RecoverLocalAchievements(Game game)
        {
            if (activeGame != null && activeGame.Id != game.Id)
            {
                ShowLocalizedMessage("另一个游戏正在由本扩展监听。请先退出该游戏，再执行补扫。", "本地成就自动解锁");
                return;
            }
            try
            {
                bool temporaryMonitor = activeGame == null;
                if (temporaryMonitor)
                {
                    activeGame = game;
                    LoadProgress(game);
                    StartMonitoring(game, false, true, true);
                }
                else
                {
                    LoadProgress(game);
                    SyncSnapshots(true);
                }
                SyncAllToSuccessStory(game);
                if (temporaryMonitor) { StopMonitoring(); }
                ShowLocalizedMessage("已读取游戏当前的本地成就状态，并完成补扫与 SuccessStory 同步。只有状态文件中已标记为解锁的成就会被记录。", "本地成就自动解锁");
            }
            catch (Exception exception)
            {
                StopMonitoring();
                ShowLocalizedError("补扫失败：\n" + exception.Message, "本地成就自动解锁");
            }
        }

        private static string ExpandPath(string path)
        {
            return Environment.ExpandEnvironmentVariables(path.Replace("/", "\\"));
        }

        private void StartWatcher(ActiveSource source)
        {
            string directory = Path.GetDirectoryName(source.Path);
            if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory)) { return; }
            source.Watcher = new FileSystemWatcher(directory, Path.GetFileName(source.Path));
            source.Watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
            source.Watcher.Changed += delegate(object _, FileSystemEventArgs __) { ProcessSource(source); };
            source.Watcher.Created += delegate(object _, FileSystemEventArgs __) { source.Position = 0; ProcessSource(source); };
            source.Watcher.EnableRaisingEvents = true;
        }

        // A snapshot's parent directory often does not exist until the first unlock.
        // FileSystemWatcher cannot subscribe to that directory at game launch, so a
        // lightweight metadata poll is needed in addition to the normal watcher.
        // Unity logs also need polling because their write notifications may coalesce.
        private void StartLogPolling()
        {
            if (logPollTimer != null) { logPollTimer.Stop(); }
            logPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            logPollTimer.Tick += delegate
            {
                if (activeGame == null) { return; }
                foreach (ActiveSource source in activeSources)
                {
                    if (source.Definition.type == "log") { ProcessSource(source); continue; }
                    if (source.Definition.type != "snapshot") { continue; }
                    try
                    {
                        var file = new FileInfo(source.Path);
                        if (!file.Exists)
                        {
                            source.LastPolledWriteUtc = DateTime.MinValue;
                            source.LastPolledLength = -1;
                            continue;
                        }
                        // Never read a large save on Playnite's UI thread each second.
                        if (file.Length > 1024 * 1024) { continue; }
                        DateTime writeTime = file.LastWriteTimeUtc;
                        long length = file.Length;
                        if (writeTime == source.LastPolledWriteUtc && length == source.LastPolledLength) { continue; }
                        source.LastPolledWriteUtc = writeTime;
                        source.LastPolledLength = length;
                        ProcessSource(source);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            };
            logPollTimer.Start();
        }

        private void ProcessSource(ActiveSource source)
        {
            try
            {
                // FileSystemWatcher raises on a worker thread while the one-second
                // log poll runs on Playnite's UI thread. Serialise access to the
                // per-source cursor and pending tail: a Unity log rollover could
                // otherwise change Pending between LastIndexOf and Substring.
                lock (source.SyncRoot)
                {
                    if (!File.Exists(source.Path)) { return; }
                    if (source.Definition.type == "snapshot")
                    {
                        string content = ReadText(source.Path, source.Definition.encoding);
                        Application.Current.Dispatcher.BeginInvoke(new Action(delegate { ProcessMatches(source, content, true); }));
                        return;
                    }
                    string appended;
                    using (var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (stream.Length < source.Position) { source.Position = 0; source.Pending = String.Empty; }
                        stream.Seek(source.Position, SeekOrigin.Begin);
                        using (var reader = new StreamReader(stream, GetEncoding(source.Definition.encoding), true)) { appended = reader.ReadToEnd(); source.Position = stream.Position; }
                    }
                    if (String.IsNullOrEmpty(appended)) { return; }
                    source.Pending += appended;
                    if (source.Pending.Length > 32768) { source.Pending = source.Pending.Substring(source.Pending.Length - 32768); }
                    string matches = source.Pending;
                    int lastLine = source.Pending.LastIndexOf('\n');
                    source.Pending = lastLine >= 0 ? source.Pending.Substring(lastLine + 1) : source.Pending;
                    if (Application.Current.Dispatcher.CheckAccess()) { ProcessMatches(source, matches, true); }
                    else { Application.Current.Dispatcher.BeginInvoke(new Action(delegate { ProcessMatches(source, matches, true); })); }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void SyncSnapshots(bool notify)
        {
            foreach (ActiveSource source in activeSources)
            {
                if (source.Definition.type != "snapshot" || !File.Exists(source.Path)) { continue; }
                try { ProcessMatches(source, ReadText(source.Path, source.Definition.encoding), notify); }
                catch (IOException) { }
            }
        }

        private void SyncAllToSuccessStory(Game game)
        {
            foreach (KeyValuePair<string, DateTime> pair in unlocked)
            {
                SyncAchievementToSuccessStory(game, pair.Key, pair.Value);
            }
        }

        // SuccessStory's in-memory database API can report a result even when a
        // particular release does not persist the change. Always update the existing
        // on-disk entry as well; the file method only fills a missing DateUnlocked.
        private SuccessStoryAchievement SyncAchievementToSuccessStory(Game game, string achievementId, DateTime unlockedAt)
        {
            SuccessStoryAchievement runtimeResult = UpdateSuccessStory(game, achievementId, unlockedAt);
            SuccessStoryAchievement fileResult = UpdateSuccessStoryFile(game, achievementId, unlockedAt);
            // The live database Update above raises SuccessStory's ItemUpdated event.
            // Retain the old manual-refresh fallback only when its runtime cache was
            // unavailable, so a successful unlock does not start an extra refresh.
            if (runtimeResult == null && fileResult != null) { RefreshSuccessStoryManual(game); }
            return runtimeResult ?? fileResult;
        }

        private void ProcessMatches(ActiveSource source, string text, bool notify)
        {
            if (activeGame == null) { return; }
            foreach (Match match in source.Regex.Matches(text))
            {
                string groupName = String.IsNullOrWhiteSpace(source.Definition.achievementGroup) ? "achievementId" : source.Definition.achievementGroup;
                Group group = match.Groups[groupName];
                string achievementId = group == null ? null : group.Value;
                if (String.IsNullOrWhiteSpace(achievementId)) { continue; }
                DateTime? observedAt = null;
                Group timestamp = match.Groups["unlockedAt"];
                DateTimeOffset parsedTimestamp;
                if (timestamp != null && timestamp.Success && DateTimeOffset.TryParse(timestamp.Value, out parsedTimestamp))
                {
                    observedAt = parsedTimestamp.LocalDateTime;
                }
                Unlock(activeGame, achievementId, notify, observedAt);
            }
        }

        private void Unlock(Game game, string achievementId, bool notify, DateTime? observedAt = null)
        {
            if (unlocked.ContainsKey(achievementId))
            {
                // A previous version could persist local progress while SuccessStory
                // synchronization failed. Reconcile that state on later scans and show
                // the missed toast exactly once when SuccessStory was still locked.
                SuccessStoryAchievement reconciled = SyncAchievementToSuccessStory(game, achievementId, unlocked[achievementId]);
                if (notify && !WasNotified(game, achievementId))
                {
                    string title = reconciled == null ? achievementId : reconciled.Title;
                    string description = reconciled == null ? "Achievement unlocked by a local game rule." : reconciled.Description;
                    ShowToast(title, description, unlocked.Count, true, reconciled == null ? null : reconciled.IconUrl, game.Name);
                    MarkNotified(game, achievementId);
                }
                return;
            }
            DateTime now = observedAt ?? DateTime.Now;
            unlocked[achievementId] = now;
            SaveProgress(game);
            SuccessStoryAchievement successStoryAchievement = SyncAchievementToSuccessStory(game, achievementId, now);
            if (notify)
            {
                string title = successStoryAchievement == null ? achievementId : successStoryAchievement.Title;
                string description = successStoryAchievement == null ? "Achievement unlocked by a local game rule." : successStoryAchievement.Description;
                ShowToast(title, description, unlocked.Count, true, successStoryAchievement == null ? null : successStoryAchievement.IconUrl, game.Name);
                MarkNotified(game, achievementId);
            }
        }

        private void LoadProgress(Game game)
        {
            unlocked.Clear();
            string path = ProgressPath(game);
            if (!File.Exists(path)) { return; }
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                string[] parts = line.Split('\t'); DateTime time;
                if (parts.Length == 2 && DateTime.TryParse(parts[1], out time)) { unlocked[parts[0]] = time; }
            }
        }

        private void SaveProgress(Game game)
        {
            var lines = new List<string>();
            foreach (KeyValuePair<string, DateTime> item in unlocked) { lines.Add(item.Key + "\t" + item.Value.ToString("o")); }
            File.WriteAllLines(ProgressPath(game), lines.ToArray(), Encoding.UTF8);
        }
        private bool WasNotified(Game game, string achievementId)
        {
            string path = NotificationPath(game);
            return File.Exists(path) && Array.Exists(File.ReadAllLines(path, Encoding.UTF8), item => String.Equals(item, achievementId, StringComparison.OrdinalIgnoreCase));
        }
        private void MarkNotified(Game game, string achievementId)
        {
            string path = NotificationPath(game);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path)) { foreach (string line in File.ReadAllLines(path, Encoding.UTF8)) { if (!String.IsNullOrWhiteSpace(line)) { ids.Add(line); } } }
            if (ids.Add(achievementId)) { File.WriteAllLines(path, new List<string>(ids).ToArray(), Encoding.UTF8); }
        }

        // Uses only SuccessStory's already-created manual data. It never creates a list,
        // imports Steam data, alters an achievement definition, or writes Steam data.
        private SuccessStoryAchievement UpdateSuccessStory(Game game, string achievementId, DateTime unlockedAt)
        {
            try
            {
                Plugin successStory = null;
                foreach (Plugin plugin in PlayniteApi.Addons.Plugins)
                {
                    // SuccessStory's current package no longer has the historical
                    // extension Guid or main-class name. Its loaded assembly is the
                    // stable identifier, so prefer that and retain old variants.
                    string typeName = plugin.GetType().FullName ?? String.Empty;
                    string assemblyName = plugin.GetType().Assembly.GetName().Name ?? String.Empty;
                    if (typeName == "SuccessStory.SuccessStory" ||
                        typeName.StartsWith("SuccessStory.", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(assemblyName, "SuccessStory", StringComparison.OrdinalIgnoreCase) ||
                        plugin.Id == new Guid("cebe6d32-8c46-4459-b993-5a5189d60788"))
                    {
                        successStory = plugin;
                        break;
                    }
                }
                if (successStory == null) { return null; }
                object database = GetSuccessStoryDatabase(successStory);
                if (database == null) { return null; }
                var get = database.GetType().GetMethod("GetOnlyCache", new Type[] { typeof(Guid) });
                object gameAchievements = get == null ? null : get.Invoke(database, new object[] { game.Id });
                // SuccessStory 3.7.x exposes its supported cache retrieval entry point
                // as Get(Guid, bool, bool). Keep the GetOnlyCache attempt for older builds.
                if (gameAchievements == null)
                {
                    var getCached = FindGetMethod(database.GetType());
                    if (getCached != null) { gameAchievements = getCached.Invoke(database, BuildGetArguments(getCached, game.Id)); }
                }
                if (gameAchievements == null) { return null; }
                var itemsProperty = gameAchievements.GetType().GetProperty("Items");
                IEnumerable items = itemsProperty == null ? null : itemsProperty.GetValue(gameAchievements, null) as IEnumerable;
                if (items == null) { return null; }
                foreach (object item in items)
                {
                    string apiName = GetProperty(item, "ApiName") as string;
                    if (!String.Equals(apiName, achievementId, StringComparison.OrdinalIgnoreCase)) { continue; }
                    object oldDate = GetProperty(item, "DateUnlocked");
                    // SuccessStory releases use both nullable DateTime and a non-null
                    // DateTime.MinValue default for locked manual achievements. The
                    // latter must not be treated as an already-unlocked achievement,
                    // otherwise only the JSON fallback changes and the live UI cache
                    // remains at its old count until Playnite restarts.
                    bool wasAlreadyUnlocked = HasRealUnlockTime(oldDate);
                    if (!wasAlreadyUnlocked)
                    {
                        SetProperty(item, "DateUnlocked", unlockedAt);
                        // Changing the cached Achievement alone does not raise the
                        // database ItemUpdated event used by SuccessStory's home
                        // controls. Commit the containing GameAchievements through
                        // its own Update method so the 0/N counter refreshes now,
                        // not only after Playnite reloads the JSON on next startup.
                        var update = FindSingleParameterMethod(database.GetType(), "Update", gameAchievements.GetType());
                        if (update != null) { update.Invoke(database, new object[] { gameAchievements }); }
                        RefreshSuccessStoryTheme(database, game);
                    }
                    return new SuccessStoryAchievement { Title = (GetProperty(item, "Name") as string) ?? achievementId, Description = (GetProperty(item, "Description") as string) ?? String.Empty, IconUrl = (GetProperty(item, "UrlUnlocked") as string) ?? String.Empty, WasAlreadyUnlocked = wasAlreadyUnlocked };
                }
            }
            catch (Exception exception)
            {
                AddLocalizedNotification("local-achievements-successstory-" + game.Id, "本地成就已解锁，但同步 SuccessStory 失败：" + exception.Message, NotificationType.Error);
            }
            return null;
        }

        // Harmony and other themes bind to SuccessStory.Settings.Unlocked rather
        // than to GameAchievements.Unlocked. The database's ItemUpdated event does
        // not recalculate those exposed settings, so refresh them for the game the
        // user is actually viewing after a new local unlock.
        private void RefreshSuccessStoryTheme(object database, Game game)
        {
            Game context = GetProperty(database, "GameContext") as Game;
            bool showingThisGame = context != null && context.Id == game.Id;
            if (!showingThisGame && PlayniteApi.MainView != null && PlayniteApi.MainView.SelectedGames != null)
            {
                foreach (Game selected in PlayniteApi.MainView.SelectedGames)
                {
                    if (selected != null && selected.Id == game.Id) { showingThisGame = true; break; }
                }
            }
            if (!showingThisGame) { return; }
            var setThemesResources = database.GetType().GetMethod("SetThemesResources", new Type[] { typeof(Game) });
            if (setThemesResources != null) { setThemesResources.Invoke(database, new object[] { game }); }
        }

        private void RefreshSuccessStoryThemeForGame(Game game)
        {
            try
            {
                if (game == null) { return; }
                foreach (Plugin plugin in PlayniteApi.Addons.Plugins)
                {
                    string typeName = plugin.GetType().FullName ?? String.Empty;
                    string assemblyName = plugin.GetType().Assembly.GetName().Name ?? String.Empty;
                    if (typeName != "SuccessStory.SuccessStory" &&
                        !typeName.StartsWith("SuccessStory.", StringComparison.OrdinalIgnoreCase) &&
                        !String.Equals(assemblyName, "SuccessStory", StringComparison.OrdinalIgnoreCase)) { continue; }
                    object database = GetSuccessStoryDatabase(plugin);
                    if (database != null) { RefreshSuccessStoryTheme(database, game); }
                    return;
                }
            }
            catch (Exception exception)
            {
                AddLocalizedNotification("local-achievements-successstory-theme-" + game.Id,
                    "成就已记录，但 SuccessStory 主题数字刷新失败：" + exception.Message, NotificationType.Error);
            }
        }

        private static bool HasRealUnlockTime(object value)
        {
            if (value == null) { return false; }
            if (value is DateTime) { return ((DateTime)value) > DateTime.MinValue; }
            if (value is DateTimeOffset) { return ((DateTimeOffset)value).UtcDateTime > DateTime.MinValue; }
            return true;
        }

        // Compatibility fallback for SuccessStory versions whose private database API is
        // not callable by a separate extension. Only an already-existing item with the
        // same ApiName is updated, and an existing unlock time is never replaced.
        private SuccessStoryAchievement UpdateSuccessStoryFile(Game game, string achievementId, DateTime unlockedAt)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite", "ExtensionsData", "cebe6d32-8c46-4459-b993-5a5189d60788", "SuccessStory", game.Id.ToString() + ".json");
                if (!File.Exists(path)) { return null; }
                string text = File.ReadAllText(path, Encoding.UTF8);
                string escapedId = Regex.Escape(achievementId);
                // Steam-emulator state files can normalize an ID's letter case
                // (for example ach_vova), while Steam/SuccessStory may retain
                // its original case (ach_Vova). ApiName is case-insensitive.
                var itemRegex = new Regex("\\{(?:(?!\\{).)*?\\\"ApiName\\\":\\\"(?<storedApiName>" + escapedId + ")\\\"(?:(?!\\{).)*?\\}", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                Match itemMatch = itemRegex.Match(text);
                if (!itemMatch.Success) { return null; }
                string itemText = itemMatch.Value;
                string storedApiName = itemMatch.Groups["storedApiName"].Value;
                string title = ReadJsonString(itemText, "Name") ?? achievementId;
                string description = ReadJsonString(itemText, "Description") ?? String.Empty;
                Match dateMatch = Regex.Match(itemText, "\\\"DateUnlocked\\\"\\s*:\\s*(null|\\\"[^\\\"]*\\\")");
                bool wasUnlocked = dateMatch.Success && !String.Equals(dateMatch.Groups[1].Value, "null", StringComparison.OrdinalIgnoreCase);
                if (!wasUnlocked)
                {
                    string replacement = "\"DateUnlocked\":\"" + unlockedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\"";
                    // Older/manual SuccessStory entries omit DateUnlocked entirely
                    // while locked. Insert it beside ApiName when there is no field.
                    string updatedItem = dateMatch.Success
                        ? itemText.Substring(0, dateMatch.Index) + replacement + itemText.Substring(dateMatch.Index + dateMatch.Length)
                        : itemText.Replace("\"ApiName\":\"" + storedApiName + "\"", "\"ApiName\":\"" + storedApiName + "\"," + replacement);
                    string backup = path + ".local-achievements.bak";
                    if (!File.Exists(backup)) { File.Copy(path, backup); }
                    File.WriteAllText(path, text.Substring(0, itemMatch.Index) + updatedItem + text.Substring(itemMatch.Index + itemMatch.Length), new UTF8Encoding(false));
                }
                return new SuccessStoryAchievement { Title = title, Description = description, IconUrl = ReadJsonString(itemText, "UrlUnlocked") ?? String.Empty, WasAlreadyUnlocked = wasUnlocked };
            }
            catch (Exception exception)
            {
                AddLocalizedNotification("local-achievements-successstory-file-" + game.Id, "本地成就已解锁，但写入 SuccessStory 记录失败：" + exception.Message, NotificationType.Error);
                return null;
            }
        }
        private void RefreshSuccessStoryManual(Game game)
        {
            try
            {
                Plugin successStory = null;
                foreach (Plugin plugin in PlayniteApi.Addons.Plugins)
                {
                    string typeName = plugin.GetType().FullName ?? String.Empty;
                    string assemblyName = plugin.GetType().Assembly.GetName().Name ?? String.Empty;
                    if (typeName == "SuccessStory.SuccessStory" || typeName.StartsWith("SuccessStory.", StringComparison.OrdinalIgnoreCase) || String.Equals(assemblyName, "SuccessStory", StringComparison.OrdinalIgnoreCase))
                    {
                        successStory = plugin;
                        break;
                    }
                }
                if (successStory == null) { return; }
                object database = GetSuccessStoryDatabase(successStory);
                if (database == null) { return; }
                var refresh = database.GetType().GetMethod("RefreshManual", new Type[] { typeof(Game) });
                if (refresh == null) { return; }
                object refreshed = refresh.Invoke(database, new object[] { game });
                if (refreshed != null)
                {
                    var afterRefresh = FindSingleParameterMethod(database.GetType(), "ActionAfterRefresh", refreshed.GetType());
                    if (afterRefresh != null) { afterRefresh.Invoke(database, new object[] { refreshed }); }
                }
            }
            catch (Exception exception)
            {
                AddLocalizedNotification("local-achievements-successstory-refresh-" + game.Id, "本地成就已写入 SuccessStory，但界面刷新失败：" + exception.Message, NotificationType.Error);
            }
        }
        private static string ReadJsonString(string itemText, string property)
        {
            Match match = Regex.Match(itemText, "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"");
            return match.Success ? Regex.Unescape(match.Groups["value"].Value) : null;
        }
        private SuccessStoryAchievement GetPreviewAchievement(Game game)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite", "ExtensionsData", "cebe6d32-8c46-4459-b993-5a5189d60788", "SuccessStory", game.Id.ToString() + ".json");
                if (!File.Exists(path)) { return null; }
                var root = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                object[] items = root == null || !root.ContainsKey("Items") ? null : root["Items"] as object[];
                if (items == null || items.Length == 0) { return null; }
                var item = items[0] as Dictionary<string, object>;
                if (item == null) { return null; }
                return new SuccessStoryAchievement
                {
                    Title = item.ContainsKey("Name") ? item["Name"] as string : null,
                    Description = item.ContainsKey("Description") ? item["Description"] as string : null,
                    IconUrl = item.ContainsKey("UrlUnlocked") ? item["UrlUnlocked"] as string : null
                };
            }
            catch { return null; }
        }

        private static object GetProperty(object target, string name)
        {
            var property = target.GetType().GetProperty(name); return property == null ? null : property.GetValue(target, null);
        }
        private static void SetProperty(object target, string name, object value)
        {
            var property = target.GetType().GetProperty(name); if (property != null && property.CanWrite) { property.SetValue(target, value, null); }
        }
        private static object GetSuccessStoryDatabase(Plugin successStory)
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            Type type = successStory.GetType();
            // The installed SuccessStory 3.7.x keeps PluginDatabase as a static
            // property on PluginExtended<,>, not an instance property. Earlier
            // builds used instance members, so keep those as compatibility paths.
            var staticProperty = FindStaticProperty(type, "PluginDatabase", flags);
            if (staticProperty != null)
            {
                object current = staticProperty.GetValue(null, null);
                if (current != null) { return current; }
            }
            var instanceFlags = flags | System.Reflection.BindingFlags.Instance;
            var property = FindInstanceProperty(type, "PluginDatabase", instanceFlags) ?? FindInstanceProperty(type, "Database", instanceFlags);
            object database = property == null ? null : property.GetValue(successStory, null);
            if (database != null) { return database; }
            var field = FindInstanceField(type, "pluginDatabase", instanceFlags) ??
                        FindInstanceField(type, "database", instanceFlags) ??
                        FindInstanceField(type, "<PluginDatabase>k__BackingField", instanceFlags);
            return field == null ? null : field.GetValue(successStory);
        }
        private static System.Reflection.PropertyInfo FindStaticProperty(Type type, string name, System.Reflection.BindingFlags flags)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(name, flags | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);
                if (property != null) { return property; }
            }
            return null;
        }
        private static System.Reflection.PropertyInfo FindInstanceProperty(Type type, string name, System.Reflection.BindingFlags flags)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(name, flags | System.Reflection.BindingFlags.DeclaredOnly);
                if (property != null) { return property; }
            }
            return null;
        }
        private static System.Reflection.FieldInfo FindInstanceField(Type type, string name, System.Reflection.BindingFlags flags)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, flags | System.Reflection.BindingFlags.DeclaredOnly);
                if (field != null) { return field; }
            }
            return null;
        }
        private static System.Reflection.MethodInfo FindSingleParameterMethod(Type type, string name, Type argumentType)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (System.Reflection.MethodInfo method in type.GetMethods(flags))
            {
                if (method.Name != name) { continue; }
                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(argumentType)) { return method; }
            }
            return null;
        }
        private static System.Reflection.MethodInfo FindGetMethod(Type type)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (System.Reflection.MethodInfo method in type.GetMethods(flags))
            {
                var parameters = method.GetParameters();
                if (method.Name == "Get" && parameters.Length >= 1 && parameters[0].ParameterType == typeof(Guid)) { return method; }
            }
            return null;
        }
        private static object[] BuildGetArguments(System.Reflection.MethodInfo method, Guid gameId)
        {
            var parameters = method.GetParameters(); var values = new object[parameters.Length];
            values[0] = gameId;
            for (int i = 1; i < parameters.Length; i++) { values[i] = parameters[i].ParameterType == typeof(bool) ? (object)(i == 1) : (parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null); }
            return values;
        }
        private void StopMonitoring()
        {
            if (logPollTimer != null) { logPollTimer.Stop(); logPollTimer = null; }
            foreach (ActiveSource source in activeSources) { if (source.Watcher != null) { source.Watcher.EnableRaisingEvents = false; source.Watcher.Dispose(); } }
            activeSources.Clear(); activeGame = null;
        }
        private void ShowStatus(Game game)
        {
            if (!File.Exists(RulesPath(game))) { ShowLocalizedMessage("此游戏尚未导入成就规则文件。", "本地成就自动解锁"); return; }
            try
            {
                RuleSet rules = ReadRules(RulesPath(game));
                ValidateRules(rules);
                if (UpgradeReplacedRuneRule(rules, game.InstallDirectory)) { File.WriteAllText(RulesPath(game), serializer.Serialize(rules), new UTF8Encoding(false)); }
                AddGseRuleSources(rules, game.InstallDirectory);
                var report = new StringBuilder();
                report.AppendLine("规则名称：" + (rules.name ?? "未命名"));
                report.AppendLine("监听来源：" + (rules.sources == null ? 0 : rules.sources.Count));
                report.AppendLine("游戏监听：" + (activeGame != null && activeGame.Id == game.Id ? "运行中" : "未运行；须通过 Playnite 启动游戏才会实时监听"));
                report.AppendLine();
                report.AppendLine("【来源状态】");
                var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (rules.sources != null)
                {
                    int sourceNumber = 0;
                    foreach (RuleSource source in rules.sources)
                    {
                        sourceNumber++;
                        string path = Environment.ExpandEnvironmentVariables((source.path ?? String.Empty).Replace('/', '\\'));
                        report.AppendLine(sourceNumber + ". " + path);
                        if (!File.Exists(path))
                        {
                            report.AppendLine("   ⚠ 未生成或无法访问：游戏目前没有可监听的本地状态。");
                            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            {
                                string alternate = Path.ChangeExtension(path, ".ini");
                                if (File.Exists(alternate)) { report.AppendLine("   ⚠ 发现同名 .ini 状态文件，但此 JSON 规则不会读取它：" + alternate); }
                            }
                            continue;
                        }
                        FileInfo info = new FileInfo(path);
                        HashSet<string> ids = ReadAchievementIds(source, path);
                        foreach (string id in ids) { sourceIds.Add(id); }
                        report.AppendLine("   ✓ 文件存在；最后写入：" + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + "；大小：" + info.Length + " 字节");
                        report.AppendLine("   当前读取到已解锁 ID：" + ids.Count);
                        if (ids.Count == 0)
                        {
                            report.AppendLine("   ℹ 文件可读取，但尚未匹配到任何已解锁标记。");
                        }
                    }
                }

                var progress = ReadProgress(game);
                report.AppendLine();
                report.AppendLine("【本扩展记录】");
                report.AppendLine("已记录本地解锁：" + progress.Count);
                string notificationHistory = NotificationPath(game);
                report.AppendLine("已通知 ID：" + (File.Exists(notificationHistory) ? File.ReadAllLines(notificationHistory, Encoding.UTF8).Length : 0) + "（已通知的成就不会重复弹窗）");
                if (sourceIds.Count > 0)
                {
                    int unrecorded = 0;
                    foreach (string id in sourceIds) { if (!progress.ContainsKey(id)) { unrecorded++; } }
                    report.AppendLine("来源中可立即补同步的解锁：" + unrecorded);
                }

                report.AppendLine();
                report.AppendLine("【SuccessStory 同步校验】");
                AppendSuccessStoryHealth(report, game, progress.Keys);
                AppendSuccessStoryThemeHealth(report, game);

                string installDirectory = game.InstallDirectory;
                if (!String.IsNullOrWhiteSpace(installDirectory) && Directory.Exists(installDirectory))
                {
                    report.AppendLine();
                    report.AppendLine("【兼容性预检】");
                    report.AppendLine(BuildAchievementPreflight(installDirectory, rules.name ?? String.Empty, rules));
                    AppendGseDefinitionHealth(report, rules);
                }
                else
                {
                    report.AppendLine();
                    report.AppendLine("【兼容性预检】\nℹ 未保存有效安装目录，无法执行接口预检。");
                }
                report.AppendLine();
                report.AppendLine("规则文件：\n" + RulesPath(game));
                ShowLocalizedMessage(report.ToString(), "本地成就自动解锁 - 健康状态");
            }
            catch (Exception exception) { ShowLocalizedError(exception.Message, "本地成就自动解锁"); }
        }

        private void AppendSuccessStoryThemeHealth(StringBuilder report, Game game)
        {
            try
            {
                foreach (Plugin plugin in PlayniteApi.Addons.Plugins)
                {
                    string typeName = plugin.GetType().FullName ?? String.Empty;
                    string assemblyName = plugin.GetType().Assembly.GetName().Name ?? String.Empty;
                    if (typeName != "SuccessStory.SuccessStory" &&
                        !typeName.StartsWith("SuccessStory.", StringComparison.OrdinalIgnoreCase) &&
                        !String.Equals(assemblyName, "SuccessStory", StringComparison.OrdinalIgnoreCase)) { continue; }
                    object database = GetSuccessStoryDatabase(plugin);
                    object viewModel = database == null ? null : GetProperty(database, "PluginSettings");
                    object settings = viewModel == null ? null : GetProperty(viewModel, "Settings");
                    if (settings == null) { report.AppendLine("ℹ SuccessStory 的主题计数暂不可读取。"); return; }
                    Game context = GetProperty(database, "GameContext") as Game;
                    string contextName = context == null ? "未设置" : context.Name;
                    report.AppendLine("主题当前游戏：" + contextName + "；主题绑定计数：" + GetProperty(settings, "Unlocked") + "/" + GetProperty(settings, "Total") +
                        (context != null && context.Id == game.Id ? "（与本游戏一致）" : "（当前不是本游戏，不用于核对）"));
                    return;
                }
            }
            catch (Exception exception) { report.AppendLine("⚠ 读取主题计数失败：" + exception.Message); }
        }

        private static void AppendGseDefinitionHealth(StringBuilder report, RuleSet rules)
        {
            if (rules == null || String.IsNullOrWhiteSpace(rules.name) || rules.name.IndexOf("(GSE ", StringComparison.OrdinalIgnoreCase) < 0) { return; }
            string settings = rules.settingsDirectory;
            if (String.IsNullOrWhiteSpace(settings) || !Directory.Exists(settings))
            {
                report.AppendLine("⚠ 尚未定位实际 GSE 配置目录。请重新执行自动识别，以便核对成就定义是否紧邻 Steam API DLL。");
                return;
            }
            string dllDirectory = Directory.GetParent(settings).FullName;
            string schema = Path.Combine(settings, "achievements.json");
            bool hasDll = File.Exists(Path.Combine(dllDirectory, "steam_api64.dll")) || File.Exists(Path.Combine(dllDirectory, "steam_api.dll"));
            report.AppendLine(hasDll ? "✓ 成就定义目录位于 Steam API DLL 旁。" : "⚠ 此成就定义目录旁没有 Steam API DLL；游戏可能没有读取它。");
            Match ruleId = Regex.Match(rules.name, "\\(GSE (?<id>\\d+)\\)", RegexOptions.IgnoreCase);
            string diskAppId = Path.Combine(settings, "steam_appid.txt");
            if (ruleId.Success && File.Exists(diskAppId))
            {
                string recordedId = File.ReadAllText(diskAppId, Encoding.UTF8).Trim();
                if (!String.Equals(ruleId.Groups["id"].Value, recordedId, StringComparison.Ordinal))
                {
                    report.AppendLine("⚠ 规则 App ID：" + ruleId.Groups["id"].Value + "；配置文件 App ID：" + recordedId + "。身份不一致时无法可靠匹配成就。");
                }
            }
            if (!File.Exists(schema)) { report.AppendLine("⚠ 缺少成就定义：" + schema + "。可使用“检查并补全 GSE 成就定义”。"); return; }
            try
            {
                if (new FileInfo(schema).Length > 16 * 1024 * 1024) { throw new InvalidDataException("文件超过 16 MB"); }
                object[] definitions = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(File.ReadAllText(schema, Encoding.UTF8)) as object[];
                if (definitions == null) { throw new InvalidDataException("不是成就定义数组"); }
                int valid = 0;
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object value in definitions)
                {
                    string id = GetDictionaryString(value as Dictionary<string, object>, "name");
                    if (!String.IsNullOrWhiteSpace(id) && ids.Add(id)) { valid++; }
                }
                report.AppendLine("成就定义：" + valid + " / " + definitions.Length + " 个有效且不重复的 API ID。" + (valid == definitions.Length ? String.Empty : " ⚠ 可尝试补全。"));
            }
            catch (Exception exception) { report.AppendLine("⚠ 成就定义无法解析：" + exception.Message); }
        }

        // A status inspection must not touch the active monitor state. It reads a
        // separate snapshot of the progress file so checking another game while one
        // is running cannot corrupt its in-memory unlock list.
        private Dictionary<string, DateTime> ReadProgress(Game game)
        {
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            string path = ProgressPath(game);
            if (!File.Exists(path)) { return result; }
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                string[] parts = line.Split('\t');
                DateTime time;
                if (parts.Length == 2 && !String.IsNullOrWhiteSpace(parts[0]) && DateTime.TryParse(parts[1], out time)) { result[parts[0]] = time; }
            }
            return result;
        }

        private static HashSet<string> ReadAchievementIds(RuleSource source, string path)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string text = ReadText(path, source.encoding);
                Regex regex = new Regex(source.match, RegexOptions.CultureInvariant);
                string groupName = String.IsNullOrWhiteSpace(source.achievementGroup) ? "achievementId" : source.achievementGroup;
                foreach (Match match in regex.Matches(text))
                {
                    Group group = match.Groups[groupName];
                    if (group != null && !String.IsNullOrWhiteSpace(group.Value)) { result.Add(group.Value.Trim()); }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return result;
        }

        private void AppendSuccessStoryHealth(StringBuilder report, Game game, IEnumerable<string> localIds)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite", "ExtensionsData", "cebe6d32-8c46-4459-b993-5a5189d60788", "SuccessStory", game.Id.ToString() + ".json");
                if (!File.Exists(path))
                {
                    report.AppendLine("⚠ 未找到该游戏的 SuccessStory 手动成就列表。请先在 SuccessStory 中添加或导入列表。");
                    return;
                }
                var root = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                object[] items = root == null || !root.ContainsKey("Items") ? null : root["Items"] as object[];
                if (items == null)
                {
                    report.AppendLine("⚠ SuccessStory 列表格式无法识别。");
                    return;
                }
                var apiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int unlockedCount = 0;
                foreach (object rawItem in items)
                {
                    var item = rawItem as Dictionary<string, object>;
                    if (item == null) { continue; }
                    object apiName;
                    if (item.TryGetValue("ApiName", out apiName) && apiName != null && !String.IsNullOrWhiteSpace(apiName.ToString())) { apiNames.Add(apiName.ToString()); }
                    object date;
                    if (item.TryGetValue("DateUnlocked", out date) && HasRealUnlockTime(date)) { unlockedCount++; }
                }
                int localCount = 0;
                int matchedCount = 0;
                foreach (string localId in localIds)
                {
                    localCount++;
                    if (apiNames.Contains(localId)) { matchedCount++; }
                }
                report.AppendLine("✓ 已找到 SuccessStory 手动列表：" + apiNames.Count + " 个成就；已显示解锁：" + unlockedCount);
                report.AppendLine("本扩展历史 ID 与 ApiName 匹配：" + matchedCount + "/" + localCount);
                if (localCount > matchedCount)
                {
                    report.AppendLine("⚠ 有 " + (localCount - matchedCount) + " 个本地 ID 未能匹配 SuccessStory 的 ApiName；这些成就会弹窗，但无法写入 SuccessStory。");
                }
                else if (localCount > 0)
                {
                    report.AppendLine("✓ 已记录的本地成就均可写入 SuccessStory。");
                }
            }
            catch (Exception exception)
            {
                report.AppendLine("⚠ 读取 SuccessStory 校验信息失败：" + exception.Message);
            }
        }
        private static Encoding GetEncoding(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || name.Equals("utf8", StringComparison.OrdinalIgnoreCase)) { return new UTF8Encoding(false, true); }
            return Encoding.GetEncoding(name);
        }
        private static string ReadText(string path, string encoding) { return File.ReadAllText(path, GetEncoding(encoding)); }
        private void ShowToast(string title, string description, int count, bool useGameMonitor, string iconUrl, string gameName)
        {
            // A log event can arrive after focus briefly returns to Playnite. Prefer
            // the running game's actual main window for real unlocks; test toasts keep
            // using the current foreground display.
            NativeRect workArea = useGameMonitor ? GetActiveGameWorkArea() : GetForegroundWorkArea();
            File.AppendAllText(Path.Combine(UserDataPath, "notification-diagnostics.log"), DateTime.Now.ToString("o") + " | " + title + " | mode=" + (useGameMonitor ? "game" : "test") + " | monitor work area=" + workArea.Left + "," + workArea.Top + "," + workArea.Right + "," + workArea.Bottom + Environment.NewLine, Encoding.UTF8);
            PlaySteamAchievementSound();
            new AchievementToast(title, description, count, workArea, iconUrl, gameName).Show();
        }
        private void PlaySteamAchievementSound()
        {
            try
            {
                string steamPath = null;
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    steamPath = key == null ? null : key.GetValue("SteamPath") as string;
                }
                if (String.IsNullOrWhiteSpace(steamPath))
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    {
                        steamPath = key == null ? null : key.GetValue("InstallPath") as string;
                    }
                }
                if (String.IsNullOrWhiteSpace(steamPath)) { return; }
                string sound = Path.Combine(steamPath.Replace('/', '\\'), "steamui", "sounds", "deck_ui_achievement_toast.wav");
                if (File.Exists(sound)) { new SoundPlayer(sound).Play(); }
            }
            catch { }
        }
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }
        private static NativeRect GetForegroundWorkArea()
        {
            var fallback = new NativeRect { Left = (int)SystemParameters.WorkArea.Left, Top = (int)SystemParameters.WorkArea.Top, Right = (int)SystemParameters.WorkArea.Right, Bottom = (int)SystemParameters.WorkArea.Bottom };
            IntPtr monitor = MonitorFromWindow(GetForegroundWindow(), 2 /* MONITOR_DEFAULTTONEAREST */);
            return GetWorkAreaForMonitor(monitor, fallback);
        }
        private NativeRect GetActiveGameWorkArea()
        {
            if (activeGame != null && activeGame.GameActions != null)
            {
                foreach (GameAction action in activeGame.GameActions)
                {
                    string executable = ExtractExecutablePath(action.Path);
                    if (String.IsNullOrWhiteSpace(executable)) { continue; }
                    string processName = Path.GetFileNameWithoutExtension(executable);
                    if (String.IsNullOrWhiteSpace(processName)) { continue; }
                    try
                    {
                        foreach (Process process in Process.GetProcessesByName(processName))
                        {
                            using (process)
                            {
                                IntPtr window = process.MainWindowHandle;
                                if (window != IntPtr.Zero) { return GetWorkAreaForMonitor(MonitorFromWindow(window, 2), GetForegroundWorkArea()); }
                            }
                        }
                    }
                    catch { }
                }
            }
            return GetForegroundWorkArea();
        }
        private static NativeRect GetWorkAreaForMonitor(IntPtr monitor, NativeRect fallback)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) { return fallback; }
            // GetMonitorInfo returns physical pixels while WPF Window.Left/Top use
            // device-independent pixels. Without this conversion, a 150%-scaled
            // second monitor produces an off-screen toast.
            uint dpiX = 96, dpiY = 96;
            try { GetDpiForMonitor(monitor, 0, out dpiX, out dpiY); } catch { }
            double scaleX = dpiX / 96.0, scaleY = dpiY / 96.0;
            return new NativeRect
            {
                Left = (int)Math.Round(info.Work.Left / scaleX),
                Top = (int)Math.Round(info.Work.Top / scaleY),
                Right = (int)Math.Round(info.Work.Right / scaleX),
                Bottom = (int)Math.Round(info.Work.Bottom / scaleY)
            };
        }
        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }

    internal class ActiveSource
    {
        public RuleSource Definition; public string Path; public Regex Regex; public FileSystemWatcher Watcher; public long Position; public string Pending;
        public DateTime LastPolledWriteUtc = DateTime.MinValue; public long LastPolledLength = -1;
        public readonly object SyncRoot = new object();
    }
    internal class SuccessStoryAchievement { public string Title; public string Description; public string IconUrl; public bool WasAlreadyUnlocked; }

    internal class AutoAchievementsSettings : ISettings
    {
        private readonly string path;
        private readonly Action languageChanged;
        private string previousLanguage;
        public string Language { get; set; }
        public AutoAchievementsSettings(string settingsPath, Action onLanguageChanged)
        {
            path = settingsPath;
            languageChanged = onLanguageChanged;
            Language = File.Exists(path) && String.Equals(File.ReadAllText(path, Encoding.UTF8).Trim(), "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        }
        public void BeginEdit() { previousLanguage = Language; }
        public void EndEdit()
        {
            bool changed = previousLanguage != null && previousLanguage != Language;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Language == "en" ? "en" : "zh", new UTF8Encoding(false));
            // Playnite reads the extension name from its static manifest only at
            // startup. Change only our known Name value so it follows the chosen
            // language on the requested restart, leaving all other metadata alone.
            string manifest = Path.Combine(Path.GetDirectoryName(typeof(AutoAchievementsPlugin).Assembly.Location), "extension.yaml");
            if (File.Exists(manifest))
            {
                string yaml = File.ReadAllText(manifest, Encoding.UTF8);
                if (yaml.Contains("Id: 6d1a3eae-5c61-4f1f-bb72-9c355dc0e5d9"))
                {
                    string translated = Regex.Replace(yaml, "(?m)^Name: (?:本地成就自动解锁|Automatic Local Achievements)(?=\\r?$)",
                        Language == "en" ? "Name: Automatic Local Achievements" : "Name: 本地成就自动解锁");
                    if (translated != yaml) { File.WriteAllText(manifest, translated, new UTF8Encoding(false)); }
                }
            }
            if (changed && languageChanged != null) { languageChanged(); }
        }
        public void CancelEdit() { Language = previousLanguage ?? "zh"; }
        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }

    internal class AchievementToast : Window
    {
        private const double ScreenMargin = 24;
        private const double ToastGap = 10;
        private static readonly Dictionary<string, List<AchievementToast>> stacks = new Dictionary<string, List<AchievementToast>>();
        private readonly AutoAchievementsPlugin.NativeRect workArea;
        private readonly string stackKey;

        public AchievementToast(string title, string description, int count, AutoAchievementsPlugin.NativeRect workArea, string iconUrl, string gameName)
        {
            this.workArea = workArea;
            stackKey = workArea.Left + ":" + workArea.Top + ":" + workArea.Right + ":" + workArea.Bottom;
            Width = 408; Height = 92; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; Opacity = 0;
            var border = new Border { Background = new SolidColorBrush(Color.FromRgb(23, 26, 33)), BorderBrush = new SolidColorBrush(Color.FromRgb(61, 80, 96)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(1), Padding = new Thickness(10) };
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            var iconBorder = new Border { Width = 60, Height = 60, Background = new SolidColorBrush(Color.FromRgb(10, 18, 27)), BorderBrush = new SolidColorBrush(Color.FromRgb(77, 119, 150)), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            if (!String.IsNullOrWhiteSpace(iconUrl))
            {
                try { iconBorder.Child = new Image { Source = new BitmapImage(new Uri(iconUrl, UriKind.Absolute)), Stretch = Stretch.UniformToFill }; }
                catch { iconBorder.Child = new TextBlock { Text = "★", Foreground = new SolidColorBrush(Color.FromRgb(102, 192, 244)), FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
            }
            else { iconBorder.Child = new TextBlock { Text = "★", Foreground = new SolidColorBrush(Color.FromRgb(102, 192, 244)), FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
            Grid.SetColumn(iconBorder, 0); root.Children.Add(iconBorder);
            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textPanel.Children.Add(new TextBlock { Text = UiLanguage.T("成就已解锁"), Foreground = new SolidColorBrush(Color.FromRgb(102, 192, 244)), FontWeight = FontWeights.SemiBold, FontSize = 12 });
            textPanel.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            textPanel.Children.Add(new TextBlock { Text = description, Foreground = new SolidColorBrush(Color.FromRgb(174, 185, 196)), FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            textPanel.Children.Add(new TextBlock { Text = gameName ?? "", Foreground = new SolidColorBrush(Color.FromRgb(125, 146, 165)), FontSize = 10, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(textPanel, 1); root.Children.Add(textPanel);
            var countPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            countPanel.Children.Add(new TextBlock { Text = "🏆", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center });
            countPanel.Children.Add(new TextBlock { Text = count.ToString(), Foreground = new SolidColorBrush(Color.FromRgb(188, 204, 217)), FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
            countPanel.Children.Add(new TextBlock { Text = UiLanguage.T("已解锁"), Foreground = new SolidColorBrush(Color.FromRgb(111, 133, 151)), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(countPanel, 2); root.Children.Add(countPanel);
            // Replace the original prototype with a compact Steam-inspired design.
            // Steam blue is deliberately substituted with the requested orange accent.
            Content = BuildSteamStyleContent(title, description, count, iconUrl, gameName);
            Loaded += delegate
            {
                RegisterInStack();
                BeginAnimation(Window.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timer.Tick += delegate { timer.Stop(); Close(); };
                timer.Start();
            };
            Closed += delegate { RemoveFromStack(); };
        }

        private static UIElement BuildSteamStyleContent(string title, string description, int count, string iconUrl, string gameName)
        {
            const byte orangeR = 242, orangeG = 137, orangeB = 36;
            var outer = new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(15, 25, 37), Color.FromRgb(34, 49, 64), new Point(0, 0), new Point(1, 1)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(75, 91, 106)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.7, BlurRadius = 18, ShadowDepth = 5, Direction = 270 }
            };
            var frame = new Grid();
            frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });
            frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            frame.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromRgb(255, 187, 60), Color.FromRgb(198, 79, 18), new Point(0, 0), new Point(1, 0)) });

            var body = new Grid { Margin = new Thickness(10, 8, 10, 8) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(61) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            Grid.SetRow(body, 1); frame.Children.Add(body); outer.Child = frame;

            var icon = new Border
            {
                Width = 53, Height = 53, VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(9, 16, 24)), BorderBrush = new SolidColorBrush(Color.FromRgb(orangeR, orangeG, orangeB)), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = Color.FromRgb(orangeR, orangeG, orangeB), Opacity = 0.3, BlurRadius = 8, ShadowDepth = 0 }
            };
            if (!String.IsNullOrWhiteSpace(iconUrl))
            {
                try { icon.Child = new Image { Source = new BitmapImage(new Uri(iconUrl, UriKind.Absolute)), Stretch = Stretch.UniformToFill }; }
                catch { icon.Child = CreateFallbackIcon(); }
            }
            else { icon.Child = CreateFallbackIcon(); }
            Grid.SetColumn(icon, 0); body.Children.Add(icon);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = UiLanguage.T("成就已解锁"), Foreground = new SolidColorBrush(Color.FromRgb(255, 183, 57)), FontWeight = FontWeights.SemiBold, FontSize = 11 });
            words.Children.Add(new TextBlock { Text = title ?? "", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(new TextBlock { Text = description ?? "", Foreground = new SolidColorBrush(Color.FromRgb(188, 202, 214)), FontSize = 10, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(new TextBlock { Text = gameName ?? "", Foreground = new SolidColorBrush(Color.FromRgb(128, 150, 168)), FontSize = 9, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(words, 1); body.Children.Add(words);

            var tally = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            tally.Children.Add(new TextBlock { Text = "♛", Foreground = new SolidColorBrush(Color.FromRgb(255, 183, 57)), FontSize = 16, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            tally.Children.Add(new TextBlock { Text = count.ToString(), Foreground = new SolidColorBrush(Color.FromRgb(236, 242, 246)), FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });
            tally.Children.Add(new TextBlock { Text = UiLanguage.T("已解锁"), Foreground = new SolidColorBrush(Color.FromRgb(139, 158, 174)), FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(tally, 2); body.Children.Add(tally);
            return outer;
        }

        private static TextBlock CreateFallbackIcon()
        {
            return new TextBlock { Text = "★", Foreground = new SolidColorBrush(Color.FromRgb(255, 183, 57)), FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        private void RegisterInStack()
        {
            List<AchievementToast> stack;
            if (!stacks.TryGetValue(stackKey, out stack))
            {
                stack = new List<AchievementToast>(); stacks[stackKey] = stack;
            }
            // The newest achievement is always placed in the normal lower-right slot;
            // older ones are pushed upwards, as in Steam's notification stack.
            stack.Insert(0, this);
            ReflowStack(stack, false);
        }

        private void RemoveFromStack()
        {
            List<AchievementToast> stack;
            if (!stacks.TryGetValue(stackKey, out stack)) { return; }
            stack.Remove(this);
            if (stack.Count == 0) { stacks.Remove(stackKey); return; }
            ReflowStack(stack, true);
        }

        private static void ReflowStack(List<AchievementToast> stack, bool animate)
        {
            for (int index = 0; index < stack.Count; index++)
            {
                AchievementToast toast = stack[index];
                double targetLeft = toast.workArea.Right - toast.Width - ScreenMargin;
                double targetTop = toast.workArea.Bottom - toast.Height - ScreenMargin - index * (toast.Height + ToastGap);
                if (!animate && index == 0)
                {
                    toast.Left = targetLeft;
                    toast.Top = targetTop;
                    continue;
                }
                toast.Left = targetLeft;
                var slide = new DoubleAnimation(targetTop, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                toast.BeginAnimation(Window.TopProperty, slide, HandoffBehavior.SnapshotAndReplace);
            }
        }
    }
}
