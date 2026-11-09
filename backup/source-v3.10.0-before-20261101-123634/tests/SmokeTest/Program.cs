using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
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

                var panel = FindByName<Border>(win, "DrawCountPanel");
                var gridButtons = FindByName<WrapPanel>(win, "DrawCountButtonsPanel");
                var confirm = FindByName<Button>(win, "BtnDrawCountConfirm");

                if (panel != null)
                {
                    if (Math.Abs(panel.ActualWidth - 420) < 1.5)
                        Pass("T5 抽取次数面板宽度已收紧", $"{panel.ActualWidth:0.#} DIP");
                    else
                        Fail("T5 抽取次数面板宽度已收紧", $"{panel.ActualWidth:0.#} DIP（期望 420）");

                    // 数字宫格的最后一列右边界 vs 两个按钮的右边界
                    if (confirm != null)
                    {
                        var confirmRight = confirm.TransformToAncestor(panel).Transform(new Point(confirm.ActualWidth, 0)).X;
                        if (panel.ActualWidth - confirmRight < 40)
                            Pass("T5 按钮与宫格右边界对齐（右侧无大片空白）", $"右侧留白 {panel.ActualWidth - confirmRight:0.#} DIP");
                        else
                            Fail("T5 按钮与宫格右边界对齐（右侧无大片空白）", $"右侧留白 {panel.ActualWidth - confirmRight:0.#} DIP");
                    }
                    else Fail("T5 找到「开始抽取」按钮", "未找到 BtnDrawCountConfirm");
                }
                else Fail("T5 找到抽取次数面板", "未找到 DrawCountPanel");

                SaveShot(win, Path.Combine(Directory.GetCurrentDirectory(), "shot-drawcount.png"));

                TestOptionsLayouts(win);
                TestAdvancedLayout();
                TestShareDialogMulti();
            }
            finally
            {
                try { win.Close(); } catch { }
                DoEvents(TimeSpan.FromMilliseconds(200));
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
                SaveShot(win, Path.Combine(Directory.GetCurrentDirectory(), "shot-options-general.png"));

                SetNav(win, "1");
                DoEvents(TimeSpan.FromMilliseconds(500));
                SaveShot(win, Path.Combine(Directory.GetCurrentDirectory(), "shot-options-pool.png"));

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

                SaveShot(win, Path.Combine(Directory.GetCurrentDirectory(), "shot-options-system.png"));

                SetNav(win, "3");
                DoEvents(TimeSpan.FromMilliseconds(700));

                string text = CollectText(win);
                bool hasLoading = text.Contains("500") && text.Contains("加载动画");
                bool hasCover = text.Contains("更换封面") && text.Contains("不需要密码");
                bool hasExport = text.Contains("导出") && text.Contains("解密");
                bool hasDelete = text.Contains("删除") && text.Contains("不需要密码");

                if (hasLoading && hasCover && hasExport && hasDelete)
                    Pass("T15 帮助页文本已同步", "含加载阈值 / 封面免密 / 导出解密 / 删除免密");
                else
                    Fail("T15 帮助页文本已同步",
                        $"loading={hasLoading} cover={hasCover} export={hasExport} delete={hasDelete}");

                SaveShot(win, Path.Combine(Directory.GetCurrentDirectory(), "shot-help.png"));
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
                SaveShot(win, Path.Combine(Directory.GetCurrentDirectory(), "shot-advanced.png"));
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

        private static string FirstMatch(string text, string key)
        {
            foreach (var line in text.Split('\n'))
                if (line.Contains(key)) return line.Trim();
            return null;
        }

        private static void Pass(string name, string detail)
        {
            Console.WriteLine($"[PASS] {name}{(string.IsNullOrEmpty(detail) ? "" : " —— " + detail)}");
        }

        private static void Fail(string name, string detail)
        {
            _failed++;
            Failures.Add(name + "：" + detail);
            Console.WriteLine($"[FAIL] {name} —— {detail}");
        }
    }
}
