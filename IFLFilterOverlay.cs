using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Color = SharpDX.Color;
using IflFilter = ItemFilterLibrary.ItemFilter;
using IflItemData = ItemFilterLibrary.ItemData;

namespace IFLFilterOverlay;

// ------------------------------------------------------------------ model

public class HighlightRule
{
    public string Name { get; set; } = "Rule";
    public bool Enabled { get; set; } = true;
    public Vector4 Color { get; set; } = new Vector4(1f, 0f, 0f, 1f);

    // IFL expression (same syntax as InvWithLinq / NPCInvWithLinq). Must evaluate to true/false.
    public string Expression { get; set; } = "";
}

/// <summary>One category = one .txt file with a set of rules.</summary>
public class FilterCategory
{
    public string Name = "";
    public string FilePath = "";
    public string Preamble = "";          // comments before the first rule (preserved as-is)
    public List<HighlightRule> Rules = new();
    public DateTime Stamp;                // file timestamp at the last read/write
    public bool Dirty;
    public long DirtyAt;
}

public class FilterOverlaySettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public ToggleNode InInventory { get; set; } = new ToggleNode(true);
    public ToggleNode InStash { get; set; } = new ToggleNode(true);
    public ToggleNode InVendor { get; set; } = new ToggleNode(true);
    // any other window with item icons (e.g. Kingsmarch "Rewards"), learned by hovering an item or by a one-time scan
    public ToggleNode InOtherWindows { get; set; } = new ToggleNode(true);

    // UI tree index paths (from IngameUi) of the learned item containers, e.g. "13,7,0,2"
    public List<string> ExtraContainers { get; set; } = new();
    public RangeNode<int> Thickness { get; set; } = new RangeNode<int>(2, 1, 6);

    // Disabled categories (file name without .txt)
    public List<string> DisabledCategories { get; set; } = new();

    // Legacy rule storage in settings - only used to migrate into filter.txt
    public List<HighlightRule> Rules { get; set; } = new();
}

// ------------------------------------------------------------------ file format
//
//   // comments at the top of the file are preserved
//   ## Rule name | #RRGGBB | on
//   HasTag("Ring") && ItemStats[GameStat.BaseMaximumLife] >= 80
//
//   ## Another rule | #00FF00AA | off
//   BaseName == "Divine Orb"
//
// A rule runs until the next "##" line. "//" comments are allowed inside expressions.

public static class FilterFile
{
    public static void Parse(string text, FilterCategory cat)
    {
        cat.Rules = new List<HighlightRule>();
        var pre = new List<string>();
        HighlightRule cur = null;
        var body = new List<string>();

        void Flush()
        {
            if (cur == null) return;
            cur.Expression = string.Join("\n", body).Trim();
            cat.Rules.Add(cur);
            cur = null;
            body.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.TrimStart();

            if (t.StartsWith("##"))
            {
                Flush();
                var parts = t.Substring(2).Split('|');
                cur = new HighlightRule { Name = parts[0].Trim() };
                if (parts.Length > 1) cur.Color = ParseHex(parts[1], cur.Color);
                if (parts.Length > 2)
                {
                    var f = parts[2].Trim().ToLowerInvariant();
                    cur.Enabled = !(f == "off" || f == "0" || f == "false");
                }
            }
            else if (cur == null) pre.Add(line);
            else body.Add(line);
        }

        Flush();
        cat.Preamble = string.Join("\n", pre).Trim();
    }

