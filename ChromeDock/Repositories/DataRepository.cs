using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NodaTime;

namespace ChromeDock.Repositories;

public sealed record BrowserEnvironment(
    string Name, int FingerprintSeed, string ProxyType, string ProxyAddress,
    int? ProxyPort, string ProxyUsername, string ProxyPassword,
    string Language, string Timezone, string Platform, string Browser);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BrowserEnvironment))]
internal partial class BrowserEnvironmentJsonContext : JsonSerializerContext;

public sealed record SelectionOption(string Value, string Label)
{
    public override string ToString()
    {
        return this.Label;
    }
}

public sealed class DataRepository
{
    private const int MaxEnvironmentCount = 8999;
    private const int MaxFingerprintSeed = 9999;
    private static readonly LogRepository Log = LogRepository.For<DataRepository>();
    private readonly string _connectionString;
    private readonly Dictionary<string, List<SelectionOption>> _options = [];

    public DataRepository()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "sqlite.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new SqliteConnectionStringBuilder { DataSource = path };
        this._connectionString = builder.ToString();

        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS environments (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                fingerprint_seed INTEGER NOT NULL,
                proxy_type TEXT NOT NULL,
                proxy_address TEXT NOT NULL,
                proxy_port INTEGER,
                proxy_username TEXT NOT NULL,
                proxy_password BLOB,
                language TEXT NOT NULL,
                timezone TEXT NOT NULL,
                platform TEXT NOT NULL,
                browser TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS selection_options (
                group_key TEXT NOT NULL,
                value TEXT NOT NULL,
                label TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                PRIMARY KEY (group_key, value)
            );
            """;
        command.ExecuteNonQuery();
        Log.Info("数据库已初始化：{0}", path);

        var userDataRoot = LoadUserDataDirectory();
        RestoreEnvironmentCopies(userDataRoot);
        SyncProfileDirectories(connection, userDataRoot);

        command.CommandText = """
            INSERT INTO environments
                (name, fingerprint_seed, proxy_type, proxy_address, proxy_port,
                 proxy_username, proxy_password, language, timezone, platform, browser)
            SELECT
                '环境 - 1001', 1001, '不使用代理', '', NULL,
                '', NULL, '跟随系统', '跟随系统', 'Windows', '默认 Chromium'
            WHERE NOT EXISTS (SELECT 1 FROM environments)
              AND NOT EXISTS (SELECT 1 FROM settings WHERE key = 'last_fingerprint_seed');
            """;
        if (command.ExecuteNonQuery() == 1)
        {
            Log.Info("环境表为空，已写入示例环境");
            command.CommandText = "INSERT INTO settings (key, value) VALUES ('last_fingerprint_seed', '1001');";
            command.ExecuteNonQuery();
        }

        command.CommandText = """
            UPDATE environments SET language = 'zh-CN' WHERE language = '简体中文（zh-CN）';
            UPDATE environments SET language = 'en-US' WHERE language = '英语（en-US）';
            """;
        var migrated = command.ExecuteNonQuery();
        if (migrated > 0)
        {
            Log.Info("已将 {0} 个旧语言值转换为标准标识", migrated);
        }

        EnsureEnvironmentCopies(userDataRoot);

        command.CommandText = "SELECT COUNT(*) FROM selection_options;";
        if ((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = """
                INSERT OR IGNORE INTO selection_options (group_key, value, label, sort_order) VALUES
                    ('proxy_type', '不使用代理', '不使用代理', 0),
                    ('proxy_type', 'HTTP', 'HTTP', 1),
                    ('proxy_type', 'SOCKS5', 'SOCKS5', 2),
                    ('language', '跟随系统', '跟随系统', 0),
                    ('language', 'zh-CN', '简体中文（zh-CN）', 1),
                    ('language', 'en-US', '英语（en-US）', 2),
                    ('timezone', '跟随系统', '跟随系统', 0),
                    ('timezone', 'Asia/Shanghai', 'Asia/Shanghai', 1),
                    ('timezone', 'UTC', 'UTC', 2),
                    ('platform', 'Windows', 'Windows', 0),
                    ('platform', 'Linux', 'Linux', 1),
                    ('platform', 'macOS', 'macOS', 2),
                    ('browser', '默认 Chromium', '默认 Chromium', 0),
                    ('browser', 'Chrome', 'Chrome', 1),
                    ('browser', 'Edge', 'Edge', 2),
                    ('browser', 'Opera', 'Opera', 3),
                    ('browser', 'Vivaldi', 'Vivaldi', 4),
                    ('status_filter', 'all', '全部状态', 0),
                    ('page_size', '20', '20', 0),
                    ('page_size', '50', '50', 1);
                """;
            command.ExecuteNonQuery();
            Log.Info("已写入默认下拉选项");
        }

        command.CommandText = "INSERT OR IGNORE INTO selection_options (group_key, value, label, sort_order) VALUES ('proxy_type', 'HTTPS', 'HTTPS', 2);";
        command.ExecuteNonQuery();

        command.CommandText = """
            INSERT OR IGNORE INTO selection_options (group_key, value, label, sort_order) VALUES
                ('status_filter', 'running', '运行中', 1),
                ('status_filter', 'stopped', '未启动', 2);
            """;
        command.ExecuteNonQuery();

        SeedLanguageAndTimezones(connection);

        command.CommandText = "SELECT group_key, value, label FROM selection_options ORDER BY group_key, sort_order, value;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var group = reader.GetString(0);
            if (!this._options.TryGetValue(group, out var options))
            {
                options = [];
                this._options.Add(group, options);
            }
            options.Add(new SelectionOption(reader.GetString(1), reader.GetString(2)));
        }
        Log.Info("已加载 {0} 组选项", this._options.Count);
    }

    private void RestoreEnvironmentCopies(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var bySeed = new Dictionary<int, BrowserEnvironment>();
        foreach (var environment in LoadEnvironments())
        {
            bySeed.TryAdd(environment.FingerprintSeed, environment);
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var folderName = Path.GetFileName(directory);
                if (!int.TryParse(folderName, NumberStyles.None, CultureInfo.InvariantCulture, out var seed) ||
                    seed < 1 || folderName != seed.ToString(CultureInfo.InvariantCulture))
                {
                    continue;
                }

                var copyPath = Path.Combine(directory, "environment.json");
                if (!File.Exists(copyPath))
                {
                    continue;
                }

                try
                {
                    var copy = JsonSerializer.Deserialize(File.ReadAllText(copyPath), BrowserEnvironmentJsonContext.Default.BrowserEnvironment);
                    if (copy is null || copy.FingerprintSeed != seed ||
                        string.IsNullOrWhiteSpace(copy.Name) || string.IsNullOrWhiteSpace(copy.ProxyType) ||
                        copy.ProxyAddress is null || copy.ProxyUsername is null || copy.ProxyPassword is null ||
                        string.IsNullOrWhiteSpace(copy.Language) || string.IsNullOrWhiteSpace(copy.Timezone) ||
                        string.IsNullOrWhiteSpace(copy.Platform) || string.IsNullOrWhiteSpace(copy.Browser) ||
                        copy.ProxyType != "不使用代理" &&
                        (string.IsNullOrWhiteSpace(copy.ProxyAddress) || copy.ProxyPort is null or < 1 or > 65535))
                    {
                        throw new JsonException("环境配置副本缺少字段或指纹种子与文件夹不符。");
                    }

                    if (bySeed.TryGetValue(seed, out var existing))
                    {
                        if (existing != copy)
                        {
                            UpdateEnvironment(existing.Name, copy, false);
                            Log.Info("已按配置副本更新数据库环境：{0}，种子 {1}", copy.Name, seed);
                        }
                    }
                    else
                    {
                        InsertRecoveredEnvironment(copy);
                        Log.Info("已从配置副本恢复数据库环境：{0}，种子 {1}", copy.Name, seed);
                    }
                    bySeed[seed] = copy;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                           SqliteException or InvalidOperationException)
                {
                    Log.Error(ex, "环境配置副本无法同步：{0}", copyPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "无法扫描环境配置副本目录：{0}", root);
        }
    }

    private void InsertRecoveredEnvironment(BrowserEnvironment environment)
    {
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO environments
                (name, fingerprint_seed, proxy_type, proxy_address, proxy_port,
                 proxy_username, proxy_password, language, timezone, platform, browser)
            VALUES ($name, $seed, $proxyType, $proxyAddress, $proxyPort,
                    $proxyUsername, $proxyPassword, $language, $timezone, $platform, $browser);
            """;
        command.Parameters.AddWithValue("$name", environment.Name);
        command.Parameters.AddWithValue("$seed", environment.FingerprintSeed);
        command.Parameters.AddWithValue("$proxyType", environment.ProxyType);
        command.Parameters.AddWithValue("$proxyAddress", environment.ProxyAddress);
        command.Parameters.AddWithValue("$proxyPort", (object?)environment.ProxyPort ?? DBNull.Value);
        command.Parameters.AddWithValue("$proxyUsername", environment.ProxyUsername);
        command.Parameters.AddWithValue("$proxyPassword", Encoding.UTF8.GetBytes(environment.ProxyPassword));
        command.Parameters.AddWithValue("$language", environment.Language);
        command.Parameters.AddWithValue("$timezone", environment.Timezone);
        command.Parameters.AddWithValue("$platform", environment.Platform);
        command.Parameters.AddWithValue("$browser", environment.Browser);
        command.ExecuteNonQuery();
    }

