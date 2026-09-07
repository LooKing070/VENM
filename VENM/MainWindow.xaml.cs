using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VENMLibrary;
using Path = System.IO.Path;

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
        private bool _isProcessing = false;
        private bool _updatingHideCheckbox = false;

        private AppLanguage _currentLanguage = AppLanguage.Ru;
        private Dictionary<string, List<string>> _hiddenObjects = new();
        private bool _isPanelOnRight = false;

        private string? _activeCanvasObject = null;
        private FrameworkElement? _draggingElement = null;
        private bool _isDragging = false;
        private bool _isResizing = false;
        private Point _dragStart;
        private double _dragOrigX, _dragOrigY, _dragOrigW, _dragOrigH;
        private readonly Dictionary<string, Dictionary<string, object?>> _pendingObjectParams = new();

        private const double SceneW = 1920;
        private const double SceneH = 1080;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;

            _modeActions = new Dictionary<EditMode, Action>
            {
                { EditMode.Scene, () => { } },
                { EditMode.Object, () => { } },
                { EditMode.Font, () => { LoadFontParameters(); } }
            };
            _createActions = new Dictionary<EditMode, Func<bool>>
            {
                { EditMode.Scene, CreateScene },
                { EditMode.Object, CreateObjectWithTexture },
                { EditMode.Font, AddFont }
            };
            _deleteActions = new Dictionary<EditMode, Func<bool>>
            {
                { EditMode.Scene, DeleteScene },
                { EditMode.Object, DeleteObject },
                { EditMode.Font, DeleteFont }
            };
            _fileEditView = new FileEditView(TextEditor, TextPreview, JsonEditorScroll, JsonEditorPanel, JsonPreviewScroll, JsonPreviewPanel);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Application.Current.DispatcherUnhandledException += (s, args) =>
            {
                ShowError($"Необработанная ошибка: {args.Exception.Message}");
                args.Handled = true;
            };

            FileDirManager.Initialize();
            FileDirManager.SetPreSaveAction(CommitPendingObjectParams);
            _hiddenObjects = FileDirManager.LoadHiddenObjects();

            _isAutoSaveEnabled = FileDirManager.LoadAutoSaveState();
            AutoSave.IsChecked = _isAutoSaveEnabled;

            _isPanelOnRight = FileDirManager.LoadPanelPosition() == "right";
            ApplyPanelPosition();

            BuildLanguageCombo();
            DisableAllControls();
            SetupComboContextMenu(ComboScene, EditMode.Scene);
            SetupComboContextMenu(ComboObject, EditMode.Object);
            SetupComboContextMenu(ComboFile, EditMode.Font);

            ApplyLanguage();

            string savedModeStr = FileDirManager.LoadEditMode();
            EditMode savedMode = Enum.TryParse<EditMode>(savedModeStr, out var m) && m != EditMode.None ? m : EditMode.Scene;
            SwitchEditMode(savedMode);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            CommitPendingObjectParams();
            SaveCurrentFileSilently();
            FileDirManager.StopAutoSave();
            Application.Current?.Shutdown();
        }

        #region Локализация
        private void BuildLanguageCombo()
        {
            var items = new List<ComboBoxItem>();
            foreach (AppLanguage l in Enum.GetValues<AppLanguage>())
            {
                string displayName = l switch
                {
                    AppLanguage.Ru => "Русский",
                    AppLanguage.En => "English",
                    AppLanguage.Zh => "中文",
                    AppLanguage.Ar => "العربية",
                    AppLanguage.Hi => "हिन्दी",
                    AppLanguage.It => "Italiano",
                    AppLanguage.Fr => "Français",
                    AppLanguage.De => "Deutsch",
                    _ => l.ToString()
                };
                items.Add(new ComboBoxItem { Content = displayName, Tag = l });
            }
            _isProcessing = true;
            ComboLanguage.ItemsSource = items;
            string savedLangStr = FileDirManager.LoadLanguage();
            _currentLanguage = Enum.TryParse<AppLanguage>(savedLangStr, true, out var parsedLang) ? parsedLang : AppLanguage.Ru;
            ComboLanguage.SelectedItem = items.FirstOrDefault(i => (AppLanguage)i.Tag == _currentLanguage) ?? items[0];
            _isProcessing = false;
        }

        private void ComboLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isProcessing) return;
            if (ComboLanguage.SelectedItem is ComboBoxItem item && item.Tag is AppLanguage lang)
            {
                _currentLanguage = lang;
                FileDirManager.SaveLanguage(lang.ToString());
                ApplyLanguage();
            }
        }

        private void BuildEditModeCombo()
        {
            var items = new List<string>
            {
                UILocalization.Get("ModeScene", _currentLanguage),
                UILocalization.Get("ModeObject", _currentLanguage),
                UILocalization.Get("ModeFont", _currentLanguage)
            };
            _isProcessing = true;
            ComboEditMode.ItemsSource = items;
            int index = _currentEditMode switch
            {
                EditMode.Scene => 0,
                EditMode.Object => 1,
                EditMode.Font => 2,
                _ => 0
            };
            if (index < items.Count) ComboEditMode.SelectedIndex = index;
            _isProcessing = false;
        }

        private void ApplyLanguage()
        {
            var lang = _currentLanguage;
            Title = UILocalization.Get("WindowTitle", lang);
            LblLanguage.Text = UILocalization.Get("LblLanguage", lang) + ":";
            LblEditMode.Text = UILocalization.Get("LblEditMode", lang) + ":";
            LblScene.Text = UILocalization.Get("LblScene", lang) + ":";
            LblObject.Text = UILocalization.Get("LblObject", lang) + ":";
            LblFile.Text = UILocalization.Get("LblFile", lang) + ":";
            BtnOpenAssets.Content = UILocalization.Get("BtnOpenAssets", lang);
            BtnCreateDemo.Content = UILocalization.Get("BtnCreateDemo", lang);
            BtnSave.Content = UILocalization.Get("BtnSave", lang);
            AutoSave.Content = UILocalization.Get("ChkAutoSave", lang);
            HideObject.Content = UILocalization.Get("ChkHideObject", lang);
            BtnTogglePanel.Content = UILocalization.Get("BtnTogglePanel", lang);
            FlowDirection = UILocalization.IsRtl(lang) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            BuildEditModeCombo();
            SetupComboContextMenu(ComboScene, EditMode.Scene);
            SetupComboContextMenu(ComboObject, EditMode.Object);
            SetupComboContextMenu(ComboFile, EditMode.Font);
            RenderScenePreview();
        }
        #endregion

        #region Переключение положения панели
        private void BtnTogglePanel_Click(object sender, RoutedEventArgs e)
        {
            _isPanelOnRight = !_isPanelOnRight;
            ApplyPanelPosition();
            FileDirManager.SavePanelPosition(_isPanelOnRight ? "right" : "left");
        }

        private void ApplyPanelPosition()
        {
            if (_isPanelOnRight)
            {
                RootGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                RootGrid.ColumnDefinitions[1].Width = new GridLength(200);
                Grid.SetColumn(ContentArea, 0);
                Grid.SetColumn(ControlPanel, 1);
                ControlPanel.BorderThickness = new Thickness(1, 0, 0, 0);
            }
            else
            {
                RootGrid.ColumnDefinitions[0].Width = new GridLength(200);
                RootGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(ControlPanel, 0);
                Grid.SetColumn(ContentArea, 1);
                ControlPanel.BorderThickness = new Thickness(0, 0, 1, 0);
            }
        }
        #endregion

        #region Переключение режимов
        private void ComboEditMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isProcessing) return;
            EditMode mode = ComboEditMode.SelectedIndex switch
            {
                0 => EditMode.Scene,
                1 => EditMode.Object,
                2 => EditMode.Font,
                _ => EditMode.Scene
            };
            SwitchEditMode(mode);
        }

        private void SwitchEditMode(EditMode newMode)
        {
            if (_currentEditMode == newMode) return;
            CommitPendingObjectParams();
            SaveCurrentFileSilently();
            FileDirManager.StopAutoSave();
            ShowFileClosedState();

            _currentScene = _currentObject = _currentFile = null;
            _activeCanvasObject = null;
            _currentEditMode = newMode;
            FileDirManager.SaveEditMode(newMode.ToString());

            EnableControlsForMode();
            if (_modeActions.TryGetValue(newMode, out var act)) act?.Invoke();

            _isProcessing = true;
            BuildEditModeCombo();
            ComboScene.SelectedItem = null;
            ComboObject.SelectedItem = null;
            ComboFile.SelectedItem = null;
            RefreshAllComboBoxes();
            _isProcessing = false;

            UpdateHideObjectCheckbox();
            if (newMode == EditMode.Font) LoadFontParameters();
        }

        private void ShowFileClosedState()
        {
            _fileEditView?.SetMode(".txt", false);
            _fileEditView?.LoadEditorContent("");
            _fileEditView?.LoadPreviewContent(UILocalization.Get("MsgFileClosed", _currentLanguage));
        }

        private void DisableAllControls()
        {
            ComboEditMode.IsEnabled = false;
            ComboScene.IsEnabled = false;
            ComboObject.IsEnabled = false;
            ComboFile.IsEnabled = false;
            BtnSave.IsEnabled = false;
        }

        private void EnableControlsForMode()
        {
            ComboEditMode.IsEnabled = true;
            ComboScene.IsEnabled = _currentEditMode != EditMode.Font;
            ComboObject.IsEnabled = _currentEditMode == EditMode.Object;
            ComboFile.IsEnabled = true;
            BtnSave.IsEnabled = true;
        }
        #endregion

        #region ComboBox & Каскадная логика
        private void RefreshAllComboBoxes()
        {
            UpdateComboBox(ComboScene, FileDirManager.GetScenes(), _currentScene);
            UpdateComboBox(ComboObject, _currentScene != null ? FileDirManager.GetObjects(_currentScene) : new List<string>(), _currentObject);
            string? fileSel = _currentEditMode == EditMode.Font ? null : _currentFile;
            UpdateComboBox(ComboFile, _currentEditMode == EditMode.Font ? FileDirManager.GetFonts() : GetFilesForCurrentSelection(), fileSel);
            RenderScenePreview();
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
            c.SelectedItem = sel != null && items.Contains(sel) ? sel : null;
        }

        private void ProcessSelection(ComboBox sender)
        {
            if (_currentEditMode == EditMode.None || sender.SelectedItem == null) return;
            string selectedName = sender.SelectedItem.ToString()!;
            if (_currentEditMode == EditMode.Font) { _currentFile = selectedName; return; }

            CommitPendingObjectParams();
            SaveCurrentFileSilently();
            FileDirManager.StopAutoSave();
            ShowFileClosedState();

            if (sender == ComboScene)
            {
                _currentScene = selectedName;
                _currentObject = null;
                _currentFile = null;
                _activeCanvasObject = null;
                if (_currentEditMode == EditMode.Scene)
                {
                    var files = GetFilesForCurrentSelection();
                    UpdateComboBox(ComboFile, files, null);
                    if (files.Count > 0) { ComboFile.SelectedIndex = 0; _currentFile = files[0]; LoadSelectedFile(); }
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
                        if (objFiles.Count > 0) { ComboFile.SelectedIndex = 0; _currentFile = objFiles[0]; LoadSelectedFile(); }
                    }
                    else UpdateComboBox(ComboFile, new List<string>(), null);
                }
            }
            else if (sender == ComboObject)
            {
                _currentObject = selectedName;
                _currentFile = null;
                _activeCanvasObject = null;
                var files = GetFilesForCurrentSelection();
                UpdateComboBox(ComboFile, files, null);
                if (files.Count > 0) { ComboFile.SelectedIndex = 0; _currentFile = files[0]; LoadSelectedFile(); }
            }
            else if (sender == ComboFile)
            {
                _currentFile = selectedName;
                LoadSelectedFile();
            }
            UpdateHideObjectCheckbox();
            RenderScenePreview();
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
            string preview = File.Exists(tip) ? FileDirManager.LoadFile(tip) : UILocalization.Get("MsgDemoNotLoaded", _currentLanguage);
            _fileEditView?.LoadPreviewContent(preview);
            if (_isAutoSaveEnabled && !isDemo)
                FileDirManager.SetupAutoSave(path, content, c => _fileEditView?.Validate() ?? true, msg => ShowError(msg));
            RenderScenePreview();
        }

        private void LoadFontParameters()
        {
            string path = FileDirManager.GetFontParametersPath();
            _currentFile = "parameters.json";
            _fileEditView?.SetMode(".json", false);
            _fileEditView?.LoadEditorContent(File.Exists(path) ? FileDirManager.LoadFile(path) : "{}");
            string tip = Path.Combine(FileDirManager.FontsPath, "parameters_tip.json");
            string preview = File.Exists(tip) ? FileDirManager.LoadFile(tip) : UILocalization.Get("MsgDemoNotLoaded", _currentLanguage);
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

        #region Скрытые объекты
        private void UpdateHideObjectCheckbox()
        {
            string? targetObj = _activeCanvasObject ?? _currentObject;
            _updatingHideCheckbox = true;
            if (targetObj == null || string.IsNullOrEmpty(_currentScene))
            {
                HideObject.IsChecked = false;
                HideObject.IsEnabled = false;
            }
            else
            {
                HideObject.IsEnabled = true;
                bool isHidden = _hiddenObjects.TryGetValue(_currentScene, out var list) && list.Contains(targetObj);
                HideObject.IsChecked = isHidden;
            }
            _updatingHideCheckbox = false;
        }

        private void HideObject_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingHideCheckbox) return;
            string? targetObj = _activeCanvasObject ?? _currentObject;
            if (targetObj == null || string.IsNullOrEmpty(_currentScene)) return;
            if (!_hiddenObjects.ContainsKey(_currentScene)) _hiddenObjects[_currentScene] = new List<string>();
            if (!_hiddenObjects[_currentScene].Contains(targetObj)) _hiddenObjects[_currentScene].Add(targetObj);
            FileDirManager.SaveHiddenObjects(_hiddenObjects);
            RenderScenePreview();
        }

        private void HideObject_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_updatingHideCheckbox) return;
            string? targetObj = _activeCanvasObject ?? _currentObject;
            if (targetObj == null || string.IsNullOrEmpty(_currentScene)) return;
            if (_hiddenObjects.TryGetValue(_currentScene, out var list))
            {
                list.Remove(targetObj);
                if (list.Count == 0) _hiddenObjects.Remove(_currentScene);
            }
            FileDirManager.SaveHiddenObjects(_hiddenObjects);
            RenderScenePreview();
        }
        #endregion

        #region Просмотрщик сцены
        private void RenderScenePreview()
        {
            if (SceneCanvas is null) return;
            SceneCanvas.Children.Clear();

            if (_currentEditMode == EditMode.Font) { AddSceneLabel(UILocalization.Get("MsgFontModeNoPreview", _currentLanguage)); return; }
            if (_currentEditMode == EditMode.None || string.IsNullOrEmpty(_currentScene)) { AddSceneLabel(UILocalization.Get("MsgSceneNotSelected", _currentLanguage)); return; }

            string scenePath = Path.Combine(FileDirManager.ScenesPath, _currentScene);
            var sceneParams = ReadJsonParams(Path.Combine(scenePath, "parameters.json"));

            string bgRel = GetParamString(sceneParams, "background");
            if (!string.IsNullOrEmpty(bgRel))
            {
                var bg = TryCreateImage(Path.Combine(FileDirManager.AssetsPath, bgRel), 0, 0, SceneW, SceneH);
                if (bg != null) SceneCanvas.Children.Add(bg);
            }

            foreach (var obj in FileDirManager.GetObjects(_currentScene))
            {
                if (_hiddenObjects.TryGetValue(_currentScene, out var hidList) && hidList.Contains(obj)) continue;

                var p = ReadJsonParams(Path.Combine(scenePath, obj, "parameters.json"));
                double x = GetParamDouble(p, "x", 100);
                double y = GetParamDouble(p, "y", 100);
                double w = GetParamDouble(p, "width", 300);
                double h = GetParamDouble(p, "height", 500);

                var container = new Grid
                {
                    Width = w,
                    Height = h,
                    Tag = obj,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.SizeAll
                };
                Canvas.SetLeft(container, x);
                Canvas.SetTop(container, y);

                string spriteRel = GetParamString(p, "sprite");
                Image? sprite = string.IsNullOrEmpty(spriteRel) ? null : TryCreateImage(Path.Combine(FileDirManager.AssetsPath, spriteRel), 0, 0, w, h);
                if (sprite != null)
                {
                    sprite.Width = w; sprite.Height = h;
                    container.Children.Add(sprite);
                }
                else
                {
                    string name = GetParamString(p, "name");
                    if (string.IsNullOrEmpty(name)) name = obj;
                    var placeholder = new Border
                    {
                        Background = Brushes.LightGray,
                        BorderBrush = Brushes.DimGray,
                        BorderThickness = new Thickness(2)
                    };
                    placeholder.Child = new TextBlock { Text = name, Foreground = Brushes.Black, FontSize = 32, Margin = new Thickness(8) };
                    container.Children.Add(placeholder);
                }

                bool isSelected = obj == _activeCanvasObject;
                if (isSelected)
                {
                    container.Children.Add(new Border { BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(3), IsHitTestVisible = false });
                    var handle = new Border
                    {
                        Width = 14,
                        Height = 14,
                        Background = Brushes.DodgerBlue,
                        BorderBrush = Brushes.White,
                        BorderThickness = new Thickness(1),
                        Cursor = Cursors.SizeNWSE,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Tag = container
                    };
                    handle.MouseLeftButtonDown += Handle_MouseDown;
                    container.Children.Add(handle);
                    Canvas.SetZIndex(container, 10);
                }

                container.MouseLeftButtonDown += ObjectContainer_MouseDown;
                SceneCanvas.Children.Add(container);
            }
        }

        private void SceneCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == SceneCanvas || e.OriginalSource is Canvas)
            {
                if (_activeCanvasObject != null)
                {
                    _activeCanvasObject = null;
                    UpdateHideObjectCheckbox();
                    RenderScenePreview();
                }
            }
        }

        private void ObjectContainer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not string obj) return;
            e.Handled = true;
            _activeCanvasObject = obj;
            UpdateHideObjectCheckbox();

            _draggingElement = el;
            _isDragging = true;
            _isResizing = false;
            _dragStart = e.GetPosition(SceneCanvas);
            _dragOrigX = Canvas.GetLeft(el);
            _dragOrigY = Canvas.GetTop(el);
            _dragOrigW = el.Width;
            _dragOrigH = el.Height;
            Mouse.OverrideCursor = Cursors.SizeAll;
            Mouse.Capture(SceneCanvas);
        }

        private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement h) return;
            if (h.Tag is not FrameworkElement container || container.Tag is not string obj) return;
            e.Handled = true;
            _activeCanvasObject = obj;
            UpdateHideObjectCheckbox();

            _draggingElement = container;
            _isResizing = true;
            _isDragging = false;
            _dragStart = e.GetPosition(SceneCanvas);
            _dragOrigX = Canvas.GetLeft(container);
            _dragOrigY = Canvas.GetTop(container);
            _dragOrigW = container.Width;
            _dragOrigH = container.Height;
            Mouse.OverrideCursor = Cursors.SizeNWSE;
            Mouse.Capture(SceneCanvas);
        }

        private void SceneCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingElement == null) return;
            var pos = e.GetPosition(SceneCanvas);
            if (_isDragging)
            {
                Canvas.SetLeft(_draggingElement, _dragOrigX + (pos.X - _dragStart.X));
                Canvas.SetTop(_draggingElement, _dragOrigY + (pos.Y - _dragStart.Y));
            }
            else if (_isResizing)
            {
                _draggingElement.Width = Math.Max(20, _dragOrigW + (pos.X - _dragStart.X));
                _draggingElement.Height = Math.Max(20, _dragOrigH + (pos.Y - _dragStart.Y));
            }
        }

        private void SceneCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggingElement == null) return;
            var el = _draggingElement;
            string? obj = el.Tag as string;

            if (!string.IsNullOrEmpty(obj))
            {
                if (!_pendingObjectParams.ContainsKey(obj)) _pendingObjectParams[obj] = new Dictionary<string, object?>();
                _pendingObjectParams[obj]["x"] = Math.Round(Canvas.GetLeft(el), 1);
                _pendingObjectParams[obj]["y"] = Math.Round(Canvas.GetTop(el), 1);
                _pendingObjectParams[obj]["width"] = Math.Round(el.Width, 1);
                _pendingObjectParams[obj]["height"] = Math.Round(el.Height, 1);

                CommitPendingObjectParams();

                bool redrawNeeded = true;
                if (_currentEditMode == EditMode.Object)
                {
                    if (_currentObject != obj)
                    {
                        _isProcessing = true;
                        _currentObject = obj;
                        ComboObject.SelectedItem = obj;
                        var files = GetFilesForCurrentSelection();
                        UpdateComboBox(ComboFile, files, null);
                        if (files.Count > 0)
                        {
                            ComboFile.SelectedIndex = 0;
                            _currentFile = files[0];
                            LoadSelectedFile();
                            redrawNeeded = false;
                        }
                        _isProcessing = false;
                    }
                    else if (_currentFile == "parameters.json")
                    {
                        LoadSelectedFile();
                        redrawNeeded = false;
                    }
                }
                if (redrawNeeded) RenderScenePreview();
            }

            Mouse.Capture(null);
            Mouse.OverrideCursor = null;
            _draggingElement = null;
            _isDragging = false;
            _isResizing = false;
        }

        private void CommitPendingObjectParams()
        {
            if (_pendingObjectParams.Count == 0 || string.IsNullOrEmpty(_currentScene)) return;
            foreach (var kv in _pendingObjectParams)
            {
                string paramPath = Path.Combine(FileDirManager.ScenesPath, _currentScene, kv.Key, "parameters.json");
                var existing = ReadJsonParams(paramPath);
                foreach (var change in kv.Value) existing[change.Key] = change.Value;
                string json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
                FileDirManager.SaveFile(paramPath, json, ".json", out _);
            }
            _pendingObjectParams.Clear();
        }

        private void AddSceneLabel(string text)
        {
            var tb = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 48 };
            Canvas.SetLeft(tb, (SceneW - 900) / 2);
            Canvas.SetTop(tb, (SceneH - 60) / 2);
            SceneCanvas.Children.Add(tb);
        }

        private static Image? TryCreateImage(string path, double x, double y, double w, double h)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                var img = new Image { Source = bmp, Width = w, Height = h };
                Canvas.SetLeft(img, x);
                Canvas.SetTop(img, y);
                return img;
            }
            catch { return null; }
        }

        private static Dictionary<string, object?> ReadJsonParams(string path)
        {
            var res = new Dictionary<string, object?>();
            if (!File.Exists(path)) return res;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    res[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }
            }
            catch { }
            return res;
        }

        private static string GetParamString(Dictionary<string, object?> p, string key)
            => p.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty;

        private static double GetParamDouble(Dictionary<string, object?> p, string key, double def)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return def;
            if (v is double d) return d;
            return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : def;
        }
        #endregion

        #region Drag & Drop
        private void SceneCanvas_DragOver(object sender, DragEventArgs e)
        {
            bool canDrop = false;
            if (!string.IsNullOrEmpty(_currentScene) && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    canDrop = files.All(f =>
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext == ".png" || ext == ".jpg" || ext == ".jpeg";
                    });
                }
            }
            e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void SceneCanvas_Drop(object sender, DragEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentScene)) { ShowError(UILocalization.Get("MsgSelectSceneFirst", _currentLanguage)); return; }
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

            foreach (var file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;

                string relPath = FileDirManager.CopyTextureToAssets(file, out string copyErr);
                if (string.IsNullOrEmpty(relPath)) { ShowError(copyErr); continue; }

                string baseName = Path.GetFileNameWithoutExtension(file);
                var sb = new StringBuilder();
                foreach (char c in baseName)
                    if (!Path.GetInvalidFileNameChars().Contains(c) && c != ' ') sb.Append(c);
                string objName = sb.Length > 0 ? sb.ToString() : "dropped_object";

                string scenePath = Path.Combine(FileDirManager.ScenesPath, _currentScene, objName);
                if (Directory.Exists(scenePath))
                {
                    int i = 1;
                    while (Directory.Exists(Path.Combine(FileDirManager.ScenesPath, _currentScene, $"{objName}_{i}"))) i++;
                    objName = $"{objName}_{i}";
                }

                if (FileDirManager.CreateObjectWithParameters(_currentScene, objName, relPath,
                        "character", 100, 100, 300, 500, out string err))
                {
                    ShowNotification($"Объект '{objName}' создан из {Path.GetFileName(file)}");
                }
                else ShowError(err);
            }
            RefreshAllComboBoxes();
        }
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
            CommitPendingObjectParams();
            if (SaveCurrentFileIfNeeded())
            {
                ShowNotification(UILocalization.Get("MsgFileSaved", _currentLanguage));
                RenderScenePreview();
            }
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
        private void ContextCreate(EditMode target)
        {
            if (target == EditMode.Font && _currentEditMode != EditMode.Font)
            {
                ShowError("Добавление шрифта доступно только в режиме редактирования шрифтов.");
                return;
            }
            if (_createActions.TryGetValue(target, out var act)) act?.Invoke();
        }

        private void ContextDelete(EditMode target)
        {
            if (target == EditMode.Font && _currentEditMode != EditMode.Font)
            {
                ShowError("Удаление шрифта доступно только в режиме редактирования шрифтов.");
                return;
            }
            if (_deleteActions.TryGetValue(target, out var act)) act?.Invoke();
        }

        private bool CreateScene()
        {
            var d = new TextInputDialog("Создание сцены", "Введите название сцены:");
            if (d.ShowDialog() != true || string.IsNullOrWhiteSpace(d.Result)) return false;
            if (FileDirManager.CreateScene(d.Result.Trim(), out string err)) { RefreshAllComboBoxes(); ShowNotification($"Сцена '{d.Result}' создана"); return true; }
            ShowError(err); return false;
        }

        private bool CreateObjectWithTexture()
        {
            if (string.IsNullOrEmpty(_currentScene))
            {
                ShowError(UILocalization.Get("MsgSelectSceneFirst", _currentLanguage));
                return false;
            }
            var dialog = new ObjectCreationDialog();
            if (dialog.ShowDialog() != true) return false;

            string objName = dialog.ObjectName.Trim();
            string objectType = dialog.ObjectType;
            string textureSource = dialog.TexturePath;

            string relPath = string.Empty;
            if (!string.IsNullOrEmpty(textureSource))
            {
                relPath = FileDirManager.CopyTextureToAssets(textureSource, out string copyErr);
                if (string.IsNullOrEmpty(relPath)) { ShowError(copyErr); return false; }
            }

            if (FileDirManager.CreateObjectWithParameters(_currentScene, objName, relPath,
                    objectType, 100, 100, 300, 500, out string err))
            {
                RefreshAllComboBoxes();
                ShowNotification($"Объект '{objName}' создан");
                return true;
            }
            ShowError(err);
            return false;
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
            CommitPendingObjectParams();
            SaveCurrentFileSilently();
            FileDirManager.StopAutoSave();
            _currentScene = null; _currentObject = null; _currentFile = null; _activeCanvasObject = null;
            ShowFileClosedState();
            if (FileDirManager.DeleteScene(sceneToDelete, out string err)) { RefreshAllComboBoxes(); ShowNotification("Сцена удалена"); return true; }
            ShowError(err); return false;
        }

        private bool DeleteObject()
        {
            if (string.IsNullOrEmpty(_currentObject) || string.IsNullOrEmpty(_currentScene)) return false;
            if (MessageBox.Show($"Удалить объект '{_currentObject}'?", "Подтверждение", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;
            string scene = _currentScene;
            string objToDelete = _currentObject;
            SaveCurrentFileSilently();
            FileDirManager.StopAutoSave();
            _currentObject = null; _currentFile = null; _activeCanvasObject = null;
            ShowFileClosedState();
            if (FileDirManager.DeleteObject(scene, objToDelete, out string err)) { RefreshAllComboBoxes(); ShowNotification("Объект удалён"); return true; }
            ShowError(err); return false;
        }

        private bool DeleteFont()
        {
            if (ComboFile.SelectedItem is not string fontName) { ShowError("Выберите шрифт из списка для удаления."); return false; }
            if (fontName.Equals("parameters.json", StringComparison.OrdinalIgnoreCase)) { ShowError("Нельзя удалить глобальный файл настроек шрифтов."); return false; }
            if (MessageBox.Show($"Удалить шрифт '{fontName}'?", "Подтверждение", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;
            if (FileDirManager.DeleteFont(fontName, out string err)) { RefreshAllComboBoxes(); ShowNotification("Шрифт удалён"); return true; }
            ShowError(err); return false;
        }
        #endregion

        #region Контекстное меню & Переименование
        private void SetupComboContextMenu(ComboBox cb, EditMode target)
        {
            var menu = new ContextMenu();
            string createKey = target switch
            {
                EditMode.Scene => "CtxCreateScene",
                EditMode.Object => "CtxCreateObject",
                EditMode.Font => "CtxAddFont",
                _ => "CtxCreateScene"
            };
            string deleteKey = target switch
            {
                EditMode.Scene => "CtxDeleteScene",
                EditMode.Object => "CtxDeleteObject",
                EditMode.Font => "CtxDeleteFont",
                _ => "CtxDeleteScene"
            };
            var createItem = new MenuItem { Header = UILocalization.Get(createKey, _currentLanguage) };
            createItem.Click += (s, e) => ContextCreate(target);
            menu.Items.Add(createItem);
            if (target != EditMode.Font)
            {
                var renameItem = new MenuItem { Header = UILocalization.Get("CtxRename", _currentLanguage) };
                renameItem.Click += (s, e) => { if (cb.SelectedItem is string n) RenameItem(target, n); };
                menu.Items.Add(renameItem);
            }
            var deleteItem = new MenuItem { Header = UILocalization.Get(deleteKey, _currentLanguage) };
            deleteItem.Click += (s, e) => ContextDelete(target);
            menu.Items.Add(deleteItem);
            cb.ContextMenu = menu;
        }

        private void RenameItem(EditMode mode, string old)
        {
            var d = new TextInputDialog("Переименование", "Новое название:", old);
            if (d.ShowDialog() != true || string.IsNullOrWhiteSpace(d.Result)) return;
            string n = d.Result.Trim();
            bool isOpen = (mode == EditMode.Scene && old == _currentScene) || (mode == EditMode.Object && old == _currentObject);
            if (isOpen) { SaveCurrentFileSilently(); FileDirManager.StopAutoSave(); }
            bool ok = mode == EditMode.Scene ? FileDirManager.RenameScene(old, n, out string err) : FileDirManager.RenameObject(_currentScene!, old, n, out err);
            if (ok)
            {
                if (mode == EditMode.Scene) _currentScene = n; else _currentObject = n;
                _isProcessing = true; RefreshAllComboBoxes(); _isProcessing = false;
                if (isOpen) LoadSelectedFile();
                ShowNotification("Переименовано");
            }
            else ShowError(err);
        }
        #endregion

        #region Утилиты
        private void BtnOpenAssets_Click(object sender, RoutedEventArgs e) => FileDirManager.OpenInExplorer(FileDirManager.AssetsPath);
        private void BtnCreateDemo_Click(object sender, RoutedEventArgs e) { FileDirManager.CreateDemoStructure(); RefreshAllComboBoxes(); LoadSelectedFile(); ShowNotification("Демо-структура создана"); }
        private void ShowNotification(string m) => MessageBox.Show(m, UILocalization.Get("MsgSuccess", _currentLanguage), MessageBoxButton.OK, MessageBoxImage.Information);
        private void ShowError(string m) => MessageBox.Show(m, UILocalization.Get("MsgError", _currentLanguage), MessageBoxButton.OK, MessageBoxImage.Error);
        #endregion
    }

    public class ObjectCreationDialog : Window
    {
        public string ObjectName { get; private set; } = string.Empty;
        public string ObjectType { get; private set; } = "character";
        public string TexturePath { get; private set; } = string.Empty;

        public ObjectCreationDialog()
        {
            Title = "Создание объекта";
            Width = 480; Height = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current.MainWindow;
            ResizeMode = ResizeMode.NoResize;

            var grid = new Grid { Margin = new Thickness(15) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lblName = new TextBlock { Text = "Имя объекта:", Margin = new Thickness(0, 0, 0, 3) };
            var tbName = new TextBox { Margin = new Thickness(0, 0, 0, 8) };

            var lblType = new TextBlock { Text = "Тип объекта:", Margin = new Thickness(0, 0, 0, 3) };
            var cbType = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            cbType.ItemsSource = new[] { "character", "background", "item", "ui_element", "prop" };
            cbType.SelectedIndex = 0;

            var lblTex = new TextBlock { Text = "Текстура (необязательно):", Margin = new Thickness(0, 0, 0, 3) };
            var texGrid = new Grid();
            texGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            texGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tbTex = new TextBox { IsReadOnly = true, Margin = new Thickness(0, 0, 5, 0), Background = Brushes.WhiteSmoke };
            var btnBrowse = new Button { Content = "Обзор...", Width = 80 };
            Grid.SetColumn(tbTex, 0);
            Grid.SetColumn(btnBrowse, 1);
            texGrid.Children.Add(tbTex);
            texGrid.Children.Add(btnBrowse);

            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "Создать", Width = 85, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Отмена", Width = 85, IsCancel = true };
            sp.Children.Add(ok);
            sp.Children.Add(cancel);

            Grid.SetRow(lblName, 0); Grid.SetRow(tbName, 1);
            Grid.SetRow(lblType, 2); Grid.SetRow(cbType, 3);
            var texStack = new StackPanel();
            texStack.Children.Add(lblTex);
            texStack.Children.Add(texGrid);
            Grid.SetRow(texStack, 4);
            grid.Children.Add(lblName); grid.Children.Add(tbName);
            grid.Children.Add(lblType); grid.Children.Add(cbType);
            grid.Children.Add(texStack);
            grid.Children.Add(sp);
            Content = grid;

            btnBrowse.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Изображения (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                    Title = "Выберите текстуру"
                };
                if (dlg.ShowDialog() == true)
                {
                    tbTex.Text = dlg.FileName;
                    TexturePath = dlg.FileName;
                }
            };

            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(tbName.Text))
                {
                    MessageBox.Show("Введите имя объекта.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ObjectName = tbName.Text.Trim();
                ObjectType = cbType.SelectedItem?.ToString() ?? "character";
                DialogResult = true;
                Close();
            };

            tbName.Loaded += (s, e) => { tbName.Focus(); tbName.SelectAll(); };
        }
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