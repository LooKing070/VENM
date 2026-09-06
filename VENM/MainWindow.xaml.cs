using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VENMLibrary;

namespace VENM
{
    public partial class MainWindow : Window
    {
        private enum EditMode { None, Scene, Object, Font }
        private EditMode _currentEditMode = EditMode.None;

        private readonly Dictionary<EditMode, Action> _modeActions;
        private readonly Dictionary<EditMode, Func<bool>> _createActions;
        private readonly Dictionary<EditMode, Func<bool>> _deleteActions;

        private FileEditView? _fileEditView;
        private string? _currentScene, _currentObject, _currentFile;
        private bool _isAutoSaveEnabled = true;
        private bool _isProcessing = false; // 🔹 Защита от рекурсии при каскадном обновлении

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed; // 🔹 Освобождение ресурсов при закрытии окна

            _modeActions = new Dictionary<EditMode, Action>
            {
                { EditMode.Scene, () => { } },
                { EditMode.Object, () => { } },
                { EditMode.Font, () => { LoadFontParameters(); } }
            };

            _createActions = new Dictionary<EditMode, Func<bool>>
            {
                { EditMode.Scene, CreateScene }, { EditMode.Object, CreateObject }, { EditMode.Font, AddFont }
            };

            _deleteActions = new Dictionary<EditMode, Func<bool>>
            {
                { EditMode.Scene, DeleteScene }, { EditMode.Object, DeleteObject }, { EditMode.Font, DeleteFont }
            };

            _fileEditView = new FileEditView(TextEditor, TextPreview, JsonEditorScroll, JsonEditorPanel, JsonPreviewScroll, JsonPreviewPanel);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 🔹 Глобальная защита от необработанных исключений
            Application.Current.DispatcherUnhandledException += (s, args) =>
            {
                ShowError($"Необработанная ошибка: {args.Exception.Message}");
                args.Handled = true;
            };

            FileDirManager.Initialize();
            _isAutoSaveEnabled = FileDirManager.LoadAutoSaveState();
            AutoSave.IsChecked = _isAutoSaveEnabled;

            DisableAllControls();
            SetupRenameContextMenu(ComboScene, EditMode.Scene);
            SetupRenameContextMenu(ComboObject, EditMode.Object);

            string savedModeStr = FileDirManager.LoadEditMode();
            EditMode savedMode = Enum.TryParse<EditMode>(savedModeStr, out var m) ? m : EditMode.Scene;
            SetRadioButtonChecked(savedMode);
            SwitchEditMode(savedMode);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            // Сначала сохраняем открытый файл, если он существует и валиден
            SaveCurrentFileSilently();

            // Полностью останавливаем фоновый таймер автосохранения
            FileDirManager.StopAutoSave();

            // Гарантируем завершение приложения
            Application.Current?.Shutdown();
        }

        #region Переключение режимов
        private void RadioScEdit_Checked(object sender, RoutedEventArgs e) => SwitchEditMode(EditMode.Scene);
        private void RadioObEdit_Checked(object sender, RoutedEventArgs e) => SwitchEditMode(EditMode.Object);
        private void RadioFoEdit_Checked(object sender, RoutedEventArgs e) => SwitchEditMode(EditMode.Font);

        private void SwitchEditMode(EditMode newMode)
        {
            if (_currentEditMode == newMode) return;

            SaveCurrentFileSilently();
            FileDirManager.StopAutoSave();
            ShowFileClosedState();

            _currentScene = _currentObject = _currentFile = null;
            _currentEditMode = newMode;
            FileDirManager.SaveEditMode(newMode.ToString());

            EnableControlsForMode();
            if (_modeActions.TryGetValue(newMode, out var act)) act?.Invoke();

            _isProcessing = true;
            ComboScene.SelectedItem = null;
            ComboObject.SelectedItem = null;
            ComboFile.SelectedItem = null;

            RefreshAllComboBoxes();

            // 🔹 УДАЛЕНО: Принудительный вызов ProcessSelection(ComboScene).
            // Теперь пользователь сам выбирает сцену/объект из списка, 
            // что гарантирует корректное срабатывание SelectionChanged.

            _isProcessing = false;
            UpdateUIForMode();

            if (newMode == EditMode.Font) LoadFontParameters();
        }

        private void ShowFileClosedState()
        {
            _fileEditView?.SetMode(".txt", false);
            _fileEditView?.LoadEditorContent("");
            _fileEditView?.LoadPreviewContent("Файл не открыт.\nВыберите файл из списка для начала работы.");
        }

