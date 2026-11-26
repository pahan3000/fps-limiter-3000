using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FPSLimiter
{
    /// <summary>A SpecialK.ini setting row: [Section] Key=Value, retaining its original line until edited.</summary>
    internal sealed class SpecialKIniEntry : INotifyPropertyChanged
    {
        private string section;
        private string key;
        private string val;
        private bool isEnabled;
        private IList<string> choices = new List<string>();

        public event PropertyChangedEventHandler PropertyChanged;

        public string OriginalSection { get; }
        public string OriginalKey { get; }
        public string OriginalValue { get; }
        public string OriginalLine { get; }

        /// <summary>A row the user added with "Add custom" or "Add known option".</summary>
        public bool IsNew { get; }

        /// <summary>A documented option that has no line in the file yet; only written once it is ticked.</summary>
        public bool IsVirtual { get; }

        public bool OriginallyEnabled { get; }

        public SpecialKIniEntry(string section, string key, string value, string originalLine,
            bool isNew, bool isVirtual, bool originallyEnabled)
        {
            this.section = section ?? string.Empty;
            this.key = key ?? string.Empty;
            val = value ?? string.Empty;
            OriginalSection = this.section;
            OriginalKey = this.key;
            OriginalValue = val;
            OriginalLine = originalLine;
            IsNew = isNew;
            IsVirtual = isVirtual;
            OriginallyEnabled = originallyEnabled;
            isEnabled = originallyEnabled;
        }

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public string Section
        {
            get => section;
            set
            {
                value = value ?? string.Empty;
                if (string.Equals(section, value, StringComparison.Ordinal)) return;
                section = value;
                Raise(nameof(Section));
            }
        }

        public string Key
        {
            get => key;
            set
            {
                value = value ?? string.Empty;
                if (string.Equals(key, value, StringComparison.Ordinal)) return;
                key = value;
                Raise(nameof(Key));
            }
        }

        public string Value
        {
            get => val;
            set
            {
                value = value ?? string.Empty;
                if (string.Equals(val, value, StringComparison.Ordinal)) return;
                val = value;
                Raise(nameof(Value));
                Raise(nameof(IsBooleanValue));
                Raise(nameof(BooleanValue));
                Raise(nameof(ShowToggle));
                Raise(nameof(ShowText));
            }
        }

        /// <summary>Ticked settings are written to SpecialK.ini; unticked ones are left out (Special K then uses its default).</summary>
        public bool IsEnabled
        {
            get => isEnabled;
            set
            {
                if (isEnabled == value) return;
                isEnabled = value;
                Raise(nameof(IsEnabled));
            }
        }

        public IList<string> Choices => choices;
        public string Description { get; private set; } = string.Empty;

        public bool ShowChoices => choices.Count > 0;
        public bool ShowToggle => IsBooleanValue && !ShowChoices;
        public bool ShowText => !ShowToggle && !ShowChoices;

        /// <summary>Section and key can only be typed for custom rows; existing keys are fixed.</summary>
        public bool CanEditName => IsNew;
        public bool IsNameFixed => !IsNew;

        public bool IsBooleanValue =>
            string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Value, "false", StringComparison.OrdinalIgnoreCase);

        public bool BooleanValue
        {
            get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (!IsBooleanValue) return;
                Value = value ? "true" : "false";
            }
        }

        public void ApplyOption(SpecialKIniOption option)
        {
            var list = option?.Choices ?? new List<string>();
            var onlyBoolean = list.Count > 0 && list.All(c =>
                string.Equals(c, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c, "false", StringComparison.OrdinalIgnoreCase));
            choices = (list.Count == 0 || onlyBoolean) ? new List<string>() : list;
            Description = option?.Description ?? string.Empty;
            Raise(nameof(Choices));
            Raise(nameof(Description));
            Raise(nameof(ShowChoices));
            Raise(nameof(ShowToggle));
            Raise(nameof(ShowText));
        }

        /// <summary>The line to write for this setting, or null when it must not appear in the file.</summary>
        public string ToIniLine()
        {
            if (!IsEnabled) return null;

            var unchanged = !IsNew && !IsVirtual &&
                string.Equals(Section, OriginalSection, StringComparison.Ordinal) &&
                string.Equals(Key, OriginalKey, StringComparison.Ordinal) &&
                string.Equals(Value, OriginalValue, StringComparison.Ordinal);
            if (unchanged && OriginallyEnabled && OriginalLine != null) return OriginalLine;
            return Key + "=" + Value;
        }
    }

    /// <summary>
    /// Visual editor for a game's SpecialK.ini (same editor as the DXVK Manager's dxvk.conf editor).
    /// Comments, blank lines, section order, the original newline style and the file's text encoding
    /// are preserved.
    /// </summary>
    internal sealed class SpecialKConfigEditor
    {
        private sealed class IniLine
        {
            public string Raw;
            public SpecialKIniEntry Entry;
            public bool IsHeader;
            public string HeaderName;
        }

        private sealed class IniDocument
        {
            private readonly List<IniLine> lines = new List<IniLine>();
            private readonly string newline;
            private bool trailingNewline;

            public ObservableCollection<SpecialKIniEntry> Entries { get; } = new ObservableCollection<SpecialKIniEntry>();

            private IniDocument(string newline, bool trailingNewline)
            {
                this.newline = newline;
                this.trailingNewline = trailingNewline;
            }

            private static bool TryParseHeader(string raw, out string name)
            {
                name = null;
                var t = raw.Trim();
                if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
                {
                    name = t.Substring(1, t.Length - 2).Trim();
                    return true;
                }
                return false;
            }

            private static bool TryParseSetting(string raw, out string key, out string value)
            {
                key = null;
                value = null;
                var t = raw.Trim();
                if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') return false;
                var eq = raw.IndexOf('=');
                if (eq <= 0) return false;
                var k = raw.Substring(0, eq).Trim();
                if (k.Length == 0) return false;
                key = k;
                value = raw.Substring(eq + 1).Trim();
                return true;
            }

            /// <summary>
            /// Loads SpecialK.ini. Every line in the file becomes a ticked row; documented options the file
            /// does not contain are added as unticked rows that are only written once they are ticked.
            /// </summary>
            public static IniDocument Load(string path, IList<SpecialKIniOption> catalog)
            {
                var text = File.Exists(path) ? SpecialKLimiterBackend.ReadIniText(path) : string.Empty;
                text = text.TrimStart('\uFEFF');
                var newline = text.Contains("\r\n") ? "\r\n" : "\n";
                var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
                var hasTrailingNewline = normalized.EndsWith("\n", StringComparison.Ordinal);
                var document = new IniDocument(newline, hasTrailingNewline);

                var source = normalized.Length == 0 ? new string[0] : normalized.Split('\n');
                var count = source.Length;
                if (hasTrailingNewline) count--;

                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var section = string.Empty;
                for (var i = 0; i < count; i++)
                {
                    var raw = source[i];

                    string header;
                    if (TryParseHeader(raw, out header))
                    {
                        section = header;
                        document.lines.Add(new IniLine { Raw = raw, IsHeader = true, HeaderName = header });
                        continue;
                    }

                    string key, value;
                    if (TryParseSetting(raw, out key, out value))
                    {
                        var entry = new SpecialKIniEntry(section, key, value, raw, false, false, true);
                        document.Entries.Add(entry);
                        document.lines.Add(new IniLine { Entry = entry });
                        present.Add(SpecialKIniCatalog.IdOf(section, key));
                        continue;
                    }

                    document.lines.Add(new IniLine { Raw = raw });
                }

                if (catalog != null)
                {
                    foreach (var option in catalog)
                    {
                        if (present.Contains(option.Id)) continue;
                        var entry = new SpecialKIniEntry(option.Section, option.Key, StartValue(option), null, false, true, false);
                        document.Entries.Add(entry);
                    }
                }

                return document;
            }

            public SpecialKIniEntry AddEntry(string section, string key, string value)
            {
                var entry = new SpecialKIniEntry(section, key, value, null, true, false, false) { IsEnabled = true };
                Entries.Add(entry);
                trailingNewline = true;
                return entry;
            }

            public void RemoveEntry(SpecialKIniEntry entry)
            {
                if (entry == null) return;
                Entries.Remove(entry);
                var line = lines.FirstOrDefault(item => ReferenceEquals(item.Entry, entry));
                if (line != null) lines.Remove(line);
            }

            public string ToText()
            {
                // Settings that have no line in the file yet are inserted at the end of their section
                // (or in a new section at the end of the file).
                var pending = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var pendingOrder = new List<string>();
                foreach (var entry in Entries)
                {
                    if (!(entry.IsNew || entry.IsVirtual) || !entry.IsEnabled) continue;
                    var section = (entry.Section ?? string.Empty).Trim();
                    List<string> bucket;
                    if (!pending.TryGetValue(section, out bucket))
                    {
                        bucket = new List<string>();
                        pending[section] = bucket;
                        pendingOrder.Add(section);
                    }
                    bucket.Add(entry.ToIniLine());
                }

                // Where each existing section ends (last non-blank line of its first occurrence).
                var insertAfter = new Dictionary<int, string>();
                var sectionEnd = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string current = null;
                var currentIsFirst = false;
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    if (line.IsHeader)
                    {
                        current = line.HeaderName;
                        currentIsFirst = seen.Add(current);
                        if (currentIsFirst) sectionEnd[current] = i;
                        continue;
                    }
                    if (current == null || !currentIsFirst) continue;
                    var blank = line.Entry == null && string.IsNullOrWhiteSpace(line.Raw);
                    if (!blank) sectionEnd[current] = i;
                }
                foreach (var pair in sectionEnd)
                {
                    if (pending.ContainsKey(pair.Key))
                        insertAfter[pair.Value] = pair.Key;
                }

                var output = new List<string>();
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var text = line.Entry == null ? line.Raw ?? string.Empty : line.Entry.ToIniLine();
                    if (text != null) output.Add(text);

                    string section;
                    if (insertAfter.TryGetValue(i, out section))
                    {
                        output.AddRange(pending[section]);
                        written.Add(section);
                    }
                }

                foreach (var section in pendingOrder)
                {
                    if (written.Contains(section)) continue;
                    if (output.Count > 0 && !string.IsNullOrWhiteSpace(output[output.Count - 1]))
                        output.Add(string.Empty);
                    output.Add("[" + section + "]");
                    output.AddRange(pending[section]);
                }

                var result = string.Join(newline, output);
                return (trailingNewline || pendingOrder.Count > 0) && output.Count > 0 ? result + newline : result;
            }
        }

        /// <summary>Recommended options first, then the file's own order.</summary>
        private sealed class RecommendedFirstComparer : System.Collections.IComparer
        {
            private readonly IDictionary<string, SpecialKIniOption> byId;
            private readonly IList<SpecialKIniEntry> entries;

            public RecommendedFirstComparer(IDictionary<string, SpecialKIniOption> byId, IList<SpecialKIniEntry> entries)
            {
                this.byId = byId;
                this.entries = entries;
            }

            // Settings FPS Limiter manages come first, then the other known options, then the rest.
            private long RankOf(SpecialKIniEntry entry)
            {
                SpecialKIniOption option;
                if (!byId.TryGetValue(SpecialKIniCatalog.IdOf(entry.Section, entry.Key), out option))
                    return long.MaxValue;
                return (option.ManagedByExtension ? 0L : 1000000L) + option.Rank;
            }

            public int Compare(object x, object y)
            {
                var a = x as SpecialKIniEntry;
                var b = y as SpecialKIniEntry;
                if (a == null || b == null) return 0;
                var byRank = RankOf(a).CompareTo(RankOf(b));
                return byRank != 0 ? byRank : entries.IndexOf(a).CompareTo(entries.IndexOf(b));
            }
        }

        private readonly Func<Window> createWindow;
        private readonly Window owner;
        private readonly FPSLimiterSettings uiSettings;
        private readonly string path;
        private readonly string title;
        private readonly IniDocument document;
        private readonly IList<SpecialKIniOption> catalog = SpecialKIniCatalog.All;
        private readonly IDictionary<string, SpecialKIniOption> catalogById = SpecialKIniCatalog.ById();

        private ResourceDictionary windowResources;
        private TextBlock descriptionText;
        private ComboBox knownOptions;
        private bool resettingKnownOptions;
        private DataGrid entriesGrid;
        private TextBox filterBox;
        private CheckBox enabledOnlyBox;
        private TextBlock statusText;
        private ICollectionView filteredEntries;
        private bool dirty;
        private bool saved;

        private SpecialKConfigEditor(Func<Window> createWindow, Window owner, FPSLimiterSettings uiSettings,
            string path, string title, IniDocument document)
        {
            this.createWindow = createWindow;
            this.owner = owner;
            this.uiSettings = uiSettings;
            this.path = path;
            this.title = title;
            this.document = document;
        }

        /// <summary>
        /// Opens the editor for <paramref name="path"/>. Returns true when the file was saved.
        /// <paramref name="createWindow"/> should create a Playnite dialog window (may return null).
        /// </summary>
        public static bool Open(Func<Window> createWindow, Window owner, FPSLimiterSettings uiSettings,
            string path, string title)
        {
            try
            {
                var document = IniDocument.Load(path, SpecialKIniCatalog.All);
                return new SpecialKConfigEditor(createWindow, owner, uiSettings, path, title, document).Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't read SpecialK.ini:\n" + ex.Message, "FPS Limiter 3000",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private static string StartValue(SpecialKIniOption option)
        {
            if (!string.IsNullOrEmpty(option.DefaultValue)) return option.DefaultValue;
            return option.Choices != null && option.Choices.Count > 0 ? option.Choices[0] : string.Empty;
        }

        private bool Show()
        {
            Window window = null;
            try { window = createWindow?.Invoke(); } catch { window = null; }
            if (window == null) window = new Window();
            window.Title = title;
            window.Width = 880;
            window.Height = 660;
            window.MinWidth = 760;
            window.MinHeight = 420;
            window.ResizeMode = ResizeMode.CanResize;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            AppearancePalette.Apply(window, uiSettings?.ThemePreset, uiSettings?.GlassEffect == true);
            windowResources = window.Resources;
            window.SetResourceReference(Control.BackgroundProperty, "ReShadeBg");
            try { if (owner != null) window.Owner = owner; }
            catch { window.WindowStartupLocation = WindowStartupLocation.CenterScreen; }

            var dock = new DockPanel { Margin = new Thickness(14) };
            var footer = BuildFooter(window);
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);

            var body = new DockPanel();
            DockPanel.SetDock(body, Dock.Top);
            var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            intro.Children.Add(Heading("SpecialK.ini options", 18));
            intro.Children.Add(Muted(
                "Every setting in this game's SpecialK.ini is listed, with the ones FPS Limiter 3000 knows about first. " +
                "Untick Active to leave a setting out of the file (Special K then uses its default); tick a listed " +
                "option to add it. True/False values use switches and fixed-value settings use a drop-down. " +
                "Comments and blank lines are preserved. Close the game first: Special K rewrites its ini while running."));
            DockPanel.SetDock(intro, Dock.Top);
            body.Children.Add(intro);

            var toolbar = BuildToolbar();
            DockPanel.SetDock(toolbar, Dock.Top);
            body.Children.Add(toolbar);

            var descriptionPanel = BuildDescriptionPanel();
            DockPanel.SetDock(descriptionPanel, Dock.Bottom);
            body.Children.Add(descriptionPanel);

            entriesGrid = BuildEntriesGrid();
            body.Children.Add(entriesGrid);
            dock.Children.Add(body);
            window.Content = dock;

            filteredEntries = CollectionViewSource.GetDefaultView(document.Entries);
            filteredEntries.Filter = FilterEntry;
            var listView = filteredEntries as ListCollectionView;
            if (listView != null)
                listView.CustomSort = new RecommendedFirstComparer(catalogById, document.Entries);
            entriesGrid.ItemsSource = filteredEntries;
            foreach (var entry in document.Entries)
            {
                ApplyCatalog(entry);
                entry.PropertyChanged += EntryChanged;
            }
            entriesGrid.SelectionChanged += (s, e) => UpdateDescription();
            UpdateDescription();

            filterBox.TextChanged += (s, e) => filteredEntries.Refresh();
            entriesGrid.CellEditEnding += (s, e) =>
            {
                if (e.EditAction == DataGridEditAction.Commit) dirty = true;
            };
            entriesGrid.RowEditEnding += (s, e) =>
            {
                if (e.EditAction == DataGridEditAction.Commit) dirty = true;
            };

            window.Closing += (s, e) =>
            {
                if (!dirty || saved) return;
                var answer = MessageBox.Show(window, "Close without saving your changes?",
                    "SpecialK.ini editor", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) e.Cancel = true;
            };

            UpdateSummary();
            window.ShowDialog();
            return saved;
        }

        private UIElement BuildFooter(Window window)
        {
            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var save = new Button { Content = "Save", MinWidth = 90, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            save.Click += (s, e) => Save(window);
            cancel.Click += (s, e) => window.Close();
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            statusText = Muted(string.Empty);
            statusText.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(statusText);
            return footer;
        }

        private UIElement BuildToolbar()
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var search = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = "Filter", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "FpsMuted");
            search.Children.Add(label);
            filterBox = new TextBox { MinWidth = 170, MaxWidth = 280, ToolTip = "Filter settings by section, name or value" };
            search.Children.Add(filterBox);
            enabledOnlyBox = new CheckBox
            {
                Content = "Active only",
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Hide settings that are not active"
            };
            enabledOnlyBox.SetResourceReference(Control.ForegroundProperty, "FpsMuted");
            enabledOnlyBox.Checked += (s, e) => filteredEntries?.Refresh();
            enabledOnlyBox.Unchecked += (s, e) => filteredEntries?.Refresh();
            search.Children.Add(enabledOnlyBox);
            Grid.SetColumn(search, 0);
            grid.Children.Add(search);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            knownOptions = new ComboBox
            {
                MinWidth = 250,
                IsEditable = true,
                IsReadOnly = true,
                Margin = new Thickness(8, 0, 0, 0),
                ItemsSource = catalog,
                DisplayMemberPath = nameof(SpecialKIniOption.Label),
                ToolTip = "Pick a documented SpecialK.ini setting to add it with its default value"
            };
            ResetKnownOptions();
            knownOptions.SelectionChanged += (s, e) =>
            {
                if (resettingKnownOptions) return;
                var option = knownOptions.SelectedItem as SpecialKIniOption;
                if (option == null) return;
                AddKnown(option);
                ResetKnownOptions();
            };
            var add = new Button { Content = "Add custom", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            var remove = new Button
            {
                Content = "Remove selected",
                MinWidth = 110,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 4, 10, 4),
                ToolTip = "Custom settings are removed; other settings are switched off (left out of the file) instead."
            };
            add.Click += (s, e) => AddCustom();
            remove.Click += (s, e) => RemoveSelected();
            actions.Children.Add(knownOptions);
            actions.Children.Add(add);
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            return grid;
        }

        private DataGrid BuildEntriesGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                MinHeight = 220,
                RowHeight = 34,
                AlternationCount = 2
            };
            grid.SetResourceReference(Control.BackgroundProperty, "FpsPanelBg");
            grid.SetResourceReference(Control.ForegroundProperty, "FpsText");
            grid.SetResourceReference(Control.BorderBrushProperty, "FpsBorder");
            grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "FpsBorder");

            var activeBox = new FrameworkElementFactory(typeof(CheckBox));
            activeBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            activeBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            activeBox.SetValue(ToolTipService.ToolTipProperty, "Active: written to SpecialK.ini. Unticked: left out of the file.");
            activeBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(SpecialKIniEntry.IsEnabled))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Active",
                CellTemplate = new DataTemplate { DataType = typeof(SpecialKIniEntry), VisualTree = activeBox },
                Width = new DataGridLength(64)
            });

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Section",
                CellTemplate = CreateNameTemplate(nameof(SpecialKIniEntry.Section)),
                Width = new DataGridLength(2, DataGridLengthUnitType.Star)
            });
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Setting",
                CellTemplate = CreateNameTemplate(nameof(SpecialKIniEntry.Key)),
                Width = new DataGridLength(2, DataGridLengthUnitType.Star)
            });
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Value",
                CellTemplate = CreateValueTemplate(),
                Width = new DataGridLength(3, DataGridLengthUnitType.Star)
            });
            return grid;
        }

        /// <summary>Plain text for existing rows; a text box for custom rows so section and key can be typed.</summary>
        private DataTemplate CreateNameTemplate(string property)
        {
            var root = new FrameworkElementFactory(typeof(Grid));

            var block = new FrameworkElementFactory(typeof(TextBlock));
            block.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            block.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 0));
            block.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            block.SetBinding(TextBlock.TextProperty, new Binding(property) { Mode = BindingMode.OneWay });
            block.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(SpecialKIniEntry.IsNameFixed))
            {
                Converter = new BooleanToVisibilityConverter()
            });
            root.AppendChild(block);

            var box = new FrameworkElementFactory(typeof(TextBox));
            box.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            box.SetBinding(TextBox.TextProperty, new Binding(property)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            box.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(SpecialKIniEntry.CanEditName))
            {
                Converter = new BooleanToVisibilityConverter()
            });
            root.AppendChild(box);

            return new DataTemplate { DataType = typeof(SpecialKIniEntry), VisualTree = root };
        }

        private DataTemplate CreateValueTemplate()
        {
            var root = new FrameworkElementFactory(typeof(Grid));

            var text = new FrameworkElementFactory(typeof(TextBox));
            text.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            text.SetBinding(TextBox.TextProperty, new Binding(nameof(SpecialKIniEntry.Value))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            text.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(SpecialKIniEntry.ShowText))
            {
                Converter = new BooleanToVisibilityConverter()
            });
            text.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(SpecialKIniEntry.IsEnabled)));
            root.AppendChild(text);

            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetValue(ComboBox.IsEditableProperty, true);
            combo.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(SpecialKIniEntry.Choices)));
            combo.SetBinding(ComboBox.TextProperty, new Binding(nameof(SpecialKIniEntry.Value))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            combo.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(SpecialKIniEntry.ShowChoices))
            {
                Converter = new BooleanToVisibilityConverter()
            });
            combo.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(SpecialKIniEntry.IsEnabled)));
            root.AppendChild(combo);

            var toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            toggle.SetValue(FrameworkElement.StyleProperty, CreateSwitchStyle());
            toggle.SetValue(ToolTipService.ToolTipProperty, "Toggle this setting");
            toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(SpecialKIniEntry.BooleanValue))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            toggle.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(SpecialKIniEntry.ShowToggle))
            {
                Converter = new BooleanToVisibilityConverter()
            });
            toggle.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(SpecialKIniEntry.IsEnabled)));
            root.AppendChild(toggle);

            return new DataTemplate { DataType = typeof(SpecialKIniEntry), VisualTree = root };
        }

        private Style CreateSwitchStyle()
        {
            var style = new Style(typeof(ToggleButton));
            style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.FocusableProperty, false));
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 42d));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 22d));

            var trackOff = GetBrush("SwitchTrackOffBrush", Color.FromRgb(0x3D, 0x41, 0x49));
            var trackOn = GetBrush("SwitchTrackOnBrush", Color.FromRgb(0x1A, 0x9F, 0xFF));
            var trackBorder = GetBrush("SwitchTrackBorderBrush", Colors.Transparent);
            var thumbOff = GetBrush("SwitchThumbBrush", Color.FromRgb(0xF5, 0xF6, 0xF7));
            var thumbOn = GetBrush("SwitchThumbOnBrush", Color.FromRgb(0xF5, 0xF6, 0xF7));
            var thumbSize = GetDouble("SwitchThumbSize", 16);
            var leftMargin = GetThickness("SwitchThumbLeftMargin", new Thickness(2, 0, 0, 0));
            var rightMargin = GetThickness("SwitchThumbRightMargin", new Thickness(0, 0, 2, 0));

            var template = new ControlTemplate(typeof(ToggleButton));
            var root = new FrameworkElementFactory(typeof(Grid));
            var track = new FrameworkElementFactory(typeof(Border), "Track");
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            track.SetValue(Border.BackgroundProperty, trackOff);
            track.SetValue(Border.BorderBrushProperty, trackBorder);
            track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            root.AppendChild(track);

            var thumb = new FrameworkElementFactory(typeof(Ellipse), "Thumb");
            thumb.SetValue(FrameworkElement.WidthProperty, thumbSize);
            thumb.SetValue(FrameworkElement.HeightProperty, thumbSize);
            thumb.SetValue(Shape.FillProperty, thumbOff);
            thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            thumb.SetValue(FrameworkElement.MarginProperty, leftMargin);
            root.AppendChild(thumb);
            template.VisualTree = root;

            var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, trackOn, "Track"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, trackOn, "Track"));
            checkedTrigger.Setters.Add(new Setter(Shape.FillProperty, thumbOn, "Thumb"));
            checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "Thumb"));
            checkedTrigger.Setters.Add(new Setter(FrameworkElement.MarginProperty, rightMargin, "Thumb"));
            template.Triggers.Add(checkedTrigger);

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
            template.Triggers.Add(disabledTrigger);

            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private Brush GetBrush(string key, Color fallback)
        {
            var brush = windowResources?[key] as Brush;
            if (brush != null) return brush;
            var result = new SolidColorBrush(fallback);
            result.Freeze();
            return result;
        }

        private double GetDouble(string key, double fallback)
        {
            return windowResources != null && windowResources.Contains(key) && windowResources[key] is double value
                ? value : fallback;
        }

        private Thickness GetThickness(string key, Thickness fallback)
        {
            return windowResources != null && windowResources.Contains(key) && windowResources[key] is Thickness value
                ? value : fallback;
        }

        private bool FilterEntry(object item)
        {
            var entry = item as SpecialKIniEntry;
            if (entry == null) return false;
            if (enabledOnlyBox != null && enabledOnlyBox.IsChecked == true && !entry.IsEnabled) return false;
            var query = (filterBox?.Text ?? string.Empty).Trim();
            return query.Length == 0 ||
                   entry.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   entry.Section.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   entry.Value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private UIElement BuildDescriptionPanel()
        {
            descriptionText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            descriptionText.SetResourceReference(TextBlock.ForegroundProperty, "FpsMuted");
            return new ScrollViewer
            {
                Content = descriptionText,
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 8, 0, 0)
            };
        }

        private void ResetKnownOptions()
        {
            if (knownOptions == null) return;
            resettingKnownOptions = true;
            try
            {
                knownOptions.SelectedIndex = -1;
                knownOptions.Text = "Add known option…";
            }
            finally { resettingKnownOptions = false; }
        }

        private void ApplyCatalog(SpecialKIniEntry entry)
        {
            SpecialKIniOption option;
            catalogById.TryGetValue(SpecialKIniCatalog.IdOf(entry.Section, entry.Key), out option);
            entry.ApplyOption(option);
        }

        private void UpdateDescription()
        {
            if (descriptionText == null) return;
            var entry = entriesGrid?.SelectedItem as SpecialKIniEntry;
            if (entry == null)
            {
                descriptionText.Text = "Select a setting to see what it does.";
                return;
            }

            SpecialKIniOption option;
            catalogById.TryGetValue(SpecialKIniCatalog.IdOf(entry.Section, entry.Key), out option);
            if (option == null || string.IsNullOrWhiteSpace(option.Description))
            {
                descriptionText.Text = string.IsNullOrWhiteSpace(entry.Key)
                    ? "Type a section and setting name, or use \"Add known option\"."
                    : "[" + entry.Section + "] " + entry.Key + ": written by Special K itself. FPS Limiter 3000 has no description for this setting.";
                return;
            }

            var text = "[" + option.Section + "] " + option.Key +
                       (option.ManagedByExtension ? "  (used by FPS Limiter 3000)" : string.Empty) + "\n" + option.Description;
            if (!string.IsNullOrEmpty(option.DefaultValue))
                text += "\n\nSpecial K default: " + option.DefaultValue;
            descriptionText.Text = text;
        }

        private void UpdateSummary()
        {
            if (statusText == null) return;
            var active = document.Entries.Count(entry => entry.IsEnabled);
            statusText.Text = active + " of " + document.Entries.Count +
                              " settings active. Changes are written only when you press Save.";
        }

        private void AddKnown(SpecialKIniOption option)
        {
            filterBox.Clear();
            var id = option.Id;
            var existing = document.Entries.FirstOrDefault(e =>
                string.Equals(SpecialKIniCatalog.IdOf(e.Section, e.Key), id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var wasInactive = !existing.IsEnabled;
                if (wasInactive)
                {
                    existing.IsEnabled = true;
                    dirty = true;
                }
                filteredEntries.Refresh();
                entriesGrid.SelectedItem = existing;
                entriesGrid.ScrollIntoView(existing);
                statusText.Text = wasInactive
                    ? "Enabled " + option.Key + ". Press Save to write it."
                    : option.Key + " is already in the file.";
                return;
            }

            var entry = document.AddEntry(option.Section, option.Key, StartValue(option));
            ApplyCatalog(entry);
            entry.PropertyChanged += EntryChanged;
            filteredEntries.Refresh();
            entriesGrid.SelectedItem = entry;
            entriesGrid.ScrollIntoView(entry);
            dirty = true;
            statusText.Text = "Added " + option.Key + ". Press Save to write it.";
        }

        private void AddCustom()
        {
            filterBox.Clear();
            var entry = document.AddEntry(string.Empty, string.Empty, string.Empty);
            entry.PropertyChanged += EntryChanged;
            filteredEntries.Refresh();
            entriesGrid.SelectedItem = entry;
            entriesGrid.ScrollIntoView(entry);
            dirty = true;
            entriesGrid.Dispatcher.BeginInvoke(new Action(() =>
            {
                entriesGrid.CurrentCell = new DataGridCellInfo(entry, entriesGrid.Columns[1]);
                entriesGrid.BeginEdit();
            }), DispatcherPriority.Background);
        }

        private void RemoveSelected()
        {
            var entry = entriesGrid.SelectedItem as SpecialKIniEntry;
            if (entry == null) return;

            if (!entry.IsNew)
            {
                if (entry.IsEnabled)
                {
                    entry.IsEnabled = false;
                    dirty = true;
                    statusText.Text = entry.Key + " switched off. Press Save to leave it out of SpecialK.ini.";
                }
                else
                {
                    statusText.Text = entry.Key + " is already inactive.";
                }
                return;
            }

            entry.PropertyChanged -= EntryChanged;
            document.RemoveEntry(entry);
            filteredEntries.Refresh();
            dirty = true;
            UpdateSummary();
        }

        private void EntryChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SpecialKIniEntry.Key) ||
                e.PropertyName == nameof(SpecialKIniEntry.Section) ||
                e.PropertyName == nameof(SpecialKIniEntry.Value))
                dirty = true;

            if (e.PropertyName == nameof(SpecialKIniEntry.IsEnabled))
            {
                dirty = true;
                UpdateSummary();
            }

            if (e.PropertyName == nameof(SpecialKIniEntry.Key) ||
                e.PropertyName == nameof(SpecialKIniEntry.Section))
            {
                var entry = sender as SpecialKIniEntry;
                if (entry != null)
                {
                    ApplyCatalog(entry);
                    UpdateDescription();
                }
            }
        }

        private static bool IsBadName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   value.IndexOf('=') >= 0 || value.IndexOf('[') >= 0 || value.IndexOf(']') >= 0 ||
                   value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0;
        }

        private void Save(Window window)
        {
            try
            {
                entriesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                entriesGrid.CommitEdit(DataGridEditingUnit.Row, true);

                var invalid = document.Entries.FirstOrDefault(entry => entry.IsEnabled && entry.IsNew &&
                    (IsBadName(entry.Section) || IsBadName(entry.Key) ||
                     entry.Key.Any(char.IsWhiteSpace) ||
                     entry.Key.TrimStart().StartsWith(";", StringComparison.Ordinal) ||
                     entry.Key.TrimStart().StartsWith("#", StringComparison.Ordinal)));
                if (invalid != null)
                {
                    statusText.Text = "Custom settings need a section and a name (no spaces, '=', '[' or ']'), e.g. Render.FrameRate / TargetFPS.";
                    entriesGrid.SelectedItem = invalid;
                    entriesGrid.ScrollIntoView(invalid);
                    return;
                }

                var multiline = document.Entries.FirstOrDefault(entry => entry.IsEnabled &&
                    (entry.Value ?? string.Empty).IndexOfAny(new[] { '\r', '\n' }) >= 0);
                if (multiline != null)
                {
                    statusText.Text = "A value can't contain a line break.";
                    entriesGrid.SelectedItem = multiline;
                    entriesGrid.ScrollIntoView(multiline);
                    return;
                }

                var backup = path + ".fpslimiter-editor.bak";
                if (File.Exists(path) && !File.Exists(backup))
                    File.Copy(path, backup);

                // Keeps the file's own encoding (Special K may write UTF-8 or UTF-16).
                SpecialKLimiterBackend.WriteIniText(path, document.ToText());
                saved = true;
                dirty = false;
                window.Close();
            }
            catch (Exception ex)
            {
                statusText.Text = "Couldn't save SpecialK.ini: " + ex.Message;
            }
        }

        private TextBlock Heading(string text, double size)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "FpsText");
            return block;
        }

        private TextBlock Muted(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "FpsMuted");
            return block;
        }
    }
}