    public static string Serialize(FilterCategory cat)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(cat.Preamble))
        {
            sb.AppendLine(cat.Preamble.Trim());
            sb.AppendLine();
        }

        foreach (var r in cat.Rules)
        {
            var name = (r.Name ?? "").Replace('|', '/').Replace('\n', ' ').Trim();
            sb.AppendLine($"## {name} | {ToHex(r.Color)} | {(r.Enabled ? "on" : "off")}");
            sb.AppendLine((r.Expression ?? "").Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ToHex(Vector4 v)
    {
        int B(float f) => Math.Max(0, Math.Min(255, (int)Math.Round(f * 255f)));
        return $"#{B(v.X):X2}{B(v.Y):X2}{B(v.Z):X2}" + (v.W < 0.999f ? $"{B(v.W):X2}" : "");
    }

    private static Vector4 ParseHex(string s, Vector4 fallback)
    {
        s = (s ?? "").Trim().TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return fallback;
        try
        {
            int G(int i) => int.Parse(s.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Vector4(G(0) / 255f, G(2) / 255f, G(4) / 255f, s.Length == 8 ? G(6) / 255f : 1f);
        }
        catch { return fallback; }
    }
}

// ------------------------------------------------------------------ plugin

public class FilterOverlayPlugin : BaseSettingsPlugin<FilterOverlaySettings>
{
    private const string DefaultPreamble =
        "// IFL FilterOverlay category file. Every rule starts with a line:\n" +
        "// ## Name | #RRGGBB | on\n" +
        "// followed by an IFL expression (may span several lines, comments start with //).";

    private readonly Dictionary<long, (Color? Col, long Time)> _cache = new();
    private readonly Dictionary<string, (IflFilter Filter, string Error)> _compiled = new();

    // learned item containers (Kingsmarch rewards, ...): cached item lists per container path
    private readonly Dictionary<string, List<NormalInventoryItem>> _extraItems = new();
    private long _extraTick;
    private string _learnMsg;

    // categories (files)
    private List<FilterCategory> _cats = new();
    private bool _scanned;
    private long _lastScan;
    private string _ioError;
    private string _curName = "";
    private string _newCat = "";

    // hint catalog: (clause to insert into the expression, label)
    private List<(string Clause, string Label)> _catalog;

    // item under the cursor
    private IflItemData _hoverData;
    private string _hoverName = "";
    private string _hoverError;
    private string _hoverRule;   // "category: rule" that highlights the hovered item
    private long _hoverAddr;
    private long _hoverBuilt;
    private int _hoverVersion;
    private List<(string Name, int Value)> _hoverStats = new();

    // live test of the selected rule
    private string _testKey;
    private int _testVersion = -1;
    private bool? _testResult;
    private string _testError;

    // UI
    private int _sel = -1;
    private bool _useOr;
    private string _filter = "";

    // Folder with the category files. If ConfigDirectory does not compile in your build,
    // replace it with any path (e.g. Path.Combine(DirectoryFullName, "Filters")).
    private string FilterDir => Path.Combine(ConfigDirectory, "Filters");

    public override bool Initialise() => true;

    // ---------------------------------------------------------------- render

    public override void Render()
    {
        if (!Settings.Enable) return;
        Tick();

        var ui = GameController.IngameState.IngameUi;

        UpdateHover();

        if (Settings.InInventory && ui.InventoryPanel.IsVisible)
            Process(ui.InventoryPanel[InventoryIndex.PlayerInventory]?.VisibleInventoryItems);

        if (Settings.InStash)
        {
            var st = ui.StashElement;
            if (st != null && st.IsVisible)
                Process(st.VisibleStash?.VisibleInventoryItems);
        }

        if (Settings.InVendor)
        {
            var pw = ui.PurchaseWindow;
            if (pw != null && pw.IsVisible)
                Process(pw.TabContainer?.VisibleStash?.VisibleInventoryItems);

            var ph = ui.PurchaseWindowHideout;
            if (ph != null && ph.IsVisible)
                Process(ph.TabContainer?.VisibleStash?.VisibleInventoryItems);
        }

        if (Settings.InOtherWindows)
            DrawExtraContainers(ui);
    }

    private void Process(IList<NormalInventoryItem> items)
    {
        if (items == null) return;
        if (_cache.Count > 5000) _cache.Clear();
        var now = Environment.TickCount64;

        foreach (var inv in items)
        {
            var ent = inv?.Item;
            if (ent == null || !ent.IsValid || !inv.IsVisible) continue;

            // evaluate once per second per item to avoid reading memory every frame
            if (!_cache.TryGetValue(ent.Address, out var c) || now - c.Time > 1000)
            {
                c = (Evaluate(ent), now);
                _cache[ent.Address] = c;
            }

            if (c.Col.HasValue)
                Graphics.DrawFrame(inv.GetClientRect(), c.Col.Value, Settings.Thickness);
        }
    }

    private Color? Evaluate(Entity ent)
    {
        IflItemData data = null;
        var (rule, _) = FindRule(ent, ref data);
        if (rule == null) return null;

        var v = rule.Color;
        return new Color(v.X, v.Y, v.Z, v.W);
    }

    /// <summary>First matching rule (enabled categories, top to bottom) and its category name.</summary>
    private (HighlightRule Rule, string Cat) FindRule(Entity ent, ref IflItemData data)
    {
        foreach (var cat in _cats)
        {
            if (Settings.DisabledCategories.Contains(cat.Name)) continue;

            foreach (var rule in cat.Rules)
            {
                if (!rule.Enabled) continue;
                var (filter, _) = Compile(rule.Expression);
                if (filter == null) continue;

                try
                {
                    data ??= new IflItemData(ent, GameController);
                    if (filter.Matches(data)) return (rule, cat.Name);
                }
                catch { /* error on this particular item - skip the rule */ }
            }
        }

        return (null, null);
    }

    // -------- the only place that compiles expressions through ItemFilterLibrary
    private (IflFilter Filter, string Error) Compile(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        if (_compiled.TryGetValue(text, out var c)) return c;

        try { c = (IflFilter.LoadFromString(text), null); }
        catch (Exception e) { c = (null, e.Message); }

        if (_compiled.Count > 300) _compiled.Clear();
        _compiled[text] = c;
        return c;
    }
    // ---------------------------------------------------------------------------------------------

    // ---------------------------------------------------------------- other windows (learned containers)

    private HashSet<long> HandledWindows(Element ui)
    {
        var set = new HashSet<long>();
        void Add(Element e) { if (e != null && e.Address != 0) set.Add(e.Address); }
        var iu = GameController.IngameState.IngameUi;
        Add(iu.InventoryPanel);
        Add(iu.StashElement);
        Add(iu.PurchaseWindow);
        Add(iu.PurchaseWindowHideout);
        return set;
    }

    /// <summary>Index path from root to el (e.g. "13,7,0"), or null if el is not below root.</summary>
    private static string PathOf(Element root, Element el)
    {
        var idx = new List<int>();
        var cur = el;
        for (var guard = 0; guard < 24 && cur != null && cur.Address != 0; guard++)
        {
            if (cur.Address == root.Address)
            {
                idx.Reverse();
                return string.Join(",", idx);
            }

            var parent = cur.Parent;
            if (parent == null || parent.Address == 0) return null;

            var kids = parent.Children;
            var i = -1;
            if (kids != null)
                for (var k = 0; k < kids.Count; k++)
                    if (kids[k].Address == cur.Address) { i = k; break; }

            if (i < 0) return null;
            idx.Add(i);
            cur = parent;
        }

        return null;
    }

    private static Element ResolvePath(Element root, string path)
    {
        try
        {
            var el = root;
            foreach (var part in path.Split(','))
            {
                var kids = el.Children;
                var i = int.Parse(part);
                if (kids == null || i < 0 || i >= kids.Count) return null;
                el = kids[i];
            }
            return el != null && el.Address != 0 ? el : null;
        }
        catch { return null; }
    }

    private static List<NormalInventoryItem> ItemsOf(Element container)
    {
        var list = new List<NormalInventoryItem>();
        var kids = container?.Children;
        if (kids == null) return list;

        foreach (var k in kids)
        {
            if (k == null || !k.IsVisible) continue;
            var ni = k.AsObject<NormalInventoryItem>();
            var ent = ni?.Item;
            if (ent != null && ent.IsValid && ent.GetComponent<Base>() != null) list.Add(ni);
        }

        return list;
    }

    /// <summary>Called when a new item is hovered: if it lives outside the known windows, remember its container.</summary>
    private void TryLearn(Element hoverEl)
    {
        if (!Settings.InOtherWindows) return;

        try
        {
            var ui = GameController.IngameState.IngameUi;
            var handled = HandledWindows(ui);

            // inside a window we already handle? then nothing to learn
            var cur = hoverEl;
            for (var i = 0; i < 16 && cur != null && cur.Address != 0; i++)
            {
                if (handled.Contains(cur.Address)) return;
                if (cur.Address == ui.Address) break;
                cur = cur.Parent;
            }

            var container = hoverEl.Parent;
            if (container == null || container.Address == 0) return;

            var path = PathOf(ui, container);
            if (path == null || Settings.ExtraContainers.Contains(path)) return;

            Settings.ExtraContainers.Add(path);
            _extraTick = 0;
            _learnMsg = "Learned a new item window (path " + path + ").";
        }
        catch { /* best effort */ }
    }

    /// <summary>One-time scan of the whole visible UI tree (heavy, runs only on button press).</summary>
    private void ScanForItemWindows()
    {
        var found = 0;
        try
        {
            var ui = GameController.IngameState.IngameUi;
            var handled = HandledWindows(ui);
            var parents = new HashSet<long>();
            var budget = 40000;

            void Walk(Element el, int depth)
            {
                if (el == null || el.Address == 0 || budget-- <= 0 || depth > 14 || !el.IsVisible ||
                    handled.Contains(el.Address)) return;

                // an item icon has a size between one cell and a big two-handed weapon
                var r = el.GetClientRect();
                if (r.Width >= 20 && r.Height >= 20 && r.Width <= 400 && r.Height <= 500)
                {
                    var ni = el.AsObject<NormalInventoryItem>();
                    var ent = ni?.Item;
                    if (ent != null && ent.IsValid && ent.GetComponent<Base>() != null)
                    {
                        var parent = el.Parent;
                        if (parent != null && parents.Add(parent.Address))
                        {
                            var path = PathOf(ui, parent);
                            if (path != null && !Settings.ExtraContainers.Contains(path))
                            {
                                Settings.ExtraContainers.Add(path);
                                found++;
                            }
                        }
                        return; // do not descend into the item itself
                    }
                }

                var kids = el.Children;
                if (kids == null) return;
                foreach (var k in kids) Walk(k, depth + 1);
            }

            Walk(ui, 0);
        }
        catch (Exception e)
        {
            _learnMsg = "Scan failed: " + e.Message;
            return;
        }

        _extraTick = 0;
        _learnMsg = found > 0
            ? $"Found {found} item window(s)."
            : "No new item windows found. Open the window with items and try again.";
    }

    private void DrawExtraContainers(Element ui)
    {
        if (Settings.ExtraContainers.Count == 0) return;
        var now = Environment.TickCount64;

        if (now - _extraTick > 500)
        {
            _extraTick = now;
            _extraItems.Clear();
            foreach (var path in Settings.ExtraContainers.ToList())
            {
                var el = ResolvePath(ui, path);
                _extraItems[path] = el != null && el.IsVisible ? ItemsOf(el) : new List<NormalInventoryItem>();
            }
        }

        foreach (var list in _extraItems.Values) Process(list);
    }

    // ---------------------------------------------------------------- category files

    private FilterCategory Cur
    {
        get
        {
            var c = _cats.FirstOrDefault(x => x.Name == _curName) ?? _cats.FirstOrDefault();
            if (c != null) _curName = c.Name;
            return c;
        }
    }

    private void Touch()
    {
        var c = Cur;
        if (c == null) return;
        c.Dirty = true;
        c.DirtyAt = Environment.TickCount64;
    }

    /// <summary>Every ~1.5 s: writes modified categories to disk and picks up edits made in an external editor.</summary>
    private void Tick()
    {
        var now = Environment.TickCount64;

        foreach (var c in _cats.Where(x => x.Dirty && now - x.DirtyAt > 700).ToList())
            Save(c);

        if (_scanned && now - _lastScan < 1500) return;
        _scanned = true;
        _lastScan = now;
        Scan();
    }

    private void Scan()
    {
        try
        {
            Directory.CreateDirectory(FilterDir);
            var files = Directory.GetFiles(FilterDir, "*.txt")
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

            // first run after the rename: copy category files from the old "ItemHighlight" plugin folder
            if (files.Count == 0)
            {
                try
                {
                    var cfg = ConfigDirectory.TrimEnd('\\', '/');
                    var legacy = Path.Combine(Path.GetDirectoryName(cfg) ?? "", "ItemHighlight", "Filters");
                    if (Directory.Exists(legacy) &&
                        !string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(FilterDir), StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var f in Directory.GetFiles(legacy, "*.txt"))
                            File.Copy(f, Path.Combine(FilterDir, Path.GetFileName(f)), false);
                        files = Directory.GetFiles(FilterDir, "*.txt")
                                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }
                catch { /* migration is best-effort */ }
            }

            // first run: create filter.txt (and migrate old rules from settings, if any)
            if (files.Count == 0)
            {
                var def = new FilterCategory
                {
                    Name = "filter",
                    FilePath = Path.Combine(FilterDir, "filter.txt"),
                    Preamble = DefaultPreamble,
                    Rules = Settings.Rules.ToList(),
                };
                Settings.Rules.Clear();
                Save(def);
                files.Add(def.FilePath);
            }

            _cats.RemoveAll(c => !c.Dirty &&
                                 !files.Contains(c.FilePath, StringComparer.OrdinalIgnoreCase));

            foreach (var f in files)
            {
                var c = _cats.FirstOrDefault(x => string.Equals(x.FilePath, f, StringComparison.OrdinalIgnoreCase));
                if (c == null)
                {
                    c = new FilterCategory { FilePath = f, Name = Path.GetFileNameWithoutExtension(f) };
                    Load(c);
                    _cats.Add(c);
                }
                else if (!c.Dirty && File.GetLastWriteTimeUtc(f) != c.Stamp)
                {
                    Load(c);
                }
            }

            _cats = _cats.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _ioError = null;
        }
        catch (Exception e)
        {
            _ioError = e.Message;
        }
    }

    private void Load(FilterCategory c)
    {
        FilterFile.Parse(File.ReadAllText(c.FilePath, Encoding.UTF8), c);
        c.Stamp = File.GetLastWriteTimeUtc(c.FilePath);
        c.Dirty = false;
        _cache.Clear();
    }

    private void Save(FilterCategory c)
    {
        try
        {
            File.WriteAllText(c.FilePath, FilterFile.Serialize(c), new UTF8Encoding(false));
            c.Stamp = File.GetLastWriteTimeUtc(c.FilePath);
            c.Dirty = false;
        }
        catch (Exception e)
        {
            _ioError = "Failed to save: " + e.Message;
            c.Dirty = false; // avoid hammering the disk every frame
        }
    }

    /// <summary>Opens the filters folder in Windows Explorer.</summary>
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(FilterDir);
            var full = Path.GetFullPath(FilterDir).Replace('/', '\\');
            System.Diagnostics.Process.Start("explorer.exe", $"\"{full}\"");
        }
        catch (Exception e)
        {
            _ioError = "Failed to open folder: " + e.Message;
        }
    }

    private void CreateCategory(string rawName)
    {
        var name = new string((rawName ?? "").Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray()).Trim();
        if (name.Length == 0) return;

        var path = Path.Combine(FilterDir, name + ".txt");
        var c = _cats.FirstOrDefault(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (c == null)
        {
            c = new FilterCategory { Name = name, FilePath = path, Preamble = DefaultPreamble };
            Save(c);
            _cats.Add(c);
            _cats = _cats.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        _curName = c.Name;
        _sel = -1;
    }

    // ---------------------------------------------------------------- item under the cursor

    private void UpdateHover()
    {
        try
        {
            var hover = GameController.IngameState.UIHover;
            if (hover == null || hover.Address == 0) return;

            var icon = hover.AsObject<NormalInventoryItem>();
            var ent = icon?.Item;
            if (ent == null || !ent.IsValid || ent.GetComponent<Base>() == null) return;

            var now = Environment.TickCount64;
            if (ent.Address != _hoverAddr || now - _hoverBuilt > 1000)
            {
                _hoverAddr = ent.Address;
                _hoverBuilt = now;
                RebuildHover(ent);
                TryLearn(hover);
            }

            UpdateTest();

            // green frame on the hovered item if the selected rule matches it
            if (_testResult == true)
                Graphics.DrawFrame(icon.GetClientRect(), Color.LimeGreen, 3);

            // show which rule highlights the item next to the tooltip corner
            if (_hoverRule != null)
            {
                var tip = icon.Tooltip;
                var r = tip != null && tip.Address != 0 ? tip.GetClientRect() : icon.GetClientRect();
                var y = r.Y > 24 ? r.Y - 22 : r.Bottom + 2;
                Graphics.DrawTextWithBackground("Rule: " + _hoverRule, new Vector2(r.X, y), Color.White, Color.Black);
            }
        }
        catch { /* UIHover can be invalid - skip this frame */ }
    }

    private void RebuildHover(Entity ent)
    {
        _hoverVersion++;
        _hoverError = null;
        _hoverRule = null;
        _hoverStats = new();
        _hoverName = ent.GetComponent<Base>()?.Name ?? "?";

        try
        {
            var data = new IflItemData(ent, GameController);
            _hoverData = data;

            var n = data.Name;
            var b = data.BaseName;
            _hoverName = string.IsNullOrEmpty(n) || n == b ? b : $"{n} - {b}";

            if (data.ItemStats != null)
                foreach (var kv in data.ItemStats)
                    if (kv.Value != 0)
                        _hoverStats.Add((kv.Key.ToString(), (int)kv.Value));

            _hoverStats = _hoverStats.OrderBy(s => s.Name).ToList();

            var (rule, cat) = FindRule(ent, ref data);
            if (rule != null) _hoverRule = $"{cat}: {rule.Name}";
        }
        catch (Exception e)
        {
            _hoverData = null;
            _hoverError = e.Message;
        }
    }

    private string CurrentExpr
    {
        get
        {
            var c = Cur;
            return c != null && _sel >= 0 && _sel < c.Rules.Count ? c.Rules[_sel].Expression ?? "" : "";
        }
    }

    private void UpdateTest()
    {
        var expr = CurrentExpr;
        if (expr == _testKey && _testVersion == _hoverVersion) return;
        _testKey = expr;
        _testVersion = _hoverVersion;
        _testResult = null;
        _testError = null;

        if (string.IsNullOrWhiteSpace(expr)) return;

        var (filter, err) = Compile(expr);
        if (filter == null) { _testError = err; return; }
        if (_hoverData == null) return;

        try { _testResult = filter.Matches(_hoverData); }
        catch (Exception e) { _testError = "Runtime error: " + e.Message; }
    }

    // ---------------------------------------------------------------- hint catalog

    private void BuildCatalog()
    {
        if (_catalog != null) return;
        _catalog = new();

        try
        {
            foreach (var s in Enum.GetNames(typeof(GameStat)).OrderBy(x => x))
                _catalog.Add(($"ItemStats[GameStat.{s}] >= 1", s));
        }
        catch (Exception e) { LogError("Could not read GameStat: " + e.Message); }

        try
        {
            var bases = GameController.Files.BaseItemTypes.Contents.Values
                .Where(b => !string.IsNullOrEmpty(b.BaseName))
                .GroupBy(b => b.BaseName)
                .Select(g => g.First())
                .OrderBy(b => b.BaseName);

            foreach (var b in bases)
                _catalog.Add(($"BaseName == \"{b.BaseName}\"", $"{b.BaseName}   (base, {b.ClassName})"));
        }
        catch (Exception e) { LogError("Could not build the base list: " + e.Message); }
    }

    // ---------------------------------------------------------------- settings

    // ImGui.NET passes the string to TextDisabled/TextColored as a printf format: a bare "%" crashes the game
    private static string F(string s) => s?.Replace("%", "%%") ?? "";

    private void AppendClause(string clause)
    {
        var cat = Cur;
        if (cat == null) return;

        if (_sel < 0 || _sel >= cat.Rules.Count)
        {
            cat.Rules.Add(new HighlightRule { Name = $"Rule {cat.Rules.Count + 1}" });
            _sel = cat.Rules.Count - 1;
        }

        var r = cat.Rules[_sel];
        r.Expression = string.IsNullOrWhiteSpace(r.Expression)
            ? clause
            : r.Expression.TrimEnd() + (_useOr ? "\n|| " : "\n&& ") + clause;
        Touch();
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        Tick();
        BuildCatalog();
        _cache.Clear(); // while settings are open, re-evaluate frames immediately

        DrawOtherWindows();
        DrawCategories();
        DrawRules();
        DrawEditor();
        DrawHints();
    }

    private void DrawOtherWindows()
    {
        if (!Settings.InOtherWindows) return;

        ImGui.Separator();
        ImGui.TextDisabled("Other item windows (e.g. Kingsmarch Rewards): hover an item in that window once,");
        ImGui.TextDisabled("or open it and press the scan button. The window is remembered afterwards.");

        if (ImGui.Button("Find item windows now")) ScanForItemWindows();
        ImGui.SameLine();
        if (ImGui.Button($"Forget learned ({Settings.ExtraContainers.Count})"))
        {
            Settings.ExtraContainers.Clear();
            _extraItems.Clear();
        }

        if (_learnMsg != null) ImGui.TextDisabled(F(_learnMsg));
    }

    private void DrawCategories()
    {
        ImGui.Separator();
        ImGui.Text("Categories (.txt files, top to bottom)");

        for (int i = 0; i < _cats.Count; i++)
        {
            var c = _cats[i];
            ImGui.PushID(i);

            bool en = !Settings.DisabledCategories.Contains(c.Name);
            if (ImGui.Checkbox("##cen", ref en))
            {
                if (en) Settings.DisabledCategories.Remove(c.Name);
                else Settings.DisabledCategories.Add(c.Name);
            }

            ImGui.SameLine();
            if (ImGui.Selectable($"{c.Name}  ({c.Rules.Count})###cat", c == Cur))
            {
                _curName = c.Name;
                _sel = -1;
            }

            ImGui.PopID();
        }

        ImGui.InputText("##newcat", ref _newCat, 60);
        ImGui.SameLine();
        if (ImGui.Button("+ Category"))
        {
            CreateCategory(_newCat);
            _newCat = "";
        }

        if (ImGui.Button("Open folder")) OpenFolder();
        ImGui.SameLine();
        if (ImGui.Button("Reload")) { _scanned = false; _compiled.Clear(); }

        ImGui.TextDisabled(F(FilterDir));
        if (_ioError != null) ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), F(_ioError));
    }

    private void DrawRules()
    {
        var cat = Cur;
        if (cat == null) return;

        ImGui.Separator();
        ImGui.Text($"Rules in \"{cat.Name}\" (first match sets the color)");

        for (int i = 0; i < cat.Rules.Count; i++)
        {
            var r = cat.Rules[i];
            ImGui.PushID(i);

            bool en = r.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { r.Enabled = en; Touch(); }

            ImGui.SameLine();
            var col = r.Color;
            if (ImGui.ColorEdit4("##col", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
            {
                r.Color = col;
                Touch();
            }

            ImGui.SameLine();
            if (ImGui.Selectable($"{r.Name}###sel", _sel == i)) _sel = i;

            ImGui.PopID();
        }

        if (ImGui.Button("+ Rule"))
        {
            cat.Rules.Add(new HighlightRule { Name = $"Rule {cat.Rules.Count + 1}" });
            _sel = cat.Rules.Count - 1;
            Touch();
        }
    }

    private void DrawEditor()
    {
        var cat = Cur;
        if (cat == null) return;

        if (_sel < 0 || _sel >= cat.Rules.Count)
        {
            ImGui.TextDisabled("Select a rule or press \"+ Rule\"");
            return;
        }

        var r = cat.Rules[_sel];
        ImGui.Separator();

        var name = r.Name ?? "";
        if (ImGui.InputText("Name", ref name, 100)) { r.Name = name.Replace('|', '/'); Touch(); }

        var ex = r.Expression ?? "";
        if (ImGui.InputTextMultiline("##expr", ref ex, 4000, new Vector2(-1, 110)))
        {
            r.Expression = ex;
            Touch();
        }

        UpdateTest();

        if (_testError != null)
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Error: " + F(_testError));
        else if (string.IsNullOrWhiteSpace(ex))
            ImGui.TextDisabled("Empty. Click the hints below or type an expression.");
        else if (_hoverData == null)
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
                _hoverError != null ? "Item could not be read: " + F(_hoverError)
                                    : "Syntax OK. Hover an item to test it.");
        else if (_testResult == true)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Matches: " + F(_hoverName));
        else
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Does not match: " + F(_hoverName));

        if (ImGui.Button(_useOr ? "Join with: ||" : "Join with: &&")) _useOr = !_useOr;
        ImGui.SameLine();
        if (ImGui.Button("Up") && _sel > 0)
        {
            (cat.Rules[_sel - 1], cat.Rules[_sel]) = (cat.Rules[_sel], cat.Rules[_sel - 1]);
            _sel--;
            Touch();
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete"))
        {
            cat.Rules.RemoveAt(_sel);
            _sel = Math.Min(_sel, cat.Rules.Count - 1);
            Touch();
        }
    }

    // Empty search = stats of the hovered item, otherwise search all stats and bases.
    // A click inserts the condition into the selected rule.
    private void DrawHints()
    {
        ImGui.Separator();
        ImGui.InputText("Search stat / base", ref _filter, 100);

        if (_filter.Length < 2)
        {
            if (_hoverStats.Count == 0)
            {
                ImGui.TextDisabled("Hover an item - its stats will appear here.");
                ImGui.TextDisabled("Or type 2+ characters to search all stats and bases.");
                return;
            }

            ImGui.TextDisabled("Stats of the hovered item: " + F(_hoverName));
            for (int i = 0; i < _hoverStats.Count; i++)
            {
                var (n, v) = _hoverStats[i];
                if (ImGui.Selectable($"{n} = {v}##hs{i}"))
                    AppendClause($"ItemStats[GameStat.{n}] >= {v}");
            }
            return;
        }

        int shown = 0;
        foreach (var (clause, label) in _catalog)
        {
            if (!label.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (ImGui.Selectable($"{label}##c{shown}")) AppendClause(clause);
            if (++shown >= 40) break;
        }
        if (shown == 0) ImGui.TextDisabled("Nothing found");
    }
}