        private void DisableAllControls()
        {
            RadioScEdit.IsChecked = false;
            RadioObEdit.IsChecked = false;
            RadioFoEditor.IsChecked = false;
            ComboScene.IsEnabled = false;
            ComboObject.IsEnabled = false;
            ComboFile.IsEnabled = false;
            BtnCreate.IsEnabled = false;
            BtnDelete.IsEnabled = false;
        }

        private void SetRadioButtonChecked(EditMode mode)
        {
            RadioScEdit.IsChecked = mode == EditMode.Scene;
            RadioObEdit.IsChecked = mode == EditMode.Object;
            RadioFoEditor.IsChecked = mode == EditMode.Font;
        }

        private void EnableControlsForMode()
        {
            BtnCreate.IsEnabled = true;
            BtnDelete.IsEnabled = true;
            ComboScene.IsEnabled = _currentEditMode != EditMode.Font;
            ComboObject.IsEnabled = _currentEditMode == EditMode.Object;
            ComboFile.IsEnabled = true;
        }

        private void UpdateUIForMode()
        {
            string modeText = _currentEditMode switch { EditMode.Scene => "сцену", EditMode.Object => "объект", EditMode.Font => "шрифт", _ => "" };
            BtnCreate.Content = $"+ создать {modeText}";
            BtnDelete.Content = $"- удалить {modeText}";
        }
        #endregion

        #region ComboBox & Каскадная логика
        private void RefreshAllComboBoxes()
        {
            UpdateComboBox(ComboScene, FileDirManager.GetScenes(), _currentScene);
            UpdateComboBox(ComboObject, _currentScene != null ? FileDirManager.GetObjects(_currentScene) : new List<string>(), _currentObject);

            // 🔹 ИСПРАВЛЕНИЕ: В режиме шрифтов ComboFile показывает только .ttf/.otf файлы.
            // Не передаем _currentFile (равный "parameters.json") в качестве выбранного элемента, чтобы не ломать UI.
            string? fileSel = _currentEditMode == EditMode.Font ? null : _currentFile;
            UpdateComboBox(ComboFile, _currentEditMode == EditMode.Font ? FileDirManager.GetFonts() : GetFilesForCurrentSelection(), fileSel);
        }

        private List<string> GetFilesForCurrentSelection() => _currentEditMode switch
        {
            EditMode.Scene when !string.IsNullOrEmpty(_currentScene) => FileDirManager.GetSceneFiles(_currentScene),
            EditMode.Object when !string.IsNullOrEmpty(_currentScene) && !string.IsNullOrEmpty(_currentObject) => FileDirManager.GetObjectFiles(_currentScene, _currentObject),
            _ => new List<string>()
        };

        private void UpdateComboBox(ComboBox c, List<string> items, string? sel)
        {
            c.ItemsSource = items;
            // 🔹 ИСПРАВЛЕНИЕ: Выделяем элемент только если он явно передан и существует в списке.
            // Иначе сбрасываем выделение в null.
            if (sel != null && items.Contains(sel))
                c.SelectedItem = sel;
            else
                c.SelectedItem = null;
        }

        private void ProcessSelection(ComboBox sender)
        {
            if (_currentEditMode == EditMode.None || sender.SelectedItem == null) return;
            string selectedName = sender.SelectedItem.ToString()!;

            if (_currentEditMode == EditMode.Font) { _currentFile = selectedName; return; }

            SaveCurrentFileSilently();
            FileDirManager.StopAutoSave();
            ShowFileClosedState();

            if (sender == ComboScene)
            {
                _currentScene = selectedName;
                _currentObject = null;
                _currentFile = null;

                if (_currentEditMode == EditMode.Scene)
                {
                    var files = GetFilesForCurrentSelection();
                    UpdateComboBox(ComboFile, files, null);
                    if (files.Count > 0)
                    {
                        ComboFile.SelectedIndex = 0;
                        _currentFile = files[0];
                        LoadSelectedFile();
                    }
                }
                else if (_currentEditMode == EditMode.Object)
                {
                    var objects = FileDirManager.GetObjects(_currentScene);
                    UpdateComboBox(ComboObject, objects, null);

                    if (objects.Count > 0)
                    {
                        ComboObject.SelectedIndex = 0;
                        _currentObject = objects[0];

                        var objFiles = GetFilesForCurrentSelection();
                        UpdateComboBox(ComboFile, objFiles, null);
                        if (objFiles.Count > 0)
                        {
                            ComboFile.SelectedIndex = 0;
                            _currentFile = objFiles[0];
                            LoadSelectedFile();
                        }
                    }
                    else
                    {
                        // 🔹 ИСПРАВЛЕНИЕ БАГА 2: Если в сцене нет объектов, принудительно очищаем ComboFile.
                        // Это предотвращает отображение "фантомных" файлов из предыдущей сцены.
                        UpdateComboBox(ComboFile, new List<string>(), null);
                    }
                }
            }
            else if (sender == ComboObject)
            {
                _currentObject = selectedName;
                _currentFile = null;
                var files = GetFilesForCurrentSelection();
                UpdateComboBox(ComboFile, files, null);
                if (files.Count > 0)
                {
                    ComboFile.SelectedIndex = 0;
                    _currentFile = files[0];
                    LoadSelectedFile();
                }
            }
            else if (sender == ComboFile)
            {
                _currentFile = selectedName;
                LoadSelectedFile();
            }
        }

