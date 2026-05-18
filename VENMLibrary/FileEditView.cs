using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VENMLibrary
{
    public enum EditorMode { Txt, Json }

    public class FileEditView
    {
        private readonly TextBox _txtEditor;
        private readonly TextBox _txtPreview;
        private readonly ScrollViewer _jsonEditorScroll;
        private readonly StackPanel _jsonEditorPanel;
        private readonly ScrollViewer _jsonPreviewScroll;
        private readonly StackPanel _jsonPreviewPanel;

        private EditorMode _currentMode = EditorMode.Txt;
        private bool _isDemo = false;
        public string LastValidationError { get; private set; } = string.Empty;

        public FileEditView(TextBox txtEditor, TextBox txtPreview, ScrollViewer jsonEditorScroll, StackPanel jsonEditorPanel, ScrollViewer jsonPreviewScroll, StackPanel jsonPreviewPanel)
        {
            _txtEditor = txtEditor; _txtPreview = txtPreview;
            _jsonEditorScroll = jsonEditorScroll; _jsonEditorPanel = jsonEditorPanel;
            _jsonPreviewScroll = jsonPreviewScroll; _jsonPreviewPanel = jsonPreviewPanel;
        }

        public void SetMode(string? ext, bool isDemo = false)
        {
            _currentMode = ext?.Equals(".json", StringComparison.OrdinalIgnoreCase) == true ? EditorMode.Json : EditorMode.Txt;
            _isDemo = isDemo;
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            bool isJson = _currentMode == EditorMode.Json;
            _txtEditor.Visibility = isJson ? Visibility.Collapsed : Visibility.Visible;
            _txtPreview.Visibility = isJson ? Visibility.Collapsed : Visibility.Visible;
            _jsonEditorScroll.Visibility = isJson ? Visibility.Visible : Visibility.Collapsed;
            _jsonPreviewScroll.Visibility = isJson ? Visibility.Visible : Visibility.Collapsed;
        }

        public void LoadEditorContent(string content)
        {
            if (_currentMode == EditorMode.Txt) _txtEditor.Text = content;
            else RenderJsonInterface(content, _jsonEditorPanel, true);
        }

        public void LoadPreviewContent(string content)
        {
            if (_currentMode == EditorMode.Txt) _txtPreview.Text = content;
            else RenderJsonInterface(content, _jsonPreviewPanel, false);
        }

        public string GetContent() => _currentMode == EditorMode.Txt ? _txtEditor.Text : BuildJsonFromInterface(_jsonEditorPanel);

        public bool Validate()
        {
            LastValidationError = string.Empty;
            if (_currentMode == EditorMode.Txt) return true;

            string json = BuildJsonFromInterface(_jsonEditorPanel, out bool hasDuplicates);
            if (hasDuplicates)
            {
                LastValidationError = "Ошибка: в JSON обнаружены поля с одинаковыми ключами.";
                return false;
            }
            if (!FileDirManager.ValidateJson(json))
            {
                LastValidationError = "Ошибка: невалидный синтаксис JSON.";
                return false;
            }
            return true;
        }

        private void RenderJsonInterface(string? json, Panel targetPanel, bool isEditable)
        {
            targetPanel.Children.Clear();
            var dict = ParseJson(json ?? "{}");
            foreach (var kvp in dict) AddJsonRow(targetPanel, kvp.Key, kvp.Value, isEditable);

            if (isEditable && !_isDemo)
            {
                var btn = new Button { Content = "+ добавить поле", Margin = new Thickness(0, 12, 0, 0), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
                btn.Click += (s, e) =>
                {
                    targetPanel.Children.Remove(btn);
                    AddJsonRow(targetPanel, "ключ", "значение", true);
                    targetPanel.Children.Add(btn);
                };
                targetPanel.Children.Add(btn);
            }
        }

        private void AddJsonRow(Panel panel, string key, object? value, bool isEditable)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (isEditable) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

            var keyBox = new TextBox
            {
                Text = key,
                IsReadOnly = !isEditable,
                Margin = new Thickness(0),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                Background = Brushes.White
            };
            var valBox = new TextBox
            {
                Text = value?.ToString() ?? "null",
                IsReadOnly = !isEditable,
                Margin = new Thickness(0),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                Background = Brushes.White
            };

            Grid.SetColumn(keyBox, 0); Grid.SetColumn(valBox, 2);
            grid.Children.Add(keyBox); grid.Children.Add(valBox);

            if (isEditable)
            {
                var del = new Button { Content = "-", Width = 18, Height = 18, FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0) };
                Grid.SetColumn(del, 3);
                del.Click += (s, e) => panel.Children.Remove(grid);
                grid.Children.Add(del);

                keyBox.TextChanged += (s, e) => ValidateFields(keyBox, valBox);
                valBox.TextChanged += (s, e) => ValidateFields(keyBox, valBox);
                ValidateFields(keyBox, valBox);
            }
            panel.Children.Add(grid);
        }

        private void ValidateFields(TextBox k, TextBox v)
        {
            bool kOk = !string.IsNullOrWhiteSpace(k.Text);
            k.BorderBrush = kOk ? Brushes.Gray : Brushes.Red;

            bool vOk = true;
            string t = v.Text.Trim();
            if (!string.IsNullOrEmpty(t) && (t.StartsWith("{") || t.StartsWith("[")))
            {
                try { JsonDocument.Parse(t); } catch { vOk = false; }
            }
            v.BorderBrush = vOk ? Brushes.Gray : Brushes.Red;
        }

        private string BuildJsonFromInterface(Panel panel, out bool hasDuplicates)
        {
            hasDuplicates = false;
            var dict = new Dictionary<string, object?>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in panel.Children.OfType<Grid>())
            {
                var k = FindChild<TextBox>(g, 0);
                var v = FindChild<TextBox>(g, 2);
                if (k != null && v != null && !string.IsNullOrWhiteSpace(k.Text))
                {
                    if (!seenKeys.Add(k.Text)) { hasDuplicates = true; break; }
                    dict[k.Text] = ParseValue(v.Text);
                }
            }
            return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        }

        private string BuildJsonFromInterface(Panel panel) => BuildJsonFromInterface(panel, out _);

        private Dictionary<string, object?> ParseJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var res = new Dictionary<string, object?>();
                foreach (var p in doc.RootElement.EnumerateObject()) res[p.Name] = Extract(p.Value);
                return res;
            }
            catch { return new Dictionary<string, object?>(); }
        }

        private static object? Extract(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => e.GetRawText()
        };

        private static object? ParseValue(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return null; t = t.Trim();
            if (t == "null") return null; if (t == "true") return true; if (t == "false") return false;
            if (double.TryParse(t, out var n)) return n;
            try { return JsonDocument.Parse(t).RootElement.Clone(); } catch { return t; }
        }

        private static T? FindChild<T>(DependencyObject p, int col) where T : DependencyObject
        {
            if (p is Grid g) foreach (var c in g.Children) if (c is FrameworkElement fe && Grid.GetColumn(fe) == col && fe is T r) return r;
            return null;
        }
    }
}