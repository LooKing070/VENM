using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace VENMLibrary
{
    public static class FileDirManager
    {
        public static string AssetsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
        public static string ScenesPath => Path.Combine(AssetsPath, "scenes");
        public static string FontsPath => Path.Combine(AssetsPath, "fonts");
        public static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.csv");

        private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

        // 🔹 UTF-8 без BOM для всех файлов (.txt, .json, .csv)
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(30);
        private static System.Threading.Timer? _autoSaveTimer;

        // 🔹 Состояние для безопасного завершения таймера
        private static readonly object _timerLock = new object();
        private static bool _isShuttingDown = false;

        private static string? _currentFilePath;
        private static string? _currentContent;
        private static Func<string, bool>? _validationCallback;
        private static Action<string>? _saveErrorCallback;

        public static void Initialize()
        {
            Directory.CreateDirectory(AssetsPath);
            Directory.CreateDirectory(ScenesPath);
            Directory.CreateDirectory(FontsPath);
        }

        #region Конфигурация (config.csv)
        public static bool LoadAutoSaveState()
        {
            if (!File.Exists(ConfigPath)) return true;
            try
            {
                foreach (var line in File.ReadAllLines(ConfigPath))
                    if (line.StartsWith("AutoSave;", StringComparison.OrdinalIgnoreCase))
                        return bool.TryParse(line.Split(';')[1].Trim(), out bool res) && res;
            }
            catch { }
            return true;
        }

        public static void SaveAutoSaveState(bool enabled) => SaveConfigLine("AutoSave", enabled.ToString());

        public static string LoadEditMode()
        {
            if (!File.Exists(ConfigPath)) return "Scene";
            try
            {
                foreach (var line in File.ReadAllLines(ConfigPath))
                    if (line.StartsWith("EditMode;", StringComparison.OrdinalIgnoreCase))
                        return line.Split(';')[1].Trim();
            }
            catch { }
            return "Scene";
        }

        public static void SaveEditMode(string mode) => SaveConfigLine("EditMode", mode);

        private static void SaveConfigLine(string key, string value)
        {
            try
            {
                var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath).ToList() : new List<string>();
                lines.RemoveAll(l => l.StartsWith($"{key};", StringComparison.OrdinalIgnoreCase));
                lines.Add($"{key};{value}");
                File.WriteAllLines(ConfigPath, lines, Utf8NoBom); // 🔹 Явно указываем UTF-8 без BOM
            }
            catch { /* Игнорируем ошибки записи конфига */ }
        }
        #endregion

        #region Валидация имён
        public static bool ValidateName(string name, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(name)) { error = "Имя не может быть пустым."; return false; }

            // 🔹 НОВАЯ ПРОВЕРКА: Защита от системных подстрок
            if (name.Contains("_tip", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("example", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("bolvanka", StringComparison.OrdinalIgnoreCase))
            {
                error = "Имя содержит зарезервированные системные слова (_tip, example, bolvanka).";
                return false;
            }

            var invalidIndex = name.IndexOfAny(InvalidNameChars);
            if (invalidIndex >= 0) { error = $"Имя содержит запрещённый символ: '{name[invalidIndex]}'"; return false; }
            if (name.Contains(' ')) { error = "Пробелы в имени запрещены."; return false; }

            return true;
        }
        #endregion

        public static void SetupAutoSave(string filePath, string content, Func<string, bool> validator, Action<string> onError)
        {
            lock (_timerLock)
            {
                _isShuttingDown = false;

                _currentFilePath = filePath;
                _currentContent = content;
                _validationCallback = validator;
                _saveErrorCallback = onError;

                _autoSaveTimer?.Dispose();
                _autoSaveTimer = new System.Threading.Timer(AutoSaveCallback, null, AutoSaveInterval, AutoSaveInterval);
            }
        }

        public static void StopAutoSave()
        {
            lock (_timerLock)
            {
                _isShuttingDown = true;

                var timer = _autoSaveTimer;
                _autoSaveTimer = null;

                if (timer != null)
                {
                    // Ждём завершения текущего колбэка, но не более 2 секунд,
                    // чтобы закрытие приложения не зависло навсегда.
                    using var waitHandle = new System.Threading.EventWaitHandle(
                        false,
                        System.Threading.EventResetMode.ManualReset
                    );

                    if (timer.Dispose(waitHandle))
                        waitHandle.WaitOne(TimeSpan.FromSeconds(2));
                    else
                        timer.Dispose();
                }

                _currentFilePath = null;
                _currentContent = null;
                _validationCallback = null;
                _saveErrorCallback = null;
            }
        }

        private static void AutoSaveCallback(object? state)
        {
            if (_isShuttingDown) return;

            string? filePath;
            string? content;
            Func<string, bool>? validator;
            Action<string>? errorCallback;

            // Локально фиксируем состояние, чтобы оно не обнулилось во время выполнения
            lock (_timerLock)
            {
                if (_isShuttingDown) return;

                filePath = _currentFilePath;
                content = _currentContent;
                validator = _validationCallback;
                errorCallback = _saveErrorCallback;
            }

            if (string.IsNullOrEmpty(filePath) || validator == null || content == null) return;

            var dispatcher = Application.Current?.Dispatcher;

            // Если приложение уже закрывается или закрыто — ничего не делаем
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            try
            {
                // 🔹 BeginInvoke вместо Invoke, чтобы фоновый поток таймера
                // не блокировался навсегда при завершении приложения.
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isShuttingDown) return;

                    try
                    {
                        if (validator(content))
                        {
                            File.WriteAllText(_currentFilePath!, _currentContent!, Utf8NoBom); // 🔹 UTF-8 без BOM
                        }
                        else
                        {
                            errorCallback?.Invoke("Автосохранение пропущено: файл содержит ошибки.");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_isShuttingDown)
                            errorCallback?.Invoke($"Ошибка автосохранения: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                if (!_isShuttingDown)
                    errorCallback?.Invoke($"Ошибка автосохранения: {ex.Message}");
            }
        }

        public static bool ValidateJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return true;
            try { using var _ = JsonDocument.Parse(json); return true; }
            catch { return false; }
        }

        public static bool SaveFile(string path, string content, string fileType, out string error)
        {
            error = string.Empty;
            try
            {
                string saveContent = content;
                if (fileType.Equals(".json", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        var node = JsonNode.Parse(content);
                        if (node != null)
                            saveContent = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    }
                    catch (Exception ex) { error = $"Некорректный JSON: {ex.Message}"; return false; }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, saveContent, Utf8NoBom); // 🔹 UTF-8 без BOM
                return true;
            }
            catch (Exception ex) { error = $"Ошибка записи: {ex.Message}"; return false; }
        }

        public static string LoadFile(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        public static string GetFontParametersPath() => Path.Combine(FontsPath, "parameters.json");

        public static List<string> GetScenes() => GetDirs(ScenesPath).Where(n => !n.EndsWith("_tip")).ToList();
        public static List<string> GetSceneFiles(string s) => GetFiles(Path.Combine(ScenesPath, s));
        public static List<string> GetObjects(string s) => GetDirs(Path.Combine(ScenesPath, s));
        public static List<string> GetObjectFiles(string s, string o) => GetFiles(Path.Combine(ScenesPath, s, o));
        public static List<string> GetFonts() => Directory.GetFiles(FontsPath, "*.otf").Concat(Directory.GetFiles(FontsPath, "*.ttf"))
            .Select(Path.GetFileName).Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).OrderBy(f => f).ToList();

        private static List<string> GetDirs(string p) => !Directory.Exists(p) ? new() : Directory.GetDirectories(p).Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).OrderBy(n => n).ToList();
        private static List<string> GetFiles(string p) => !Directory.Exists(p) ? new() : Directory.GetFiles(p, "*.*").Select(Path.GetFileName)
            .Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).OrderBy(f => f).ToList();

        public static string GetTipFilePath(string currentFilePath)
        {
            // 🔹 ИСПРАВЛЕНИЕ: Убираем проверку File.Exists(currentFilePath).
            // Файл может быть еще не создан на диске, но мы всё равно должны найти для него тип.
            if (string.IsNullOrEmpty(currentFilePath)) return string.Empty;

            string rel = Path.GetRelativePath(AssetsPath, currentFilePath);
            string[] parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string tip = parts.Length == 3
                 ? Path.Combine(AssetsPath, "scenes", "example_tip", parts[2])
                : parts.Length >= 4 ? Path.Combine(AssetsPath, "scenes", "example_tip", "bolvanka", parts[3])
                : Path.Combine(FontsPath, "parameters_tip.json");

            return File.Exists(tip) ? tip : string.Empty;
        }

        public static bool CreateScene(string name, out string error)
        {
            error = string.Empty;
            if (!ValidateName(name, out error)) return false;
            name = name.Trim();
            string path = Path.Combine(ScenesPath, name);
            if (Directory.Exists(path)) { error = "Сцена с таким именем уже существует."; return false; }
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "parameters.json"), "{}", Utf8NoBom);
            File.WriteAllText(Path.Combine(path, "script.txt"), "// Скрипт сцены", Utf8NoBom);
            return true;
        }

        public static bool DeleteScene(string name, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return false;
            string path = Path.Combine(ScenesPath, name);
            if (!Directory.Exists(path)) { error = "Сцена не найдена."; return false; }
            Directory.Delete(path, true);
            return true;
        }

        public static bool RenameScene(string oldName, string newName, out string error)
        {
            error = string.Empty;
            if (!ValidateName(newName, out error)) return false;
            newName = newName.Trim();
            string op = Path.Combine(ScenesPath, oldName), np = Path.Combine(ScenesPath, newName);
            if (!Directory.Exists(op)) { error = "Исходная сцена не найдена."; return false; }
            if (Directory.Exists(np)) { error = "Сцена с новым именем уже существует."; return false; }
            Directory.Move(op, np);
            return true;
        }

        public static bool CreateObject(string sceneName, string objName, out string error)
        {
            error = string.Empty;
            if (!ValidateName(objName, out error)) return false;
            objName = objName.Trim();
            string path = Path.Combine(ScenesPath, sceneName, objName);
            if (Directory.Exists(path)) { error = "Объект с таким именем уже существует."; return false; }
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "parameters.json"), "{}", Utf8NoBom);
            File.WriteAllText(Path.Combine(path, "events.json"), "{}", Utf8NoBom);
            File.WriteAllText(Path.Combine(path, "speech.txt"), "// Реплики", Utf8NoBom);
            return true;
        }

        public static bool DeleteObject(string sceneName, string objName, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(objName)) return false;
            string path = Path.Combine(ScenesPath, sceneName, objName);
            if (!Directory.Exists(path)) { error = "Объект не найден."; return false; }
            Directory.Delete(path, true);
            return true;
        }

        public static bool RenameObject(string sceneName, string oldName, string newName, out string error)
        {
            error = string.Empty;
            if (!ValidateName(newName, out error)) return false;
            newName = newName.Trim();
            string op = Path.Combine(ScenesPath, sceneName, oldName), np = Path.Combine(ScenesPath, sceneName, newName);
            if (!Directory.Exists(op)) { error = "Исходный объект не найден."; return false; }
            if (Directory.Exists(np)) { error = "Объект с новым именем уже существует."; return false; }
            Directory.Move(op, np);
            return true;
        }

        public static bool AddFont(string src)
        {
            if (!File.Exists(src)) return false;
            string ext = Path.GetExtension(src).ToLowerInvariant();
            if (ext != ".otf" && ext != ".ttf") return false;
            File.Copy(src, Path.Combine(FontsPath, Path.GetFileName(src)), true);
            return true;
        }

        public static bool DeleteFont(string name, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return false;
            string path = Path.Combine(FontsPath, name);
            if (!File.Exists(path)) { error = "Шрифт не найден."; return false; }
            File.Delete(path);
            return true;
        }

        public static void CreateDemoStructure()
        {
            string sc = Path.Combine(ScenesPath, "example_tip"); if (Directory.Exists(sc)) return;
            Directory.CreateDirectory(sc);
            File.WriteAllText(Path.Combine(sc, "parameters.json"), "{\n  \"name\": \"Пример сцены\",\n  \"bg\": \"image.jpg\"\n}", Utf8NoBom);
            File.WriteAllText(Path.Combine(sc, "script.txt"), "show bg\nsay \"Привет\"", Utf8NoBom);
            string ob = Path.Combine(sc, "bolvanka"); Directory.CreateDirectory(ob);
            File.WriteAllText(Path.Combine(ob, "parameters.json"), "{\n  \"name\": \"Болванка\"\n}", Utf8NoBom);
            File.WriteAllText(Path.Combine(ob, "events.json"), "{\n  \"onClick\": \"start\"\n}", Utf8NoBom);
            File.WriteAllText(Path.Combine(ob, "speech.txt"), "Болванка: Пример текста", Utf8NoBom);
            File.WriteAllText(Path.Combine(FontsPath, "parameters_tip.json"), "{\n  \"size\": 16,\n  \"color\": \"#FFFFFF\"\n}", Utf8NoBom);
        }

        public static bool IsDemoPath(string p) => p.Contains("_tip") || p.Contains("example_tip") || p.Contains("bolvanka");
        public static void OpenInExplorer(string p) { if (Directory.Exists(p)) System.Diagnostics.Process.Start("explorer.exe", p); }
    }
}