        private void Combo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isProcessing || sender is not ComboBox cb) return;
            _isProcessing = true;
            try { ProcessSelection(cb); }
            finally { _isProcessing = false; }
        }
        #endregion

        #region Загрузка файлов
        private void LoadSelectedFile()
        {
            if (_currentEditMode == EditMode.Font || _currentEditMode == EditMode.None || string.IsNullOrEmpty(_currentFile)) return;

            string? path = GetFilePathForCurrentSelection();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            bool isDemo = FileDirManager.IsDemoPath(path);
            string content = FileDirManager.LoadFile(path);
            string ext = Path.GetExtension(path) ?? ".txt";

            _fileEditView?.SetMode(ext, isDemo);
            _fileEditView?.LoadEditorContent(content);

            string tip = FileDirManager.GetTipFilePath(path);
            string preview = File.Exists(tip) ? FileDirManager.LoadFile(tip) : "Демо-структура не загружена.\nНажмите 'Создать демо-структуру' для просмотра примеров.";
            _fileEditView?.LoadPreviewContent(preview);

            if (_isAutoSaveEnabled && !isDemo)
                FileDirManager.SetupAutoSave(path, content, c => _fileEditView?.Validate() ?? true, msg => ShowError(msg));
        }

        private void LoadFontParameters()
        {
            string path = FileDirManager.GetFontParametersPath();
            _currentFile = "parameters.json"; // Убран лишний пробел
            _fileEditView?.SetMode(".json", false);
            _fileEditView?.LoadEditorContent(File.Exists(path) ? FileDirManager.LoadFile(path) : "{}");

            // 🔹 ИСПРАВЛЕНИЕ БАГА 1: 
            // Для шрифтов путь к превью всегда фиксирован и не зависит от GetTipFilePath.
            // Читаем parameters_tip.json напрямую из папки FontsPath.
            string tip = Path.Combine(FileDirManager.FontsPath, "parameters_tip.json");
            string preview = File.Exists(tip)
                ? FileDirManager.LoadFile(tip)
                : "Демо-структура не загружена.\nНажмите 'Создать демо-структуру' для просмотра примеров.";

            _fileEditView?.LoadPreviewContent(preview);

            if (_isAutoSaveEnabled) FileDirManager.SetupAutoSave(path, _fileEditView!.GetContent(), _ => true, msg => ShowError(msg));
        }

        private string? GetFilePathForCurrentSelection() => string.IsNullOrEmpty(_currentFile) ? null : _currentEditMode switch
        {
            EditMode.Scene when !string.IsNullOrEmpty(_currentScene) => Path.Combine(FileDirManager.ScenesPath, _currentScene, _currentFile),
            EditMode.Object when !string.IsNullOrEmpty(_currentScene) && !string.IsNullOrEmpty(_currentObject) => Path.Combine(FileDirManager.ScenesPath, _currentScene, _currentObject, _currentFile),
            EditMode.Font => FileDirManager.GetFontParametersPath(),
            _ => null
        };
        #endregion

        #region Сохранение
        private void SaveCurrentFileSilently()
        {
            if (string.IsNullOrEmpty(_currentFile) || _fileEditView == null || _currentEditMode == EditMode.None) return;
            if (!_fileEditView.Validate()) return;

            string? path = GetFilePathForCurrentSelection();
            if (string.IsNullOrEmpty(path) || FileDirManager.IsDemoPath(path)) return;

            FileDirManager.SaveFile(path, _fileEditView.GetContent(), Path.GetExtension(path) ?? "", out _);
        }

        private bool SaveCurrentFileIfNeeded()
        {
            if (string.IsNullOrEmpty(_currentFile) || _fileEditView == null || _currentEditMode == EditMode.None) return true;
            if (!_fileEditView.Validate()) { ShowError(_fileEditView.LastValidationError); return false; }

            string? path = GetFilePathForCurrentSelection();
            if (string.IsNullOrEmpty(path) || FileDirManager.IsDemoPath(path)) return true;

            string content = _fileEditView.GetContent();
            if (FileDirManager.SaveFile(path, content, Path.GetExtension(path) ?? "", out string err)) return true;
            ShowError(err); return false;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (SaveCurrentFileIfNeeded()) ShowNotification("Файл успешно сохранён!");
        }

        private void AutoSave_Checked(object sender, RoutedEventArgs e)
        {
            _isAutoSaveEnabled = true;
            FileDirManager.SaveAutoSaveState(true);
            LoadSelectedFile();
        }

        private void AutoSave_Unchecked(object sender, RoutedEventArgs e)
        {
            _isAutoSaveEnabled = false;
            FileDirManager.SaveAutoSaveState(false);
            FileDirManager.StopAutoSave();
        }
        #endregion

        #region CRUD
        private void BtnCreate_Click(object sender, RoutedEventArgs e) { if (_createActions.TryGetValue(_currentEditMode, out var act)) act?.Invoke(); }
        private void BtnDelete_Click(object sender, RoutedEventArgs e) { if (_deleteActions.TryGetValue(_currentEditMode, out var act)) act?.Invoke(); }

        private bool CreateScene()
        {
            var d = new TextInputDialog("Создание сцены", "Введите название сцены:");
            if (d.ShowDialog() != true || string.IsNullOrWhiteSpace(d.Result)) return false;
            if (FileDirManager.CreateScene(d.Result.Trim(), out string err)) { RefreshAllComboBoxes(); ShowNotification($"Сцена '{d.Result}' создана"); return true; }
            ShowError(err); return false;
        }

        private bool CreateObject()
        {
            if (string.IsNullOrEmpty(_currentScene)) { ShowError("Сначала выберите сцену!"); return false; }
            var d = new TextInputDialog("Создание объекта", "Введите название объекта:");
            if (d.ShowDialog() != true || string.IsNullOrWhiteSpace(d.Result)) return false;
            if (FileDirManager.CreateObject(_currentScene, d.Result.Trim(), out string err)) { RefreshAllComboBoxes(); ShowNotification($"Объект '{d.Result}' создан"); return true; }
            ShowError(err); return false;
        }

        private bool AddFont()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Fonts (*.otf;*.ttf)|*.otf;*.ttf" };
            if (d.ShowDialog() != true) return false;
            if (FileDirManager.AddFont(d.FileName)) { RefreshAllComboBoxes(); ShowNotification($"Шрифт '{Path.GetFileName(d.FileName)}' добавлен"); return true; }
            ShowError("Не удалось добавить шрифт."); return false;
        }

        private bool DeleteScene()
        {
            if (string.IsNullOrEmpty(_currentScene)) return false;
            if (MessageBox.Show($"Удалить сцену '{_currentScene}'?", "Подтверждение", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;

            string sceneToDelete = _currentScene;

            // 1. Безопасно закрываем текущий файл и останавливаем таймер ДО удаления
            SaveCurrentFileSilently();
            FileDirManager.StopAutoSave();

            // 2. Сбрасываем состояние
            _currentScene = null;
            _currentObject = null; // Объекты тоже сбрасываем, так как сцена удалена
            _currentFile = null;
            ShowFileClosedState(); // Очищаем UI редактора

            // 3. Физическое удаление
            if (FileDirManager.DeleteScene(sceneToDelete, out string err))
            {
                RefreshAllComboBoxes();
                ShowNotification("Сцена удалена");
                return true;
            }
            ShowError(err); return false;
        }

        private bool DeleteObject()
        {
            if (string.IsNullOrEmpty(_currentObject) || string.IsNullOrEmpty(_currentScene)) return false;
            if (MessageBox.Show($"Удалить объект '{_currentObject}'?", "Подтверждение", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;

            string scene = _currentScene;
            string objToDelete = _currentObject;

            // 1. Остановка таймера и сохранение (на случай, если папка еще существует)
            SaveCurrentFileSilently();
            FileDirManager.StopAutoSave();

            // 2. Сброс состояния
            _currentObject = null;
            _currentFile = null;
            ShowFileClosedState();

            // 3. Удаление
            if (FileDirManager.DeleteObject(scene, objToDelete, out string err))
            {
                RefreshAllComboBoxes();
                ShowNotification("Объект удалён");
                return true;
            }
            ShowError(err); return false;
        }

        private bool DeleteFont()
        {
            // 🔹 ИСПРАВЛЕНИЕ: Игнорируем _currentFile (он указывает на parameters.json).
            // Удаляем только если пользователь явно выбрал .ttf/.otf файл в списке.
            if (ComboFile.SelectedItem is not string fontName)
            {
                ShowError("Выберите шрифт из списка для удаления.");
                return false;
            }

            // 🔹 ДОПОЛНИТЕЛЬНАЯ ЗАЩИТА: На всякий случай блокируем удаление файла настроек
            if (fontName.Equals("parameters.json", StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Нельзя удалить глобальный файл настроек шрифтов.");
                return false;
            }

            if (MessageBox.Show($"Удалить шрифт '{fontName}'?", "Подтверждение", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;

            if (FileDirManager.DeleteFont(fontName, out string err))
            {
                RefreshAllComboBoxes();
                ShowNotification("Шрифт удалён");
                return true;
            }
            ShowError(err); return false;
        }
        #endregion

        #region Переименование & Утилиты
        private void SetupRenameContextMenu(ComboBox cb, EditMode mode) { cb.ContextMenu = new ContextMenu(); var item = new MenuItem { Header = "Переименовать" }; item.Click += (s, e) => { if (cb.SelectedItem is string n) RenameItem(mode, n); }; cb.ContextMenu.Items.Add(item); }

        private void RenameItem(EditMode mode, string old)
        {
            var d = new TextInputDialog("Переименование", "Новое название:", old);
            if (d.ShowDialog() != true || string.IsNullOrWhiteSpace(d.Result)) return;
            string n = d.Result.Trim();

            // Проверяем, является ли переименовываемый элемент текущим открытым
            bool isOpen = (mode == EditMode.Scene && old == _currentScene) ||
                          (mode == EditMode.Object && old == _currentObject);

            if (isOpen)
            {
                SaveCurrentFileSilently();
                FileDirManager.StopAutoSave(); // Останавливаем таймер, чтобы он не писал в старую папку
            }

            bool ok = mode == EditMode.Scene ? FileDirManager.RenameScene(old, n, out string err) : FileDirManager.RenameObject(_currentScene!, old, n, out err);
            if (ok)
            {
                if (mode == EditMode.Scene) _currentScene = n;
                else _currentObject = n;

                // Блокируем события, чтобы RefreshAllComboBoxes не вызвал лишнюю загрузку
                _isProcessing = true;
                RefreshAllComboBoxes();
                _isProcessing = false;

                if (isOpen)
                {
                    // Перезагружаем файл, чтобы обновить _currentFilePath для таймера автосохранения
                    LoadSelectedFile();
                }

                ShowNotification("Переименовано");
            }
            else ShowError(err);
        }

        private void BtnOpenResources_Click(object sender, RoutedEventArgs e) => FileDirManager.OpenInExplorer(FileDirManager.AssetsPath);
        private void BtnCreateDemo_Click(object sender, RoutedEventArgs e) { FileDirManager.CreateDemoStructure(); RefreshAllComboBoxes(); LoadSelectedFile(); ShowNotification("Демо-структура создана"); }
        private void ShowNotification(string m) => MessageBox.Show(m, "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
        private void ShowError(string m) => MessageBox.Show(m, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        #endregion
    }

    public class TextInputDialog : Window
    {
        public string Result { get; private set; } = string.Empty;
        public TextInputDialog(string title, string prompt, string def = "")
        {
            Title = title; Width = 400; Height = 160; WindowStartupLocation = WindowStartupLocation.CenterOwner; Owner = Application.Current.MainWindow; ResizeMode = ResizeMode.NoResize;
            var grid = new Grid { Margin = new Thickness(15) }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            var tb = new TextBox { Text = def, Margin = new Thickness(0, 0, 0, 12) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 75, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cn = new Button { Content = "Отмена", Width = 75, IsCancel = true };
            ok.Click += (s, e) => { Result = tb.Text; DialogResult = true; Close(); }; cn.Click += (s, e) => DialogResult = false;
            sp.Children.Add(ok); sp.Children.Add(cn); Grid.SetRow(lbl, 0); Grid.SetRow(tb, 1); Grid.SetRow(sp, 2); grid.Children.Add(lbl); grid.Children.Add(tb); grid.Children.Add(sp); Content = grid;
            tb.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
        }
    }
}