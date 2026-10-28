using HarmonyLib;
using MediaBrowser.Controller.Entities;
using SQLitePCL.pretty;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Mod
{
    public class EnhanceChineseSearch : PatchBase<EnhanceChineseSearch>
    {
        private static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;

        private static Type raw;
        private static MethodInfo sqlite3_enable_load_extension;
        private static FieldInfo sqlite3_db;
        private static MethodInfo _createConnection;
        private static MethodInfo _createConnectionManager;
        private static FieldInfo _managerId;
        private static PropertyInfo _dbFilePath;
        private static readonly HashSet<string> _loggedHandles = new HashSet<string>();

        /// <summary>
        /// 已加载分词器的原生句柄。键是 <c>SQLiteDatabaseConnection.db</c> 对象本身，
        /// 克隆共享同一句柄 → 一个池位只需加载一次。用弱表避免持有句柄导致泄漏。
        /// 只用于**加速**：表里没有记录时仍以探针为准，不会漏加载。
        /// </summary>
        private static readonly ConditionalWeakTable<object, object> _tokenizedHandles =
            new ConditionalWeakTable<object, object>();

        private static readonly object TokenizedMarker = new object();

        private static MethodInfo _getJoinCommandText;
        private static MethodInfo _createSearchTerm;
        private static MethodInfo _cacheIdsFromTextParams;
        private static bool _getJoinCommandTextReturnsStringBuilder;

        public static string CurrentTokenizerName { get; private set; } = "unknown";

        /// <summary>
        /// 搜索索引方案号。
        /// <para>
        /// **改了分词器、归一化规则或 FTS 表结构就必须 +1** —— 旧索引与新代码不匹配时，
        /// 搜索只会静默出错（不抛异常），靠日志也看不出来。
        /// </para>
        /// <para>
        /// 启动时的自动关闭只看这个号，不看插件版本号：纯 UI / 文案的小版本升级
        /// 不该把功能关掉。当前值 2 对应 <c>strmassistant_chinese_search_norm2.done</c>。
        /// </para>
        /// </summary>
        public const int IndexSchemeVersion = 2;

        /// <summary>
        /// 分词器扩展是否可用（文件存在且能识别 / 导出当前架构的版本）。
        /// <para>
        /// 启动时用它兜底：索引依赖插件自带的 libsimple，扩展加载不上时
        /// **每次元数据写入都会整体回滚**，所以宁可关掉功能，也不能带着坏索引继续跑。
        /// </para>
        /// </summary>
        public static bool IsTokenizerAvailable()
        {
            try
            {
                // 启动兜底可能早于补丁实例构造，这里补一次路径初始化。
                if (string.IsNullOrEmpty(_tokenizerPath))
                {
                    _tokenizerPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginsPath, "libsimple.so");
                }

                return EnsureTokenizerExists();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn(
                    $"EnhanceChineseSearch: tokenizer availability check failed: {ex.Message}");
                return false;
            }
        }


        private static string _tokenizerPath;
        private static readonly object _lock = new object();
        private static volatile bool _patchPhase2Initialized;
        private static int _phase2Queued;
        private static string _libraryDbPath;
        private static readonly string RebuildMarkerPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, "strmassistant_chinese_search_rebuild.pending");
        private static readonly string NormV2MarkerPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, "strmassistant_chinese_search_norm2.done");
        private static readonly string TokenizerMarkerPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, "strmassistant_chinese_search_tokenizer.done");
        private static readonly Dictionary<string, Regex> patterns = new Dictionary<string, Regex>
        {
            { "imdb", new Regex(@"^tt\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tmdb", new Regex(@"^tmdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tvdb", new Regex(@"^tvdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) }
        };

        /// <summary>
        /// 搜索索引状态摘要（运维页展示）：分词器名与索引同步状态。
        /// </summary>
        public static string GetSearchIndexStatus()
        {
            var inSync = HasNormV2Marker() && GetTokenizerMarker() == ComputeSha1(_tokenizerPath);

            return $"{CurrentTokenizerName} ({(inSync ? "InSync" : "RebuildPending")})";
        }

        /// <summary>
        /// 排程下次重启重建搜索索引（清除同步标记，Phase2 检测到缺失即重建）。
        /// </summary>
        public static void ScheduleIndexRebuild()
        {
            try
            {
                if (File.Exists(NormV2MarkerPath)) File.Delete(NormV2MarkerPath);
                if (File.Exists(TokenizerMarkerPath)) File.Delete(TokenizerMarkerPath);

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Index rebuild scheduled for next restart");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch: failed to schedule index rebuild: {ex.Message}");
            }
        }

        /// <summary>
        /// 立即优化搜索索引：对 FTS5 表执行 <c>optimize</c> 合并倒排段，并回收库内空闲页。
        /// 返回 (回收页数, 回收字节)；library.db 不可用或执行失败时返回 null。
        ///
        /// 注意：不同于 <see cref="ScheduleIndexRebuild"/>（只写标记、下次重启重建），
        /// 本方法**当场**打开 library.db 执行，不改变索引内容、不影响查询结果。
        /// </summary>
        public static (long ReclaimedPages, long ReclaimedBytes)? OptimizeSearchIndex()
        {
            const string ftsTableName = "fts_search9";
            SQLiteDatabaseConnection connection = null;

            try
            {
                var dbPath = ResolveLibraryDbPath();
                if (dbPath == null)
                {
                    Plugin.Instance.Logger.Warn("EnhanceChineseSearch - OptimizeSearchIndex skipped: library.db not found");
                    return null;
                }

                connection = SQLite3.Open(dbPath, ConnectionFlags.ReadWrite, null, true);

                // 短暂等待库锁，避免与 Emby 正在进行的写入冲突
                connection.Execute("PRAGMA busy_timeout = 5000");

                // 自定义 tokenizer 未注册时无法打开 FTS5 虚表（读取 sqlite_master 即报 no such tokenizer）
                LoadTokenizerExtension(connection);

                var pageSize = ReadPragmaInt64(connection, "page_size");
                var beforeFree = ReadPragmaInt64(connection, "freelist_count");

                connection.Execute($"INSERT INTO {ftsTableName}({ftsTableName}) VALUES('optimize')");

                var afterFree = ReadPragmaInt64(connection, "freelist_count");
                var reclaimedPages = Math.Max(0, afterFree - beforeFree);
                var reclaimedBytes = reclaimedPages * (pageSize > 0 ? pageSize : 4096);

                Plugin.Instance.Logger.Info(
                    $"EnhanceChineseSearch - OptimizeSearchIndex reclaimed {reclaimedPages} pages ({reclaimedBytes} bytes)");

                return (reclaimedPages, reclaimedBytes);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - OptimizeSearchIndex failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
                return null;
            }
            finally
            {
                connection?.Dispose();
            }
        }

        /// <summary>
        /// 定位 library.db：优先用 <see cref="CreateConnectionPostfix"/> 缓存的真实路径，
        /// 回退到 ProgramData 下的常见位置。
        /// </summary>
        private static string ResolveLibraryDbPath()
        {
            if (!string.IsNullOrEmpty(_libraryDbPath) && File.Exists(_libraryDbPath))
            {
                return _libraryDbPath;
            }

            var programDataPath = Plugin.Instance.ApplicationPaths?.ProgramDataPath;
            if (string.IsNullOrEmpty(programDataPath)) return null;

            foreach (var candidate in new[]
                     {
                         Path.Combine(programDataPath, "data", "library.db"),
                         Path.Combine(programDataPath, "library.db")
                     })
            {
                if (File.Exists(candidate))
                {
                    _libraryDbPath = candidate;
                    return candidate;
                }
            }

            return null;
        }

        private static long ReadPragmaInt64(IDatabaseConnection connection, string pragma)
        {
            using (var statement = connection.PrepareStatement("PRAGMA " + pragma))
            {
                return statement.MoveNext() ? statement.Current.GetInt64(0) : 0;
            }
        }

        /// <summary>
        /// 索引侧归一化：剥离引号/句点/间隔号/连字符，中文统一转为简体，保证索引与查询可简繁互通。
        /// 当 MixedChineseVariants 开启时，额外做变体归一化（全角/标点变体等）。
        /// </summary>
        internal static string NormalizeSearchTerm(string searchTerm)
        {
            if (string.IsNullOrEmpty(searchTerm)) return searchTerm;

            if (IsMixedVariantsEnabled())
            {
                return LanguageUtility.NormalizeChineseVariants(searchTerm);
            }

            var normalized = searchTerm
                .Replace("'", string.Empty)
                .Replace(".", string.Empty)
                .Replace("·", string.Empty)
                .Replace("-", string.Empty);

            return LanguageUtility.IsChinese(normalized)
                ? LanguageUtility.ConvertTraditionalToSimplified(normalized)
                : normalized;
        }

        /// <summary>
        /// 查询侧归一化：与索引侧同步处理间隔号/连字符并繁转简；句点/引号保持原有查询行为不变。
        /// 当 MixedChineseVariants 开启时，与索引侧使用同一套变体归一化规则。
        /// </summary>
        private static string NormalizeQueryTerm(string searchTerm)
        {
            if (string.IsNullOrEmpty(searchTerm)) return searchTerm;

            if (IsMixedVariantsEnabled())
            {
                return LanguageUtility.NormalizeChineseVariants(searchTerm);
            }

            var normalized = searchTerm
                .Replace("·", string.Empty)
                .Replace("-", string.Empty);

            return LanguageUtility.IsChinese(normalized)
                ? LanguageUtility.ConvertTraditionalToSimplified(normalized)
                : normalized;
        }

        public EnhanceChineseSearch()
        {
            _tokenizerPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginsPath, "libsimple.so");

            Initialize();

            // A simple index can remain after enhancement is disabled. Keep its tokenizer
            // available to every connection so metadata writes and searches still work.
            PatchPhase1();
        }

        protected override void OnInitialize()
        {
            try
            {
                // 加载 SQLitePCLRawEx
                var sqlitePCLEx = EmbyVersionCompatibility.TryLoadAssembly("SQLitePCLRawEx.core");
                if (sqlitePCLEx != null)
                {
                    raw = EmbyVersionCompatibility.TryGetType(sqlitePCLEx, "SQLitePCLEx.raw");
                    if (raw != null)
                    {
                        sqlite3_enable_load_extension = raw.GetMethod("sqlite3_enable_load_extension",
                            BindingFlags.Static | BindingFlags.Public);
                    }
                }

                // 获取 SQLite 数据库连接字段
                sqlite3_db = typeof(SQLiteDatabaseConnection).GetField("db", BindingFlags.NonPublic | BindingFlags.Instance);

                // 加载 Emby.Sqlite
                var embySqlite = EmbyVersionCompatibility.TryLoadAssembly("Emby.Sqlite");
                if (embySqlite != null)
                {
                    var baseSqliteRepository = EmbyVersionCompatibility.TryGetType(embySqlite, "Emby.Sqlite.BaseSqliteRepository");
                    if (baseSqliteRepository != null)
                    {
                        // 尝试获取 CreateConnection 方法，处理可能的重载
                        var createConnectionMethods = baseSqliteRepository.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                            .Where(m => m.Name == "CreateConnection")
                            .Where(m =>
                            {
                                try { GetWriteConnectionArguments(m); return true; }
                                catch (NotSupportedException) { return false; }
                            })
                            .ToArray();
                        
                        if (createConnectionMethods.Length == 1)
                        {
                            _createConnection = createConnectionMethods[0];
                        }
                        else if (createConnectionMethods.Length > 1)
                        {
                            // Prefer the shared checkout overload used by both read and write paths.
                            _createConnection = createConnectionMethods.FirstOrDefault(m =>
                                    m.GetParameters().Select(p => p.ParameterType).SequenceEqual(
                                        new[] { typeof(bool), typeof(CancellationToken) }))
                                ?? createConnectionMethods.FirstOrDefault(m => m.GetParameters().Length == 0)
                                ?? createConnectionMethods.FirstOrDefault(m => m.GetParameters().Length == 1);
                            
                            if (Plugin.Instance.DebugMode && _createConnection != null)
                            {
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Selected CreateConnection with {_createConnection.GetParameters().Length} parameters");
                            }
                        }
                        
                        _dbFilePath = baseSqliteRepository.GetProperty("DbFilePath", 
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    // 连接管理器：所有连接（读+写）的唯一出口，override 虚方法不可内联。
                    // 前置重载（CreateConnection() / (bool)）是一行转发，会被 JIT 内联 → 打在那上面覆盖不到写路径。
                    var connectionManager = EmbyVersionCompatibility.TryGetType(embySqlite, "Emby.Sqlite.PooledDatabaseConnectionManager");
                    if (connectionManager != null)
                    {
                        _createConnectionManager = connectionManager.GetMethod("CreateConnection",
                            BindingFlags.Public | BindingFlags.Instance, null,
                            new[] { typeof(bool), typeof(CancellationToken) }, null);
                        _managerId = connectionManager.GetField("Id", BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                }

                // 加载 Emby.Server.Implementations
                var embyServerImplementationsAssembly = EmbyVersionCompatibility.TryLoadAssembly("Emby.Server.Implementations");
                if (embyServerImplementationsAssembly != null)
                {
                    var sqliteItemRepository = EmbyVersionCompatibility.TryGetType(
                        embyServerImplementationsAssembly, 
                        "Emby.Server.Implementations.Data.SqliteItemRepository");
                    
                    if (sqliteItemRepository != null)
                    {
                        // 处理 GetJoinCommandText 可能的重载
                        var sqliteItemRepositoryType = sqliteItemRepository;
                        _getJoinCommandText = EmbyVersionCompatibility.FindCompatibleMethod(
                            sqliteItemRepositoryType, 
                            "GetJoinCommandText", 
                            BindingFlags.NonPublic | BindingFlags.Instance,
                            // Emby 4.9.1.x 及以上版本，移除了 itemLinks2TableQualifier 参数
                            new[] { typeof(MediaBrowser.Controller.Entities.InternalItemsQuery), typeof(List<KeyValuePair<string, string>>), typeof(string), typeof(bool) },
                            // Emby 4.9.0.x 版本
                            new[] { typeof(MediaBrowser.Controller.Entities.InternalItemsQuery), typeof(List<KeyValuePair<string, string>>), typeof(string), typeof(string), typeof(bool) }
                        );

                        if (_getJoinCommandText != null)
                        {
                            if (Plugin.Instance.DebugMode)
                            {
                                var paramCount = _getJoinCommandText.GetParameters().Length;
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Selected GetJoinCommandText with {paramCount} parameters");
                            }

                            // 检查返回类型
                            var returnType = _getJoinCommandText.ReturnType;
                            _getJoinCommandTextReturnsStringBuilder = returnType.Name == "StringBuilder" ||
                                returnType.FullName == "System.Text.StringBuilder";
                            
                            if (Plugin.Instance.DebugMode)
                            {
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: GetJoinCommandText returns {returnType.Name}");
                            }
                        }
                        
                        _createSearchTerm = sqliteItemRepository.GetMethod("CreateSearchTerm", 
                            BindingFlags.NonPublic | BindingFlags.Static);
                        _cacheIdsFromTextParams = sqliteItemRepository.GetMethod("CacheIdsFromTextParams",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    }
                }

                // 验证所有必需的组件
                var missingComponents = new List<string>();
                if (raw == null) missingComponents.Add("SQLitePCLEx.raw");
                if (sqlite3_enable_load_extension == null) missingComponents.Add("sqlite3_enable_load_extension");
                if (sqlite3_db == null) missingComponents.Add("SQLiteDatabaseConnection.db");
                if (_createConnection == null) missingComponents.Add("CreateConnection");
                if (_dbFilePath == null) missingComponents.Add("DbFilePath");
                if (_getJoinCommandText == null) missingComponents.Add("GetJoinCommandText");
                if (_createSearchTerm == null) missingComponents.Add("CreateSearchTerm");
                if (_cacheIdsFromTextParams == null) missingComponents.Add("CacheIdsFromTextParams");

                if (missingComponents.Any())
                {
                    Plugin.Instance.Logger.Warn($"EnhanceChineseSearch: Missing components - {string.Join(", ", missingComponents)}");
                    Plugin.Instance.Logger.Warn($"Chinese search enhancement may not work on Emby {AppVer}");
                    Plugin.Instance.Logger.Info("This feature requires internal SQLite APIs that may have changed in this Emby version");
                    
                    // 标记为不可用
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(EnhanceChineseSearch),
                        false,
                        $"{missingComponents.Count} required components not found");
                }
                else
                {
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch: All components loaded successfully");
                    Plugin.Instance.Logger.Info($"Chinese search enhancement is compatible with Emby {AppVer}");
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(EnhanceChineseSearch),
                        true,
                        "All required SQLite components available");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error($"EnhanceChineseSearch initialization failed: {ex.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {ex.GetType().Name}");
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
                
                EmbyVersionCompatibility.LogCompatibilityInfo(
                    nameof(EnhanceChineseSearch),
                    false,
                    "Initialization error - feature disabled");
            }
        }

        protected override void Prepare(bool apply)
        {
            // No action needed
        }

        private static void PatchPhase1()
        {
            if (!EnsureTokenizerExists())
            {
                if (Plugin.Instance.DebugMode && !PatchManager.IsArm64)
                {
                    Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchPhase1 Failed: tokenizer not ready");
                }
                ResetOptions();
                return;
            }

            // 双补丁：仓库侧（能拿到 DbFilePath，用于定位 library.db）+ 管理器侧（覆盖全部连接路径）。
            var patchedRepository = PatchUnpatch(Instance.PatchTracker, true, _createConnection,
                postfix: nameof(CreateConnectionPostfix));

            var patchedManager = _createConnectionManager != null &&
                                 PatchUnpatch(Instance.PatchTracker, true, _createConnectionManager,
                                     postfix: nameof(CreateConnectionPostfix));

            Plugin.Instance.Logger.Info(
                $"EnhanceChineseSearch - PatchPhase1: repository={patchedRepository}, connectionManager={patchedManager}");

            if (patchedRepository || patchedManager) return;

            if (Plugin.Instance.DebugMode && !PatchManager.IsArm64)
            {
                Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchPhase1 Failed");
            }
            
            ResetOptions();
        }

        /// <summary>
        /// 把 Phase2（状态判定 + 可能的全量索引重建）排到后台执行。
        ///
        /// 为什么必须离开 postfix：
        /// 1. 写路径的 postfix 是在持有 <c>_writeConnectionPool</c> 信号量时被调用的，重建会把整个库冻住；
        /// 2. postfix 持有静态 <c>_lock</c>，重建期间所有建连都被阻塞。
        ///
        /// 为什么用「仓库的写连接」而不是自己 <c>SQLite3.Open</c>：
        /// 首个触发 postfix 的连接通常是**只读**连接（<c>ConnectionFlags.ReadOnly</c>），
        /// 在上面执行 DROP/CREATE/INSERT 必然抛 readonly（实测已复现）。
        /// 走仓库的写连接还能复用 Emby 的写信号量，避免并发写撞 SQLITE_BUSY。
        /// </summary>
        private static void QueuePhase2()
        {
            if (_patchPhase2Initialized || Interlocked.CompareExchange(ref _phase2Queued, 1, 0) != 0) return;

            Task.Run(async () =>
            {
                IDatabaseConnection connection = null;

                try
                {
                    // 稍等启动高峰过去，避免和 Emby 的启动写入抢写锁
                    await Task.Delay(3000).ConfigureAwait(false);

                    connection = OpenRepositoryWriteConnection();
                    if (connection == null)
                    {
                        Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Phase2 skipped: no writable library connection");
                        return;
                    }

                    if (!LoadTokenizerExtension(connection)) return;
                    _patchPhase2Initialized = PatchPhase2(connection);
                }
                catch (Exception e)
                {
                    Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Phase2 failed: {e.Message}");
                }
                finally
                {
                    try
                    {
                        connection?.Dispose();
                    }
                    catch (Exception)
                    {
                        // 释放失败不影响后续
                    }
                    // Queued means in flight, not completed. Failed attempts can retry on the next checkout.
                    Interlocked.Exchange(ref _phase2Queued, 0);
                }
            });
        }

        /// <summary>通过仓库的 <c>CreateConnection(false, ct)</c> 取一条可写连接（参与 Emby 写信号量）。</summary>
        private static IDatabaseConnection OpenRepositoryWriteConnection()
        {
            try
            {
                var repository = BaseItem.ItemRepository;
                if (repository == null || _createConnection == null) return null;

                return _createConnection.Invoke(repository, GetWriteConnectionArguments(_createConnection))
                    as IDatabaseConnection;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - open writable connection failed: {e.Message}");
                return null;
            }
        }

        private static object[] GetWriteConnectionArguments(MethodInfo method)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0) return Array.Empty<object>();
            if (parameters.Length == 1)
            {
                if (parameters[0].ParameterType == typeof(bool)) return new object[] { false };
                if (parameters[0].ParameterType == typeof(CancellationToken))
                    return new object[] { CancellationToken.None };
            }
            if (parameters.Length == 2 && parameters[0].ParameterType == typeof(bool) &&
                parameters[1].ParameterType == typeof(CancellationToken))
                return new object[] { false, CancellationToken.None };
            throw new NotSupportedException("Unsupported repository CreateConnection signature");
        }

        private static bool PatchPhase2(IDatabaseConnection connection)
        {
            var ftsTableName = "fts_search9";

            var tokenizerCheckQuery = $@"
                SELECT 
                    CASE 
                        WHEN instr(sql, 'tokenize=""simple""') > 0 THEN 'simple'
                        WHEN instr(sql, 'tokenize=""unicode61 remove_diacritics 2""') > 0 THEN 'unicode61 remove_diacritics 2'
                        ELSE 'unknown'
                    END AS tokenizer_name
                FROM 
                    sqlite_master 
                WHERE 
                    type = 'table' AND 
                    name = '{ftsTableName}';";

            var rebuildFtsResult = true;
            var patchSearchFunctionsResult = false;

            try
            {
                CurrentTokenizerName = "unknown";
                using (var statement = connection.PrepareStatement(tokenizerCheckQuery))
                {
                    if (statement.MoveNext())
                    {
                        CurrentTokenizerName = statement.Current?.GetString(0) ?? "unknown";
                    }
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Current tokenizer (before) is " + CurrentTokenizerName);

                var tokenizerUnknown = string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal);

                if (Plugin.Instance.MainOptionsStore.GetOptions().ChineseSearch.EnhanceChineseSearchRestore)
                {
                    if (tokenizerUnknown)
                    {
                        Plugin.Instance.Logger.Warn("EnhanceChineseSearch: tokenizer state unknown, skip restore for safety");
                    }
                    else
                    {
                        if (string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                        {
                            rebuildFtsResult = RebuildFts(connection, ftsTableName, "unicode61 remove_diacritics 2");
                        }
                        if (rebuildFtsResult)
                        {
                            CurrentTokenizerName = "unicode61 remove_diacritics 2";
                            Plugin.Instance.Logger.Info("EnhanceChineseSearch - Restore Success");
                        }
                    }
                    // Preserve the restore request on failure so the next connection can retry.
                    if (!tokenizerUnknown && rebuildFtsResult)
                    {
                        Plugin.Instance.MainOptionsStore.GetOptions().ChineseSearch.EnhanceChineseSearch = false;
                        Plugin.Instance.MainOptionsStore.GetOptions().ChineseSearch.EnhanceChineseSearchRestore = false;
                        Plugin.Instance.MainOptionsStore.SavePluginOptionsSuppress();
                        return true;
                    }
                    return false;
                }
                else if (Plugin.Instance.MainOptionsStore.GetOptions().ChineseSearch.EnhanceChineseSearch)
                {
                    patchSearchFunctionsResult = PatchSearchFunctions();

                    if (patchSearchFunctionsResult)
                    {
                        var hasScheduledRebuild = HasScheduledRebuild();
                        var tokenizerIsSimple = string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal);

                        Plugin.Instance.Logger.Info(
                            $"EnhanceChineseSearch - Phase2 state: tokenizer='{CurrentTokenizerName}', scheduledRebuild={hasScheduledRebuild}");

                        if (tokenizerUnknown)
                        {
                            ScheduleRebuildOnNextRestart();
                            Plugin.Instance.Logger.Warn("EnhanceChineseSearch: tokenizer state unknown, rebuild scheduled for next restart");
                        }
                        else if (!tokenizerIsSimple)
                        {
                            // 关键修复：只要当前不是 simple，就必须重建到 simple。
                            // 不再仅凭 tokenizer 二进制版本判断“已升级”。
                            if (!hasScheduledRebuild)
                            {
                                ScheduleRebuildOnNextRestart();
                                Plugin.Instance.Logger.Info(
                                    "EnhanceChineseSearch: tokenizer is not 'simple'; force rebuild to simple and mark retry on next restart if it fails");
                            }
                            else
                            {
                                Plugin.Instance.Logger.Info(
                                    "EnhanceChineseSearch: scheduled rebuild marker exists; executing rebuild to tokenizer 'simple'");
                            }

                            rebuildFtsResult = RebuildFts(connection, ftsTableName, "simple");
                            if (rebuildFtsResult)
                            {
                                ClearScheduledRebuild();
                                CurrentTokenizerName = "simple";
                                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Rebuild to tokenizer 'simple' completed");
                            }
                            else
                            {
                                Plugin.Instance.Logger.Warn(
                                    "EnhanceChineseSearch - Rebuild to tokenizer 'simple' failed; marker kept for next restart retry");
                            }
                        }
                        else
                        {
                            // 当前已经是 simple，功能处于启用态
                            CurrentTokenizerName = "simple";
                            if (hasScheduledRebuild)
                            {
                                ClearScheduledRebuild();
                                Plugin.Instance.Logger.Info(
                                    "EnhanceChineseSearch: tokenizer already 'simple'; cleared stale scheduled rebuild marker");
                            }

                            // 归一化 v2 或分词器二进制变更未落到索引时，升级后触发一次重建
                            var tokenizerSha1 = ComputeSha1(_tokenizerPath);
                            var tokenizerChanged = GetTokenizerMarker() != tokenizerSha1;

                            if (!HasNormV2Marker() || tokenizerChanged)
                            {
                                Plugin.Instance.Logger.Info(tokenizerChanged
                                    ? "EnhanceChineseSearch: tokenizer binary changed, rebuilding FTS index"
                                    : "EnhanceChineseSearch: norm v2 (simp-trad + interpunct/hyphen) pending, rebuilding FTS index");

                                rebuildFtsResult = RebuildFts(connection, ftsTableName, "simple");
                                if (rebuildFtsResult)
                                {
                                    CurrentTokenizerName = "simple";
                                    Plugin.Instance.Logger.Info(tokenizerChanged
                                        ? "EnhanceChineseSearch - Tokenizer upgrade rebuild completed"
                                        : "EnhanceChineseSearch - Norm v2 rebuild completed");
                                }
                                else
                                {
                                    Plugin.Instance.Logger.Warn(
                                        "EnhanceChineseSearch - Rebuild failed; retry on next restart");
                                }
                            }
                            else
                            {
                                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Load Success (search patches active)");
                            }
                        }
                    }
                    else
                    {
                        Plugin.Instance.Logger.Warn("EnhanceChineseSearch: PatchSearchFunctions returned false");
                    }
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Current tokenizer (after) is " + CurrentTokenizerName);
            }
            catch (Exception e)
            {
                // 记录错误信息，但只在 Debug 模式下记录详细堆栈
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - PatchPhase2 Failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {e.GetType().Name}");
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    if (e.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    }
                }
                return false;
            }

            // A transient connection, patch or rebuild failure must not disable the requested feature.
            // Success requires both active search patches and a successfully built simple index.
            return patchSearchFunctionsResult && rebuildFtsResult &&
                   string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal);
        }

        private static bool RebuildFts(IDatabaseConnection connection, string ftsTableName, string tokenizerName)
        {
            string populateQuery;

            populateQuery =
                $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                GetSearchColumnNormalization("Name") + ", " +
                GetSearchColumnNormalization("OriginalTitle") + ", " +
                GetSearchColumnNormalization("SeriesName") + ", " +
                GetSearchColumnNormalization(
                    "(select case when AlbumId is null then null else (select name from MediaItems where Id = AlbumId limit 1) end)") +
                " from MediaItems";

            // 用 IMMEDIATE 而不是 Deferred：重建是纯写事务（第一条就是 DROP TABLE），
            // Deferred 会以读事务开局、在首次写入时才升级成写锁；WAL 模式下若这期间
            // 别的连接提交过，升级会返回 SQLITE_BUSY_SNAPSHOT，而 busy_timeout 对它无效
            // （等多久都没用，只能整个事务重来）。IMMEDIATE 在 BEGIN 处直接抢写锁，
            // 拿不到就立即失败，交由上层重试。
            connection.BeginTransaction(TransactionMode.Immediate);
            try
            {
                var dropFtsTableQuery = $"DROP TABLE IF EXISTS {ftsTableName}";
                connection.Execute(dropFtsTableQuery);

                var createFtsTableQuery =
                    $"CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTableName} USING FTS5 (Name, OriginalTitle, SeriesName, Album, tokenize=\"{tokenizerName}\", prefix='1 2 3 4')";
                connection.Execute(createFtsTableQuery);

                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {ftsTableName} Start");

                connection.Execute(populateQuery);

                ApplySimplifiedSweep(connection, ftsTableName);

                // MixedChineseVariants：在简繁归一化之上再做变体归一化（全角/标点变体等）。
                if (Plugin.Instance.MainOptionsStore.GetOptions().ChineseSearch.MixedChineseVariants)
                {
                    ApplyVariantsSweep(connection, ftsTableName);
                }

                connection.CommitTransaction();

                WriteNormV2Marker();
                WriteTokenizerMarker(ComputeSha1(_tokenizerPath));

                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {ftsTableName} Complete");

                return true;
            }
            catch (Exception e)
            {
                connection.RollbackTransaction();

                // 记录错误信息，但只在 Debug 模式下记录详细堆栈
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - RebuildFts Failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {e.GetType().Name}");
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    if (e.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 重建填充后扫描 FTS 全表，把仍为繁体的词条改写为简体（SQL populate 无法调用托管转换，
        /// 用一次性 UPDATE 补齐），失败仅降级为无简繁索引，不阻断重建。
        /// </summary>
        private static void ApplySimplifiedSweep(IDatabaseConnection connection, string ftsTableName)
        {
            try
            {
                var convertedRows = new Dictionary<long, string[]>();

                using (var select = connection.PrepareStatement(
                           $"SELECT RowId, Name, OriginalTitle, SeriesName, Album FROM {ftsTableName}"))
                {
                    while (select.MoveNext())
                    {
                        var row = select.Current;
                        var rowId = row.GetInt64(0);
                        var converted = new string[4];
                        var differs = false;

                        for (var i = 0; i < 4; i++)
                        {
                            var original = row.GetString(i + 1);
                            converted[i] = ConvertToSimplified(original);

                            if (!string.Equals(original, converted[i], StringComparison.Ordinal))
                            {
                                differs = true;
                            }
                        }

                        if (differs)
                        {
                            convertedRows[rowId] = converted;
                        }
                    }
                }

                foreach (var pair in convertedRows)
                {
                    var values = pair.Value;

                    connection.Execute(
                        $"UPDATE {ftsTableName} SET Name = {SqlQuote(values[0])}, OriginalTitle = {SqlQuote(values[1])}, " +
                        $"SeriesName = {SqlQuote(values[2])}, Album = {SqlQuote(values[3])} WHERE RowId = {pair.Key}");
                }

                if (convertedRows.Count > 0)
                {
                    Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Simplified sweep converted {convertedRows.Count} FTS rows");
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Simplified sweep failed, continue without trad-to-simp index: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }
        }

        /// <summary>
        /// 读取 MixedChineseVariants 开关（防御式：Plugin 未初始化时返回 false）。
        /// </summary>
        private static bool IsMixedVariantsEnabled()
        {
            try
            {
                return Plugin.Instance?.MainOptionsStore?.GetOptions()?.ChineseSearch?.MixedChineseVariants == true;
            }
            catch
            {
                return false;
            }
        }

        private static string ConvertToSimplified(string input)
        {            if (string.IsNullOrEmpty(input) || !LanguageUtility.IsChinese(input)) return input;

            var simplified = LanguageUtility.ConvertTraditionalToSimplified(input);

            return string.Equals(input, simplified, StringComparison.Ordinal) ? input : simplified;
        }

        /// <summary>
        /// MixedChineseVariants 索引侧扫描：对 FTS 表中不满足变体归一化的行做一次写回，
        /// 保证索引侧与查询侧使用同一套变体归一化规则（否则互通匹配会失效）。
        /// </summary>
        private static void ApplyVariantsSweep(IDatabaseConnection connection, string ftsTableName)
        {
            try
            {
                var convertedRows = new Dictionary<long, string[]>();

                using (var select = connection.PrepareStatement(
                           $"SELECT RowId, Name, OriginalTitle, SeriesName, Album FROM {ftsTableName}"))
                {
                    while (select.MoveNext())
                    {
                        var row = select.Current;
                        var rowId = row.GetInt64(0);
                        var converted = new string[4];
                        var differs = false;

                        for (var i = 0; i < 4; i++)
                        {
                            var original = row.GetString(i + 1);
                            converted[i] = LanguageUtility.NormalizeChineseVariants(original);

                            if (!string.Equals(original, converted[i], StringComparison.Ordinal))
                            {
                                differs = true;
                            }
                        }

                        if (differs)
                        {
                            convertedRows[rowId] = converted;
                        }
                    }
                }

                foreach (var pair in convertedRows)
                {
                    var values = pair.Value;

                    connection.Execute(
                        $"UPDATE {ftsTableName} SET Name = {SqlQuote(values[0])}, OriginalTitle = {SqlQuote(values[1])}, " +
                        $"SeriesName = {SqlQuote(values[2])}, Album = {SqlQuote(values[3])} WHERE RowId = {pair.Key}");
                }

                if (convertedRows.Count > 0)
                {
                    Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Variants sweep normalized {convertedRows.Count} FTS rows");
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Variants sweep failed, continue without variant normalization: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }
        }

        private static string SqlQuote(string value)
        {
            return value == null ? "NULL" : "'" + value.Replace("'", "''") + "'";
        }

        private static bool HasNormV2Marker()
        {
            try
            {
                return File.Exists(NormV2MarkerPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 分词器变更检测：标记记录上次建索引用的分词器二进制哈希，
        /// 二进制升级后哈希不一致即触发 FTS 重建（新分词器切分规则变化，旧索引必须重建）。
        /// </summary>
        private static string GetTokenizerMarker()
        {
            try
            {
                return File.Exists(TokenizerMarkerPath) ? File.ReadAllText(TokenizerMarkerPath).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteTokenizerMarker(string sha1)
        {
            try
            {
                var dir = Path.GetDirectoryName(TokenizerMarkerPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(TokenizerMarkerPath, sha1 ?? string.Empty);
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch: failed to write tokenizer marker: {ex.Message}");
            }
        }

        private static void WriteNormV2Marker()
        {
            try
            {
                var dir = Path.GetDirectoryName(NormV2MarkerPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(NormV2MarkerPath, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch: failed to write norm v2 marker: {ex.Message}");
            }
        }

        private static void ScheduleRebuildOnNextRestart()
        {
            try
            {
                var dir = Path.GetDirectoryName(RebuildMarkerPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(RebuildMarkerPath, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch: failed to write rebuild marker: {ex.Message}");
            }
        }

        private static bool HasScheduledRebuild()
        {
            try
            {
                return File.Exists(RebuildMarkerPath);
            }
            catch
            {
                return false;
            }
        }

        private static void ClearScheduledRebuild()
        {
            try
            {
                if (File.Exists(RebuildMarkerPath))
                {
                    File.Delete(RebuildMarkerPath);
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch: failed to clear rebuild marker: {ex.Message}");
            }
        }

        private static string GetSearchColumnNormalization(string columnName)
        {
            return "replace(replace(replace(replace(" + columnName + ",'''',''),'.',''),'·',''),'-','')";
        }

        private static bool EnsureTokenizerExists()
        {
            var resourceName = GetTokenizerResourceName();
            var expectedSha1 = GetExpectedSha1();

            if (resourceName == null || expectedSha1 == null) return false;

            try
            {
                if (File.Exists(_tokenizerPath))
                {
                    var existingSha1 = ComputeSha1(_tokenizerPath);

                    if (expectedSha1.ContainsValue(existingSha1))
                    {
                        var highestVersion = expectedSha1.Keys.Max();
                        var highestSha1 = expectedSha1[highestVersion];

                        if (existingSha1 == highestSha1)
                        {
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Tokenizer exists with matching SHA-1 for the highest version {highestVersion}");
                        }
                        else
                        {
                            var currentVersion = expectedSha1.FirstOrDefault(x => x.Value == existingSha1).Key;
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Tokenizer exists for version {currentVersion} but does not match the highest version {highestVersion}. Upgrading...");
                            ExportTokenizer(resourceName);
                        }

                        return true;
                    }

                    Plugin.Instance.Logger.Info(
                        "EnhanceChineseSearch - Tokenizer exists but SHA-1 is not recognized. No action taken.");

                    // ★ arm64 兼容：已存在文件的 SHA-1 不属于当前架构的预期列表。
                    //   可能残留了其他架构的 libsimple.so（例如 x86 版被拷到 arm64 服务器），
                    //   直接导出当前架构的正确版本覆盖，避免加载失败。
                    if (File.Exists(_tokenizerPath))
                    {
                        var existingFileArch = GetElfArch(_tokenizerPath);
                        var expectedArch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                            ? "aarch64"
                            : RuntimeInformation.ProcessArchitecture == Architecture.X64
                                ? "x86-64"
                                : null;
                        if (existingFileArch != null && expectedArch != null &&
                            existingFileArch != expectedArch)
                        {
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Existing tokenizer is {existingFileArch} but current arch is {expectedArch}. Overwriting with correct arch...");
                            ExportTokenizer(resourceName);
                        }
                    }

                    return true;
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Tokenizer does not exist. Exporting...");
                ExportTokenizer(resourceName);

                return true;
            }
            catch (Exception e)
            {
                // 记录错误信息，但只在 Debug 模式下记录详细堆栈
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - EnsureTokenizerExists Failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {e.GetType().Name}");
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    if (e.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    }
                }
            }

            return false;
        }

        private static void ExportTokenizer(string resourceName)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                using (var fileStream = new FileStream(_tokenizerPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }
            }

            Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Exported {resourceName} to {_tokenizerPath}");
        }

        private static string GetTokenizerResourceName()
        {
            var tokenizerNamespace = Assembly.GetExecutingAssembly().GetName().Name + ".Tokenizer";
            var winSimpleTokenizer = $"{tokenizerNamespace}.win.libsimple.so";
            var linuxSimpleTokenizer = $"{tokenizerNamespace}.linux.libsimple.so";
            var linuxArm64SimpleTokenizer = $"{tokenizerNamespace}.linux_arm64.libsimple.so";

            var architecture = RuntimeInformation.ProcessArchitecture;
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when architecture == Architecture.X64:
                    return winSimpleTokenizer;
                case PlatformID.Unix when architecture == Architecture.X64:
                    return linuxSimpleTokenizer;
                // ★ arm64 支持：aarch64 Linux 使用专用的 arm64 libsimple.so
                case PlatformID.Unix when architecture == Architecture.Arm64:
                    return linuxArm64SimpleTokenizer;
                default:
                    return null;
            }
        }

        private static Dictionary<Version, string> GetExpectedSha1()
        {
            var architecture = RuntimeInformation.ProcessArchitecture;
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 5, 0), "aed57350b46b51bb7d04321b7fe8e5e60b0cdbdc" },
                        { new Version(0, 7, 1), "f8a3c2d9374ceff6e24275f148e4767dc23c674c" }
                    };
                case PlatformID.Unix when architecture == Architecture.X64:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 5, 0), "8e36162f96c67d77c44b36093f31ae4d297b15c0" },
                        { new Version(0, 7, 1), "44f471dc18f1c2d6ec84410088f084e7e99c61b3" }
                    };
                // ★ arm64 支持：保留自建 v0.5.0（上游 0.7.1 arm64 构建要求 glibc 2.38，
                //   老 NAS 发行版不满足；分词器名与 SQL API 与 0.7.1 一致，混用无影响）
                case PlatformID.Unix when architecture == Architecture.Arm64:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 5, 0), "8870fff9ed9a3d81b75d15cc362778209438d741" }
                    };
                default:
                    return null;
            }
        }

        private static string ComputeSha1(string filePath)
        {
            using (var sha1 = SHA1.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha1.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// 读取 ELF header 判断已存在 libsimple.so 的架构（e_machine），返回 "x86-64"/"aarch64" 等，
        /// 非 ELF 或无法解析时返回 null。用于 arm64 服务器上覆盖残留的错误架构 tokenizer。
        /// </summary>
        private static string GetElfArch(string filePath)
        {
            try
            {
                using (var fs = File.OpenRead(filePath))
                {
                    var header = new byte[20];
                    if (fs.Read(header, 0, header.Length) != header.Length) return null;
                    // ELF magic
                    if (header[0] != 0x7f || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
                        return null;
                    // e_machine at offset 18 (LE)
                    var machine = BitConverter.ToUInt16(header, 18);
                    switch (machine)
                    {
                        case 0x3e: return "x86-64";
                        case 0xb7: return "aarch64";
                        case 0x14: return "x86";
                        case 0x28: return "arm";
                        default: return machine.ToString("x");
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static void ResetOptions()
        {
            Plugin.Instance.MainOptionsStore.GetOptions().ChineseSearch.EnhanceChineseSearch = false;
            Plugin.Instance.MainOptionsStore.SavePluginOptionsSuppress();
        }

        private static bool PatchSearchFunctions()
        {
            // 根据返回类型选择正确的 Postfix 方法
            string getJoinCommandTextPostfix = _getJoinCommandTextReturnsStringBuilder 
                ? nameof(GetJoinCommandTextPostfixStringBuilder) 
                : nameof(GetJoinCommandTextPostfix);
            
            return PatchUnpatch(Instance.PatchTracker, true, _getJoinCommandText,
                       postfix: getJoinCommandTextPostfix) &&
                   PatchUnpatch(Instance.PatchTracker, true, _createSearchTerm,
                       prefix: nameof(CreateSearchTermPrefix)) &&
                   PatchUnpatch(Instance.PatchTracker, true,
                       _cacheIdsFromTextParams, prefix: nameof(CacheIdsFromTextParamsPrefix));
        }

        private static bool HasTokenizerOnConnection(IDatabaseConnection connection)
        {
            try
            {
                // Pooled wrappers may share a native handle. Probe before registering again.
                connection.Execute("SELECT simple_query('strmassistant_connection_probe')");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool LoadTokenizerExtension(IDatabaseConnection connection)
        {
            object handle = null;
            try
            {
                handle = sqlite3_db?.GetValue(connection);
            }
            catch (Exception)
            {
                // 拿不到句柄就退回逐次探针
            }

            // 该句柄已确认加载过（克隆共享句柄）→ 直接返回，省掉一次 SQL 往返
            if (handle != null && _tokenizedHandles.TryGetValue(handle, out _)) return true;

            if (HasTokenizerOnConnection(connection))
            {
                MarkTokenized(handle);
                return true;
            }

            try
            {
                // 检查必需的组件是否存在
                if (sqlite3_db == null)
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug("EnhanceChineseSearch: sqlite3_db field not found, cannot load tokenizer");
                    }
                    return false;
                }
                
                if (sqlite3_enable_load_extension == null || raw == null)
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug("EnhanceChineseSearch: sqlite3_enable_load_extension method or raw type not found, cannot load tokenizer");
                    }
                    return false;
                }
                
                if (string.IsNullOrEmpty(_tokenizerPath) || !File.Exists(_tokenizerPath))
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Tokenizer file not found at {_tokenizerPath}");
                    }
                    return false;
                }

                var db = sqlite3_db.GetValue(connection);
                if (db == null)
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug("EnhanceChineseSearch: Could not get SQLite database handle from connection");
                    }
                    return false;
                }
                
                sqlite3_enable_load_extension.Invoke(raw, new[] { db, 1 });
                connection.Execute("SELECT load_extension('" + _tokenizerPath + "')");

                MarkTokenized(handle);

                return true;
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Load tokenizer failed.");
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
                else
                {
                    Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Load tokenizer failed: {e.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// 从连接管理器实例取仓库标识（<c>PooledDatabaseConnectionManager.Id</c> = 仓库类型名）。
        /// 传入仓库实例时返回 null。
        /// </summary>
        private static string TryGetManagerId(object instance)
        {
            if (instance == null || _managerId == null) return null;

            try
            {
                return _managerId.GetValue(instance) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>记下「该原生句柄已加载分词器」，后续克隆直接命中。</summary>
        private static void MarkTokenized(object handle)
        {
            if (handle == null) return;

            try
            {
                _tokenizedHandles.GetValue(handle, _ => TokenizedMarker);
            }
            catch (Exception)
            {
                // 去重表异常只影响性能，不影响正确性
            }
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfix(object __instance, ref IDatabaseConnection __result)
        {
            // Tokenizers are registered per SQLite handle; only phase 2 is once-per-process.
            lock (_lock)
            {
                try
                {
                    // 尝试获取数据库文件路径，如果属性不存在则尝试其他方式
                    string db = null;
                    var managerId = TryGetManagerId(__instance);

                    if (_dbFilePath != null)
                    {
                        // __instance 可能是连接管理器（新补丁目标），此时仓库属性不适用
                        try
                        {
                            db = _dbFilePath.GetValue(__instance) as string;
                        }
                        catch (TargetException)
                        {
                            db = null;
                        }
                    }
                    else
                    {
                        // 如果 DbFilePath 属性不存在，尝试通过反射查找其他可能的属性或字段
                        var instanceType = __instance.GetType();

                        // 尝试查找 DbFilePath 属性（可能名称不同）
                        var dbFilePathProp = instanceType.GetProperty("DbFilePath",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (dbFilePathProp != null)
                        {
                            db = dbFilePathProp.GetValue(__instance) as string;
                        }
                        else
                        {
                            // 尝试查找 _dbFilePath 字段
                            var dbFilePathField = instanceType.GetField("_dbFilePath",
                                BindingFlags.NonPublic | BindingFlags.Instance);
                            if (dbFilePathField != null)
                            {
                                db = dbFilePathField.GetValue(__instance) as string;
                            }
                            else
                            {
                                // 尝试查找 dbFilePath 字段（小写开头）
                                dbFilePathField = instanceType.GetField("dbFilePath",
                                    BindingFlags.NonPublic | BindingFlags.Instance);
                                if (dbFilePathField != null)
                                {
                                    db = dbFilePathField.GetValue(__instance) as string;
                                }
                            }
                        }
                    }

                    // 如果无法获取路径，或者路径不是 library.db，则跳过
                    // 在新版本中，如果无法确定路径，我们仍然尝试加载 tokenizer
                    // 因为可能只有 library.db 会调用 CreateConnection
                    if (db != null && !db.EndsWith("library.db", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Skipping non-library database: {db}");
                        }
                        return;
                    }

                    if (db != null && db.EndsWith("library.db", StringComparison.OrdinalIgnoreCase))
                    {
                        _libraryDbPath = db;
                    }

                    // 管理器侧拿不到路径，用 Id（= 仓库类型名）判断；非条目库跳过
                    if (db == null && managerId != null &&
                        !managerId.Equals("SqliteItemRepository", StringComparison.Ordinal))
                    {
                        return;
                    }

                    // 如果 db 为 null，可能是新版本中路径获取方式改变了
                    // 在这种情况下，我们仍然尝试加载，但记录警告
                    if (db == null && Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug("EnhanceChineseSearch: Could not determine database path, attempting to load tokenizer anyway");
                    }

                    var handle = sqlite3_db?.GetValue(__result);
                    var handleKey = handle == null ? "null" : RuntimeHelpers.GetHashCode(handle).ToString("x8");
                    var tokenizerLoaded = LoadTokenizerExtension(__result);

                    if (_loggedHandles.Add($"{__instance.GetType().Name}|{managerId}|{handleKey}|{__result.IsReadOnly}"))
                    {
                        Plugin.Instance.Logger.Info(
                            $"EnhanceChineseSearch - tokenizer {(tokenizerLoaded ? "ready" : "NOT loaded")} on " +
                            $"{__instance.GetType().Name}({managerId ?? Path.GetFileName(db) ?? "-"}) " +
                            $"handle={handleKey} readOnly={__result.IsReadOnly}");
                    }

                    if (tokenizerLoaded && !_patchPhase2Initialized)
                    {
                        // Phase2 含全量索引重建，绝不能跑在 CreateConnection 的调用栈里：
                        // 写路径此时持有写信号量 + _lock，重建会把整个库冻住甚至死锁。
                        QueuePhase2();
                    }
                }
                catch (Exception ex)
                {
                    // 捕获所有异常，避免影响正常的数据库连接创建
                    // 只在 Debug 模式下记录详细错误，避免日志噪音
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: CreateConnectionPostfix error: {ex.Message}");
                        Plugin.Instance.Logger.Debug($"Exception type: {ex.GetType().Name}");
                        Plugin.Instance.Logger.Debug(ex.StackTrace);
                        if (ex.InnerException != null)
                        {
                            Plugin.Instance.Logger.Debug($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                        }
                    }
                    // 对于非关键错误，不记录警告，避免日志噪音
                    // 只有在真正影响功能时才记录警告
                }
            }
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfix(InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams, string mediaItemsTableQualifier,
            bool allowJoinOnItemLinks, ref string __result)
        {
            var modified = false;

            if (!string.IsNullOrEmpty(query.SearchTerm) && __result.Contains("match @SearchTerm"))
            {
                if (!Plugin.Instance.MainOptionsStore.GetOptions().ChineseSearch.ExcludeOriginalTitleFromSearch)
                {
                    __result = __result.Replace("match @SearchTerm", "match simple_query(@SearchTerm)");
                }
                else
                {
                    __result = __result.Replace("match @SearchTerm", "match '-OriginalTitle:' || simple_query(@SearchTerm)");
                }

                modified = true;
            }

            if (!string.IsNullOrEmpty(query.Name) && __result.Contains("match @SearchTerm"))
            {
                __result = __result.Replace("match @SearchTerm", "match 'Name:' || simple_query(@SearchTerm)");

                modified = true;
            }

            if (modified)
            {
                TransformSearchTermBindParams(bindParams);
            }
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfixStringBuilder(InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams, string mediaItemsTableQualifier,
            bool allowJoinOnItemLinks, System.Text.StringBuilder __result)
        {
            if (__result == null) return;

            var resultString = __result.ToString();
            var modified = false;

            if (!string.IsNullOrEmpty(query.SearchTerm) && resultString.Contains("match @SearchTerm"))
            {
                if (!Plugin.Instance.MainOptionsStore.GetOptions().ChineseSearch.ExcludeOriginalTitleFromSearch)
                {
                    resultString = resultString.Replace("match @SearchTerm", "match simple_query(@SearchTerm)");
                    modified = true;
                }
                else
                {
                    resultString = resultString.Replace("match @SearchTerm", "match '-OriginalTitle:' || simple_query(@SearchTerm)");
                    modified = true;
                }
            }

            if (!string.IsNullOrEmpty(query.Name) && resultString.Contains("match @SearchTerm"))
            {
                resultString = resultString.Replace("match @SearchTerm", "match 'Name:' || simple_query(@SearchTerm)");
                modified = true;
            }

            if (modified)
            {
                TransformSearchTermBindParams(bindParams);

                __result.Clear();
                __result.Append(resultString);
            }
        }

        /// <summary>
        /// 查询参数归一化：Name: 前缀剥离（原有行为）+ 简繁互通与间隔号/连字符清理，
        /// 与索引侧 NormalizeSearchTerm 保持一致。
        /// </summary>
        private static void TransformSearchTermBindParams(List<KeyValuePair<string, string>> bindParams)
        {
            for (var i = 0; i < bindParams.Count; i++)
            {
                if (!string.Equals(bindParams[i].Key, "@SearchTerm", StringComparison.Ordinal)) continue;

                var currentValue = bindParams[i].Value;

                if (currentValue != null && currentValue.StartsWith("Name:", StringComparison.Ordinal))
                {
                    currentValue = currentValue
                        .Substring(currentValue.IndexOf(":", StringComparison.Ordinal) + 1)
                        .Trim('\"', '^', '$')
                        .Replace(".", string.Empty)
                        .Replace("'", string.Empty);
                }

                bindParams[i] = new KeyValuePair<string, string>(bindParams[i].Key, NormalizeQueryTerm(currentValue));
            }
        }

        [HarmonyPrefix]
        private static bool CreateSearchTermPrefix(string searchTerm, ref string __result)
        {
            __result = NormalizeSearchTerm(searchTerm);

            return false;
        }

        [HarmonyPrefix]
        private static bool CacheIdsFromTextParamsPrefix(InternalItemsQuery query, IDatabaseConnection db)
        {
            if ((query.PersonTypes?.Length ?? 0) == 0)
            {
                var nameStartsWith = query.NameStartsWith;
                if (!string.IsNullOrEmpty(nameStartsWith))
                {
                    query.SearchTerm = nameStartsWith;
                    query.NameStartsWith = null;
                }

                var searchTerm = query.SearchTerm;
                if (query.IncludeItemTypes.Length == 0 && !string.IsNullOrEmpty(searchTerm))
                {
                    query.IncludeItemTypes = GetSearchScope();
                }

                if (!string.IsNullOrEmpty(searchTerm))
                {
                    foreach (var provider in patterns)
                    {
                        var match = provider.Value.Match(searchTerm.Trim());
                        if (match.Success)
                        {
                            var idValue = provider.Key == "imdb" ? match.Value : match.Groups[2].Value;

                            query.AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                            {
                                new KeyValuePair<string, string>(provider.Key, idValue)
                            };
                            query.SearchTerm = null;
                            break;
                        }
                    }
                }

                // DigitsAsTmdbId：纯数字搜索词直接当作 TMDB ID 精确匹配。
                // 仅当已开启该开关、且前面的 provider 前缀匹配未命中时生效；匹配后不再走分词查询。
                if (!string.IsNullOrEmpty(query.SearchTerm) &&
                    Plugin.Instance.MainOptionsStore.GetOptions().ChineseSearch.DigitsAsTmdbId)
                {
                    var digits = query.SearchTerm.Trim();
                    if (digits.Length > 0 && digits.All(char.IsDigit))
                    {
                        query.AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                        {
                            new KeyValuePair<string, string>("tmdb", digits)
                        };
                        query.SearchTerm = null;
                    }
                }

                if (!string.IsNullOrEmpty(query.SearchTerm))
                {
                    var result = LoadTokenizerExtension(db);
                }
            }

            return true;
        }
    }
}
