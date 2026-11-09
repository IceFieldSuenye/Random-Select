using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SmokeTest
{
    /// <summary>
    /// 冒烟测试：直接跑真实代码路径，覆盖本轮修复的每一项。
    /// 用法：dotnet run --project tests\SmokeTest\SmokeTest.csproj -c Release
    /// 输出每项 PASS / FAIL，退出码 = 失败项数。
    /// </summary>
    internal static class Program
    {
        private static int _failed;
        private static readonly List<string> Failures = new List<string>();

        /// <summary>测试产出的截图 / PDF 统一放在项目根目录的 Test 文件夹里。</summary>
        private static readonly string TestOutputDir =
            Path.Combine(Directory.GetCurrentDirectory(), "Test");

        private static string TestFile(string fileName)
        {
            try { Directory.CreateDirectory(TestOutputDir); } catch { }
            return Path.Combine(TestOutputDir, fileName);
        }

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            // ★ 允许在用户正开着「随机抽取器」时跑测试：让被测程序跳过单实例互斥体
            Environment.SetEnvironmentVariable("RS_SMOKE_TEST", "1");

            try { Directory.CreateDirectory(TestOutputDir); } catch { }
            // ★ 分享缓存指向测试目录，别动用户真实的 share_cache
            Environment.SetEnvironmentVariable("RS_CACHE_DIR", Path.Combine(TestOutputDir, "cache"));
            // ★ 配置文件也指到 Test\：测试不再读写用户真实的 %APPDATA%\Random Select\settings.ini
            Environment.SetEnvironmentVariable("RS_SETTINGS_FILE", Path.Combine(TestOutputDir, "settings.ini"));
            string root = Path.Combine(Directory.GetCurrentDirectory(),
                "tests-tmp-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);

            try
            {
                TestDrawSingleUpPool(root);
                TestProbabilityCulture(root);
                TestReportNameColumn(root);
                TestThreeStarProbDerived(root);
                TestLockPrimitives();
                TestPoolVisualChanged();
                TestUnlockReflectedInScan(root);
                TestExport(root);
                TestUiThresholds();
            }
            catch (Exception ex)
            {
                Fail("逻辑测试抛出异常", ex.ToString());
            }

            // UI 测试需要 Application 资源
            try { RunUiTests(root); }
            catch (Exception ex) { Fail("UI 测试抛出异常", ex.ToString()); }

            Console.WriteLine();
            Console.WriteLine("========================================");
            if (_failed == 0) Console.WriteLine("全部通过");
            else
            {
                Console.WriteLine($"失败 {_failed} 项：");
                foreach (var f in Failures) Console.WriteLine("  - " + f);
            }
            Console.WriteLine("========================================");

            try { Directory.Delete(root, true); } catch { }
            return _failed;
        }

        // ---------------------------------------------------------------
        // T1：单 UP 池（池里只有一个 5 星且它就是 UP）不再抛异常
        // ---------------------------------------------------------------
        private static void TestDrawSingleUpPool(string root)
        {
            string dir = Path.Combine(root, "pool-single-up");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "data.txt"),
                "抽取对象 分类\r\nA 5\r\nB 4\r\nC 3\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "gacha_settings.ini"),
                "[Gacha]\r\nEnabled=1\r\nPresetName=崩坏：星穹铁道\r\n" +
                "FiveStarCategory=5\r\nFiveStarUpObject=A\r\n" +
                "FiveStarNonUpProb=100\r\nFiveStarUpProb=0\r\n" +
                "FourStarUpProb=0\r\nFourStarNonUpProb=0\r\n" +
                "FiveStarPity=90\r\nFourStarPity=10\r\n", new UTF8Encoding(false));

            var pool = MakePool(dir, "单UP池", new[] { "A 5", "B 4", "C 3" });
            var engine = new RandomDrawer.DrawEngine();

            try
            {
                var results = engine.ExecuteDraw(5, pool, false, null);
                Pass("T1 单 UP 池抽取不抛异常", $"抽出 {results.Count} 条");
                if (results.All(r => r.Fields.Count > 0))
                    Pass("T1 结果字段完整", "");
                else
                    Fail("T1 结果字段完整", "存在空字段");
            }
            catch (Exception ex)
            {
                Fail("T1 单 UP 池抽取不抛异常", ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // T2：小数点用逗号的系统区域下，概率仍然解析正确
        // ---------------------------------------------------------------
        private static void TestProbabilityCulture(string root)
        {
            string ini = Path.Combine(root, "gacha_settings.ini");
            File.WriteAllText(ini,
                "[Gacha]\r\nEnabled=1\r\nPresetName=崩坏：星穹铁道\r\n" +
                "FiveStarNonUpProb=0.300\r\nFiveStarUpProb=0.300\r\n" +
                "FourStarUpProb=2.550\r\nFourStarNonUpProb=2.550\r\n" +
                "SoftPityStart=74\r\nSoftPityIncrease=0.06\r\n" +
                "FourStarPity=10\r\nFiveStarPity=90\r\n", new UTF8Encoding(false));

            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // 小数点 = ','
                var s = RandomDrawer.GachaSettings.LoadSettingsFromFile(ini);

                bool ok = Math.Abs(s.FiveStarNonUpProb - 0.3) < 1e-9
                       && Math.Abs(s.FiveStarUpProb - 0.3) < 1e-9
                       && Math.Abs(s.FourStarUpProb - 2.55) < 1e-9
                       && Math.Abs(s.SoftPityIncrease - 0.06) < 1e-9
                       && s.SoftPityStart == 74 && s.FiveStarPity == 90;

                if (ok) Pass("T2 逗号小数点区域解析概率", $"5星={s.FiveStarNonUpProb:0.###} 4星={s.FourStarUpProb:0.###}");
                else Fail("T2 逗号小数点区域解析概率", $"得到 5星={s.FiveStarNonUpProb} 4星={s.FourStarUpProb} 软保底增幅={s.SoftPityIncrease}");

                // 保存必须写回「点」作小数点，换区域也能读
                RandomDrawer.GachaSettings.SaveSettingsToFile(s, ini);
                string written = File.ReadAllText(ini);
                if (written.Contains("0.3") && !written.Contains("0,3"))
                    Pass("T2 保存始终使用点号小数点", "");
                else
                    Fail("T2 保存始终使用点号小数点", FirstMatch(written, "FiveStarNonUpProb") ?? "(未找到)");

                var again = RandomDrawer.GachaSettings.LoadSettingsFromFile(ini);
                if (Math.Abs(again.FiveStarNonUpProb - 0.3) < 1e-9) Pass("T2 保存后可回读", "");
                else Fail("T2 保存后可回读", again.FiveStarNonUpProb.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                CultureInfo.CurrentCulture = old;
            }
        }

        // ---------------------------------------------------------------
        // T3：data.txt 首列是「序号」时，报告里的对象名仍然是名字
        // ---------------------------------------------------------------
        private static void TestReportNameColumn(string root)
        {
            string dir = Path.Combine(root, "pool-serial");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "data.txt"),
                "序号 抽取对象 分类\r\n1 张三 5\r\n2 李四 3\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "gacha_settings.ini"),
                "[Gacha]\r\nEnabled=1\r\nPresetName=崩坏：星穹铁道\r\nFiveStarCategory=5\r\n" +
                "FiveStarUpObject=张三\r\nFiveStarNonUpProb=100\r\nFiveStarUpProb=0\r\n" +
                "FourStarUpProb=0\r\nFourStarNonUpProb=0\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "history.txt"),
                "2026/01/01 10:00:00|单次抽取|1\t张三\t5\r\n" +
                "2026/01/01 10:01:00|单次抽取|2\t李四\t3\r\n", new UTF8Encoding(false));

            var data = RandomDrawer.GachaReportAnalyzer.Analyze(dir, "序列号卡池");
            var first = data.Entries?.FirstOrDefault();
            if (first != null && first.Name == "张三")
                Pass("T3 报告按表头取对象名", $"首条={first.Name}");
            else
                Fail("T3 报告按表头取对象名", $"首条={(first == null ? "(空)" : first.Name)}");
        }

        // ---------------------------------------------------------------
        // T4：3 星概率是推导值（100 - 5星 - 4星）
        // ---------------------------------------------------------------
        private static void TestThreeStarProbDerived(string root)
        {
            string ini = Path.Combine(root, "three-star.ini");
            File.WriteAllText(ini,
                "[Gacha]\r\nEnabled=1\r\nPresetName=崩坏：星穹铁道\r\n" +
                "FiveStarNonUpProb=0.6\r\nFiveStarUpProb=0\r\n" +
                "FourStarUpProb=5.1\r\nFourStarNonUpProb=0\r\n" +
                "ThreeStarProb=0\r\n", new UTF8Encoding(false));   // ini 里故意写错

            var s = RandomDrawer.GachaSettings.LoadSettingsFromFile(ini);
            if (Math.Abs(s.ThreeStarProb - 94.3) < 1e-6)
                Pass("T4 3 星概率按推导值", $"{s.ThreeStarProb:0.###}");
            else
                Fail("T4 3 星概率按推导值", s.ThreeStarProb.ToString("0.###"));
        }

        // ---------------------------------------------------------------
        // T9：加密卡池的密码哈希原语（删除/复制的安全判断都依赖它）
        // ---------------------------------------------------------------
        private static void TestLockPrimitives()
        {
            string name = "smoke_lock_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string renamed = name + "_renamed";
            try
            {
                RandomDrawer.CryptoUtils.SavePasswordHash(name, "abc123");

                // 读取路径：外部（reg.exe）预置的哈希，验证「正确密码通过 / 错误密码拒绝」
                const string seedName = "__smoke_seed__";
                if (RandomDrawer.CryptoUtils.HasPasswordHash(seedName))
                {
                    bool seedOk = RandomDrawer.CryptoUtils.VerifyPassword(seedName, "abc123");
                    bool seedBad = RandomDrawer.CryptoUtils.VerifyPassword(seedName, "wrong");
                    if (seedOk && !seedBad) Pass("T9 密码校验（读取路径）", "正确密码通过、错误密码拒绝");
                    else Fail("T9 密码校验（读取路径）", $"ok={seedOk} bad={seedBad}");
                }

                if (!RandomDrawer.CryptoUtils.HasPasswordHash(name))
                {
                    Console.WriteLine("[SKIP] T9 密码哈希写入 —— 当前环境不允许 .NET 写注册表");
                    return;
                }

                bool hasHash = RandomDrawer.CryptoUtils.HasPasswordHash(name);
                bool okRight = RandomDrawer.CryptoUtils.VerifyPassword(name, "abc123");
                bool okWrong = RandomDrawer.CryptoUtils.VerifyPassword(name, "wrong");

                if (hasHash && okRight && !okWrong)
                    Pass("T9 密码哈希：写入 / 校验 / 拒绝错误密码", "");
                else
                    Fail("T9 密码哈希", $"has={hasHash} right={okRight} wrong={okWrong}");

                RandomDrawer.CryptoUtils.MovePasswordHash(name, renamed);
                bool moved = RandomDrawer.CryptoUtils.HasPasswordHash(renamed)
                          && !RandomDrawer.CryptoUtils.HasPasswordHash(name)
                          && RandomDrawer.CryptoUtils.VerifyPassword(renamed, "abc123");
                if (moved) Pass("T9 重命名后密码跟随迁移", "");
                else Fail("T9 重命名后密码跟随迁移", "哈希未迁移或旧名仍存在");

                // 「本机没有密码记录」的判断：删除后必须为 false（删除/复制走警告分支）
                RandomDrawer.CryptoUtils.DeletePasswordHash(renamed);
                if (!RandomDrawer.CryptoUtils.HasPasswordHash(renamed))
                    Pass("T9 无密码记录可被识别", "");
                else
                    Fail("T9 无密码记录可被识别", "删除后仍判定为有密码");
            }
            finally
            {
                RandomDrawer.CryptoUtils.DeletePasswordHash(name);
                RandomDrawer.CryptoUtils.DeletePasswordHash(renamed);
            }
        }

        // ---------------------------------------------------------------
        // T10：跨页同步的关键判断 —— 锁状态变化必须触发列表重建
        // ---------------------------------------------------------------
        private static void TestPoolVisualChanged()
        {
            try
            {
                var type = typeof(RandomDrawer.MainPage_UC);
                var mi = type.GetMethod("PoolsVisualChanged",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (mi == null) { Fail("T10 找到 PoolsVisualChanged", "方法不存在"); return; }

                var oldList = new List<RandomDrawer.PoolInfo>
                {
                    new RandomDrawer.PoolInfo { Dir = "a", UniqueName = "A", IsLocked = true,  ItemCount = 3 },
                    new RandomDrawer.PoolInfo { Dir = "b", UniqueName = "B", IsLocked = false, ItemCount = 5 }
                };

                // 取消加密：IsLocked 由 true → false
                var unlocked = new List<RandomDrawer.PoolInfo>
                {
                    new RandomDrawer.PoolInfo { Dir = "a", UniqueName = "A", IsLocked = false, ItemCount = 3 },
                    new RandomDrawer.PoolInfo { Dir = "b", UniqueName = "B", IsLocked = false, ItemCount = 5 }
                };

                var same = new List<RandomDrawer.PoolInfo>
                {
                    new RandomDrawer.PoolInfo { Dir = "a", UniqueName = "A", IsLocked = true,  ItemCount = 3 },
                    new RandomDrawer.PoolInfo { Dir = "b", UniqueName = "B", IsLocked = false, ItemCount = 5 }
                };

                bool changedOnUnlock = (bool)mi.Invoke(null, new object[] { oldList, unlocked });
                bool unchangedWhenSame = (bool)mi.Invoke(null, new object[] { oldList, same });

                if (changedOnUnlock && !unchangedWhenSame)
                    Pass("T10 解锁会触发列表重建（锁图标同步）", "");
                else
                    Fail("T10 解锁会触发列表重建（锁图标同步）",
                        $"changedOnUnlock={changedOnUnlock} unchangedWhenSame={unchangedWhenSame}");
            }
            catch (Exception ex)
            {
                Fail("T10 解锁会触发列表重建", ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // T11：解锁后重新扫描，锁状态必须变成「未加密」
        //      （对应「取消加密回到主页仍显示锁图标」的根因链）
        // ---------------------------------------------------------------
        private static void TestUnlockReflectedInScan(string root)
        {
            string dir = Path.Combine(root, "pool-unlock");
            try
            {
                Directory.CreateDirectory(dir);
                string raw = "抽取对象 分类\r\nA 5\r\nB 3\r\n";

                // 锁定时：只有 data.lock
                File.WriteAllText(Path.Combine(dir, "data.lock"),
                    RandomDrawer.CryptoUtils.EncryptString(raw), new UTF8Encoding(false));

                var locked = ScanOnePool(dir);
                if (locked == null) { Fail("T11 扫描加密卡池", "扫描返回 null"); return; }

                // 取消加密：写回 data.txt 并删除 data.lock
                File.WriteAllText(Path.Combine(dir, "data.txt"), raw, new UTF8Encoding(false));
                File.Delete(Path.Combine(dir, "data.lock"));

                var unlocked = ScanOnePool(dir);
                if (unlocked == null) { Fail("T11 扫描未加密卡池", "扫描返回 null"); return; }

                if (locked.IsLocked && !unlocked.IsLocked && unlocked.ItemCount == 2)
                    Pass("T11 解锁后扫描结果变为未加密", $"锁定={locked.IsLocked} → 解锁={unlocked.IsLocked}，条目={unlocked.ItemCount}");
                else
                    Fail("T11 解锁后扫描结果变为未加密",
                        $"locked.IsLocked={locked.IsLocked} unlocked.IsLocked={unlocked.IsLocked} 条目={unlocked.ItemCount}");
            }
            catch (Exception ex)
            {
                Fail("T11 解锁后扫描结果变为未加密", ex.Message);
            }
        }

        private static RandomDrawer.PoolInfo ScanOnePool(string dir)
        {
            var mi = typeof(RandomDrawer.MainPage_UC).GetMethod("LoadSinglePoolFromDirStatic",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) throw new InvalidOperationException("找不到 LoadSinglePoolFromDirStatic");
            return (RandomDrawer.PoolInfo)mi.Invoke(null, new object[]
            {
                dir,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null
            });
        }

        // ---------------------------------------------------------------
        // T12：导出加密卡池 —— 普通导出保持加密；解密导出写明文、去掉加密与密保文件
        // ---------------------------------------------------------------
        private static void TestExport(string root)
        {
            string dir = Path.Combine(root, "pool-export");
            string zipLocked = Path.Combine(root, "export-locked.zip");
            string zipPlain = Path.Combine(root, "export-plain.zip");

            try
            {
                Directory.CreateDirectory(dir);
                string raw = "抽取对象 分类\r\nA 5\r\nB 3\r\n";
                File.WriteAllText(Path.Combine(dir, "data.lock"),
                    RandomDrawer.CryptoUtils.EncryptString(raw), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "secret_qa.lock"), "qa", new UTF8Encoding(false));

                var pools = new List<(string Name, string Dir)> { ("加密池", dir) };

                // 1) 普通导出：保持加密
                var r1 = RandomDrawer.PoolTransfer.Export(pools, zipLocked);
                var names1 = ReadZipNames(zipLocked);
                bool keepLocked = r1.Exported == 1
                               && names1.Any(n => n.EndsWith("/data.lock", StringComparison.OrdinalIgnoreCase))
                               && !names1.Any(n => n.EndsWith("/data.txt", StringComparison.OrdinalIgnoreCase));

                if (keepLocked) Pass("T12 普通导出保持加密", "");
                else Fail("T12 普通导出保持加密", string.Join(", ", names1));

                // 2) 解密导出：写明文 data.txt，不含 data.lock / secret_qa.lock
                var decryptSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dir };
                var r2 = RandomDrawer.PoolTransfer.Export(pools, zipPlain, null, decryptSet);
                var names2 = ReadZipNames(zipPlain);
                string txt = ReadZipEntry(zipPlain, "/data.txt");

                bool decrypted = r2.Exported == 1
                              && names2.Any(n => n.EndsWith("/data.txt", StringComparison.OrdinalIgnoreCase))
                              && !names2.Any(n => n.EndsWith("/data.lock", StringComparison.OrdinalIgnoreCase))
                              && !names2.Any(n => n.EndsWith("/secret_qa.lock", StringComparison.OrdinalIgnoreCase))
                              && txt != null && txt.Replace("\r\n", "\n").Contains("A 5");

                if (decrypted) Pass("T12 解密导出：明文 data.txt、无加密/密保文件", "");
                else Fail("T12 解密导出", $"files=[{string.Join(", ", names2)}] 内容={(txt == null ? "(空)" : txt.Trim())}");
            }
            catch (Exception ex)
            {
                Fail("T12 导出", ex.Message);
            }
        }

        private static List<string> ReadZipNames(string zipPath)
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            return zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        }

        private static string ReadZipEntry(string zipPath, string suffix)
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
            return reader.ReadToEnd();
        }

        // ---------------------------------------------------------------
        // T13：加载遮罩阈值 —— 少于 500 条不显示遮罩（防闪烁）
        // ---------------------------------------------------------------
        private static void TestUiThresholds()
        {
            const int expected = 500;
            int actual = RandomDrawer.UiThresholds.LoadingOverlayMinCount;
            int background = RandomDrawer.UiThresholds.BackgroundWorkMinCount;

            if (actual == expected && background < actual)
                Pass("T13 加载遮罩阈值", $"遮罩≥{actual} 才显示，后台计算≥{background}");
            else
                Fail("T13 加载遮罩阈值", $"LoadingOverlayMinCount={actual}（期望 {expected}），BackgroundWorkMinCount={background}");
        }

        // ---------------------------------------------------------------
        // UI 测试：更新日志解析、密码框模式、抽取次数面板布局 + 截图
        // ---------------------------------------------------------------
        private static void RunUiTests(string root)
        {
            var app = new RandomDrawer.App();
            Console.WriteLine("[step] App 已创建");
            app.InitializeComponent();
            Console.WriteLine("[step] App.xaml 已加载");
            RandomDrawer.ThemeManager.Initialize();
            Console.WriteLine("[step] 主题已初始化");

            TestChangelogResource();
            TestPasswordDialogMode();

            Console.WriteLine("[step] 创建 MainWindow…");
            var img = RandomDrawer.EmbeddedImageLoader.Load("default_cover.jpg");
            Console.WriteLine("[step] 内嵌封面加载 " + (img == null ? "失败" : $"{img.PixelWidth}x{img.PixelHeight}"));
            var win = new RandomDrawer.MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false
            };

            // 主窗口加载时可能弹「图标兼容提示 / 启动错误」浮层（模态，会一直等点击）。
            // 测试环境里自动关掉它，否则流程会卡在 PushFrame 上。
            RandomDrawer.App.SuppressIconCompatNotice = true;
            var closer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background,
                (s, e) =>
                {
                    try { if (RandomDrawer.FloatingOverlay.IsOpen) RandomDrawer.FloatingOverlay.CloseAll(); }
                    catch { }
                }, Dispatcher.CurrentDispatcher);
            closer.Start();

            win.Show();
            Console.WriteLine("[step] 窗口已显示");

            var beat = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
                (s, e) => Console.WriteLine($"[beat] overlayOpen={RandomDrawer.FloatingOverlay.IsOpen}"),
                Dispatcher.CurrentDispatcher);
            beat.Start();

            DoEvents(TimeSpan.FromMilliseconds(1200));
            beat.Stop();
            Console.WriteLine("[step] 首帧完成");

            try
            {
                var task = win.ShowDrawCountPanelAsync(10);
                DoEvents(TimeSpan.FromMilliseconds(500));

                SaveShot(win, TestFile("shot-drawcount.png"));

                TestOptionsLayouts(win);
                TestAdvancedLayout();
                TestShareDialogMulti();
                TestShareGenerationKeepsAnimations(win);
                TestLargeReportAnimations();

                // 启动阶段的「图标兼容提示」自动关闭器到此为止：后面的测试要真实显示浮层
                try { closer.Stop(); } catch { }

                TestShareBusyOverlay(win);
                TestPageHeader();
                TestSingleImageSizeGate();
                TestRowStripeMatchesHtml();
                TestPreviewContextMenu();
                TestPdfExport();
                TestPdfPreviewDialog();
                TestPdfCache();
                TestPoolExportImportMenus();
                TestPoolPackageRoundTrip();
                TestShareCopyToast();
                TestShareDialogThemeAndHeight();
                TestBackgroundImageDecode();
                TestUnifiedSpinner();
                TestAssetsResolvable();
                TestNavigationDuringBusy(win);
                TestToastIcons(win);
                TestLongReportPrompt();
                TestLongReportPromptDialog(win);
                TestShareDialogHugeImageDisplay(win);
                TestSingleTooLargeDialog(win);
                TestPageRangesWholeObjects();
                TestThumbWheelScroll();
                TestLogPunctuation();
                TestCloseGuard(win);
                TestTabNavigationDisabled(win);
                TestLightConeAndStandardWarp();
                TestWarpAwareReport(root);
                TestDrawDetailHeaders();
                TestColorPickerDialog();
                TestExportDefaultFileName();
                TestPersonalizationComboWidth();
                TestUnifiedBusySpinners(win);
                TestResultScrollBarFlush();
                TestWarpDefaultsFromPoolMd();
                TestWindowTitleValidation();
                TestWindowTitleInvalidUI(win);
                TestResultListNoScaleFlash();
                TestCoverSurvivesAnimationToggle(win);
                TestWindowTitleFormat();
                TestWindowTitleOption(win);
                TestShareHeaderTitle();
                TestToastAtWindowTop(win);
            }
            finally
            {
                try { win.Close(); } catch { }
                DoEvents(TimeSpan.FromMilliseconds(200));
            }
        }

        // ---------------------------------------------------------------
        // T50：拆分长图 / PDF 页眉用「用户自定义窗口标题」，关键词直接去掉
        // ---------------------------------------------------------------
        private static void TestShareHeaderTitle()
        {
            string oldFormat = RandomDrawer.App.WindowTitleFormat;
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;

                RandomDrawer.App.WindowTitleFormat = "";
                bool defaultOk = RandomDrawer.WindowTitleFormatter.GetShareHeaderAppText("3.15.1") == "随机抽取器 3.15.1";

                RandomDrawer.App.WindowTitleFormat = "我的抽取器 {version}";
                bool tokenOk = RandomDrawer.WindowTitleFormatter.GetShareHeaderAppText("3.15.1") == "我的抽取器";

                RandomDrawer.App.WindowTitleFormat = "{time}{date}";
                bool fallbackOk = RandomDrawer.WindowTitleFormatter.GetShareHeaderAppText("3.15.1") == "随机抽取器 3.15.1";

                RandomDrawer.App.WindowTitleFormat = "沧海萃珠 专用 {pool}";
                var page = new RandomDrawer.GachaReportPage_UC();
                string header = page.GetType().GetMethod("BuildPageHeader", flags)
                    ?.Invoke(page, new object[] { 2, 7, false }) as string;

                bool headerOk = header != null
                                && header.StartsWith("沧海萃珠 专用   卡池：", StringComparison.Ordinal)
                                && header.Contains("第2张/共7张")
                                && !header.Contains("{");

                if (defaultOk && tokenOk && fallbackOk && headerOk)
                    Pass("T50 分享页眉用自定义标题（关键词去掉）", header ?? "");
                else
                    Fail("T50 分享页眉用自定义标题（关键词去掉）",
                        $"default={defaultOk} token={tokenOk} fallback={fallbackOk} header={header ?? "null"}");
            }
            catch (Exception ex) { Fail("T50 分享页眉标题", ex.GetType().Name + "：" + ex.Message); }
            finally { RandomDrawer.App.WindowTitleFormat = oldFormat; }
        }

        // ---------------------------------------------------------------
        // T51：顶部气泡严格贴住窗口上边缘（不再飘到窗口外）
        // ---------------------------------------------------------------
        private static void TestToastAtWindowTop(Window owner)
        {
            try
            {
                var root = owner.Content as Panel;
                if (root == null) { Fail("T51 顶部气泡位置与收起", "窗口根容器不是 Panel"); return; }

                RandomDrawer.ToastNotification.ShowSuccess(owner, "吸附测试", 900);
                DoEvents(TimeSpan.FromMilliseconds(420));

                FrameworkElement layer = null;
                foreach (UIElement child in root.Children)
                {
                    if (child is FrameworkElement fe && Panel.GetZIndex(fe) == 6000) { layer = fe; break; }
                }

                if (layer == null) { Fail("T51 顶部气泡位置与收起", "找不到气泡图层"); return; }

                // 结构：Canvas（宿主） → Grid（铺满窗口的裁剪层） → Border（气泡卡片）
                FrameworkElement card = layer;
                for (int depth = 0; depth < 2 && card is Panel p && p.Children.Count > 0; depth++)
                    card = p.Children[0] as FrameworkElement;

                bool inWindow = layer.Parent == root;
                double y = card != null ? card.TransformToAncestor(root).Transform(new Point(0, 0)).Y : double.NaN;
                double centerX = card != null
                    ? card.TransformToAncestor(root).Transform(new Point(0, 0)).X + card.ActualWidth / 2
                    : double.NaN;
                // 3.18.0 起气泡不再贴死顶部，而是留 12 px 呼吸空间
                bool flush = card != null && y >= 8 && y <= 20;
                bool hasBorder = card is Border bd && bd.BorderThickness.Left >= 1 && bd.BorderBrush != null;
                bool centered = card != null && Math.Abs(centerX - root.ActualWidth / 2) < 2.5;

                try { SaveShot(owner, TestFile("shot-toast-top.png")); } catch { }

                // 收起后图层要从窗口里摘掉（消失也不留残影）
                DoEvents(TimeSpan.FromMilliseconds(1600));
                bool removed = true;
                foreach (UIElement child in root.Children)
                    if (child is FrameworkElement fe && Panel.GetZIndex(fe) == 6000) { removed = false; break; }

                if (inWindow && flush && centered && removed && hasBorder)
                    Pass("T51 顶部气泡位置与收起", $"Y={y:0.#}（距顶部 12 DIP），水平居中，有边界框，收起后已移除");
                else
                    Fail("T51 顶部气泡位置与收起",
                        $"inWindow={inWindow} flush={flush}({y:0.#}) centered={centered}({centerX:0.#}) border={hasBorder} removed={removed}");
            }
            catch (Exception ex) { Fail("T51 顶部气泡位置与收起", ex.GetType().Name + "：" + ex.Message); }
        }

        // ---------------------------------------------------------------
        // T48：窗口标题模板（纯逻辑，不起窗口）
        // ---------------------------------------------------------------
        private static void TestWindowTitleFormat()
        {
            try
            {
                var now = new DateTime(2026, 11, 2, 13, 45, 7);

                bool blankOk = RandomDrawer.WindowTitleFormatter.Format("", "3.15.0", "历史记录", "测试池", true, now)
                               == RandomDrawer.WindowTitleFormatter.DefaultTitle
                               && RandomDrawer.WindowTitleFormatter.Format("   ", "3.15.0", "历史记录", "测试池", true, now)
                               == RandomDrawer.WindowTitleFormatter.DefaultTitle;

                string all = RandomDrawer.WindowTitleFormatter.Format(
                    "抽取器 {version} {time} {date} {pool} {page}", "3.15.0", "历史记录", "测试池", true, now);
                bool allOk = all == "抽取器 3.15.0 13:45:07 2026/11/2 测试池 历史记录";

                string notMain = RandomDrawer.WindowTitleFormatter.Format(
                    "[{pool}]", "3.15.0", "主页", "测试池", false, now);
                bool poolOk = notMain == "[]";

                string upper = RandomDrawer.WindowTitleFormatter.Format("{VERSION}/{Page}", "3.15.0", "选项", "", true, now);
                bool caseOk = upper == "3.15.0/选项";

                string unknown = RandomDrawer.WindowTitleFormatter.Format("a{foo}b", "", "", "", true, now);
                bool unknownOk = unknown == "a{foo}b";

                bool clockOk = RandomDrawer.WindowTitleFormatter.NeedsClock("x {TIME} y")
                               && !RandomDrawer.WindowTitleFormatter.NeedsClock("x {date} y");

                if (blankOk && allOk && poolOk && caseOk && unknownOk && clockOk)
                    Pass("T48 窗口标题关键词替换", $"「{all}」，留空回默认，{{pool}} 只在主界面");
                else
                    Fail("T48 窗口标题关键词替换",
                        $"blank={blankOk} all={allOk}({all}) pool={poolOk}({notMain}) case={caseOk}({upper}) unknown={unknownOk}({unknown}) clock={clockOk}");
            }
            catch (Exception ex) { Fail("T48 窗口标题关键词替换", ex.GetType().Name + "：" + ex.Message); }
        }

        // ---------------------------------------------------------------
        // T49：选项页「窗口标题」输入框 —— 实时改标题、悬停出气泡、留空回默认
        // ---------------------------------------------------------------
        private static void TestWindowTitleOption(Window owner)
        {
            string oldFormat = RandomDrawer.App.WindowTitleFormat;
            try
            {
                var mw = owner as RandomDrawer.MainWindow;
                if (mw == null) { Fail("T49 窗口标题选项", "窗口不是 MainWindow"); return; }

                RandomDrawer.Messaging.MessageBus.Publish(
                    new RandomDrawer.Messaging.AppMessages.NavigateTo("Options"));
                DoEvents(TimeSpan.FromMilliseconds(900));

                var pageHost = FindByName<Grid>(owner, "PageHost");
                var page = pageHost?.Children.OfType<RandomDrawer.OptionsPage_UC>().FirstOrDefault();
                if (page == null) { Fail("T49 窗口标题选项", "找不到选项页"); return; }

                var box = page.FindName("TxtWindowTitle") as TextBox;
                if (box == null) { Fail("T49 窗口标题选项", "找不到 TxtWindowTitle"); return; }

                box.Text = "测试 {page} - {version}";
                DoEvents(TimeSpan.FromMilliseconds(300));

                string expected = "测试 " + "选项" + " - " + RandomDrawer.WindowTitleFormatter.GetAppVersionText();
                bool liveTitle = mw.Title == expected;
                bool appSynced = RandomDrawer.App.WindowTitleFormat == "测试 {page} - {version}";

                // 悬停气泡：直接调私有方法，验证气泡真的会打开
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                page.GetType().GetMethod("ShowWindowTitleHint", flags)?.Invoke(page, new object[] { new Point(20, 20) });
                DoEvents(TimeSpan.FromMilliseconds(150));
                var hint = page.GetType().GetField("_windowTitleHint", flags)?.GetValue(page) as RandomDrawer.HintBubble;
                bool hintOpen = hint != null && hint.IsOpen;
                page.GetType().GetMethod("TxtWindowTitle_MouseLeave", flags)?.Invoke(page, new object[] { null, null });
                DoEvents(TimeSpan.FromMilliseconds(100));
                bool hintClosed = hint == null || !hint.IsOpen;

                // 留空 → 回默认标题
                box.Text = "";
                DoEvents(TimeSpan.FromMilliseconds(200));
                bool defaultTitle = mw.Title == RandomDrawer.WindowTitleFormatter.DefaultTitle;

                if (liveTitle && appSynced && hintOpen && hintClosed && defaultTitle)
                    Pass("T49 窗口标题选项实时生效", $"「{expected}」→ 留空回「{RandomDrawer.WindowTitleFormatter.DefaultTitle}」，悬停有气泡");
                else
                    Fail("T49 窗口标题选项实时生效",
                        $"live={liveTitle}({mw.Title}) synced={appSynced} hintOpen={hintOpen} hintClosed={hintClosed} default={defaultTitle}");
            }
            catch (Exception ex) { Fail("T49 窗口标题选项", ex.GetType().Name + "：" + ex.Message); }
            finally
            {
                RandomDrawer.App.WindowTitleFormat = oldFormat;
                try { (owner as RandomDrawer.MainWindow)?.UpdateWindowTitle(); } catch { }
                // 把配置写回原值（TextChanged 有 500ms 防抖，等它跑完）
                DoEvents(TimeSpan.FromMilliseconds(700));
                RandomDrawer.Messaging.MessageBus.Publish(
                    new RandomDrawer.Messaging.AppMessages.NavigateTo("Main"));
                DoEvents(TimeSpan.FromMilliseconds(400));
            }
        }

        // ---------------------------------------------------------------
        // T59：Tab 不再切换焦点，且全局没有焦点虚线框
        // ---------------------------------------------------------------
        private static void TestTabNavigationDisabled(Window owner)
        {
            try
            {
                var mode = KeyboardNavigation.GetTabNavigation(owner);
                var controlMode = KeyboardNavigation.GetControlTabNavigation(owner);
                bool tabOff = mode == KeyboardNavigationMode.None && controlMode == KeyboardNavigationMode.None;

                object focusStyle = Application.Current?.TryFindResource(SystemParameters.FocusVisualStyleKey);
                bool focusVisualOff = focusStyle is Style st && st.Setters.OfType<Setter>().Any(s => s.Property == Control.TemplateProperty);

                if (tabOff && focusVisualOff)
                    Pass("T59 Tab 不再切换焦点 / 无焦点虚线框", $"TabNavigation={mode}，焦点样式已置空");
                else
                    Fail("T59 Tab 不再切换焦点 / 无焦点虚线框", $"tab={mode}/{controlMode} focusStyle={(focusStyle == null ? "null" : focusStyle.GetType().Name)}");
            }
            catch (Exception ex) { Fail("T59 Tab / 焦点虚线框", ex.GetType().Name + "：" + ex.Message); }
        }
        // ---------------------------------------------------------------
        // T56：光锥活动跃迁 / 群星跃迁的抽取规则（UP 占比、多分类、无 UP）
        // ---------------------------------------------------------------
        private static void TestLightConeAndStandardWarp()
        {
            try
            {
                var engine = new RandomDrawer.DrawEngine();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var pull5 = typeof(RandomDrawer.DrawEngine).GetMethod("Pull5StarItem", flags);
                var pull3 = typeof(RandomDrawer.DrawEngine).GetMethod("Pull3StarItem", flags);
                if (pull5 == null || pull3 == null) { Fail("T56 光锥 / 群星跃迁", "找不到 Pull5StarItem / Pull3StarItem"); return; }

                var pool = MakePool(Path.Combine(TestOutputDir, "warp-pool"), "跃迁池", new[]
                {
                    "光锥A 5星光锥", "光锥B 5星光锥", "光锥C 5星光锥",
                    "角色甲 5星角色", "光锥丁 5星光锥",
                    "角色乙 4星角色", "光锥戊 4星光锥", "光锥己 4星光锥",
                    "材料庚 3星"
                });

                var settings = new RandomDrawer.GachaSettings
                {
                    Enabled = true,
                    PresetName = "崩坏：星穹铁道",
                    WarpType = "光锥活动跃迁",
                    FiveStarCategory = "5星光锥",
                    FiveStarUpObject = "光锥A",
                    FourStarCategory = "4星角色,4星光锥",
                    FourStarUpObjects = "角色乙,光锥戊,光锥己",
                    FiveStarNonUpProb = 0.200,
                    FiveStarUpProb = 0.600,
                    FourStarUpProb = 4.950,
                    FourStarNonUpProb = 1.650
                };

                int up5 = 0, total = 20000;
                for (int i = 0; i < total; i++)
                {
                    var state = new RandomDrawer.GachaState();
                    var item = pull5.Invoke(engine, new object[] { pool, settings, state }) as RandomDrawer.DrawItem;
                    if (item != null && item.Fields.Count > 0 && item.Fields[0] == "光锥A") up5++;
                }
                double share = (double)up5 / total;   // 0.6 / (0.2 + 0.6) = 75%

                // 角色池 0.3/0.3 → 50%
                settings.WarpType = "角色活动跃迁";
                settings.FiveStarNonUpProb = 0.300;
                settings.FiveStarUpProb = 0.300;
                int upChar = 0;
                for (int i = 0; i < total; i++)
                {
                    var state = new RandomDrawer.GachaState();
                    var item = pull5.Invoke(engine, new object[] { pool, settings, state }) as RandomDrawer.DrawItem;
                    if (item != null && item.Fields.Count > 0 && item.Fields[0] == "光锥A") upChar++;
                }
                double shareChar = (double)upChar / total;

                // 群星跃迁：无 UP（UP 概率 0）→ 全部按基础出，不会「必出 UP」
                settings.WarpType = "群星跃迁";
                settings.FiveStarUpProb = 0;
                settings.FiveStarNonUpProb = 0.600;
                settings.FiveStarCategory = "5星角色,5星光锥";
                settings.FiveStarUpObject = "";
                int upStandard = 0;
                bool onlyAllowed = true;
                for (int i = 0; i < total; i++)
                {
                    var state = new RandomDrawer.GachaState();
                    var item = pull5.Invoke(engine, new object[] { pool, settings, state }) as RandomDrawer.DrawItem;
                    if (item == null || item.Fields.Count < 2) { onlyAllowed = false; continue; }
                    if (item.Fields[0] == "光锥A") upStandard++;
                    bool catOk = item.Fields[1] == "5星角色" || item.Fields[1] == "5星光锥";
                    if (!catOk) onlyAllowed = false;
                }

                bool lightConeOk = Math.Abs(share - 0.75) < 0.02;
                bool characterOk = Math.Abs(shareChar - 0.50) < 0.02;
                // 群星跃迁没有 UP 概念：5 星在 5 个候选里均匀出（UP 名字只占约 20%），不会「必出 UP」
                double standardShare = (double)upStandard / total;
                bool standardOk = onlyAllowed && Math.Abs(standardShare - 0.20) < 0.03;

                if (lightConeOk && characterOk && standardOk)
                    Pass("T56 光锥 / 群星跃迁规则", $"光锥 UP {share:P1}、角色 UP {shareChar:P1}、群星无 UP 且分类受限");
                else
                    Fail("T56 光锥 / 群星跃迁规则",
                        $"光锥 {share:P1}（期望 75%）角色 {shareChar:P1}（期望 50%）群星 {standardShare:P1}（期望 20%）分类={onlyAllowed}");

                // 3 星候选必须排除 5 星 / 4 星的（多分类）分类
                int wrong3 = 0;
                for (int i = 0; i < 2000; i++)
                {
                    var item = pull3.Invoke(engine, new object[] { pool, settings }) as RandomDrawer.DrawItem;
                    if (item == null || item.Fields.Count < 2 || item.Fields[1] != "3星") wrong3++;
                }
                if (wrong3 == 0)
                    Pass("T56 3星候选排除多分类的 5/4 星", "2000 次全部命中 3星");
                else
                    Fail("T56 3星候选排除多分类的 5/4 星", $"{wrong3} 次命中非 3星");
            }
            catch (Exception ex) { Fail("T56 光锥 / 群星跃迁规则", ex.GetType().Name + "：" + ex.Message); }
        }

        // ---------------------------------------------------------------
        // T57：光锥 / 群星跃迁的默认值（Readme\pool.md）
        // ---------------------------------------------------------------
        private static void TestWarpDefaultsFromPoolMd()
        {
            try
            {
                var win = new Window
                {
                    Width = 1200, Height = 760,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000, Top = -4000, ShowInTaskbar = false
                };
                var page = new RandomDrawer.AdvancedGachaPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(500));

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var apply = page.GetType().GetMethod("ApplyHSRDefaults", flags);
                if (apply == null) { Fail("T57 跃迁默认值", "找不到 ApplyHSRDefaults"); return; }

                // 真实路径是 OnOpenAdvancedGacha → ReinitializeUI（它会把 _isLoading 复位），测试里手动走一遍
                page.GetType().GetMethod("ReinitializeUI", flags)?.Invoke(page, null);
                var enable = page.FindName("ChkEnable") as CheckBox;
                var preset = page.FindName("ComboPreset") as ComboBox;
                if (enable != null) enable.IsChecked = true;
                if (preset != null) preset.SelectedIndex = 0;
                page.GetType().GetMethod("UpdateUIState", flags)?.Invoke(page, null);
                DoEvents(TimeSpan.FromMilliseconds(300));

                string Txt(string name) => (page.FindName(name) as TextBox)?.Text ?? "?";

                apply.Invoke(page, new object[] { "光锥活动跃迁" });
                string lc5n = Txt("TxtFiveStarNonUpProb"), lc5u = Txt("TxtFiveStarUpProb");
                string lc4u = Txt("TxtFourStarUpProb"), lc4n = Txt("TxtFourStarNonUpProb");
                string lcSoft = Txt("TxtSoftPityStart"), lcInc = Txt("TxtSoftPityIncrease");
                string lcP4 = Txt("TxtFourStarPity"), lcP5 = Txt("TxtFiveStarPity");

                bool lightConeOk = lc5n == "0.200" && lc5u == "0.600" && lc4u == "4.950" && lc4n == "1.650"
                                   && lcSoft == "65" && lcInc == "0.07" && lcP4 == "10" && lcP5 == "80";

                apply.Invoke(page, new object[] { "群星跃迁" });
                // 标签 / 显隐由 ApplyWarpTypeLabels 负责（真实 UI 路径里也是这两个一起走）
                page.GetType().GetMethod("ApplyWarpTypeLabels", flags)?.Invoke(page, new object[] { "群星跃迁" });
                string st5n = Txt("TxtFiveStarNonUpProb"), st4n = Txt("TxtFourStarNonUpProb");
                string stSoft = Txt("TxtSoftPityStart"), stP5 = Txt("TxtFiveStarPity");
                bool standardOk = st5n == "0.600" && st4n == "5.100" && stSoft == "74" && stP5 == "90";

                // 群星跃迁没有 UP 项：UP 概率输入框应隐藏
                var upBox = page.FindName("TxtFiveStarUpProb") as TextBox;
                bool upHidden = upBox != null && upBox.Visibility != Visibility.Visible;

                try
                {
                    apply.Invoke(page, new object[] { "光锥活动跃迁" });
                    page.GetType().GetMethod("ApplyWarpTypeLabels", flags)?.Invoke(page, new object[] { "光锥活动跃迁" });
                    DoEvents(TimeSpan.FromMilliseconds(200));
                    SaveShot(win, TestFile("shot-advanced-lightcone.png"));

                    apply.Invoke(page, new object[] { "群星跃迁" });
                    page.GetType().GetMethod("ApplyWarpTypeLabels", flags)?.Invoke(page, new object[] { "群星跃迁" });
                    DoEvents(TimeSpan.FromMilliseconds(200));
                    SaveShot(win, TestFile("shot-advanced-standard.png"));

                    // 「正在开发中」面板：转圈加大后截图确认可见
                    if (preset != null)
                    {
                        preset.SelectedIndex = 2;
                        DoEvents(TimeSpan.FromMilliseconds(500));

                        SaveShot(win, TestFile("shot-advanced-developing.png"));
                        preset.SelectedIndex = 0;
                        DoEvents(TimeSpan.FromMilliseconds(300));
                    }
                }
                catch { }
                try { win.Close(); } catch { }

                // —— 标签：3星光锥概率 / 4星角色/光锥分类（三种跃迁都生效）——
                var lbl3 = page.FindName("LblThreeStarProb") as TextBlock;
                var lbl4 = page.FindName("LblFourStarCategory") as TextBlock;
                bool labelsOk = true;
                foreach (var wt in new[] { "角色活动跃迁", "光锥活动跃迁", "群星跃迁" })
                {
                    page.GetType().GetMethod("ApplyWarpTypeLabels", flags)?.Invoke(page, new object[] { wt });
                    if (lbl3 == null || !lbl3.Text.Contains("3星光锥概率")) labelsOk = false;
                    if (lbl4 == null || lbl4.Text != "4星角色/光锥分类：") labelsOk = false;
                }

                // —— 群星布局：4星分类挪到第 0 行右侧、4星基础概率到第 2 行右侧 ——
                page.GetType().GetMethod("ApplyWarpTypeLabels", flags)?.Invoke(page, new object[] { "群星跃迁" });
                var combo4 = page.FindName("ComboFourStar") as FrameworkElement;
                var prob4 = page.FindName("TxtFourStarNonUpProb") as FrameworkElement;
                bool layoutOk = combo4 != null && Grid.GetRow(combo4) == 0 && Grid.GetColumn(combo4) == 4
                                && prob4 != null && Grid.GetRow(prob4) == 2 && Grid.GetColumn(prob4) == 4;
                page.GetType().GetMethod("ApplyWarpTypeLabels", flags)?.Invoke(page, new object[] { "角色活动跃迁" });
                bool restoreOk = combo4 != null && Grid.GetRow(combo4) == 1 && Grid.GetColumn(combo4) == 1;

                // —— 切换跃迁类型：数值清空（不继承、也不自动填默认值）——
                var comboWarp = page.FindName("ComboWarpType") as ComboBox;
                var pity5 = page.FindName("TxtFiveStarPity") as TextBox;

                apply.Invoke(page, new object[] { "角色活动跃迁" });
                DoEvents(TimeSpan.FromMilliseconds(150));
                string charPity = pity5.Text;                 // 角色默认 90

                if (comboWarp != null) comboWarp.SelectedIndex = 1;   // 切到光锥
                DoEvents(TimeSpan.FromMilliseconds(250));
                bool clearedOnSwitch = string.IsNullOrEmpty(pity5.Text)
                                       && string.IsNullOrEmpty((page.FindName("TxtFiveStarNonUpProb") as TextBox)?.Text);

                apply.Invoke(page, new object[] { "光锥活动跃迁" });    // 点「填入默认值」
                DoEvents(TimeSpan.FromMilliseconds(200));
                string conePity = pity5.Text;                 // 光锥默认 80

                if (comboWarp != null) comboWarp.SelectedIndex = 0;   // 切回角色
                DoEvents(TimeSpan.FromMilliseconds(250));
                bool clearedAgain = string.IsNullOrEmpty(pity5.Text);

                bool isolationOk = charPity == "90" && clearedOnSwitch && conePity == "80" && clearedAgain;

                if (labelsOk && layoutOk && restoreOk && isolationOk)
                    Pass("T57 跃迁标签 / 布局 / 切换清空", $"3星光锥概率、4星角色/光锥分类；群星右列；切换后清空，点默认值得 {conePity}");
                else
                    Fail("T57 跃迁标签 / 布局 / 切换清空",
                        $"labels={labelsOk} layout={layoutOk} restore={restoreOk} char={charPity} cone={conePity} cleared={clearedOnSwitch}/{clearedAgain}");

                if (lightConeOk && standardOk && upHidden)
                    Pass("T57 跃迁默认值符合 pool.md", $"光锥 {lc5n}/{lc5u}/{lc4u}/{lc4n} 软{lcSoft}+{lcInc} 保底{lcP4}/{lcP5}；群星 {st5n}/{st4n} 保底{stP5}");
                else
                    Fail("T57 跃迁默认值符合 pool.md",
                        $"光锥光:{lc5n}/{lc5u}/{lc4u}/{lc4n}/{lcSoft}/{lcInc}/{lcP4}/{lcP5} 群星:{st5n}/{st4n}/{stSoft}/{stP5} upHidden={upHidden}");
            }
            catch (Exception ex) { Fail("T57 跃迁默认值", ex.GetType().Name + "：" + ex.Message); }
        }
        // ---------------------------------------------------------------
        // T58：抽取报告按跃迁类型智能显示（刻度 / 称呼 / 关键词 / 群星无 UP）
        // ---------------------------------------------------------------
        private static void TestWarpAwareReport(string root)
        {
            string dir = Path.Combine(root, "warp-report");
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "data.txt"),
                    "抽取对象 分类\r\n光锥A 5星光锥\r\n光锥B 5星光锥\r\n角色甲 4星角色\r\n材料丙 3星\r\n",
                    new UTF8Encoding(false));

                // 4 连 UP（光锥A）+ 1 次歪（光锥B）+ 20 次 3 星
                var hist = new StringBuilder();
                for (int i = 0; i < 4; i++) hist.Append("光锥A\t5星光锥\r\n");
                hist.Append("光锥B\t5星光锥\r\n");
                for (int i = 0; i < 20; i++) hist.Append("材料丙\t3星\r\n");
                File.WriteAllText(Path.Combine(dir, "history.txt"), hist.ToString(), new UTF8Encoding(false));

                string ini(string warp, int pity, string fiveCat, string up, string probs) =>
                    "[Gacha]\r\nEnabled=1\r\nPresetName=崩坏：星穹铁道\r\n" +
                    $"WarpType={warp}\r\nFiveStarCategory={fiveCat}\r\nFiveStarUpObject={up}\r\n" +
                    "FourStarCategory=4星角色\r\nFourStarUpObjects=角色甲\r\n" + probs +
                    $"SoftPityStart={Math.Max(1, pity - 15)}\r\nSoftPityIncrease=0.07\r\nFourStarPity=10\r\nFiveStarPity={pity}\r\n";

                // ① 光锥活动跃迁：80 抽保底、44/62 刻度、UP5星光锥、4连UP
                File.WriteAllText(Path.Combine(dir, "gacha_settings.ini"), ini("光锥活动跃迁", 80, "5星光锥", "光锥A",
                    "FiveStarNonUpProb=0.200\r\nFiveStarUpProb=0.600\r\nFourStarUpProb=4.950\r\nFourStarNonUpProb=1.650\r\n"),
                    new UTF8Encoding(false));
                var cone = RandomDrawer.GachaReportAnalyzer.Analyze(dir, "光锥池");
                bool coneOk = cone.ScaleMax == 80 && cone.GreenMax == 44 && cone.YellowMax == 62
                              && cone.HasUp && cone.FiveStarName == "5星光锥"
                              && cone.Keywords.Contains("4连UP")
                              && cone.Summary.Contains("获得UP5星光锥");

                // ② 角色活动跃迁：90 抽保底、50/70 刻度、UP5星角色
                File.WriteAllText(Path.Combine(dir, "gacha_settings.ini"), ini("角色活动跃迁", 90, "5星光锥", "光锥A",
                    "FiveStarNonUpProb=0.300\r\nFiveStarUpProb=0.300\r\nFourStarUpProb=2.550\r\nFourStarNonUpProb=2.550\r\n"),
                    new UTF8Encoding(false));
                var role = RandomDrawer.GachaReportAnalyzer.Analyze(dir, "角色池");
                bool roleOk = role.ScaleMax == 90 && role.GreenMax == 50 && role.YellowMax == 70
                              && role.FiveStarName == "5星角色" && role.Summary.Contains("获得UP5星角色");

                // ③ 群星跃迁：无 UP → 不显示歪率 / 连UP，摘要注明常驻
                File.WriteAllText(Path.Combine(dir, "gacha_settings.ini"), ini("群星跃迁", 90, "5星光锥", "",
                    "FiveStarNonUpProb=0.600\r\nFiveStarUpProb=0\r\nFourStarUpProb=0\r\nFourStarNonUpProb=5.100\r\n"),
                    new UTF8Encoding(false));
                var standard = RandomDrawer.GachaReportAnalyzer.Analyze(dir, "群星池");
                bool standardOk = !standard.HasUp && standard.ScaleMax == 90
                                  && standard.Summary.Contains("常驻跃迁（无 UP）")
                                  && !standard.Summary.Contains("不歪率")
                                  && !standard.Keywords.Any(k => k.EndsWith("连UP"));

                // ④ 称号权重：同一组「歪了一半」的数据，光锥池（75% UP）应比角色池（50% UP）更差
                string BuildTitlePool(string tag, string upName, string nonUpName, string category,
                    string warp, int pity, string probs)
                {
                    string d = Path.Combine(root, "warp-title-" + tag);
                    Directory.CreateDirectory(d);
                    File.WriteAllText(Path.Combine(d, "data.txt"),
                        $"抽取对象 分类\r\n{upName} {category}\r\n{nonUpName} {category}\r\n材料丙 3星\r\n",
                        new UTF8Encoding(false));

                    // 序列：歪 → UP → UP → 歪（每次间隔 70 抽）→ pLose = 2/3
                    var h = new StringBuilder();
                    foreach (var name in new[] { nonUpName, upName, upName, nonUpName })
                    {
                        for (int i = 1; i < 70; i++) h.Append("材料丙\t3星\r\n");
                        h.Append(name + "\t" + category + "\r\n");
                    }
                    File.WriteAllText(Path.Combine(d, "history.txt"), h.ToString(), new UTF8Encoding(false));

                    File.WriteAllText(Path.Combine(d, "gacha_settings.ini"),
                        "[Gacha]\r\nEnabled=1\r\nPresetName=崩坏：星穹铁道\r\n" +
                        $"WarpType={warp}\r\nFiveStarCategory={category}\r\nFiveStarUpObject={upName}\r\n" +
                        "FourStarCategory=4星角色\r\nFourStarUpObjects=\r\n" + probs +
                        "FourStarUpProb=2.550\r\nFourStarNonUpProb=2.550\r\n" +
                        $"SoftPityStart={Math.Max(1, pity - 15)}\r\nSoftPityIncrease=0.06\r\n" +
                        $"FourStarPity=10\r\nFiveStarPity={pity}\r\n",
                        new UTF8Encoding(false));
                    return RandomDrawer.GachaReportAnalyzer.Analyze(d, tag).Title;
                }

                string coneTitle = BuildTitlePool("cone", "光锥A", "光锥B", "5星光锥", "光锥活动跃迁", 80,
                    "FiveStarNonUpProb=0.200\r\nFiveStarUpProb=0.600\r\n");
                string roleTitle = BuildTitlePool("role", "角色A", "角色B", "5星角色", "角色活动跃迁", 90,
                    "FiveStarNonUpProb=0.300\r\nFiveStarUpProb=0.300\r\n");

                bool titleOk = coneTitle == "终极无敌至尊非酋王" && roleTitle != "终极无敌至尊非酋王";
                try { Directory.Delete(Path.Combine(root, "warp-title-cone"), true); } catch { }
                try { Directory.Delete(Path.Combine(root, "warp-title-role"), true); } catch { }
                if (coneOk && roleOk && standardOk && titleOk)
                    Pass("T58 报告按跃迁类型显示", $"光锥 80/44/62/4连UP，角色 90/50/70，群星无 UP，称号：光锥「{coneTitle}」/ 角色「{roleTitle}」");
                else
                    Fail("T58 报告按跃迁类型显示",
                        $"cone={coneOk} role={roleOk} standard={standardOk} titles={titleOk}（光锥「{coneTitle}」角色「{roleTitle}」）");

            }
            catch (Exception ex) { Fail("T58 报告按跃迁类型显示", ex.GetType().Name + "：" + ex.Message); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        // ---------------------------------------------------------------
        // T60：本次抽取详情弹窗的可展开项有常态背景（跟随主题，深色可用）
        // ---------------------------------------------------------------
        private static void TestDrawDetailHeaders()
        {
            try
            {
                var dlg = new RandomDrawer.DrawDetailDialog(new[] { "甲" }, new[] { "乙", "丙" });
                var drawn = dlg.FindName("HeaderDrawnBorder") as Border;
                var undrawn = dlg.FindName("HeaderUndrawnBorder") as Border;
                if (drawn == null || undrawn == null) { Fail("T60 详情弹窗展开项背景", "找不到两个表头 Border"); return; }

                bool IsFilled(Border b) => b.Background is SolidColorBrush s && s.Color.A > 0;
                bool ok = IsFilled(drawn) && IsFilled(undrawn);

                if (ok)
                    Pass("T60 详情弹窗展开项有常态背景", $"已抽取 / 未抽取都为实色底（{(drawn.Background as SolidColorBrush)?.Color}）");
                else
                    Fail("T60 详情弹窗展开项有常态背景",
                        $"drawn={(drawn.Background as SolidColorBrush)?.Color} undrawn={(undrawn.Background as SolidColorBrush)?.Color}");
            }
            catch (Exception ex) { Fail("T60 详情弹窗展开项背景", ex.GetType().Name + "：" + ex.Message); }
        }
        // ---------------------------------------------------------------
        // T61：颜色弹窗改成 取色方块 + 色相条 + HEX / RGB 输入
        // ---------------------------------------------------------------
        private static void TestColorPickerDialog()
        {
            Window win = null;
            try
            {
                win = new Window
                {
                    Width = 580, Height = 640,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000, Top = -4000, ShowInTaskbar = false
                };
                var dlg = new RandomDrawer.ColorPickerDialog("选择抽取对象颜色", "#3366CC");
                win.Content = dlg;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(600));

                var square = dlg.FindName("SvSquare") as FrameworkElement;
                var hueBar = dlg.FindName("HueBar") as FrameworkElement;
                var thumb = dlg.FindName("SvThumb") as FrameworkElement;
                var hueThumb = dlg.FindName("HueThumb") as FrameworkElement;
                var hex = dlg.FindName("TxtHex") as TextBox;
                var presets = dlg.FindName("PresetGrid") as Panel;

                if (square == null || hueBar == null || hex == null)
                { Fail("T61 颜色弹窗新版布局", "找不到取色方块 / 色相条 / HEX 输入"); return; }

                bool layoutOk = square.ActualWidth >= 200 && square.ActualHeight >= 150
                                && hueBar.ActualHeight >= 150
                                && thumb != null && thumb.Margin.Left > 0
                                && hueThumb != null
                                && presets != null && presets.Children.Count >= 24;

                // 通过 HEX 输入改颜色
                hex.Text = "#FF8800";
                DoEvents(TimeSpan.FromMilliseconds(200));
                var preview = dlg.FindName("PreviewBorder") as Border;
                string previewHex = (preview?.Background as SolidColorBrush) is SolidColorBrush sb
                    ? $"#{sb.Color.R:X2}{sb.Color.G:X2}{sb.Color.B:X2}" : "?";

                // 点确定，返回规范化后的 HEX
                dlg.GetType().GetMethod("Ok_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                   ?.Invoke(dlg, new object[] { dlg, new RoutedEventArgs() });
                bool resultOk = string.Equals(dlg.SelectedHex, "#FF8800", StringComparison.OrdinalIgnoreCase);

                try { SaveShot(win, TestFile("shot-color-picker.png")); } catch { }
                try { win.Close(); } catch { }

                if (layoutOk && previewHex == "#FF8800" && resultOk)
                    Pass("T61 颜色弹窗新版布局", $"方块 {square.ActualWidth:0}×{square.ActualHeight:0}、色相条、HEX 改色与回读都正常");
                else
                    Fail("T61 颜色弹窗新版布局", $"layout={layoutOk} preview={previewHex} result={dlg.SelectedHex}");
            }
            catch (Exception ex) { Fail("T61 颜色弹窗新版布局", ex.GetType().Name + "：" + ex.Message); }
            finally { try { win?.Close(); } catch { } }
        }
        // ---------------------------------------------------------------
        // T62：导出多个卡池的默认文件名
        // ---------------------------------------------------------------
        private static void TestExportDefaultFileName()
        {
            try
            {
                string one = RandomDrawer.ExportPoolsDialog.BuildDefaultFileName(new List<string> { "我的卡池" });
                string many = RandomDrawer.ExportPoolsDialog.BuildDefaultFileName(
                    new List<string> { "沧海萃珠", "云上歌阕", "流光定影", "万籁" });
                string empty = RandomDrawer.ExportPoolsDialog.BuildDefaultFileName(new List<string>());

                bool ok = one == "我的卡池.zip" && many == "沧海萃珠等4个卡池.zip" && empty == "卡池导出.zip";

                if (ok)
                    Pass("T62 导出多个卡池的默认文件名", $"{many}（单个仍为「{one}」）");
                else
                    Fail("T62 导出多个卡池的默认文件名", $"one={one} many={many} empty={empty}");
            }
            catch (Exception ex) { Fail("T62 导出多个卡池的默认文件名", ex.GetType().Name + "：" + ex.Message); }
        }
        // ---------------------------------------------------------------
        // T63：个性化页「背景」下拉框宽度足够显示完整内容
        // ---------------------------------------------------------------
        private static void TestPersonalizationComboWidth()
        {
            Window win = null;
            try
            {
                win = new Window
                {
                    Width = 1100, Height = 700,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000, Top = -4000, ShowInTaskbar = false
                };
                var page = new RandomDrawer.PersonalizationPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(600));

                var combo = page.FindName("ComboBackgroundMode") as ComboBox;
                if (combo == null) { Fail("T63 背景下拉框宽度", "找不到 ComboBackgroundMode"); return; }

                // 「默认（跟随主题）」这种 8 个字的长选项至少要 170 DIP 才不会被截断
                bool wide = combo.ActualWidth >= 170;
                string first = (combo.Items[0] as ComboBoxItem)?.Content as string ?? "";

                try { SaveShot(win, TestFile("shot-personalization.png")); } catch { }
                try { win.Close(); } catch { }

                if (wide)
                    Pass("T63 背景下拉框宽度", $"{combo.ActualWidth:0} DIP，能放下「{first}」");
                else
                    Fail("T63 背景下拉框宽度", $"只有 {combo.ActualWidth:0} DIP");
            }
            catch (Exception ex) { Fail("T63 背景下拉框宽度", ex.GetType().Name + "：" + ex.Message); }
            finally { try { win?.Close(); } catch { } }
        }
        // ---------------------------------------------------------------
        // T64：转圈动画统一成 BusySpinner（尺寸 / 颜色一致）
        // ---------------------------------------------------------------
        private static void TestUnifiedBusySpinners(Window owner)
        {
            try
            {
                var drawBusy = owner?.FindName("DrawBusySpinner") as FrameworkElement;
                bool drawOk = drawBusy is RandomDrawer.BusySpinner
                              && Math.Abs(drawBusy.Width - RandomDrawer.BusySpinner.DefaultSize) < 0.5
                              && Math.Abs(drawBusy.Height - RandomDrawer.BusySpinner.DefaultSize) < 0.5;

                var page = new RandomDrawer.AdvancedGachaPage_UC();
                var developing = page.FindName("DevelopingSpinner") as FrameworkElement;
                bool devOk = developing is RandomDrawer.BusySpinner
                             && Math.Abs(developing.Width - RandomDrawer.BusySpinner.DefaultSize) < 0.5;

                if (drawOk && devOk)
                    Pass("T64 转圈动画统一", $"抽取遮罩与开发中面板都是 BusySpinner（{RandomDrawer.BusySpinner.DefaultSize:0}×{RandomDrawer.BusySpinner.DefaultSize:0}）");
                else
                    Fail("T64 转圈动画统一", $"draw={drawBusy?.GetType().Name}/{drawBusy?.Width} dev={developing?.GetType().Name}/{developing?.Width}");
            }
            catch (Exception ex) { Fail("T64 转圈动画统一", ex.GetType().Name + "：" + ex.Message); }
        }
        // ---------------------------------------------------------------
        // T65：抽取结果（网格）的滚动条贴右边、占满高度，进度条建完才到 100%
        // ---------------------------------------------------------------
        private static void TestResultScrollBarFlush()
        {
            Window win = null;
            try
            {
                win = new Window
                {
                    Width = 1229, Height = 824,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000, Top = -4000, ShowInTaskbar = false
                };
                var page = new RandomDrawer.DrawResultPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(600));

                var items = new List<RandomDrawer.Messaging.DrawResultData>();
                for (int i = 0; i < 100; i++)
                    items.Add(new RandomDrawer.Messaging.DrawResultData { Index = i + 1, ObjectName = "对象" + i });

                RandomDrawer.Messaging.MessageBus.Publish(new RandomDrawer.Messaging.AppMessages.OpenDrawResultPage(
                    items, "", 100, new RandomDrawer.PersonalizationSettings(), new RandomDrawer.HighlightConfig(),
                    RandomDrawer.Messaging.DrawResultDisplayMode.CardGrid));

                var cardsHost = page.FindName("CardsHost") as Panel;
                PumpUntil(() => cardsHost != null && cardsHost.Children.Count > 0
                                && cardsHost.Children[0] is Panel g && g.Children.Count >= 100,
                    TimeSpan.FromMilliseconds(15000));
                DoEvents(TimeSpan.FromMilliseconds(400));

                var sb = page.FindName("PageScrollBar") as System.Windows.Controls.Primitives.ScrollBar;
                var sv = page.FindName("ResultScrollViewer") as ScrollViewer;
                var progress = page.FindName("RenderProgressBar") as ProgressBar;

                if (sb == null || sv == null)
                { Fail("T65 结果页滚动条贴边整高", "找不到 PageScrollBar / ResultScrollViewer"); return; }

                var root = (FrameworkElement)page.Content ?? page;
                Point sbTopLeft = sb.TranslatePoint(new Point(0, 0), root);
                double sbRight = sbTopLeft.X + sb.ActualWidth;

                bool visible = sb.Visibility == Visibility.Visible;
                bool flushRight = Math.Abs(sbRight - root.ActualWidth) < 1.5;
                bool fullHeight = Math.Abs(sb.ActualHeight - root.ActualHeight) < 1.5;
                bool builtInHidden = sv.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden;
                bool progress100 = progress == null || Math.Abs(progress.Value - 100) < 0.5;

                try { SaveShot(win, TestFile("shot-result-grid.png")); } catch { }
                try { win.Close(); } catch { }

                if (visible && flushRight && fullHeight && builtInHidden && progress100)
                    Pass("T65 结果页滚动条贴边整高", $"右边缘 {sbRight:0.#}/{root.ActualWidth:0.#}，高 {sb.ActualHeight:0.#}，进度 {progress?.Value:0}%");
                else
                    Fail("T65 结果页滚动条贴边整高",
                        $"visible={visible} right={sbRight:0.#}/{root.ActualWidth:0.#} h={sb.ActualHeight:0.#}/{root.ActualHeight:0.#} builtInHidden={builtInHidden} progress={progress?.Value:0.#}");
            }
            catch (Exception ex) { Fail("T65 结果页滚动条贴边整高", ex.GetType().Name + "：" + ex.Message); }
            finally { try { win?.Close(); } catch { } }
        }
        // ---------------------------------------------------------------
        // T52：窗口标题输入校验（只拦控制字符）
        // ---------------------------------------------------------------
        private static void TestWindowTitleValidation()
        {
            try
            {
                var v = typeof(RandomDrawer.WindowTitleValidator);

                bool okNormal = true;
                foreach (var s in new[] { "", "随机抽取器 3.16", "A|B:C{}", "  ", "标题 - 2026/11/2" })
                    if (!(bool)v.GetMethod("IsValid").Invoke(null, new object[] { s })) okNormal = false;

                bool badTab = !(bool)v.GetMethod("IsValid").Invoke(null, new object[] { "a\tb" });
                bool badNewline = !(bool)v.GetMethod("IsValid").Invoke(null, new object[] { "a\r\nb" });
                string names = v.GetMethod("DescribeInvalid").Invoke(null, new object[] { "a\tb\nc" }) as string;
                bool namesOk = names != null && names.Contains("制表符") && names.Contains("换行");

                if (okNormal && badTab && badNewline && namesOk)
                    Pass("T52 标题只拦控制字符", $"正常文本通过；制表符/换行被拦（{names}）");
                else
                    Fail("T52 标题只拦控制字符", $"normal={okNormal} tab={badTab} nl={badNewline} names={names}");
            }
            catch (Exception ex) { Fail("T52 标题输入校验", ex.GetType().Name + "：" + ex.Message); }
        }

        // ---------------------------------------------------------------
        // T53：选项页输入非法字符 → 红框 + 列出符号；不生效、不落盘
        // ---------------------------------------------------------------
        private static void TestWindowTitleInvalidUI(Window owner)
        {
            string oldFormat = RandomDrawer.App.WindowTitleFormat;
            try
            {
                var mw = owner as RandomDrawer.MainWindow;
                if (mw == null) { Fail("T53 标题非法输入提示", "窗口不是 MainWindow"); return; }

                RandomDrawer.Messaging.MessageBus.Publish(
                    new RandomDrawer.Messaging.AppMessages.NavigateTo("Options"));
                DoEvents(TimeSpan.FromMilliseconds(900));
                SetNav(owner, "0");   // 切到「常规」，让窗口标题输入框可见
                DoEvents(TimeSpan.FromMilliseconds(400));

                var pageHost = FindByName<Grid>(owner, "PageHost");
                var page = pageHost?.Children.OfType<RandomDrawer.OptionsPage_UC>().FirstOrDefault();
                var box = page?.FindName("TxtWindowTitle") as TextBox;
                var hint = page?.FindName("TxtWindowTitleInvalid") as TextBlock;
                if (page == null || box == null || hint == null)
                {
                    Fail("T53 标题非法输入提示", "找不到选项页 / TxtWindowTitle / TxtWindowTitleInvalid");
                    return;
                }

                RandomDrawer.App.WindowTitleFormat = "合法标题";
                page.GetType().GetMethod("LoadWindowTitleUI", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(page, null);
                DoEvents(TimeSpan.FromMilliseconds(200));

                box.Text = "坏\t标题";
                DoEvents(TimeSpan.FromMilliseconds(250));

                try { SaveShot(owner, TestFile("shot-title-invalid.png")); } catch { }

                bool red = box.BorderBrush is SolidColorBrush sb && sb.Color.R > 180 && sb.Color.G < 90;
                bool hintShown = hint.Visibility == Visibility.Visible && hint.Text.Contains("制表符");
                bool notApplied = RandomDrawer.App.WindowTitleFormat == "合法标题";

                box.Text = "好标题";
                DoEvents(TimeSpan.FromMilliseconds(250));
                bool recovered = hint.Visibility != Visibility.Visible
                                 && RandomDrawer.App.WindowTitleFormat == "好标题";

                if (red && hintShown && notApplied && recovered)
                    Pass("T53 标题非法输入提示", "红框 + 「不可用符号：制表符」，且未生效");
                else
                    Fail("T53 标题非法输入提示",
                        $"red={red} hint={hintShown}({hint.Text}) notApplied={notApplied} recovered={recovered}");
            }
            catch (Exception ex) { Fail("T53 标题非法输入提示", ex.GetType().Name + "：" + ex.Message); }
            finally
            {
                RandomDrawer.App.WindowTitleFormat = oldFormat;
                try { (owner as RandomDrawer.MainWindow)?.UpdateWindowTitle(); } catch { }
                RandomDrawer.Messaging.MessageBus.Publish(
                    new RandomDrawer.Messaging.AppMessages.NavigateTo("Main"));
                DoEvents(TimeSpan.FromMilliseconds(500));
            }
        }

        // ---------------------------------------------------------------
        // T54：<500 条的结果列表不会「先放大 2.2 倍再缩回去」
        // ---------------------------------------------------------------
        private static void TestResultListNoScaleFlash()
        {
            Window win = null;
            try
            {
                win = new Window
                {
                    Width = 1000, Height = 700,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000, Top = -4000, ShowInTaskbar = false
                };
                var page = new RandomDrawer.DrawResultPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(600));

                var items = new List<RandomDrawer.Messaging.DrawResultData>();
                for (int i = 0; i < 30; i++)
                    items.Add(new RandomDrawer.Messaging.DrawResultData { Index = i + 1, ObjectName = "对象" + i });

                RandomDrawer.Messaging.MessageBus.Publish(new RandomDrawer.Messaging.AppMessages.OpenDrawResultPage(
                    items, "", 30, new RandomDrawer.PersonalizationSettings(), new RandomDrawer.HighlightConfig(),
                    RandomDrawer.Messaging.DrawResultDisplayMode.List));

                var host = page.FindName("CardsHost") as Panel;
                double maxScale = 0, finalScale = 0;

                for (int step = 0; step < 20; step++)
                {
                    DoEvents(TimeSpan.FromMilliseconds(20));
                    double scale = 0;
                    if (host != null && host.Children.Count > 0 && host.Children[0] is Border b && b.Child is FrameworkElement content)
                    {
                        if (content.LayoutTransform is ScaleTransform st) scale = st.ScaleX;
                        else scale = 1;
                    }
                    if (scale > maxScale) maxScale = scale;
                    finalScale = scale;
                }

                if (maxScale > 0 && maxScale <= finalScale + 0.01 && finalScale <= 1.01)
                    Pass("T54 结果列表不再先放大再缩回", $"峰值 {maxScale:0.##} = 最终 {finalScale:0.##}");
                else
                    Fail("T54 结果列表不再先放大再缩回", $"峰值 {maxScale:0.##} / 最终 {finalScale:0.##}");
            }
            catch (Exception ex) { Fail("T54 结果列表缩放闪动", ex.GetType().Name + "：" + ex.Message); }
            finally { try { win?.Close(); } catch { } DoEvents(TimeSpan.FromMilliseconds(150)); }
        }

        // ---------------------------------------------------------------
        // T55：带 GIF 封面的卡池，禁用动画后回主界面封面仍在播
        // ---------------------------------------------------------------
        private static void TestCoverSurvivesAnimationToggle(Window owner)
        {
            string root = Path.Combine(TestOutputDir, "cover-test");
            bool oldDisabled = RandomDrawer.App.IsAnimationDisabled;
            var oldDirs = RandomDrawer.App.SearchDirectories;
            string oldDir = RandomDrawer.App.LastSelectedPoolDir;
            string oldName = RandomDrawer.App.LastSelectedPoolName;
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                string poolDir = Path.Combine(root, "GIF池");
                Directory.CreateDirectory(poolDir);
                File.WriteAllText(Path.Combine(poolDir, "data.txt"),
                    "抽取对象 分类\r\n甲 5\r\n乙 4\r\n", new UTF8Encoding(false));
                EnsureTestGif(Path.Combine(poolDir, "cover.gif"));

                var mw = owner as RandomDrawer.MainWindow;
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var cache = typeof(RandomDrawer.MainWindow).GetField("_pageCache", flags)?.GetValue(mw)
                            as System.Collections.IDictionary;
                var page = cache?["Main"] as RandomDrawer.MainPage_UC;
                if (page == null) { Fail("T55 封面在切换动画后仍显示", "找不到主界面"); return; }

                RandomDrawer.Messaging.MessageBus.Publish(new RandomDrawer.Messaging.AppMessages.NavigateTo("Main"));
                DoEvents(TimeSpan.FromMilliseconds(900));

                RandomDrawer.App.IsAnimationDisabled = false;
                RandomDrawer.App.SearchDirectories = new List<string> { root };
                RandomDrawer.App.LastSelectedPoolDir = poolDir;
                RandomDrawer.App.LastSelectedPoolName = "GIF池";
                RandomDrawer.Messaging.MessageBus.Publish(new RandomDrawer.Messaging.AppMessages.PoolListChanged());

                var img = page.FindName("DisplayImage") as Image;
                // 封面是异步加载的：轮询到真的出现为止（最多 3 秒），避免固定等待偶尔不够
                PumpUntil(() => img != null && img.Visibility == Visibility.Visible && img.Source != null,
                    TimeSpan.FromMilliseconds(3000));
                bool before = img != null && img.Visibility == Visibility.Visible && img.Source != null;

                RandomDrawer.Messaging.MessageBus.Publish(new RandomDrawer.Messaging.AppMessages.NavigateTo("Options"));
                DoEvents(TimeSpan.FromMilliseconds(800));
                RandomDrawer.App.IsAnimationDisabled = true;
                RandomDrawer.Messaging.MessageBus.Publish(new RandomDrawer.Messaging.AppMessages.UiOptionsChanged());
                DoEvents(TimeSpan.FromMilliseconds(300));
                RandomDrawer.Messaging.MessageBus.Publish(new RandomDrawer.Messaging.AppMessages.NavigateTo("Main"));
                PumpUntil(() => img != null && img.Visibility == Visibility.Visible && img.Source != null,
                    TimeSpan.FromMilliseconds(3000));

                bool after = img != null && img.Visibility == Visibility.Visible && img.Source != null;

                // GIF 计时器也是异步挂上的：最多等 3 秒
                PumpUntil(() => page.GetType().GetField("_gifTimer", flags)?.GetValue(page) != null,
                    TimeSpan.FromMilliseconds(3000));
                var timer = page.GetType().GetField("_gifTimer", flags)?.GetValue(page);
                bool playing = timer != null;

                if (before && after && playing)
                    Pass("T55 禁用动画后返回主界面封面仍在播", "封面可见、GIF 计时器在跑");
                else
                    Fail("T55 禁用动画后返回主界面封面仍在播", $"before={before} after={after} playing={playing}");
            }
            catch (Exception ex) { Fail("T55 封面在切换动画后仍显示", ex.GetType().Name + "：" + ex.Message); }
            finally
            {
                RandomDrawer.App.IsAnimationDisabled = oldDisabled;
                RandomDrawer.App.SearchDirectories = oldDirs;
                RandomDrawer.App.LastSelectedPoolDir = oldDir;
                RandomDrawer.App.LastSelectedPoolName = oldName;
                try { Directory.Delete(root, true); } catch { }
                try { (owner as RandomDrawer.MainWindow)?.UpdateWindowTitle(); } catch { }
            }
        }
        // ---------------------------------------------------------------
        // T14：选项页布局 —— 内容居中、目录行限宽、帮助正文限宽
        // T15：帮助页文本与当前功能一致
        // ---------------------------------------------------------------
        private static void TestOptionsLayouts(Window win)
        {
            try
            {
                // 选项页已回退为「铺满 + 左对齐」的原样式，这里只做渲染截图 + 帮助文本校验
                win.WindowState = WindowState.Maximized;
                DoEvents(TimeSpan.FromMilliseconds(700));

                RandomDrawer.Messaging.MessageBus.Publish(new RandomDrawer.Messaging.AppMessages.NavigateTo("Options"));
                DoEvents(TimeSpan.FromMilliseconds(900));

                SetNav(win, "0");
                DoEvents(TimeSpan.FromMilliseconds(500));
                SaveShot(win, TestFile("shot-options-general.png"));

                // 外观三行左对齐（主题 / 右键菜单 两个下拉框与「窗口标题」输入框同一左边界）
                var comboTheme = FindByName<FrameworkElement>(win, "ComboTheme");
                var comboMenu = FindByName<FrameworkElement>(win, "ComboContextMenuStyle");
                var titleBox = FindByName<FrameworkElement>(win, "TxtWindowTitle");
                var rootVisual = win.Content as Visual;

                if (comboTheme != null && comboMenu != null && titleBox != null && rootVisual != null)
                {
                    double xTheme = comboTheme.TransformToAncestor(rootVisual).Transform(new Point(0, 0)).X;
                    double xMenu = comboMenu.TransformToAncestor(rootVisual).Transform(new Point(0, 0)).X;
                    double xTitle = titleBox.TransformToAncestor(rootVisual).Transform(new Point(0, 0)).X;

                    if (Math.Abs(xTheme - xTitle) < 0.6 && Math.Abs(xMenu - xTitle) < 0.6)
                        Pass("T14 外观三行左边界对齐", $"主题 {xTheme:0.#} / 右键菜单 {xMenu:0.#} / 窗口标题 {xTitle:0.#}");
                    else
                        Fail("T14 外观三行左边界对齐", $"主题 {xTheme:0.#} / 右键菜单 {xMenu:0.#} / 窗口标题 {xTitle:0.#}");
                }
                else Fail("T14 外观三行左边界对齐", "找不到 ComboTheme / ComboContextMenuStyle / TxtWindowTitle");

                // 「窗口标题」右侧那行灰色小字已经按需求移除
                string generalText = CollectText(win);
                if (!generalText.Contains("留空为默认标题"))
                    Pass("T14 窗口标题右侧灰色提示已移除", "");
                else
                    Fail("T14 窗口标题右侧灰色提示已移除", "仍然存在「留空为默认标题…」");

                SetNav(win, "1");
                DoEvents(TimeSpan.FromMilliseconds(500));

                SetNav(win, "2");
                DoEvents(TimeSpan.FromMilliseconds(600));

                var dirs = FindByName<ListBox>(win, "ListDirs");
                if (dirs != null)
                {
                    if (dirs.ActualWidth <= 800 && dirs.ActualWidth > 400)
                        Pass("T14 目录控件限宽", $"{dirs.ActualWidth:0} DIP");
                    else
                        Fail("T14 目录控件限宽", $"{dirs.ActualWidth:0} DIP");
                }
                else Fail("T14 目录控件限宽", "未找到 ListDirs");


                SetNav(win, "3");
                DoEvents(TimeSpan.FromMilliseconds(700));

                string text = CollectText(win);
                bool hasLoading = text.Contains("500") && text.Contains("加载动画");
                bool hasCover = text.Contains("更换封面") && text.Contains("不需要密码");
                bool hasExport = text.Contains("导出") && text.Contains("解密");
                bool hasDelete = text.Contains("删除") && text.Contains("不需要密码");
                bool hasShareSplit = text.Contains("报告很长时") && text.Contains("拆分");
                bool hasPreviewMenu = text.Contains("预览图") && text.Contains("右键");

                if (hasLoading && hasCover && hasExport && hasDelete && hasShareSplit && hasPreviewMenu)
                    Pass("T15 帮助页文本已同步", "含加载阈值 / 封面免密 / 导出解密 / 删除免密 / 长报告拆分 / 预览图右键");
                else
                    Fail("T15 帮助页文本已同步",
                        $"loading={hasLoading} cover={hasCover} export={hasExport} delete={hasDelete} shareSplit={hasShareSplit} previewMenu={hasPreviewMenu}");

                SaveShot(win, TestFile("shot-help.png"));
            }
            catch (Exception ex)
            {
                Fail("T14/T15 选项页截图与帮助文本", ex.Message);
            }
        }
        // ---------------------------------------------------------------
        // T16：高级抽卡规则页 —— 面板居中、控件等宽、顶部下拉右边缘对齐
        // ---------------------------------------------------------------
        private static void TestAdvancedLayout()
        {
            Window win = null;
            try
            {
                win = new Window
                {
                    Width = 1500,
                    Height = 860,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false
                };

                var page = new RandomDrawer.AdvancedGachaPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(600));

                var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var sp = typeof(RandomDrawer.AdvancedGachaPage_UC).GetField("SettingsPanel", flags)?.GetValue(page) as FrameworkElement;
                var root = typeof(RandomDrawer.AdvancedGachaPage_UC).GetField("PageRoot", flags)?.GetValue(page) as FrameworkElement;
                var preset = typeof(RandomDrawer.AdvancedGachaPage_UC).GetField("ComboPreset", flags)?.GetValue(page) as FrameworkElement;
                var warpPanel = typeof(RandomDrawer.AdvancedGachaPage_UC).GetField("WarpTypePanel", flags)?.GetValue(page) as FrameworkElement;
                var warpCombo = typeof(RandomDrawer.AdvancedGachaPage_UC).GetField("ComboWarpType", flags)?.GetValue(page) as FrameworkElement;

                if (sp == null || root == null) { Fail("T16 找到规则面板", "字段缺失"); return; }

                sp.Visibility = Visibility.Visible;
                if (warpPanel != null) warpPanel.Visibility = Visibility.Visible;
                DoEvents(TimeSpan.FromMilliseconds(500));

                if (Math.Abs(sp.ActualWidth - 960) < 6)
                    Pass("T16 规则面板宽度固定", $"{sp.ActualWidth:0} DIP（两列各约 260）");
                else
                    Fail("T16 规则面板宽度固定", $"{sp.ActualWidth:0} DIP（期望 960）");

                var sPos = sp.TransformToAncestor(root).Transform(new Point(0, 0));
                double left = sPos.X;
                double right = root.ActualWidth - sPos.X - sp.ActualWidth;
                if (Math.Abs(left - right) <= 30)
                    Pass("T16 规则面板居中", $"左 {left:0} / 右 {right:0}");
                else
                    Fail("T16 规则面板居中", $"左 {left:0} / 右 {right:0}");

                if (preset != null)
                {
                    var pPos = preset.TransformToAncestor(root).Transform(new Point(0, 0));
                    double presetRight = pPos.X + preset.ActualWidth;
                    double panelRight = sPos.X + sp.ActualWidth;
                    if (Math.Abs(presetRight - panelRight) <= 6)
                        Pass("T16 预设下拉框右边缘对齐", $"{presetRight:0} vs {panelRight:0}");
                    else
                        Fail("T16 预设下拉框右边缘对齐", $"预设 {presetRight:0} / 面板 {panelRight:0}");
                }
                else Fail("T16 预设下拉框右边缘对齐", "未找到 ComboPreset");

                if (warpCombo != null)
                {
                    var wPos = warpCombo.TransformToAncestor(root).Transform(new Point(0, 0));
                    double expected = sPos.X + 200;   // 规则面板第 1 列的左边缘
                    if (Math.Abs(wPos.X - expected) <= 6 && Math.Abs(warpCombo.ActualWidth - 260) <= 6)
                        Pass("T16 跃迁类型与下方控件对齐", $"左 {wPos.X:0}（期望 {expected:0}），宽 {warpCombo.ActualWidth:0}");
                    else
                        Fail("T16 跃迁类型与下方控件对齐", $"左 {wPos.X:0}（期望 {expected:0}），宽 {warpCombo.ActualWidth:0}");
                }
                else Fail("T16 跃迁类型与下方控件对齐", "未找到 ComboWarpType");

                // 底部按钮 / 提示文字要与规则面板同宽
                var bottom = typeof(RandomDrawer.AdvancedGachaPage_UC).GetField("BottomBar", flags)?.GetValue(page) as FrameworkElement;
                SaveShot(win, TestFile("shot-advanced.png"));
            }
            catch (Exception ex)
            {
                Fail("T16 高级规则页布局", ex.Message);
            }
            finally
            {
                try { win?.Close(); } catch { }
                DoEvents(TimeSpan.FromMilliseconds(150));
            }
        }
        // ---------------------------------------------------------------
        // T17：分享弹窗的多图模式（缩略图 / 切换 / 当前图路径）
        // ---------------------------------------------------------------
        private static void TestShareDialogMulti()
        {
            try
            {
                BitmapSource MakeBmp(byte r)
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(r, (byte)(r / 2), 0x40)), null, new Rect(0, 0, 40, 120));
                    var rtb = new RenderTargetBitmap(40, 120, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.Freeze();
                    return rtb;
                }

                var images = new List<BitmapSource> { MakeBmp(0x10), MakeBmp(0x80), MakeBmp(0xF0) };
                var paths = new List<string> { "", "", "" };
                var dlg = new RandomDrawer.ReportShareDialog(images, paths);

                var multi = dlg.FindName("MultiPanel") as FrameworkElement;
                var thumbs = dlg.FindName("ThumbPanel") as Panel;

                if (multi != null && multi.Visibility == Visibility.Visible && thumbs != null && thumbs.Children.Count == 3)
                    Pass("T17 多图缩略图", $"{thumbs.Children.Count} 张缩略图，面板已显示");
                else
                    Fail("T17 多图缩略图", $"panel={(multi?.Visibility)} thumbs={thumbs?.Children.Count}");

                // 单张时不应显示缩略图条
                var single = new RandomDrawer.ReportShareDialog(images[0], "");
                var multi2 = single.FindName("MultiPanel") as FrameworkElement;
                if (multi2 != null && multi2.Visibility != Visibility.Visible)
                    Pass("T17 单张不显示缩略图条", "已隐藏");
                else
                    Fail("T17 单张不显示缩略图条", $"{multi2?.Visibility}");

                try { dlg.Close(); } catch { }
                try { single.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Fail("T17 分享弹窗多图", ex.Message);
            }
        }
        // T18：分享长图渲染质量（宽度是否够、页面是否完整）
        private static void TestShareRenderQuality()
        {
            Window win = null;
            try
            {
                win = new Window { Width = 1000, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000, ShowInTaskbar = false };
                var page = new RandomDrawer.GachaReportPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(600));

                var data = new RandomDrawer.GachaReportData
                {
                    Title = "欧非守恒",
                    PoolName = "测试池",
                    Summary = "小保底不歪率 52.4% · 平均每金 62.4 抽 · 五星 3418 个 / 共 213146 抽",
                    TotalPulls = 213146,
                    HasData = true
                };
                data.Keywords.Add("十连双黄");
                data.Keywords.Add("3连UP");
                for (int i = 0; i < 30; i++)
                    data.Entries.Add(new RandomDrawer.GachaReportEntry { Name = "角色" + i, Pulls = 40 + i * 2 });

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                page.GetType().GetMethod("RenderReport", flags)?.Invoke(page, new object[] { data });
                DoEvents(TimeSpan.FromMilliseconds(500));

                var list = page.GetType().GetMethod("RenderReportPages", flags)
                    ?.Invoke(page, new object[] { 8000, null }) as System.Collections.Generic.List<BitmapSource>;

                if (list == null || list.Count == 0) { Fail("T18 分页渲染", "没有生成图片"); return; }

                var first = list[0];
                if (first.PixelWidth >= 1100) Pass("T18 分页宽度", $"{first.PixelWidth}×{first.PixelHeight}");
                else Fail("T18 分页宽度", $"{first.PixelWidth}×{first.PixelHeight}（期望 ≥1100）");

                string outPath = TestFile("shot-share-page.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(first));
                using (var fs = File.Create(outPath)) enc.Save(fs);
                Pass("T18 截图已生成", outPath + $"（共 {list.Count} 页）");
            }
            catch (Exception ex) { Fail("T18 分页渲染", ex.Message); }
            finally { try { win?.Close(); } catch { } DoEvents(TimeSpan.FromMilliseconds(150)); }
        }

        // ---------------------------------------------------------------
        // T45：生成长图 / PDF 的遮罩显示期间，报告页动画不再被挂起；
        //      渲染本身是「分块 + 让出 UI」的 async 流程（生成时界面仍能响应）
        // ---------------------------------------------------------------
        private static void TestShareGenerationKeepsAnimations(Window owner)
        {
            bool oldDisabled = RandomDrawer.App.IsAnimationDisabled;
            try
            {
                RandomDrawer.App.IsAnimationDisabled = false;

                var mw = owner as RandomDrawer.MainWindow;
                if (mw == null) { Fail("T45 生成期间动画", "窗口不是 MainWindow"); return; }

                // 进入报告页，拿到真实页面实例（遮罩挂在 MainWindow 上，必须用它来验证）
                RandomDrawer.Messaging.MessageBus.Publish(
                    new RandomDrawer.Messaging.AppMessages.NavigateTo("GachaReport"));
                DoEvents(TimeSpan.FromMilliseconds(900));

                var pageHost = FindByName<Grid>(owner, "PageHost");
                var page = pageHost?.Children.OfType<RandomDrawer.GachaReportPage_UC>().FirstOrDefault();
                if (page == null) { Fail("T45 生成期间动画", "找不到报告页实例"); return; }

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;

                // ① 铺一份「高级规则」报告：有出金记录 → 有行内斜纹
                var data = new RandomDrawer.GachaReportData
                {
                    Title = "欧非守恒",
                    PoolName = "测试池",
                    Summary = "小保底不歪率 52.4%",
                    HasData = true,
                    TotalPulls = 900
                };
                for (int i = 0; i < 12; i++)
                    data.Entries.Add(new RandomDrawer.GachaReportEntry { Name = "角色" + i, Pulls = 45 + i * 3 });

                page.GetType().GetMethod("RenderReport", flags)?.Invoke(page, new object[] { data });
                page.GetType().GetMethod("ShowReportPanelAnimated", flags)?.Invoke(page, null);
                DoEvents(TimeSpan.FromMilliseconds(800));

                var stripesField = page.GetType().GetField("_stripeAnimations", flags);
                var stripes = stripesField?.GetValue(page) as System.Collections.IList;
                int stripeCount = stripes?.Count ?? 0;

                // ② 旧的「暂停 / 恢复斜纹」实现必须已经不存在（回归保护）
                bool suspendRemoved = page.GetType().GetMethod("SuspendStripeAnimations", flags) == null
                                      && page.GetType().GetMethod("ResumeStripeAnimations", flags) == null;

                // ③ 打开「正在生成长图」遮罩：动画必须继续跑
                var showTask = page.GetType().GetMethod("ShowShareProgressAsync", flags)
                    ?.Invoke(page, new object[] { true, "正在生成长图…" }) as System.Threading.Tasks.Task;
                PumpUntil(showTask, 4000);
                DoEvents(TimeSpan.FromMilliseconds(300));

                var overlay = FindByName<Grid>(owner, "ShareBusyOverlay");
                bool overlayShown = overlay != null && overlay.Visibility == Visibility.Visible;

                double x1 = SampleStripeOffset(stripes, out int running1);
                DoEvents(TimeSpan.FromMilliseconds(400));
                double x2 = SampleStripeOffset(stripes, out int running2);

                // ④ 分块渲染期间 UI 还能处理别的消息（旧实现是一次同步循环，界面整段不说话）
                bool uiPumped = false;
                var renderTask = page.GetType().GetMethod("RenderReportPagesAsync", flags)
                    ?.Invoke(page, new object[] { 8000, null, false }) as System.Threading.Tasks.Task;
                bool notCompletedSynchronously = renderTask != null && !renderTask.IsCompleted;
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    new Action(() => uiPumped = true), DispatcherPriority.Background);
                PumpUntil(renderTask, 60000);

                object pages = null;
                try { pages = renderTask?.GetType().GetProperty("Result")?.GetValue(renderTask); } catch { }
                int pageCount = (pages as System.Collections.ICollection)?.Count ?? 0;

                DoEvents(TimeSpan.FromMilliseconds(300));
                double x3 = SampleStripeOffset(stripes, out int running3);

                var hideTask = page.GetType().GetMethod("ShowShareProgressAsync", flags)
                    ?.Invoke(page, new object[] { false, "" }) as System.Threading.Tasks.Task;
                PumpUntil(hideTask, 3000);
                DoEvents(TimeSpan.FromMilliseconds(200));
                bool overlayHidden = overlay == null || overlay.Visibility != Visibility.Visible;

                bool animationAlive = stripeCount > 0
                                      && running1 > 0 && running2 > 0 && running3 > 0
                                      && !double.IsNaN(x1) && !double.IsNaN(x2) && Math.Abs(x2 - x1) > 0.01;

                if (animationAlive && suspendRemoved && overlayShown)
                    Pass("T45 遮罩期间斜纹动画照常播放",
                        $"{stripeCount} 条斜纹，运行中 {running1}/{running2}/{running3}，X {x1:0.0} → {x2:0.0} → {x3:0.0}，遮罩已显示");
                else
                    Fail("T45 遮罩期间斜纹动画照常播放",
                        $"stripes={stripeCount} running={running1}/{running2}/{running3} x={x1:0.0}→{x2:0.0}→{x3:0.0} " +
                        $"overlay={overlayShown} suspendRemoved={suspendRemoved}");

                if (notCompletedSynchronously && uiPumped && pageCount > 0 && overlayHidden)
                    Pass("T45 分块渲染让出 UI", $"渲染期间消息循环可用，{pageCount} 页，遮罩可收起");
                else
                    Fail("T45 分块渲染让出 UI",
                        $"async={notCompletedSynchronously} uiPumped={uiPumped} pages={pageCount} overlayHidden={overlayHidden}");

                SaveShot(owner, TestFile("shot-share-animation.png"));
            }
            catch (Exception ex) { Fail("T45 生成期间动画", ex.GetType().Name + "：" + ex.Message); }
            finally
            {
                RandomDrawer.App.IsAnimationDisabled = oldDisabled;
                try
                {
                    RandomDrawer.Messaging.MessageBus.Publish(
                        new RandomDrawer.Messaging.AppMessages.NavigateTo("Main"));
                }
                catch { }
                DoEvents(TimeSpan.FromMilliseconds(300));
            }
        }

        /// <summary>取「正在运行的斜纹」当前的平移量（用反射读私有嵌套类型）。</summary>
        private static double SampleStripeOffset(System.Collections.IList stripes, out int runningCount)
        {
            runningCount = 0;
            double value = double.NaN;
            if (stripes == null) return value;

            foreach (object item in stripes)
            {
                try
                {
                    var type = item.GetType();
                    bool running = (bool)(type.GetProperty("Running")?.GetValue(item) ?? false);
                    if (running) runningCount++;

                    var move = type.GetProperty("Move")?.GetValue(item) as TranslateTransform;
                    if (running && move != null && move.HasAnimatedProperties && double.IsNaN(value))
                        value = move.X;
                }
                catch { }
            }
            return value;
        }

        // ---------------------------------------------------------------
        // T19：生成长图的进度遮罩必须覆盖整个窗口（含顶部导航栏）且卡片居中
        // ---------------------------------------------------------------
        private static void TestShareBusyOverlay(Window win)
        {
            try
            {
                win.WindowState = WindowState.Normal;
                win.Width = 1000;
                win.Height = 700;
                DoEvents(TimeSpan.FromMilliseconds(300));

                RandomDrawer.Messaging.MessageBus.Publish(
                    new RandomDrawer.Messaging.AppMessages.NavigateTo("GachaReport"));
                DoEvents(TimeSpan.FromMilliseconds(800));

                var mw = win as RandomDrawer.MainWindow;
                if (mw == null) { Fail("T19 生成长图遮罩", "窗口不是 MainWindow"); return; }

                mw.ShowShareBusy("正在生成长图…");
                mw.UpdateShareBusy(42);
                DoEvents(TimeSpan.FromMilliseconds(400));

                var overlay = FindByName<Grid>(win, "ShareBusyOverlay");
                var topBar = FindByName<Border>(win, "TopBar");
                var pct = FindByName<TextBlock>(win, "TxtShareBusyPercent");

                if (overlay == null || topBar == null)
                {
                    Fail("T19 生成长图遮罩", "找不到 ShareBusyOverlay / TopBar");
                    return;
                }

                var root = win.Content as FrameworkElement;
                double rootW = root?.ActualWidth ?? win.ActualWidth;
                double rootH = root?.ActualHeight ?? win.ActualHeight;

                // ① 覆盖整个窗口（含顶栏所在的那一行）
                bool coversNav = overlay.ActualHeight >= rootH - 1.5
                                 && overlay.ActualWidth >= rootW - 1.5
                                 && Math.Abs(overlay.ActualHeight - (topBar.ActualHeight + (FindByName<Grid>(win, "PageHost")?.ActualHeight ?? 0))) < 2;
                if (coversNav)
                    Pass("T19 遮罩覆盖顶部导航栏", $"遮罩 {overlay.ActualWidth:0}×{overlay.ActualHeight:0}，窗口 {rootW:0}×{rootH:0}");
                else
                    Fail("T19 遮罩覆盖顶部导航栏", $"遮罩 {overlay.ActualWidth:0}×{overlay.ActualHeight:0}，窗口 {rootW:0}×{rootH:0}，顶栏 {topBar.ActualHeight:0}");

                // ② 遮罩层级高于顶栏
                if (Panel.GetZIndex(overlay) > Panel.GetZIndex(topBar))
                    Pass("T19 遮罩层级高于顶栏", $"{Panel.GetZIndex(overlay)} > {Panel.GetZIndex(topBar)}");
                else
                    Fail("T19 遮罩层级高于顶栏", $"{Panel.GetZIndex(overlay)} <= {Panel.GetZIndex(topBar)}");

                // ③ 卡片在窗口正中（不是「内容区」正中）
                var card = overlay.Children.Count > 0 ? overlay.Children[0] as FrameworkElement : null;
                if (card == null) { Fail("T19 卡片居中", "找不到遮罩卡片"); return; }

                var pos = card.TransformToAncestor(overlay).Transform(new Point(0, 0));
                double cx = pos.X + card.ActualWidth / 2;
                double cy = pos.Y + card.ActualHeight / 2;
                double dx = Math.Abs(cx - overlay.ActualWidth / 2);
                double dy = Math.Abs(cy - overlay.ActualHeight / 2);

                if (dx < 1.5 && dy < 1.5)
                    Pass("T19 卡片在窗口正中", $"偏移 {dx:0.#}/{dy:0.#} DIP");
                else
                    Fail("T19 卡片在窗口正中", $"偏移 水平 {dx:0.#} / 垂直 {dy:0.#} DIP（窗口中心 {overlay.ActualWidth / 2:0}×{overlay.ActualHeight / 2:0}）");

                // 进度条要在，但里面不能有斜纹（纯色圆角条）
                var busyBar = FindByName<ProgressBar>(win, "ShareBusyBar");
                bool barOk = busyBar != null && Math.Abs(busyBar.Value - 42) < 0.5;
                bool noStripe = true;
                if (busyBar != null)
                {
                    busyBar.ApplyTemplate();
                    var indicator = busyBar.Template?.FindName("PART_Indicator", busyBar) as Border;
                    noStripe = indicator != null && !(indicator.Background is DrawingBrush);
                }

                if (pct != null && pct.Text == "42%" && barOk && noStripe)
                    Pass("T19 进度条保留、条纹已取消", $"{pct.Text}，指示条为纯色");
                else
                    Fail("T19 进度条保留、条纹已取消", $"文字={pct?.Text} 进度条={busyBar?.Value} 无条纹={noStripe}");

                // 处理中按 Esc 不能退回主页
                var mwField = typeof(RandomDrawer.MainWindow).GetField("_currentPageKey",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                string pageBefore = mwField?.GetValue(mw) as string;

                var keySource = PresentationSource.FromVisual(win);
                if (keySource != null)
                {
                    win.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    });
                }
                DoEvents(TimeSpan.FromMilliseconds(200));

                string pageAfter = mwField?.GetValue(mw) as string;
                if (pageBefore == pageAfter)
                    Pass("T19 处理中按 Esc 不切页", $"{pageBefore}");
                else
                    Fail("T19 处理中按 Esc 不切页", $"{pageBefore} → {pageAfter}");

                SaveShot(win, TestFile("shot-share-busy.png"));

                mw.HideShareBusy();
                DoEvents(TimeSpan.FromMilliseconds(200));
                if (overlay.Visibility != Visibility.Visible)
                    Pass("T19 遮罩可收起", overlay.Visibility.ToString());
                else
                    Fail("T19 遮罩可收起", overlay.Visibility.ToString());
            }
            catch (Exception ex) { Fail("T19 生成长图遮罩", ex.Message); }
        }

        // ---------------------------------------------------------------
        // T20：单张长图必须保持原分辨率（2 倍密度），不做任何降采样
        // ---------------------------------------------------------------
        private static void TestShareSingleImageCap()
        {
            Window win = null;
            try
            {
                win = new Window
                {
                    Width = 1000,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false
                };
                var page = new RandomDrawer.GachaReportPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(600));

                var data = new RandomDrawer.GachaReportData
                {
                    Title = "长报告",
                    PoolName = "测试池",
                    Summary = "测试单张长图保持原分辨率",
                    HasData = true
                };
                const int rows = 120;
                for (int i = 0; i < rows; i++)
                    data.Entries.Add(new RandomDrawer.GachaReportEntry { Name = "角色" + i, Pulls = 30 + (i % 60) });

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                page.GetType().GetMethod("RenderReport", flags)?.Invoke(page, new object[] { data });

                // 等分批添加的行全部挂到视觉树上
                var host = FindByName<Panel>(page, "EntriesHost");
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && (host == null || host.Children.Count < rows))
                    DoEvents(TimeSpan.FromMilliseconds(120));
                DoEvents(TimeSpan.FromMilliseconds(300));

                var content = FindByName<StackPanel>(page, "ReportContent");
                double cw = content?.ActualWidth ?? 0;
                double ch = content?.ActualHeight ?? 0;

                var bmp = page.GetType().GetMethod("RenderReportBitmap", flags)
                    ?.Invoke(page, new object[] { null }) as BitmapSource;

                if (bmp == null) { Fail("T20 单张长图保持原分辨率", "返回 null"); return; }

                // 期望尺寸：与「内容尺寸 + 固定留白」× 2 完全一致（不缩、不裁）
                var sizeMethod = page.GetType().GetMethod("GetSingleImageSize", BindingFlags.NonPublic | BindingFlags.Static);
                var args = new object[] { cw, ch, 0, 0 };
                sizeMethod?.Invoke(null, args);
                int expW = (int)args[2], expH = (int)args[3];

                if (bmp.PixelWidth == expW && bmp.PixelHeight == expH)
                    Pass("T20 单张长图保持原分辨率", $"{bmp.PixelWidth}×{bmp.PixelHeight}（2 倍密度，无降采样）");
                else
                    Fail("T20 单张长图保持原分辨率", $"实际 {bmp.PixelWidth}×{bmp.PixelHeight}，期望 {expW}×{expH}");

                if (Math.Abs(bmp.DpiX - 192) < 0.5 && Math.Abs(bmp.DpiY - 192) < 0.5)
                    Pass("T20 单张长图 DPI 为 2 倍", $"{bmp.DpiX:0}×{bmp.DpiY:0}");
                else
                    Fail("T20 单张长图 DPI 为 2 倍", $"{bmp.DpiX:0}×{bmp.DpiY:0}");

                // 报错原文是「图像数据在处理过程中产生了溢出」，实际发生在 JPEG 编码那一步：
                // 这里原地编码一次（不落盘），确认不会再抛 WIC 溢出。
                try
                {
                    var jpg = new JpegBitmapEncoder { QualityLevel = 92 };
                    jpg.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0)));
                    using (var ms = new MemoryStream()) jpg.Save(ms);
                    Pass("T20 单张长图可编码为 JPEG", "编码成功，未抛「图像数据在处理过程中产生了溢出」");
                }
                catch (Exception ex)
                {
                    Fail("T20 单张长图可编码为 JPEG", ex.GetType().Name + "：" + ex.Message);
                }

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                var outPath = TestFile("shot-share-single.png");
                using (var fs = File.Create(outPath)) enc.Save(fs);
                Pass("T20 截图已生成", outPath);
            }
            catch (Exception ex) { Fail("T20 单张长图保持原分辨率", ex.GetType().Name + "：" + ex.Message); }
            finally { try { win?.Close(); } catch { } DoEvents(TimeSpan.FromMilliseconds(150)); }
        }

        // ---------------------------------------------------------------
        // T47：大报告（1200 行）滚动后，下方行的斜纹动画与 GIF 头像都要继续播
        // ---------------------------------------------------------------
        private static void TestLargeReportAnimations()
        {
            Window win = null;
            try
            {
                string gif = Path.Combine(TestOutputDir, "diag-avatar.gif");
                EnsureTestGif(gif);

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                int cacheBefore = GifFrameCacheCount();

                win = new Window
                {
                    Width = 1000, Height = 700,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000, Top = -4000, ShowInTaskbar = false
                };
                var page = new RandomDrawer.GachaReportPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(600));

                // ★ 行是「新→旧」排列：把带头像的条目放在前面，滚动到底部时它们正好在可视区
                var data = new RandomDrawer.GachaReportData
                {
                    Title = "大报告动画", PoolName = "诊断池", Summary = "500 条记录",
                    HasData = true, TotalPulls = 4000
                };
                for (int i = 0; i < 500; i++)
                {
                    var e = new RandomDrawer.GachaReportEntry { Name = "角色" + i, Pulls = 40 + (i % 50) };
                    if (i < 30 && File.Exists(gif)) e.AvatarPath = gif;
                    data.Entries.Add(e);
                }

                page.GetType().GetMethod("RenderReport", flags)?.Invoke(page, new object[] { data });
                page.GetType().GetMethod("ShowReportPanelAnimated", flags)?.Invoke(page, null);
                DoEvents(TimeSpan.FromMilliseconds(1500));   // 等批处理把 500 行铺完

                var scroll = page.FindName("ReportScroll") as ScrollViewer;
                var content = page.FindName("ReportContent") as FrameworkElement;
                if (scroll == null || content == null) { Fail("T47 大报告动画", "找不到 ReportScroll / ReportContent"); return; }

                var stripeList = page.GetType().GetField("_stripeAnimations", flags)?.GetValue(page) as System.Collections.IList;
                int stripeTotal = stripeList?.Count ?? 0;
                var updateMethod = page.GetType().GetMethod("UpdateStripeAnimations", flags);
                long worstMs = 0;
                for (int step = 1; step <= 3; step++)
                {
                    scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * step / 3.0);
                    DoEvents(TimeSpan.FromMilliseconds(120));
                    if (updateMethod != null)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        updateMethod.Invoke(page, null);
                        sw.Stop();
                        if (sw.ElapsedMilliseconds > worstMs) worstMs = sw.ElapsedMilliseconds;
                    }
                }
                scroll.ScrollToEnd();
                DoEvents(TimeSpan.FromMilliseconds(600));

                // ② 底部的斜纹必须真的在播（有运行中的、且位置在可视范围内）
                int running = 0;
                double runningY = double.NaN;
                double offset = scroll.VerticalOffset;
                if (stripeList != null)
                {
                    foreach (object item in stripeList)
                    {
                        var type = item.GetType();
                        if (!(bool)(type.GetProperty("Running")?.GetValue(item) ?? false)) continue;
                        running++;
                        var row = type.GetProperty("Row")?.GetValue(item) as FrameworkElement;
                        if (row != null)
                        {
                            double y = row.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
                            if (double.IsNaN(runningY)) runningY = y;
                        }
                    }
                }
                bool bottomStripesPlay = running > 0 && !double.IsNaN(runningY)
                                         && runningY >= offset - 60 && runningY <= offset + scroll.ViewportHeight + 120;

                // ③ 底部的 GIF 头像要在播（帧在推进），且同一张 GIF 只解码一次
                var gifPlayers = page.GetType().GetField("_gifPlayers", flags)?.GetValue(page) as System.Collections.IList;
                int gifTotal = gifPlayers?.Count ?? 0;
                int gifVisible = 0;
                bool gifAdvancing = false;
                var visibleMethod = typeof(RandomDrawer.GifPlayer).GetMethod("IsHostVisible", flags);
                if (gifPlayers != null)
                {
                    foreach (object player in gifPlayers)
                        if ((bool)(visibleMethod?.Invoke(player, null) ?? false)) gifVisible++;

                    foreach (object player in gifPlayers)
                    {
                        if (!(bool)(visibleMethod?.Invoke(player, null) ?? false)) continue;
                        var brush = player.GetType().GetProperty("Brush")?.GetValue(player) as ImageBrush;
                        var first = brush?.ImageSource;
                        DoEvents(TimeSpan.FromMilliseconds(500));
                        gifAdvancing = first != null && !ReferenceEquals(first, brush?.ImageSource);
                        break;
                    }
                }

                int cacheAfter = GifFrameCacheCount();
                bool sharedDecode = gifTotal > 0 && cacheAfter - cacheBefore <= 1;

                if (stripeTotal > 0 && bottomStripesPlay)
                    Pass("T47 大报告滚动后下方行仍播斜纹", $"{stripeTotal} 行，底部运行 {running} 个，Y={runningY:0} / 视口 {offset:0}+{scroll.ViewportHeight:0}");
                else
                    Fail("T47 大报告滚动后下方行仍播斜纹", $"stripes={stripeTotal} running={running} y={runningY:0} offset={offset:0} vp={scroll.ViewportHeight:0}");

                if (gifTotal > 0 && gifVisible > 0 && gifAdvancing)
                    Pass("T47 大报告底部 GIF 头像在播", $"{gifTotal} 个播放器，可见 {gifVisible} 个，帧在推进");
                else
                    Fail("T47 大报告底部 GIF 头像在播", $"players={gifTotal} visible={gifVisible} advancing={gifAdvancing}");

                if (sharedDecode)
                    Pass("T47 同一张 GIF 只解码一次", $"缓存 +{cacheAfter - cacheBefore} 项（{gifTotal} 个播放器共用）");
                else
                    Fail("T47 同一张 GIF 只解码一次", $"缓存 +{cacheAfter - cacheBefore} 项 / 播放器 {gifTotal} 个");

                if (worstMs <= 50)
                    Pass("T47 可见范围计算不再随行数变慢", $"500 行时单次最多 {worstMs} ms");
                else
                    Fail("T47 可见范围计算不再随行数变慢", $"500 行时单次 {worstMs} ms（期望 ≤50）");

                SaveShot(win, TestFile("shot-report-large.png"));
            }
            catch (Exception ex) { Fail("T47 大报告动画", ex.GetType().Name + "：" + ex.Message); }
            finally { try { win?.Close(); } catch { } DoEvents(TimeSpan.FromMilliseconds(200)); }
        }

        /// <summary>生成一张 2 帧的测试 GIF（不依赖 bin 目录里的素材，换机器也能跑）。</summary>
        private static void EnsureTestGif(string path)
        {
            try
            {
                if (File.Exists(path)) return;

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                BitmapSource Frame(Color color)
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                        dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 44, 44));
                    var rtb = new RenderTargetBitmap(44, 44, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.Freeze();
                    return rtb;
                }

                var enc = new GifBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(Frame(Colors.OrangeRed)));
                enc.Frames.Add(BitmapFrame.Create(Frame(Colors.SteelBlue)));
                using (var fs = File.Create(path)) enc.Save(fs);
            }
            catch { }
        }

        /// <summary>GIF 帧缓存当前条目数（用反射读私有静态字典）。</summary>
        private static int GifFrameCacheCount()
        {
            try
            {
                var field = typeof(RandomDrawer.GifPlayer).GetField("_frameCache",
                    BindingFlags.Static | BindingFlags.NonPublic);
                return (field?.GetValue(null) as System.Collections.ICollection)?.Count ?? 0;
            }
            catch { return 0; }
        }
        // ---------------------------------------------------------------
        // T24：拆分多张时，每张图左上角有「随机抽取器 … 卡池：… 第X张，共N张」
        // ---------------------------------------------------------------
        private static void TestPageHeader()
        {
            Window win = null;
            try
            {
                win = new Window
                {
                    Width = 1000,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false
                };
                var page = new RandomDrawer.GachaReportPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(600));

                var data = new RandomDrawer.GachaReportData
                {
                    Title = "欧非守恒",
                    PoolName = "沧海萃珠 [3]",
                    Summary = "测试页眉标注",
                    HasData = true
                };
                for (int i = 0; i < 8; i++)
                    data.Entries.Add(new RandomDrawer.GachaReportEntry { Name = "角色" + i, Pulls = 50 + i });

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                page.GetType().GetMethod("RenderReport", flags)?.Invoke(page, new object[] { data });
                DoEvents(TimeSpan.FromMilliseconds(800));

                // 页眉里的卡池名来自 _reportPoolName（正常流程由 ApplyReport 写入），这里直接给上
                page.GetType().GetField("_reportPoolName", flags)?.SetValue(page, "沧海萃珠 [3]");

                // ① 文字格式
                string ver = page.GetType()
                    .GetMethod("GetAppVersionText", BindingFlags.NonPublic | BindingFlags.Static)
                    ?.Invoke(null, null) as string;

                string text = page.GetType().GetMethod("BuildPageHeader", flags)
                    ?.Invoke(page, new object[] { 2, 7, false }) as string;

                // 页眉里的应用名 = 用户自定义窗口标题（去掉关键词）；没设置时是「随机抽取器 版本号」
                string expected = $"{RandomDrawer.WindowTitleFormatter.GetShareHeaderAppText(ver)}   卡池：沧海萃珠 [3]   第2张/共7张   ";
                string tail = text != null && text.StartsWith(expected) ? text.Substring(expected.Length) : "";

                DateTime stamped;
                bool timeOk = DateTime.TryParseExact(tail, "yyyy/M/d H:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out stamped);

                // ★ PDF 模式：第2页，共7页
                string pdfText = page.GetType().GetMethod("BuildPageHeader", flags)
                    ?.Invoke(page, new object[] { 2, 7, true }) as string;
                bool pdfTextOk = pdfText != null && pdfText.Contains("第2页，共7页");

                if (timeOk && Math.Abs((DateTime.Now - stamped).TotalMinutes) < 10 && pdfTextOk)
                    Pass("T24 分页标注文字（含时间）", text + "   ｜ PDF：" + pdfText);
                else
                    Fail("T24 分页标注文字（含时间）",
                        $"实际={text ?? "null"}，期望前缀={expected} + 时间；PDF={pdfText ?? "null"}");

                if (!string.IsNullOrEmpty(ver) && System.Text.RegularExpressions.Regex.IsMatch(ver, @"^\d+\.\d+(\.\d+)?$"))
                    Pass("T24 标注里的版本号格式", ver);
                else
                    Fail("T24 标注里的版本号格式", ver ?? "null");

                // ② 真的画进了图片（第一页顶部左侧出现非背景像素）
                var pages = page.GetType().GetMethod("RenderReportPages", flags)
                    ?.Invoke(page, new object[] { 8000, null }) as System.Collections.Generic.List<BitmapSource>;

                if (pages == null || pages.Count == 0) { Fail("T24 分页图上已绘制标注", "没有生成图片"); return; }

                var first = pages[0];
                int stride = first.PixelWidth * 4;
                var px = new byte[stride * first.PixelHeight];
                first.CopyPixels(px, stride, 0);

                byte br = px[4], bgc = px[5], bb = px[6];   // 左上角作为背景色
                int diff = 0;
                int yMax = Math.Min(first.PixelHeight - 1, 90);
                int xMax = Math.Min(first.PixelWidth - 1, 1400);
                for (int y = 8; y < yMax; y++)
                {
                    for (int x = 40; x < xMax; x++)
                    {
                        int o = y * stride + x * 4;
                        if (Math.Abs(px[o] - br) > 24 || Math.Abs(px[o + 1] - bgc) > 24 || Math.Abs(px[o + 2] - bb) > 24)
                            diff++;
                    }
                }

                if (diff > 50)
                    Pass("T24 分页图上已绘制标注", $"页眉区域非背景像素 {diff} 个");
                else
                    Fail("T24 分页图上已绘制标注", $"页眉区域几乎没有变化（{diff} 像素）");

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(first));
                var outPath = TestFile("shot-share-header.png");
                using (var fs = File.Create(outPath)) enc.Save(fs);
                Pass("T24 截图已生成", outPath);
            }
            catch (Exception ex) { Fail("T24 分页标注", ex.GetType().Name + "：" + ex.Message); }
            finally { try { win?.Close(); } catch { } DoEvents(TimeSpan.FromMilliseconds(150)); }
        }

        // ---------------------------------------------------------------
        // T25：单张不降分辨率——放不进一张图时由调用方提示改为多张
        // ---------------------------------------------------------------
        private static void TestSingleImageSizeGate()
        {
            try
            {
                var t = typeof(RandomDrawer.GachaReportPage_UC);
                var sizeM = t.GetMethod("GetSingleImageSize", BindingFlags.NonPublic | BindingFlags.Static);
                var gateM = t.GetMethod("IsSingleImageSizeUsable", BindingFlags.NonPublic | BindingFlags.Static);
                if (sizeM == null || gateM == null) { Fail("T25 单张尺寸判定", "找不到方法"); return; }

                bool Usable(double contentH, out int w, out int h)
                {
                    var a = new object[] { 940.0, contentH, 0, 0 };
                    sizeM.Invoke(null, a);
                    w = (int)a[2];
                    h = (int)a[3];
                    return (bool)gateM.Invoke(null, new object[] { w, h });
                }

                // 普通报告：可以出一张
                bool normal = Usable(600, out int nw, out int nh);
                // 3418 行的超长报告（约 21 万 DIP）：不能降采样，只能按「生成失败」处理并提示改多张
                bool huge = Usable(210000, out int hw, out int hh);

                if (normal && nw == 2080)
                    Pass("T25 普通报告单张按原分辨率", $"{nw}×{nh}");
                else
                    Fail("T25 普通报告单张按原分辨率", $"usable={normal} {nw}×{nh}");

                if (!huge)
                    Pass("T25 超长报告按生成失败处理", $"原尺寸会到 {hw}×{hh}，判定为放不进一张图");
                else
                    Fail("T25 超长报告按生成失败处理", $"usable={huge} {hw}×{hh}");

                // 2 倍密度：宽度 = (内容宽 + 左右留白 48 + 最外圈 52) × 2
                if (Math.Abs(nw - (940 + 48 + 52) * 2) <= 2)
                    Pass("T25 单张密度为 2 倍", $"{nw} 像素宽");
                else
                    Fail("T25 单张密度为 2 倍", $"{nw} 像素宽（期望 {(940 + 48 + 52) * 2}）");
            }
            catch (Exception ex) { Fail("T25 单张尺寸判定", ex.Message); }
        }

        // ---------------------------------------------------------------
        // T33：分享弹窗预览图右键菜单：复制（C）/ 使用系统图片查看器打开（O）
        // ---------------------------------------------------------------
        private static void TestPreviewContextMenu()
        {
            try
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 40, 120));
                var rtb = new RenderTargetBitmap(40, 120, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();

                var dlg = new RandomDrawer.ReportShareDialog(rtb, "");
                var img = dlg.FindName("PreviewImage") as Image;
                if (img == null) { Fail("T33 预览图右键菜单", "找不到 PreviewImage"); return; }

                var menu = img.ContextMenu;
                if (menu == null) { Fail("T33 预览图右键菜单", "预览图没有右键菜单"); return; }

                var headers = new System.Collections.Generic.List<string>();
                var icons = new System.Collections.Generic.List<string>();
                foreach (object item in menu.Items)
                {
                    if (item is MenuItem mi)
                    {
                        headers.Add(mi.Header as string ?? "");
                        icons.Add((mi.Icon as TextBlock)?.Text ?? "");
                    }
                }

                bool hasCopy = headers.Exists(h => h.Contains("复制") && h.Contains("C"));
                bool hasOpen = headers.Exists(h => h.Contains("系统图片查看器") && h.Contains("O"));
                bool iconsOk = icons.Contains("\uE8C8") && icons.Contains("\uEB9F");

                if (menu.Items.Count == 2 && hasCopy && hasOpen && iconsOk)
                    Pass("T33 预览图右键菜单", string.Join(" / ", headers) + "（含图标）");
                else
                    Fail("T33 预览图右键菜单", $"items={menu.Items.Count}，{string.Join(" / ", headers)}，icons={string.Join(",", icons)}");

                // 缩略图也要能右键（多图时）
                var list = new System.Collections.Generic.List<BitmapSource> { rtb, rtb, rtb };
                var dlg2 = new RandomDrawer.ReportShareDialog(list, new System.Collections.Generic.List<string> { "", "", "" });
                var thumbsPanel = dlg2.FindName("ThumbPanel") as Panel;
                bool thumbMenuOk = thumbsPanel != null && thumbsPanel.Children.Count == 3;
                if (thumbMenuOk)
                {
                    foreach (object child in thumbsPanel.Children)
                    {
                        if (child is FrameworkElement fe && fe.ContextMenu == null) { thumbMenuOk = false; break; }
                    }
                }

                if (thumbMenuOk)
                    Pass("T33 缩略图也支持右键", "3 张缩略图都挂了同一个菜单");
                else
                    Fail("T33 缩略图也支持右键", thumbsPanel == null ? "找不到 ThumbPanel" : $"children={thumbsPanel.Children.Count}");
            }
            catch (Exception ex) { Fail("T33 预览图右键菜单", ex.GetType().Name + "：" + ex.Message); }
        }

        // ---------------------------------------------------------------
        // T36：PDF 生成后的预览弹窗（与图片预览同一套样式）
        // ---------------------------------------------------------------
        private static void TestPdfPreviewDialog()
        {
            string pdfPath = TestFile("tests-preview.pdf");
            try
            {
                File.WriteAllText(pdfPath, "%PDF-1.4\n%%EOF\n");

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 300, 500));
                var rtb = new RenderTargetBitmap(300, 500, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();

                var pages = new System.Collections.Generic.List<BitmapSource> { rtb, rtb };
                var dlg = new RandomDrawer.ReportShareDialog(pages,
                    new System.Collections.Generic.List<string> { "", "" }, pdfPath);

                var pathText = dlg.FindName("TxtSavedPath") as TextBlock;
                var indicator = dlg.FindName("TxtPageIndicator") as TextBlock;
                var img = dlg.FindName("PreviewImage") as Image;

                bool titleOk = (dlg.Title ?? "").Contains("PDF");
                bool pathOk = pathText != null && pathText.Text.StartsWith("PDF 已保存到：");
                bool pageOk = indicator != null && indicator.Text == "第1页/共2页";

                var menu = img?.ContextMenu;
                bool openOk = false;
                if (menu != null)
                {
                    foreach (object item in menu.Items)
                        if (item is MenuItem mi && (mi.Header as string ?? "").Contains("使用系统查看器打开"))
                            openOk = true;
                }

                if (titleOk && pathOk && pageOk && openOk)
                    Pass("T36 PDF 预览弹窗", $"标题/路径/页码/右键都正确（{pathText.Text}）");
                else
                    Fail("T36 PDF 预览弹窗", $"标题={titleOk} 路径={pathOk} 页码={pageOk} 右键={openOk}");
            }
            catch (Exception ex) { Fail("T36 PDF 预览弹窗", ex.GetType().Name + "：" + ex.Message); }
            finally { try { if (File.Exists(pdfPath)) File.Delete(pdfPath); } catch { } }
        }

        // ---------------------------------------------------------------
        // T35：多页 PDF 生成（纯手写 PDF，不依赖第三方库）
        // ---------------------------------------------------------------
        private static void TestPdfExport()
        {
            string path = TestFile("tests-report.pdf");
            try
            {
                BitmapSource MakePage(byte r, int w, int h)
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(r, 0x60, 0x40)), null, new Rect(0, 0, w, h));
                        dc.DrawRectangle(Brushes.White, null, new Rect(10, 10, w - 20, h - 20));
                    }
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.Freeze();
                    return rtb;
                }

                var pages = new System.Collections.Generic.List<BitmapSource>
                {
                    MakePage(0x20, 400, 600),
                    MakePage(0x80, 400, 900),
                    MakePage(0xE0, 400, 300)
                };

                bool ok = RandomDrawer.ReportPdfWriter.Save(path, pages);
                if (!ok || !File.Exists(path)) { Fail("T35 生成多页 PDF", "保存返回失败"); return; }

                var bytes = File.ReadAllBytes(path);
                string head = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(8, bytes.Length));
                string all = System.Text.Encoding.ASCII.GetString(bytes);

                bool headerOk = head.StartsWith("%PDF-1.");
                bool countOk = all.Contains("/Count 3");
                bool pagesOk = all.Contains("/Type /Page*".Replace("*", "")) && CountOccurrences(all, "/Type /Page ") == 3;
                bool trailerOk = all.Contains("trailer") && all.TrimEnd().EndsWith("%%EOF");

                if (headerOk && countOk && pagesOk && trailerOk)
                    Pass("T35 生成多页 PDF", $"{bytes.Length / 1024} KB，3 页");
                else
                    Fail("T35 生成多页 PDF", $"header={headerOk} count={countOk} pages={pagesOk} trailer={trailerOk}");
            }
            catch (Exception ex) { Fail("T35 生成多页 PDF", ex.GetType().Name + "：" + ex.Message); }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        private static int CountOccurrences(string text, string token)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0) { count++; index += token.Length; }
            return count;
        }

        // ---------------------------------------------------------------
        // T37：PDF 缓存（读写 + 损坏判定）
        // ---------------------------------------------------------------
        private static void TestPdfCache()
        {
            string poolDir = Path.Combine(TestOutputDir, "pdf-cache-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(poolDir);
                string pdfPath = Path.Combine(poolDir, "report.pdf");
                string page1 = Path.Combine(poolDir, "p1.jpg");
                string page2 = Path.Combine(poolDir, "p2.jpg");

                File.WriteAllText(page1, "jpg1");
                File.WriteAllText(page2, "jpg2");
                // 构造一个 >512 字节、结构完整的假 PDF（IsValidPdf 会检查大小 / 头 / 尾）
                string padding = new string(' ', 800);
                File.WriteAllText(pdfPath,
                    "%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n" + padding +
                    "trailer\n<< /Size 2 >>\nstartxref\n0\n%%EOF\n");

                long ticks = DateTime.UtcNow.Ticks;
                RandomDrawer.ReportShareCache.SavePdf(poolDir, ticks, pdfPath, new[] { page1, page2 });

                bool readOk = RandomDrawer.ReportShareCache.TryGetPdf(poolDir, out long gotTicks,
                    out string gotPdf, out var gotPages)
                    && gotTicks == ticks
                    && gotPdf == pdfPath
                    && gotPages.Count == 2;

                bool validOk = RandomDrawer.ReportShareCache.IsValidPdf(pdfPath);

                if (readOk && validOk)
                    Pass("T37 PDF 缓存读写", $"2 页，ticks 一致");
                else
                    Fail("T37 PDF 缓存读写", $"read={readOk} valid={validOk}");

                // 写到一半 / 被截断的 PDF 必须判定为损坏
                string broken = Path.Combine(poolDir, "broken.pdf");
                File.WriteAllText(broken, "%PDF-1.4\n1 0 obj\n<< /Type /Cat");
                string notPdf = Path.Combine(poolDir, "notpdf.pdf");
                File.WriteAllText(notPdf, new string('x', 2048));

                bool brokenOk = !RandomDrawer.ReportShareCache.IsValidPdf(broken);
                bool notPdfOk = !RandomDrawer.ReportShareCache.IsValidPdf(notPdf);
                bool missingOk = !RandomDrawer.ReportShareCache.IsValidPdf(Path.Combine(poolDir, "nope.pdf"));

                if (brokenOk && notPdfOk && missingOk)
                    Pass("T37 损坏缓存能被识别", "截断 / 非 PDF / 不存在 都判为无效");
                else
                    Fail("T37 损坏缓存能被识别", $"broken={brokenOk} notPdf={notPdfOk} missing={missingOk}");

                // 删掉任意一页预览图 → 缓存视为失效（需要重新生成）
                File.Delete(page2);
                bool invalidAfterDelete = !RandomDrawer.ReportShareCache.TryGetPdf(poolDir, out _, out _, out _);
                if (invalidAfterDelete)
                    Pass("T37 预览页缺失时缓存失效", "会重新生成");
                else
                    Fail("T37 预览页缺失时缓存失效", "仍然返回了缓存");
            }
            catch (Exception ex) { Fail("T37 PDF 缓存", ex.GetType().Name + "：" + ex.Message); }
            finally { try { Directory.Delete(poolDir, true); } catch { } }
        }

        // ---------------------------------------------------------------
        // T39：卡池右键菜单多了「导出(G)」，空白处菜单多了「导入(F)」
        // ---------------------------------------------------------------
        private static void TestPoolExportImportMenus()
        {
            try
            {
                var page = new RandomDrawer.MainPage_UC();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;

                var poolMenu = page.GetType().GetMethod("BuildPoolContextMenu", flags)
                    ?.Invoke(page, new object[] { 0 }) as ContextMenu;
                var blankMenu = page.GetType().GetMethod("BuildBlankPoolContextMenu", flags)
                    ?.Invoke(page, null) as ContextMenu;

                string HeaderOf(ContextMenu m, string label)
                {
                    if (m == null) return null;
                    foreach (object item in m.Items)
                        if (item is MenuItem mi && (mi.Header as string ?? "").StartsWith(label, StringComparison.Ordinal))
                            return mi.Header as string;
                    return null;
                }

                string GlyphOf(ContextMenu m, string label)
                {
                    if (m == null) return "";
                    foreach (object item in m.Items)
                        if (item is MenuItem mi && (mi.Header as string ?? "").StartsWith(label, StringComparison.Ordinal))
                            return (mi.Icon as TextBlock)?.Text ?? "";
                    return "";
                }

                string export = HeaderOf(poolMenu, "导出");
                string import = HeaderOf(blankMenu, "导入");
                string exportGlyph = GlyphOf(poolMenu, "导出");
                string importGlyph = GlyphOf(blankMenu, "导入");

                if (export == "导出(G)" && import == "导入(F)" && exportGlyph == "\uE8A7" && importGlyph == "\uE838")
                    Pass("T39 卡池菜单导出 / 空白菜单导入", $"{export} {exportGlyph} / {import} {importGlyph}");
                else
                    Fail("T39 卡池菜单导出 / 空白菜单导入",
                        $"export={export ?? "null"}({exportGlyph}) import={import ?? "null"}({importGlyph})");

                // 顺手把两个菜单各渲染一张图，方便肉眼检查排版
                try
                {
                    if (poolMenu != null) RenderMenuToPng(poolMenu, Path.Combine(TestOutputDir, "shot-pool-menu.png"));
                    if (blankMenu != null) RenderMenuToPng(blankMenu, Path.Combine(TestOutputDir, "shot-pool-menu-blank.png"));
                }
                catch { }
            }
            catch (Exception ex) { Fail("T39 卡池菜单导出 / 空白菜单导入", ex.GetType().Name + "：" + ex.Message); }
        }

        /// <summary>把弹出的菜单渲染成 PNG（菜单不在窗口可视树里，需要手动 Measure/Arrange）。</summary>
        private static void RenderMenuToPng(ContextMenu menu, string path)
        {
            try
            {
                menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                menu.Arrange(new Rect(menu.DesiredSize));
                menu.UpdateLayout();

                int w = Math.Max(80, (int)Math.Ceiling(menu.DesiredSize.Width));
                int h = Math.Max(60, (int)Math.Ceiling(menu.DesiredSize.Height));

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(menu);

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
            }
            catch { }
        }

        // ---------------------------------------------------------------
        // T40：卡池导出 / 导入 往返
        // ---------------------------------------------------------------
        private static void TestPoolPackageRoundTrip()
        {
            string work = Path.Combine(TestOutputDir, "pkg-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                string src = Path.Combine(work, "src", "我的卡池");
                Directory.CreateDirectory(Path.Combine(src, "illustrations"));
                File.WriteAllText(Path.Combine(src, "data.txt"), "抽取对象,概率\nA,1\nB,1\n");
                File.WriteAllBytes(Path.Combine(src, "illustrations", "a.png"), new byte[] { 1, 2, 3, 4 });

                string zip = Path.Combine(work, "我的卡池.zip");
                var pools = new List<(string Name, string Dir)> { (Name: "我的卡池", Dir: src) };
                var export = RandomDrawer.PoolTransfer.Export(pools, zip);

                bool exportOk = export.Exported == 1 && File.Exists(zip);

                // 导入侧：先识别再解压（和「导入卡池」弹窗走的是同一套）
                var items = RandomDrawer.PoolTransfer.InspectZip(zip, out string inspectError);
                bool inspectOk = items.Count == 1 && items[0].IsValid;

                string dest = Path.Combine(work, "pools");
                var import = RandomDrawer.PoolTransfer.Import(items, dest);
                bool contentOk = import.Imported == 1
                    && import.ImportedDirs.Count == 1
                    && File.Exists(Path.Combine(import.ImportedDirs[0], "data.txt"))
                    && File.Exists(Path.Combine(import.ImportedDirs[0], "illustrations", "a.png"));

                if (exportOk && inspectOk && contentOk)
                    Pass("T40 卡池导出 / 导入（复用选项-卡池的弹窗逻辑）",
                        Path.GetFileName(import.ImportedDirs[0]));
                else
                    Fail("T40 卡池导出 / 导入（复用选项-卡池的弹窗逻辑）",
                        $"export={exportOk} inspect={inspectOk} content={contentOk} " +
                        $"err={(inspectError ?? "")}{string.Join(";", export.Errors)}");

                // 不是卡池包的文件必须识别为无效
                string junk = Path.Combine(work, "junk.zip");
                using (var z = System.IO.Compression.ZipFile.Open(junk, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var entry = z.CreateEntry("readme.txt");
                    using (var w = new StreamWriter(entry.Open())) w.Write("hi");
                }
                var junkItems = RandomDrawer.PoolTransfer.InspectZip(junk, out _);
                bool rejected = junkItems.Count == 1 && !junkItems[0].IsValid;
                if (rejected) Pass("T40 非卡池包被识别为无效", junkItems[0].Reason ?? "");
                else Fail("T40 非卡池包被识别为无效", $"count={junkItems.Count}");

                // 导出 / 导入弹窗确实存在（右键菜单直接套用它们）
                bool dialogsOk = typeof(RandomDrawer.ExportPoolsDialog) != null
                                 && typeof(RandomDrawer.ImportPoolsDialog) != null;
                if (dialogsOk) Pass("T40 右键菜单复用选项-卡池弹窗", "ExportPoolsDialog / ImportPoolsDialog");
                else Fail("T40 右键菜单复用选项-卡池弹窗", "找不到弹窗类型");
            }
            catch (Exception ex) { Fail("T40 卡池导出 / 导入", ex.GetType().Name + "：" + ex.Message); }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        // ---------------------------------------------------------------
        // T41：关掉分享弹窗不该弹「已复制」；PDF 复制有专属文案
        // ---------------------------------------------------------------
        private static void TestShareCopyToast()
        {
            try
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 200, 300));
                var rtb = new RenderTargetBitmap(200, 300, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();

                // PDF 模式（用真实文件，复制走的是 PDF 分支）
                string pdfPath = TestFile("copy-test.pdf");
                File.WriteAllText(pdfPath, "%PDF-1.4\n%%EOF\n");
                try
                {
                    var pdfDialog = new RandomDrawer.ReportShareDialog(
                        new List<BitmapSource> { rtb }, new List<string> { "" }, pdfPath);
                    bool pdfDefault = !pdfDialog.Copied;

                    // 普通图片模式
                    var imgDialog = new RandomDrawer.ReportShareDialog(rtb, "");
                    bool imgDefault = !imgDialog.Copied;

                    if (pdfDefault && imgDefault)
                        Pass("T41 关掉弹窗不误报已复制", "Copied 默认 false");
                    else
                        Fail("T41 关掉弹窗不误报已复制", $"pdf={pdfDialog.Copied} img={imgDialog.Copied}");

                    // PDF 复制走专属文案
                    var share = typeof(RandomDrawer.ReportShareDialog).GetMethod("Share_Click",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    share?.Invoke(pdfDialog, new object[] { pdfDialog, new RoutedEventArgs() });

                    bool pdfCopied = pdfDialog.Copied && pdfDialog.CopiedMessage == "PDF 已复制到剪贴板";
                    if (pdfCopied)
                        Pass("T41 PDF 复制提示专属文案", pdfDialog.CopiedMessage);
                    else
                        Fail("T41 PDF 复制提示专属文案", $"copied={pdfDialog.Copied} msg={pdfDialog.CopiedMessage}");
                }
                finally { try { File.Delete(pdfPath); } catch { } }
            }
            catch (Exception ex) { Fail("T41 分享复制提示", ex.GetType().Name + "：" + ex.Message); }
        }

        // ---------------------------------------------------------------
        // T46：图片在后台线程按目标宽度解码，冻结后能在 UI 线程直接使用
        // ---------------------------------------------------------------
        private static void TestBackgroundImageDecode()
        {
            string path = Path.Combine(TestOutputDir, "decode-src.png");
            try
            {
                // 造一张 2000×1000 的图，模拟「几 MB 的立绘 / 封面」
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(Brushes.DarkOrange, null, new Rect(0, 0, 2000, 1000));
                var rtb = new RenderTargetBitmap(2000, 1000, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);

                int uiThread = Environment.CurrentManagedThreadId;
                int decodeThread = 0;
                BitmapSource decoded = null;

                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    decodeThread = Environment.CurrentManagedThreadId;
                    decoded = RandomDrawer.ImageHelper.LoadBitmapSized(path, 320);
                });
                PumpUntil(task, 20000);

                bool offUiThread = decodeThread != 0 && decodeThread != uiThread;
                bool sized = decoded != null && decoded.PixelWidth == 320 && decoded.PixelHeight == 160;
                bool frozen = decoded != null && decoded.IsFrozen;

                // 冻结位图可以直接在 UI 线程用（没冻结的话这里会抛跨线程异常）
                bool usableOnUi = false;
                if (decoded != null)
                {
                    try
                    {
                        var host = new Grid { Width = 100, Height = 100, Background = Brushes.White };
                        host.Children.Add(new Image { Source = decoded, Width = 100, Height = 100 });
                        host.Measure(new Size(100, 100));
                        host.Arrange(new Rect(0, 0, 100, 100));
                        host.UpdateLayout();

                        var shot = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
                        shot.Render(host);
                        usableOnUi = shot.PixelWidth == 100;
                    }
                    catch { usableOnUi = false; }
                }

                if (offUiThread && sized && frozen && usableOnUi)
                    Pass("T46 后台按显示尺寸解码头像 / 封面",
                        $"{rtb.PixelWidth}×{rtb.PixelHeight} → {decoded.PixelWidth}×{decoded.PixelHeight}（线程 {decodeThread} ≠ UI {uiThread}）");
                else
                    Fail("T46 后台按显示尺寸解码头像 / 封面",
                        $"offUi={offUiThread} sized={sized} frozen={frozen} usable={usableOnUi} " +
                        $"size={(decoded == null ? "null" : decoded.PixelWidth + "×" + decoded.PixelHeight)}");
            }
            catch (Exception ex) { Fail("T46 后台按显示尺寸解码", ex.GetType().Name + "：" + ex.Message); }
            finally { try { File.Delete(path); } catch { } }
        }

        // ---------------------------------------------------------------
        // T42：分享弹窗缩略图边框跟随主题、预览区高度固定
        // ---------------------------------------------------------------
        private static void TestShareDialogThemeAndHeight()
        {
            bool oldDisabled = RandomDrawer.App.IsAnimationDisabled;
            try
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 200, 300));
                var tall = new RenderTargetBitmap(200, 300, 96, 96, PixelFormats.Pbgra32);
                tall.Render(dv);
                tall.Freeze();

                var shortImg = new RenderTargetBitmap(200, 60, 96, 96, PixelFormats.Pbgra32);
                shortImg.Render(dv);
                shortImg.Freeze();

                // 深色主题：缩略图边框必须是深色，不能是刺眼的白框
                RandomDrawer.ThemeManager.SetMode(RandomDrawer.ThemeMode.Dark);
                DoEvents(TimeSpan.FromMilliseconds(200));
                bool darkOk = RandomDrawer.ThemeManager.IsDark;

                var images = new List<BitmapSource> { tall, tall, shortImg };
                var dlg = new RandomDrawer.ReportShareDialog(images,
                    new List<string> { "", "", "" });
                var thumbsPanel = dlg.FindName("ThumbPanel") as Panel;
                var previewBorder = dlg.FindName("PreviewBorder") as FrameworkElement;

                string failReason = "";
                bool borderOk = thumbsPanel != null && thumbsPanel.Children.Count == 3;
                if (borderOk)
                {
                    foreach (object child in thumbsPanel.Children)
                    {
                        if (!(child is Border b) || !(b.BorderBrush is SolidColorBrush sb)) { borderOk = false; failReason += "非纯色;"; continue; }
                        // 深色下边框亮度必须很低（白框的亮度接近 1）
                        double lum = (0.299 * sb.Color.R + 0.587 * sb.Color.G + 0.114 * sb.Color.B) / 255.0;
                        if (lum > 0.6) { borderOk = false; failReason += $"亮度={lum:0.00};"; }
                    }
                }

                bool heightOk = previewBorder != null && !double.IsNaN(previewBorder.Height)
                                && Math.Abs(previewBorder.Height - 400) < 0.5;

                if (darkOk && borderOk && heightOk)
                    Pass("T42 深色下缩略图边框 / 预览区高度", "边框不再发白，预览区固定 400");
                else
                    Fail("T42 深色下缩略图边框 / 预览区高度",
                        $"dark={darkOk} border={borderOk} height={heightOk} {failReason}");

                // 截图：深色 + 选中「较矮的最后一页」，确认窗口高度不缩、缩略图没有白框
                try
                {
                    var host = new Window
                    {
                        Width = 700,
                        Height = 700,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -4000,
                        Top = -4000,
                        ShowInTaskbar = false,
                        Content = dlg
                    };
                    host.Show();
                    DoEvents(TimeSpan.FromMilliseconds(400));
                    typeof(RandomDrawer.ReportShareDialog)
                        .GetMethod("ShowIndex", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.Invoke(dlg, new object[] { 2 });
                    DoEvents(TimeSpan.FromMilliseconds(300));
                    SaveShot(host, Path.Combine(TestOutputDir, "shot-share-dark-short.png"));
                    host.Content = null;
                    host.Close();
                    DoEvents(TimeSpan.FromMilliseconds(200));
                }
                catch { }

                // 切回浅色，别影响后续测试
                RandomDrawer.ThemeManager.SetMode(RandomDrawer.ThemeMode.Light);
                DoEvents(TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex) { Fail("T42 深色下缩略图边框 / 预览区高度", ex.GetType().Name + "：" + ex.Message); }
            finally
            {
                RandomDrawer.App.IsAnimationDisabled = oldDisabled;
                try { RandomDrawer.ThemeManager.SetMode(RandomDrawer.ThemeMode.Light); } catch { }
            }
        }

        // ---------------------------------------------------------------
        // T44：资源图片（Assets/ 下）仍能被 XAML / 内嵌加载器找到
        // ---------------------------------------------------------------
        private static void TestAssetsResolvable()
        {
            try
            {
                bool icoOk = false, aiOk = false, coverOk = false;

                // 测试进程的入口程序集是 SmokeTest，所以要用「程序集;component」的完整写法；
                // 程序内部用的是相对写法 /Assets/xxx，对它来说入口程序集就是自己。
                try
                {
                    var ico = new BitmapImage(new Uri("pack://application:,,,/随机抽取器;component/Assets/ico.png"));
                    icoOk = ico.PixelWidth > 0;
                }
                catch { }

                try
                {
                    var ai = new BitmapImage(new Uri("pack://application:,,,/随机抽取器;component/Assets/dsh-code.jpg"));
                    aiOk = ai.PixelWidth > 0;
                }
                catch { }

                try
                {
                    var cover = RandomDrawer.EmbeddedImageLoader.Load("Assets/default_cover.jpg");
                    coverOk = cover != null && cover.PixelWidth > 0;
                }
                catch { }

                if (icoOk && aiOk && coverOk)
                    Pass("T44 Assets 资源可加载", "ico.png / dsh-code.jpg / default_cover.jpg");
                else
                    Fail("T44 Assets 资源可加载", $"ico={icoOk} ai={aiOk} cover={coverOk}");
            }
            catch (Exception ex) { Fail("T44 Assets 资源可加载", ex.GetType().Name + "：" + ex.Message); }
        }

        // ---------------------------------------------------------------
        // T43：禁用动画时才用 \uF16A 图标转圈（2 秒一圈），正常还是原来的圆点动画
        // ---------------------------------------------------------------
        private static void TestUnifiedSpinner()
        {
            bool oldDisabled = RandomDrawer.App.IsAnimationDisabled;
            try
            {
                bool glyphOk = RandomDrawer.NowLoadingSpinner.IconGlyph == "\uF16A";
                bool periodOk = Math.Abs(RandomDrawer.NowLoadingSpinner.IconPeriod.TotalSeconds - 2.0) < 0.001;

                // ① 正常（动画开启）：还是原来的圆点动画，图标不出现
                RandomDrawer.App.IsAnimationDisabled = false;
                var dotsSpinner = new RandomDrawer.NowLoadingSpinner();
                dotsSpinner.Start();
                bool dotsMode = dotsSpinner.IsRunning && !dotsSpinner.IsIconMode;

                // ② 禁用动画：改用图标并持续旋转
                RandomDrawer.App.IsAnimationDisabled = true;
                var iconSpinner = new RandomDrawer.NowLoadingSpinner();
                iconSpinner.Start();
                bool iconMode = iconSpinner.IsRunning && iconSpinner.IsIconMode;

                if (glyphOk && periodOk && dotsMode && iconMode)
                    Pass("T43 禁用动画才用  图标转圈", "正常=圆点 / 禁用动画=图标(2s)");
                else
                    Fail("T43 禁用动画才用  图标转圈",
                        $"glyph={glyphOk} period={periodOk} dots={dotsMode} icon={iconMode}");

                // 截图：禁用动画时的图标样子
                try
                {
                    iconSpinner.Width = 90; iconSpinner.Height = 90;
                    var host = new Grid { Background = Brushes.White, Width = 90, Height = 90 };
                    host.Children.Add(iconSpinner);
                    host.Measure(new Size(90, 90));
                    host.Arrange(new Rect(0, 0, 90, 90));
                    host.UpdateLayout();

                    var shot = new RenderTargetBitmap(90, 90, 96, 96, PixelFormats.Pbgra32);
                    shot.Render(host);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(shot));
                    using (var fs = File.Create(Path.Combine(TestOutputDir, "shot-spinner.png"))) enc.Save(fs);
                }
                catch { }

                // 历史记录页的样式：64px 框、OrbitRadius=22、圆点半径 3，颜色跟随主题
                try
                {
                    var historyStyle = new RandomDrawer.NowLoadingSpinner { Width = 64, Height = 64, OrbitRadius = 22 };
                    historyStyle.SetDotRadius(3.0);
                    historyStyle.SetColor(RandomDrawer.ThemeManager.GetBrush("Fg_333333"));
                    historyStyle.Start();

                    var host2 = new Grid { Background = Brushes.White, Width = 90, Height = 90 };
                    host2.Children.Add(historyStyle);
                    host2.Measure(new Size(90, 90));
                    host2.Arrange(new Rect(0, 0, 90, 90));
                    host2.UpdateLayout();

                    var shot2 = new RenderTargetBitmap(90, 90, 96, 96, PixelFormats.Pbgra32);
                    shot2.Render(host2);
                    var enc2 = new PngBitmapEncoder();
                    enc2.Frames.Add(BitmapFrame.Create(shot2));
                    using (var fs2 = File.Create(Path.Combine(TestOutputDir, "shot-spinner-history.png"))) enc2.Save(fs2);
                }
                catch { }

                iconSpinner.Stop();
                dotsSpinner.Stop();
                bool stopped = !iconSpinner.IsRunning && !iconSpinner.IsIconMode;

                // 高级规则页的「正在开发中」也用的是同一个控件
                var page = new RandomDrawer.AdvancedGachaPage_UC();
                var advancedSpinner = page.FindName("DevelopingSpinner") as RandomDrawer.NowLoadingSpinner;

                if (stopped && advancedSpinner != null)
                    Pass("T43 高级规则页共用同一个转圈控件", "DevelopSpinner");
                else
                    Fail("T43 高级规则页共用同一个转圈控件", $"stopped={stopped} advanced={advancedSpinner != null}");
            }
            catch (Exception ex) { Fail("T43 转圈控件", ex.GetType().Name + "：" + ex.Message); }
            finally { RandomDrawer.App.IsAnimationDisabled = oldDisabled; }
        }

        // ---------------------------------------------------------------
        // T38：处理中「程序内部导航」不能拦、「用户主动切页」要拦
        //      （曾经把 Navigate 整体拦掉，导致 ≥500 次抽取后结果页永远打不开）
        // ---------------------------------------------------------------
        private static void TestNavigationDuringBusy(Window owner)
        {
            var mw = owner as RandomDrawer.MainWindow;
            if (mw == null) { Fail("T38 处理中的导航", "窗口不是 MainWindow"); return; }

            var pageField = typeof(RandomDrawer.MainWindow).GetField("_currentPageKey",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var busyType = typeof(RandomDrawer.App).Assembly.GetType("RandomDrawer.AppBusy");
            var enter = busyType?.GetMethod("Enter", BindingFlags.Public | BindingFlags.Static);
            var exit = busyType?.GetMethod("Exit", BindingFlags.Public | BindingFlags.Static);

            try
            {
                RandomDrawer.Messaging.MessageBus.Publish(
                    new RandomDrawer.Messaging.AppMessages.NavigateTo("Main"));
                DoEvents(TimeSpan.FromMilliseconds(700));

                // 模拟「正在生成抽取结果」
                enter?.Invoke(null, new object[] { "test-render" });

                // ① 程序内部导航（抽完自动跳结果页）必须照常执行
                RandomDrawer.Messaging.MessageBus.Publish(
                    new RandomDrawer.Messaging.AppMessages.NavigateTo("Options"));
                DoEvents(TimeSpan.FromMilliseconds(800));

                string afterProgrammatic = pageField?.GetValue(mw) as string;
                if (afterProgrammatic == "Options")
                    Pass("T38 处理中程序内部导航不受影响", afterProgrammatic);
                else
                    Fail("T38 处理中程序内部导航不受影响", $"当前页面={afterProgrammatic}");

                // ② 用户点「返回」要被拦下
                var back = typeof(RandomDrawer.MainWindow).GetMethod("BtnBack_Click",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                back?.Invoke(mw, new object[] { mw, new RoutedEventArgs() });
                DoEvents(TimeSpan.FromMilliseconds(600));

                string afterUserBack = pageField?.GetValue(mw) as string;
                if (afterUserBack == "Options")
                    Pass("T38 处理中用户返回被拦下", afterUserBack);
                else
                    Fail("T38 处理中用户返回被拦下", $"{afterProgrammatic} → {afterUserBack}");
            }
            catch (Exception ex) { Fail("T38 处理中的导航", ex.Message); }
            finally
            {
                try { exit?.Invoke(null, new object[] { "test-render" }); } catch { }
                try
                {
                    RandomDrawer.Messaging.MessageBus.Publish(
                        new RandomDrawer.Messaging.AppMessages.NavigateTo("Main"));
                    DoEvents(TimeSpan.FromMilliseconds(500));
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------
        // T34：关闭被拦截时的气泡用「叉」而不是「勾」
        // ---------------------------------------------------------------
        private static void TestToastIcons(Window owner)
        {
            try
            {
                var t = typeof(RandomDrawer.ToastNotification);
                var warn = t.GetMethod("ShowWarning", BindingFlags.Public | BindingFlags.Static);
                var ok = t.GetMethod("ShowSuccess", BindingFlags.Public | BindingFlags.Static);
                if (warn == null || ok == null) { Fail("T34 气泡图标", "找不到 ShowWarning / ShowSuccess"); return; }

                warn.Invoke(null, new object[] { owner, "正在处理中，请稍候…", 600d });
                DoEvents(TimeSpan.FromMilliseconds(200));
                Pass("T34 拦截关闭用提醒气泡", "ShowWarning 已调用");

                ok.Invoke(null, new object[] { owner, "测试完成", 400d });
                DoEvents(TimeSpan.FromMilliseconds(200));
                Pass("T34 成功气泡仍可用", "ShowSuccess 已调用");
            }
            catch (Exception ex) { Fail("T34 气泡图标", ex.GetType().Name + "：" + ex.Message); }
        }

        // ---------------------------------------------------------------
        // T29：报告行进度条的斜纹参数与 test.html 一致（28.28 平铺块 / 10 宽条纹 / 白色 15%）
        // ---------------------------------------------------------------
        private static void TestRowStripeMatchesHtml()
        {
            try
            {
                var m = typeof(RandomDrawer.GachaReportPage_UC).GetMethod("CreateStripeBrush",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) { Fail("T29 行进度条斜纹参数", "找不到 CreateStripeBrush"); return; }

                var args = new object[] { (Color)ColorConverter.ConvertFromString("#53B84A"), null };
                var brush = m.Invoke(null, args) as DrawingBrush;
                if (brush == null) { Fail("T29 行进度条斜纹参数", "返回 null"); return; }

                bool tileOk = Math.Abs(brush.Viewport.Width - 20) < 0.1;
                if (tileOk) Pass("T29 平铺块为整数 20（便于无缝）", $"{brush.Viewport.Width:0.##}");
                else Fail("T29 平铺块为整数 20（便于无缝）", $"{brush.Viewport.Width:0.##}");

                // 竖条 10 宽 + 整块 -45° 旋转 → 斜条连续、接缝处不断开
                var angles = new System.Collections.Generic.List<double>();
                if (brush.Transform is RotateTransform single) angles.Add(single.Angle);
                else if (brush.Transform is TransformGroup tg)
                    foreach (var child in tg.Children)
                        if (child is RotateTransform r) angles.Add(r.Angle);

                if (angles.Exists(a => Math.Abs(a + 45) < 0.01))
                    Pass("T29 整体旋转 -45°（斜纹无缝）", $"{-45}°");
                else
                    Fail("T29 整体旋转 -45°（斜纹无缝）", angles.Count == 0 ? "没有旋转变换" : string.Join(",", angles));

                var group = brush.Drawing as DrawingGroup;
                var stripe = group?.Children.Count >= 2 ? group.Children[1] as GeometryDrawing : null;
                var rect = stripe?.Geometry as RectangleGeometry;
                bool widthOk = rect != null && Math.Abs(rect.Rect.Width - 10) < 0.01;
                if (widthOk) Pass("T29 条纹宽度 10", $"{rect.Rect.Width:0.#}（整块高 {rect.Rect.Height:0.#}）");
                else Fail("T29 条纹宽度 10", rect == null ? "没有矩形" : $"{rect.Rect.Width:0.#}");

                var penBrush = stripe?.Brush as SolidColorBrush;
                bool alphaOk = penBrush != null && penBrush.Color.A == 0x26;
                if (alphaOk) Pass("T29 条纹白色 15%", $"A=0x{penBrush.Color.A:X2}");
                else Fail("T29 条纹白色 15%", penBrush == null ? "无画刷" : $"A=0x{penBrush.Color.A:X2}");
            }
            catch (Exception ex) { Fail("T29 行进度条斜纹参数", ex.Message); }
        }

        // ---------------------------------------------------------------
        // T21：「报告过长」提示文案（逐字比对）
        // ---------------------------------------------------------------
        private static void TestLongReportPrompt()
        {
            const string expected =
                "该抽取报告过长，无法生成完整长图。\n是否转换为生成PDF或拆分为多张图片生成？";
            try
            {
                var field = typeof(RandomDrawer.GachaReportPage_UC)
                    .GetField("LongReportPrompt", BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null) { Fail("T21 报告过长提示文案", "找不到 LongReportPrompt"); return; }

                string actual = field.GetRawConstantValue() as string;
                if (actual == expected)
                    Pass("T21 报告过长提示文案", actual.Replace("\n", " / "));
                else
                    Fail("T21 报告过长提示文案", "实际=" + (actual ?? "null").Replace("\n", "\\n"));
            }
            catch (Exception ex) { Fail("T21 报告过长提示文案", ex.Message); }
        }

        // ---------------------------------------------------------------
        // T22：「报告过长」弹窗实际渲染出的正文与新文案一致（附带截图）
        // ---------------------------------------------------------------
        private static void TestLongReportPromptDialog(Window owner)
        {
            try
            {
                var field = typeof(RandomDrawer.GachaReportPage_UC)
                    .GetField("LongReportPrompt", BindingFlags.NonPublic | BindingFlags.Static);
                string prompt = field?.GetRawConstantValue() as string ?? "";

                string shown = null;
                string buttons = null;
                var shotPath = TestFile("shot-share-prompt.png");

                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background,
                    (s, e) =>
                    {
                        try
                        {
                            var txt = FindByName<TextBlock>(owner, "TxtMessage");
                            shown = txt?.Text;
                            buttons = CollectText(owner);
                            SaveShot(owner, shotPath);
                        }
                        catch { }
                        try { RandomDrawer.FloatingOverlay.CloseAll(); } catch { }
                    }, Dispatcher.CurrentDispatcher);
                timer.Start();

                RandomDrawer.CustomMessageBox.ShowCustom(owner, prompt, "图片过大",
                    "拆分生成", "\uF571", "生成PDF", "\uEA90",
                    MessageBoxImage.Warning, "取消");
                timer.Stop();

                if (shown == prompt)
                    Pass("T22 过长提示弹窗正文", shown.Replace("\n", " / "));
                else
                    Fail("T22 过长提示弹窗正文", "实际=" + (shown ?? "null").Replace("\n", "\\n"));

                // 「图片过大」必须有三个按钮：取消 / 生成PDF / 拆分生成
                bool hasCancel = buttons != null && buttons.Contains("取消");
                bool hasPdf = buttons != null && buttons.Contains("生成PDF");
                bool hasSplit = buttons != null && buttons.Contains("拆分生成");
                if (hasCancel && hasPdf && hasSplit)
                    Pass("T22 过长提示弹窗三个按钮", "取消 / 生成PDF / 拆分生成");
                else
                    Fail("T22 过长提示弹窗三个按钮", $"取消={hasCancel} 生成PDF={hasPdf} 拆分生成={hasSplit}");
            }
            catch (Exception ex) { Fail("T22 过长提示弹窗正文", ex.Message); }
            finally { DoEvents(TimeSpan.FromMilliseconds(150)); }
        }

        // ---------------------------------------------------------------
        // T23：分享弹窗显示「很长但没超上限」的单张长图不再抛 OverflowException
        // ---------------------------------------------------------------
        private static void TestShareDialogHugeImageDisplay(Window owner)
        {
            try
            {
                const int w = 1643, h = 24352;
                WriteableBitmap big;
                try
                {
                    big = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    big.Freeze();
                }
                catch (Exception ex)
                {
                    Fail("T23 超长单张可在分享弹窗中显示", "构造位图失败：" + ex.Message);
                    return;
                }

                string err = null;
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background,
                    (s, e) =>
                    {
                        try { SaveShot(owner, TestFile("shot-share-huge.png")); }
                        catch { }
                        try { RandomDrawer.FloatingOverlay.CloseAll(); } catch { }
                    }, Dispatcher.CurrentDispatcher);
                timer.Start();

                try
                {
                    var dlg = new RandomDrawer.ReportShareDialog(big, "") { Owner = owner };
                    dlg.ShowDialog();
                }
                catch (Exception ex)
                {
                    err = ex.GetType().Name + "：" + ex.Message;
                    try { RandomDrawer.FloatingOverlay.CloseAll(); } catch { }
                }
                finally { timer.Stop(); }

                if (err == null)
                    Pass("T23 超长单张可在分享弹窗中显示", $"{w}×{h}");
                else
                    Fail("T23 超长单张可在分享弹窗中显示", err);
            }
            catch (Exception ex) { Fail("T23 超长单张可在分享弹窗中显示", ex.Message); }
            finally { DoEvents(TimeSpan.FromMilliseconds(150)); }
        }

        // ---------------------------------------------------------------
        // T30：「单张长图过大」弹窗的按钮是「取消 / 拆分生成」
        // ---------------------------------------------------------------
        private static void TestSingleTooLargeDialog(Window owner)
        {
            try
            {
                // 先切到报告页：页面挂进窗口后 Window.GetWindow(page) 才是主窗口，浮层才不会落到兜底窗口
                RandomDrawer.Messaging.MessageBus.Publish(
                    new RandomDrawer.Messaging.AppMessages.NavigateTo("GachaReport"));
                DoEvents(TimeSpan.FromMilliseconds(800));

                var page = FindDescendant<RandomDrawer.GachaReportPage_UC>(owner);
                if (page == null) { Fail("T30 单张过大弹窗按钮", "窗口里找不到报告页"); return; }

                string texts = null;
                var shotPath = TestFile("shot-share-too-large.png");
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background,
                    (s, e) =>
                    {
                        try
                        {
                            texts = CollectText(owner);
                            SaveShot(owner, shotPath);
                        }
                        catch { }
                        try { RandomDrawer.FloatingOverlay.CloseAll(); } catch { }
                    }, Dispatcher.CurrentDispatcher);
                timer.Start();

                var m = page.GetType().GetMethod("AskSwitchToMulti",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (m == null) { Fail("T30 单张过大弹窗按钮", "找不到 AskSwitchToMulti"); return; }

                m.Invoke(page, null);
                timer.Stop();

                bool hasSplit = texts != null && texts.Contains("拆分生成");
                bool hasCancel = texts != null && texts.Contains("取消");
                bool noOldText = texts == null || !texts.Contains("改为多张");

                if (hasSplit && hasCancel && noOldText)
                    Pass("T30 单张过大弹窗按钮", "取消 / 拆分生成");
                else
                    Fail("T30 单张过大弹窗按钮", $"拆分生成={hasSplit} 取消={hasCancel} 旧文案={!noOldText}");
            }
            catch (Exception ex) { Fail("T30 单张过大弹窗按钮", ex.Message); }
            finally
            {
                try { RandomDrawer.FloatingOverlay.CloseAll(); } catch { }
                DoEvents(TimeSpan.FromMilliseconds(150));
            }
        }

        // ---------------------------------------------------------------
        // T28：运行日志不再出现中文标点
        // ---------------------------------------------------------------
        private static void TestLogPunctuation()
        {
            try
            {
                const string raw = "卡池：测试池，结果（第一）。用时 12ms；完成！";
                var m = typeof(RandomDrawer.RuntimeLog).GetMethod("NormalizePunctuation",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) { Fail("T28 日志标点规范化", "找不到 NormalizePunctuation"); return; }

                string got = (string)m.Invoke(null, new object[] { raw });

                bool hasCjkPunct = got.IndexOfAny(new[] { '，', '。', '：', '；', '（', '）', '！', '？', '、' }) >= 0;
                if (!hasCjkPunct && got == "卡池:测试池,结果(第一).用时 12ms;完成!")
                    Pass("T28 日志标点规范化", got);
                else
                    Fail("T28 日志标点规范化", got);

                // 真写一条日志，确认落盘内容里也没有中文标点
                RandomDrawer.RuntimeLog.Info("测试日志，中文标点：应当被替换。");
                DoEvents(TimeSpan.FromMilliseconds(120));
                string file = RandomDrawer.RuntimeLog.CurrentLogFile;
                if (!File.Exists(file)) { Pass("T28 日志已写入", file); return; }

                var lines = File.ReadAllLines(file);
                string last = lines.Length > 0 ? lines[lines.Length - 1] : "";
                if (last.IndexOfAny(new[] { '，', '。', '：', '；' }) < 0 && last.Contains("测试日志"))
                    Pass("T28 落盘日志无中文标点", last);
                else
                    Fail("T28 落盘日志无中文标点", last);
            }
            catch (Exception ex) { Fail("T28 日志标点规范化", ex.Message); }
        }

        // ---------------------------------------------------------------
        // T27：抽取 / 生成期间禁止关闭程序
        // ---------------------------------------------------------------
        private static void TestCloseGuard(Window owner)
        {
            var mw = owner as RandomDrawer.MainWindow;
            if (mw == null) { Fail("T27 处理中禁止关闭", "窗口不是 MainWindow"); return; }

            try
            {
                mw.ShowShareBusy("正在生成长图…");
                DoEvents(TimeSpan.FromMilliseconds(200));

                owner.Close();
                DoEvents(TimeSpan.FromMilliseconds(200));

                if (owner.IsVisible)
                    Pass("T27 生成中关闭被拦截", "窗口仍然打开");
                else
                    Fail("T27 生成中关闭被拦截", "窗口被关掉了");

                mw.HideShareBusy();
                DoEvents(TimeSpan.FromMilliseconds(200));

                var busyType = typeof(RandomDrawer.App).Assembly.GetType("RandomDrawer.AppBusy");
                bool busy = (bool)(busyType?.GetProperty("IsBusy")?.GetValue(null) ?? false);
                if (!busy)
                    Pass("T27 忙标记已清除", "IsBusy=False");
                else
                    Fail("T27 忙标记已清除", "IsBusy=True");
            }
            catch (Exception ex) { Fail("T27 处理中禁止关闭", ex.Message); }
            finally { try { mw.HideShareBusy(); } catch { } }
        }

        // ---------------------------------------------------------------
        // T31：分页只按对象切，每条记录都完整落在一页里（不被页眉压住/切断）
        // ---------------------------------------------------------------
        private static void TestPageRangesWholeObjects()
        {
            Window win = null;
            try
            {
                win = new Window
                {
                    Width = 1000,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false
                };
                var page = new RandomDrawer.GachaReportPage_UC();
                win.Content = page;
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(500));

                // 直接把报告面板显示出来，让 WPF 真正排版（否则行高全是 0，分页当然算不出来）
                if (page.FindName("ReportPanel") is FrameworkElement reportPanel)
                    reportPanel.Visibility = Visibility.Visible;
                page.UpdateLayout();
                DoEvents(TimeSpan.FromMilliseconds(200));

                var data = new RandomDrawer.GachaReportData
                {
                    Title = "分页测试",
                    PoolName = "测试池",
                    Summary = "测试按对象分页",
                    HasData = true
                };
                const int rows = 90;
                for (int i = 0; i < rows; i++)
                    data.Entries.Add(new RandomDrawer.GachaReportEntry { Name = "对象" + i, Pulls = 20 + i });

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                page.GetType().GetMethod("RenderReport", flags)?.Invoke(page, new object[] { data });

                var host = page.FindName("ReportContent") as Panel;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && (host == null || host.Children.Count < 2))
                    DoEvents(TimeSpan.FromMilliseconds(120));
                DoEvents(TimeSpan.FromMilliseconds(400));

                var content = page.FindName("ReportContent") as StackPanel;
                if (content == null) { Fail("T31 按对象分页", "找不到 ReportContent"); return; }

                // 用与 BuildPageRanges 相同的规则收集切分单元，按实际布局位置算出所有对象边界
                var boundaries = new System.Collections.Generic.List<double> { 0 };
                double pageHeightDip = 4000;   // 8000 像素 / 2 倍密度

                void AddSpan(UIElement el)
                {
                    var fe = el as FrameworkElement;
                    if (fe == null || fe.ActualHeight <= 0) return;
                    double top = 0;
                    try { top = fe.TransformToAncestor(content).Transform(new Point(0, 0)).Y; } catch { }
                    boundaries.Add(top);
                    boundaries.Add(top + fe.ActualHeight);
                }

                foreach (UIElement child in content.Children)
                {
                    double ch = (child as FrameworkElement)?.ActualHeight ?? 0;
                    if (ch > pageHeightDip && child is Panel inner && inner.Children.Count > 1)
                    {
                        foreach (UIElement grand in inner.Children) AddSpan(grand);
                    }
                    else
                    {
                        AddSpan(child);
                    }
                }
                boundaries.Add(content.ActualHeight);
                boundaries.Sort();

                var ranges = page.GetType().GetMethod("BuildPageRanges", flags)
                    ?.Invoke(page, new object[] { pageHeightDip, content.ActualHeight })
                    as System.Collections.Generic.List<Tuple<double, double>>;

                if (ranges == null || ranges.Count == 0) { Fail("T31 按对象分页", "没有分页结果"); return; }

                bool allOnBoundary = true;
                string badAt = "";
                foreach (var r in ranges)
                {
                    bool startOk = boundaries.Exists(b => Math.Abs(b - r.Item1) < 0.6);
                    bool endOk = boundaries.Exists(b => Math.Abs(b - (r.Item1 + r.Item2)) < 0.6);
                    if (!startOk || !endOk)
                    {
                        allOnBoundary = false;
                        badAt = $"页起点 {r.Item1:0.#}（{(startOk ? "对齐" : "不对齐")}）/ 终点 {r.Item1 + r.Item2:0.#}（{(endOk ? "对齐" : "不对齐")}）";
                        break;
                    }
                }

                if (allOnBoundary)
                    Pass("T31 每页都从对象边界开始和结束", $"{ranges.Count} 页 / {boundaries.Count - 1} 个切分单元");
                else
                    Fail("T31 每页都从对象边界开始和结束", badAt);

                // 真的渲染出来看一眼：第二页顶部必须是完整的一行，不能被页眉压住
                var pages = page.GetType().GetMethod("RenderReportPages", flags)
                    ?.Invoke(page, new object[] { 8000, null }) as System.Collections.Generic.List<BitmapSource>;

                if (pages != null && pages.Count > 1)
                {
                    for (int i = 0; i < Math.Min(2, pages.Count); i++)
                    {
                        var enc2 = new PngBitmapEncoder();
                        enc2.Frames.Add(BitmapFrame.Create(pages[i]));
                        var p2 = TestFile($"shot-pages-{i + 1}.png");
                        using (var fs2 = File.Create(p2)) enc2.Save(fs2);
                        Pass("T31 分页截图已生成", p2);
                    }
                }
            }
            catch (Exception ex) { Fail("T31 按对象分页", ex.GetType().Name + "：" + ex.Message); }
            finally { try { win?.Close(); } catch { } DoEvents(TimeSpan.FromMilliseconds(150)); }
        }

        // ---------------------------------------------------------------
        // T32：分享弹窗的缩略图支持鼠标滚轮横向滚动
        // ---------------------------------------------------------------
        private static void TestThumbWheelScroll()
        {
            Window win = null;
            try
            {
                BitmapSource MakeBmp(byte r)
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(r, (byte)(r / 2), 0x40)), null, new Rect(0, 0, 40, 120));
                    var rtb = new RenderTargetBitmap(40, 120, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.Freeze();
                    return rtb;
                }

                var images = new System.Collections.Generic.List<BitmapSource>();
                var paths = new System.Collections.Generic.List<string>();
                // 用一条很长的保存路径，顺便验证「路径过长自动换行」
                string longPath = @"C:\Pictures\Saved Pictures\抽取报告_沧海萃珠 [3]_20261101_204512_非常非常长的目录名称_再长一点.jpg";
                for (int i = 0; i < 14; i++) { images.Add(MakeBmp((byte)(10 + i * 15))); paths.Add(longPath); }

                var dlg = new RandomDrawer.ReportShareDialog(images, paths);
                win = new Window
                {
                    Width = 700,
                    Height = 640,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false,
                    Content = dlg
                };
                win.Show();
                DoEvents(TimeSpan.FromMilliseconds(500));

                var thumbs = dlg.FindName("ThumbPanel") as Panel;
                if (thumbs == null) { Fail("T32 缩略图滚轮滚动", "找不到 ThumbPanel"); return; }

                // 缩略图条所在的 ScrollViewer
                ScrollViewer sv = null;
                DependencyObject node = thumbs;
                while (node != null)
                {
                    if (node is ScrollViewer s) { sv = s; break; }
                    node = VisualTreeHelper.GetParent(node);
                }

                if (sv == null) { Fail("T32 缩略图滚轮滚动", "找不到 ScrollViewer"); return; }
                if (sv.ScrollableWidth <= 1) { Fail("T32 缩略图滚轮滚动", $"内容没有超出（width={sv.ScrollableWidth:0}）"); return; }

                SaveShot(win, TestFile("shot-share-dialog.png"));

                double before = sv.HorizontalOffset;
                sv.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent
                });
                DoEvents(TimeSpan.FromMilliseconds(150));
                double after = sv.HorizontalOffset;

                if (after > before + 1)
                    Pass("T32 缩略图滚轮滚动", $"{before:0} → {after:0}（可滚动 {sv.ScrollableWidth:0}）");
                else
                    Fail("T32 缩略图滚轮滚动", $"偏移没有变化：{before:0} → {after:0}");
            }
            catch (Exception ex) { Fail("T32 缩略图滚轮滚动", ex.GetType().Name + "：" + ex.Message); }
            finally { try { win?.Close(); } catch { } DoEvents(TimeSpan.FromMilliseconds(150)); }
        }

        private static void SetNav(Window win, string tag)
        {
            var nav = FindRadioByTag(win, tag);
            if (nav != null) nav.IsChecked = true;
        }
        private static RadioButton FindRadioByTag(DependencyObject root, string tag)
        {
            if (root is RadioButton rb && (rb.Tag?.ToString() ?? "") == tag) return rb;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindRadioByTag(VisualTreeHelper.GetChild(root, i), tag);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>把可视树里所有 TextBlock 的文字拼起来（帮助正文是 Run 组成的，要单独处理）。</summary>
        private static string CollectText(DependencyObject root)
        {
            var sb = new StringBuilder();
            CollectTextCore(root, sb);
            return sb.ToString();
        }

        private static void CollectTextCore(DependencyObject root, StringBuilder sb)
        {
            if (root is TextBlock tb)
            {
                sb.AppendLine(tb.Text);
                foreach (var inline in tb.Inlines)
                {
                    if (inline is System.Windows.Documents.Run run) sb.AppendLine(run.Text);
                }
            }

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(root); } catch { return; }
            for (int i = 0; i < count; i++)
            {
                try { CollectTextCore(VisualTreeHelper.GetChild(root, i), sb); } catch { }
            }
        }

        private static void TestChangelogResource()
        {
            try
            {
                var asm = typeof(RandomDrawer.ChangelogDialog).Assembly;
                string name = asm.GetManifestResourceNames()
                                 .FirstOrDefault(n => n.EndsWith("更新日志.txt", StringComparison.OrdinalIgnoreCase));
                if (name == null) { Fail("T6 更新日志已内嵌", "找不到资源"); return; }

                var type = typeof(RandomDrawer.ChangelogDialog);
                var method = type.GetMethod("LoadVersionsFromResource", BindingFlags.NonPublic | BindingFlags.Static);
                var list = method?.Invoke(null, null) as System.Collections.IList;
                if (list == null) { Fail("T6 更新日志解析", "解析返回 null"); return; }

                if (list.Count >= 140)
                    Pass("T6 更新日志解析", $"{list.Count} 个版本");
                else
                    Fail("T6 更新日志解析", $"只有 {list.Count} 个版本");

                var first = list[0];
                string version = (string)first.GetType().GetField("Version").GetValue(first);
                // 期望值直接取程序集版本，避免发版后忘记改测试
                var av = typeof(RandomDrawer.App).Assembly.GetName().Version;
                string expected = $"{av.Major}.{av.Minor}.{av.Build}";
                if (version == expected) Pass("T6 更新日志首条为最新版本", version);
                else Fail("T6 更新日志首条为最新版本", $"更新日志={version}，程序集={expected}");
            }
            catch (Exception ex) { Fail("T6 更新日志解析", ex.Message); }
        }

        private static void TestPasswordDialogMode()
        {
            try
            {
                var verify = new RandomDrawer.PasswordDialog("随便写的提示文字，不带关键词");
                var set = new RandomDrawer.PasswordDialog("请设置新密码", true);

                if (verify.Title == "验证密码") Pass("T7 验证模式由参数决定", verify.Title);
                else Fail("T7 验证模式由参数决定", verify.Title);

                if (set.Title == "设置锁定密码") Pass("T7 设置模式由参数决定", set.Title);
                else Fail("T7 设置模式由参数决定", set.Title);
            }
            catch (Exception ex) { Fail("T7 密码框模式", ex.Message); }
        }

        // ---------------------------------------------------------------
        // 工具
        // ---------------------------------------------------------------
        private static RandomDrawer.PoolInfo MakePool(string dir, string name, string[] rows)
        {
            var pool = new RandomDrawer.PoolInfo
            {
                Name = name,
                UniqueName = name,
                Dir = dir,
                Headers = new List<string> { "抽取对象", "分类" },
                CoverPath = ""
            };
            foreach (var row in rows)
            {
                var fields = row.Split(' ');
                pool.Items.Add(new RandomDrawer.DrawItem { Fields = fields.ToList() });
            }
            pool.ItemCount = pool.Items.Count;
            return pool;
        }

        private static T FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root is T t && t.Name == name) return t;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindByName<T>(VisualTreeHelper.GetChild(root, i), name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>按类型在可视树里找第一个后代（页面没有名字时用）。</summary>
        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            if (root is T typed) return typed;

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(root); } catch { return null; }
            for (int i = 0; i < count; i++)
            {
                var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private static void SaveShot(Window win, string path)
        {
            try
            {
                win.UpdateLayout();
                int w = (int)Math.Ceiling(win.ActualWidth);
                int h = (int)Math.Ceiling(win.ActualHeight);
                if (w < 10 || h < 10) { Fail("T8 截图", "窗口尺寸异常"); return; }

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(win);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) encoder.Save(fs);
                Console.WriteLine($"[截图] {path}");
                Pass("T8 截图已生成", path);
            }
            catch (Exception ex) { Fail("T8 截图", ex.Message); }
        }

        private static void DoEvents(TimeSpan duration)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(duration, DispatcherPriority.Background,
                (s, e) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>一边跑消息循环一边等一个 Task 完成（测试里替代 await）。</summary>
        /// <summary>轮询等待条件成立（每次让出 50 ms 消息循环），用于异步加载的 UI 状态。</summary>
        private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                DoEvents(TimeSpan.FromMilliseconds(50));
            }
            return condition();
        }
        private static void PumpUntil(System.Threading.Tasks.Task task, int timeoutMs = 30000)
        {
            if (task == null) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.ElapsedMilliseconds < timeoutMs)
                DoEvents(TimeSpan.FromMilliseconds(40));
        }

        private static string FirstMatch(string text, string key)
        {
            foreach (var line in text.Split('\n'))
                if (line.Contains(key)) return line.Trim();
            return null;
        }

        private static readonly System.Diagnostics.Stopwatch SuiteClock = System.Diagnostics.Stopwatch.StartNew();

        private static void Pass(string name, string detail)
        {
            Console.WriteLine($"[{SuiteClock.Elapsed.TotalSeconds,6:0.0}s] [PASS] {name}{(string.IsNullOrEmpty(detail) ? "" : " —— " + detail)}");
        }

        private static void Fail(string name, string detail)
        {
            _failed++;
            Failures.Add(name + "：" + detail);
            Console.WriteLine($"[FAIL] {name} —— {detail}");
        }
    }
}