    private void EnsureEnvironmentCopies(string root)
    {
        foreach (var environment in LoadEnvironments())
        {
            var copyPath = Path.Combine(root, environment.FingerprintSeed.ToString(CultureInfo.InvariantCulture), "environment.json");
            if (!File.Exists(copyPath))
            {
                WriteEnvironmentCopy(environment, root);
            }
        }
    }

    private static void WriteEnvironmentCopy(BrowserEnvironment environment, string root)
    {
        var directory = Path.Combine(root, environment.FingerprintSeed.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var copyPath = Path.Combine(directory, "environment.json");
        var tempPath = Path.Combine(directory, $".environment-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(environment, BrowserEnvironmentJsonContext.Default.BrowserEnvironment), Encoding.UTF8);
            File.Move(tempPath, copyPath, true);
            Log.Debug("环境配置副本已写入：{0}，种子 {1}", copyPath, environment.FingerprintSeed);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void SyncProfileDirectories(SqliteConnection connection, string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var inserted = 0;
        var maxSeed = 0;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var seedParameter = command.Parameters.Add("$seed", SqliteType.Integer);
        var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var folderName = Path.GetFileName(directory);
                if (!int.TryParse(folderName, NumberStyles.None, CultureInfo.InvariantCulture, out var seed) ||
                    seed < 1 || folderName != seed.ToString(CultureInfo.InvariantCulture))
                {
                    continue;
                }

                maxSeed = Math.Max(maxSeed, seed);
                seedParameter.Value = seed;
                command.CommandText = "SELECT name FROM environments WHERE fingerprint_seed = $seed LIMIT 1;";
                if (command.ExecuteScalar() is string existingName)
                {
                    if (existingName.Length == 0)
                    {
                        command.CommandText = "UPDATE OR IGNORE environments SET name = $name WHERE fingerprint_seed = $seed;";
                        nameParameter.Value = $"环境 - {seed}";
                        command.ExecuteNonQuery();
                    }
                    continue;
                }

                var baseName = $"环境 - {seed}";
                var name = baseName;
                var suffix = 2;
                while (true)
                {
                    command.CommandText = """
                        INSERT INTO environments
                            (name, fingerprint_seed, proxy_type, proxy_address, proxy_port,
                             proxy_username, proxy_password, language, timezone, platform, browser)
                        VALUES ($name, $seed, '不使用代理', '', NULL, '', NULL,
                                '跟随系统', '跟随系统', 'Windows', '默认 Chromium');
                        """;
                    nameParameter.Value = name;
                    try
                    {
                        command.ExecuteNonQuery();
                        inserted++;
                        Log.Info("从用户数据目录恢复环境：{0}，种子 {1}", name, seed);
                        break;
                    }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                    {
                        name = $"{baseName} ({suffix})";
                        suffix++;
                    }
                }
            }

            if (maxSeed > 0)
            {
                command.CommandText = """
                    INSERT INTO settings (key, value) VALUES ('last_fingerprint_seed', $value)
                    ON CONFLICT(key) DO UPDATE SET value =
                        MAX(CAST(value AS INTEGER), CAST(excluded.value AS INTEGER));
                    """;
                command.Parameters.AddWithValue("$value", maxSeed.ToString(CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            Log.Info("用户数据目录同步完成：{0}，恢复 {1} 个环境", root, inserted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "无法扫描用户数据目录：{0}", root);
        }
    }

    private static void SeedLanguageAndTimezones(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO selection_options (group_key, value, label, sort_order)
            VALUES ($group, $value, $label, 100);
            """;
        var group = command.Parameters.Add("$group", SqliteType.Text);
        var value = command.Parameters.Add("$value", SqliteType.Text);
        var label = command.Parameters.Add("$label", SqliteType.Text);
        var inserted = 0;

        group.Value = "language";
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures))
        {
            if (culture.Name.Length == 0)
            {
                continue;
            }

            value.Value = culture.Name;
            label.Value = $"{culture.DisplayName}（{culture.Name}）";
            inserted += command.ExecuteNonQuery();
        }

        group.Value = "timezone";
        foreach (var zoneId in DateTimeZoneProviders.Tzdb.Ids)
        {
            value.Value = zoneId;
            label.Value = zoneId;
            inserted += command.ExecuteNonQuery();
        }

        transaction.Commit();
        Log.Info("语言和 IANA 时区候选项已同步；新增 {0} 项", inserted);
    }

    public IReadOnlyList<SelectionOption> GetOptions(string group)
    {
        return this._options[group];
    }

    public List<BrowserEnvironment> LoadEnvironments()
    {
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, fingerprint_seed, proxy_type, proxy_address, proxy_port,
                   proxy_username, proxy_password, language, timezone, platform, browser
            FROM environments ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var environments = new List<BrowserEnvironment>();
        while (reader.Read())
        {
            var password = string.Empty;
            if (!reader.IsDBNull(6))
            {
                password = Encoding.UTF8.GetString((byte[])reader.GetValue(6));
            }

            int? proxyPort = null;
            if (!reader.IsDBNull(4))
            {
                proxyPort = reader.GetInt32(4);
            }

            environments.Add(new BrowserEnvironment(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                proxyPort, reader.GetString(5), password,
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10)));
        }

        Log.Info("已读取 {0} 个环境", environments.Count);
        return environments;
    }

    public BrowserEnvironment? GetEnvironment(string name)
    {
        foreach (var environment in LoadEnvironments())
        {
            if (environment.Name == name)
            {
                return environment;
            }
        }
        return null;
    }

    public bool CanCreateEnvironment()
    {
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        return GetNextSeed(connection) <= MaxFingerprintSeed;
    }

    public string GetNextEnvironmentName()
    {
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        var seed = GetNextSeed(connection);
        if (seed > MaxFingerprintSeed)
        {
            throw new OverflowException("最多支持 8999 个环境，ID 范围为 1001–9999。");
        }
        return $"环境 - {seed}";
    }

    private static long GetNextSeed(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM environments;";
        if ((long)command.ExecuteScalar()! >= MaxEnvironmentCount)
        {
            return MaxFingerprintSeed + 1;
        }
        command.CommandText = """
            SELECT MAX(
                COALESCE((SELECT MAX(fingerprint_seed) FROM environments), 0),
                COALESCE((SELECT CAST(value AS INTEGER) FROM settings WHERE key = 'last_fingerprint_seed'), 0));
            """;
        var seed = Math.Max(1000, (long)command.ExecuteScalar()!) + 1;
        command.CommandText = "SELECT COUNT(*) FROM environments WHERE name = $name;";
        var name = command.Parameters.Add("$name", SqliteType.Text);
        while (seed <= MaxFingerprintSeed)
        {
            name.Value = $"环境 - {seed}";
            if ((long)command.ExecuteScalar()! == 0)
            {
                return seed;
            }
            seed++;
        }
        return seed;
    }

    public BrowserEnvironment SaveEnvironment(BrowserEnvironment environment)
    {
        var userDataRoot = LoadUserDataDirectory();
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var nextSeed = GetNextSeed(connection, transaction);
        if (nextSeed > MaxFingerprintSeed)
        {
            throw new OverflowException("最多支持 8999 个环境，ID 范围为 1001–9999。");
        }

        var seed = (int)nextSeed;
        var name = environment.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"环境 - {seed}";
        }
        command.CommandText = """
            INSERT INTO environments
                (name, fingerprint_seed, proxy_type, proxy_address, proxy_port, proxy_username,
                 proxy_password, language, timezone, platform, browser)
            VALUES (
                $name, $seed,
                $proxyType, $proxyAddress, $proxyPort, $proxyUsername,
                $proxyPassword, $language, $timezone, $platform, $browser);
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$seed", seed);
        command.Parameters.AddWithValue("$proxyType", environment.ProxyType);
        command.Parameters.AddWithValue("$proxyAddress", environment.ProxyAddress);
        command.Parameters.AddWithValue("$proxyPort", (object?)environment.ProxyPort ?? DBNull.Value);
        command.Parameters.AddWithValue("$proxyUsername", environment.ProxyUsername);
        command.Parameters.AddWithValue("$proxyPassword", Encoding.UTF8.GetBytes(environment.ProxyPassword));
        command.Parameters.AddWithValue("$language", environment.Language);
        command.Parameters.AddWithValue("$timezone", environment.Timezone);
        command.Parameters.AddWithValue("$platform", environment.Platform);
        command.Parameters.AddWithValue("$browser", environment.Browser);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES ('last_fingerprint_seed', $seed)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$seed", seed.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        var savedEnvironment = environment with { Name = name, FingerprintSeed = seed };
        WriteEnvironmentCopy(savedEnvironment, userDataRoot);
        transaction.Commit();

        Log.Info("环境已保存：{0}，指纹种子：{1}，代理类型：{2}",
            savedEnvironment.Name, savedEnvironment.FingerprintSeed, savedEnvironment.ProxyType);
        return savedEnvironment;
    }

    public void UpdateEnvironment(string originalName, BrowserEnvironment environment)
    {
        UpdateEnvironment(originalName, environment, true);
    }

    private void UpdateEnvironment(string originalName, BrowserEnvironment environment, bool writeCopy)
    {
        var userDataRoot = LoadUserDataDirectory();
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE environments SET
                name = $name, proxy_type = $proxyType, proxy_address = $proxyAddress,
                proxy_port = $proxyPort, proxy_username = $proxyUsername,
                proxy_password = $proxyPassword, language = $language,
                timezone = $timezone, platform = $platform, browser = $browser
            WHERE name = $originalName;
            """;
        command.Parameters.AddWithValue("$originalName", originalName);
        command.Parameters.AddWithValue("$name", environment.Name);
        command.Parameters.AddWithValue("$proxyType", environment.ProxyType);
        command.Parameters.AddWithValue("$proxyAddress", environment.ProxyAddress);
        command.Parameters.AddWithValue("$proxyPort", (object?)environment.ProxyPort ?? DBNull.Value);
        command.Parameters.AddWithValue("$proxyUsername", environment.ProxyUsername);
        command.Parameters.AddWithValue("$proxyPassword", Encoding.UTF8.GetBytes(environment.ProxyPassword));
        command.Parameters.AddWithValue("$language", environment.Language);
        command.Parameters.AddWithValue("$timezone", environment.Timezone);
        command.Parameters.AddWithValue("$platform", environment.Platform);
        command.Parameters.AddWithValue("$browser", environment.Browser);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("环境记录已不存在，无法编辑。");
        }
        if (writeCopy)
        {
            WriteEnvironmentCopy(environment, userDataRoot);
        }
        transaction.Commit();
        Log.Info("环境已编辑：{0} → {1}，指纹种子 {2}", originalName, environment.Name, environment.FingerprintSeed);
    }

    public BrowserEnvironment CopyEnvironment(string name)
    {
        var source = GetEnvironment(name) ?? throw new InvalidOperationException("环境记录已不存在，无法复制。");
        var copied = SaveEnvironment(source with { Name = string.Empty, FingerprintSeed = 0 });
        Log.Info("环境已复制：{0} → {1}", name, copied.Name);
        return copied;
    }

    public bool DeleteEnvironment(string name)
    {
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM environments WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        var deleted = command.ExecuteNonQuery() == 1;
        if (deleted)
        {
            Log.Info("环境记录已删除：{0}", name);
        }
        return deleted;
    }

    public string LoadIpInfoToken()
    {
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'ipinfo_token';";
        var value = command.ExecuteScalar();
        if (value is not byte[] bytes)
        {
            return string.Empty;
        }

        Log.Debug("已读取 IPinfo Token 设置");
        return Encoding.UTF8.GetString(bytes);
    }

    public string LoadUserDataDirectory()
    {
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'user_data_directory';";
        var value = command.ExecuteScalar();
        if (value is byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return Path.Combine(AppContext.BaseDirectory, "UserData");
    }

    public void SaveSettings(string token, string userDataDirectory)
    {
        if (!Path.IsPathFullyQualified(userDataDirectory))
        {
            throw new ArgumentException("用户数据目录必须是完整路径", nameof(userDataDirectory));
        }

        userDataDirectory = Path.GetFullPath(userDataDirectory);
        Directory.CreateDirectory(userDataDirectory);
        using var connection = new SqliteConnection(this._connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            if (token.Length == 0)
            {
                command.CommandText = "DELETE FROM settings WHERE key = 'ipinfo_token';";
            }
            else
            {
                command.CommandText = """
                    INSERT INTO settings (key, value) VALUES ('ipinfo_token', $value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                command.Parameters.AddWithValue("$value", Encoding.UTF8.GetBytes(token));
            }
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO settings (key, value) VALUES ('user_data_directory', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$value", Encoding.UTF8.GetBytes(userDataDirectory));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        Log.Info("设置已保存；IPinfo Token 已配置：{0}；用户数据目录：{1}", token.Length > 0, userDataDirectory);
        EnsureEnvironmentCopies(userDataDirectory);
    }
}
