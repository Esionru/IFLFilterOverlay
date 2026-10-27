using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
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

namespace ItemHighlight;

// ------------------------------------------------------------------ настройки

public class HighlightRule
{
    public string Name { get; set; } = "Правило";
    public bool Enabled { get; set; } = true;
    public Vector4 Color { get; set; } = new Vector4(1f, 0f, 0f, 1f);

    // Выражение в синтаксисе IFL (как в InvWithLinq / NPCInvWithLinq). Должно вернуть true/false.
    public string Expression { get; set; } = "";
}

public class HighlightSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public ToggleNode InInventory { get; set; } = new ToggleNode(true);
    public ToggleNode InStash { get; set; } = new ToggleNode(true);
    public ToggleNode InVendor { get; set; } = new ToggleNode(true);
    public RangeNode<int> Thickness { get; set; } = new RangeNode<int>(2, 1, 6);

    // Первое совпавшее (сверху вниз) правило задаёт цвет рамки
    public List<HighlightRule> Rules { get; set; } = new();
}

// ------------------------------------------------------------------ плагин

public class HighlightPlugin : BaseSettingsPlugin<HighlightSettings>
{
    private readonly Dictionary<long, (Color? Col, long Time)> _cache = new();
    private readonly Dictionary<string, (IflFilter Filter, string Error)> _compiled = new();

    // каталог для подсказок: (что вставить в выражение, подпись)
    private List<(string Clause, string Label)> _catalog;

    // предмет под курсором (запоминается, чтобы можно было править выражение, отведя мышь в настройки)
    private IflItemData _hoverData;
    private string _hoverName = "";
    private string _hoverError;
    private long _hoverAddr;
    private long _hoverBuilt;
    private int _hoverVersion;
    private List<(string Name, int Value)> _hoverStats = new();

    // живая проверка выбранного правила
    private string _testKey;
    private int _testVersion = -1;
    private bool? _testResult;
    private string _testError;

    // UI
    private int _sel = -1;
    private bool _useOr;
    private string _filter = "";

    public override bool Initialise() => true;

    // ---------------------------------------------------------------- render

    public override void Render()
    {
        if (!Settings.Enable) return;
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
    }

    private void Process(IList<NormalInventoryItem> items)
    {
        if (items == null) return;
        if (_cache.Count > 5000) _cache.Clear();
        var now = Environment.TickCount64;

        foreach (var inv in items)
        {
            var ent = inv?.Item;
            if (ent == null || !ent.IsValid) continue;

            // результат считаем раз в секунду на предмет, чтобы не грузить память каждый кадр
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

        foreach (var rule in Settings.Rules)
        {
            if (!rule.Enabled) continue;
            var (filter, _) = Compile(rule.Expression);
            if (filter == null) continue;

            try
            {
                data ??= new IflItemData(ent, GameController);
                if (filter.Matches(data))
                {
                    var v = rule.Color;
                    return new Color(v.X, v.Y, v.Z, v.W);
                }
            }
            catch { /* ошибка на конкретном предмете - пропускаем правило */ }
        }

        return null;
    }

    // -------- единственное место, которое компилирует выражения через ItemFilterLibrary
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

    // ---------------------------------------------------------------- предмет под курсором

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
            }

            UpdateTest();

            // зелёная рамка на предмете под курсором, если выбранное правило ему подходит
            if (_testResult == true)
                Graphics.DrawFrame(icon.GetClientRect(), Color.LimeGreen, 3);
        }
        catch { /* UIHover бывает невалиден - пропускаем кадр */ }
    }

    private void RebuildHover(Entity ent)
    {
        _hoverVersion++;
        _hoverError = null;
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
        }
        catch (Exception e)
        {
            _hoverData = null;
            _hoverError = e.Message;
        }
    }

    private string CurrentExpr =>
        _sel >= 0 && _sel < Settings.Rules.Count ? Settings.Rules[_sel].Expression ?? "" : "";

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
        catch (Exception e) { _testError = "Ошибка выполнения: " + e.Message; }
    }

    // ---------------------------------------------------------------- каталог подсказок

    private void BuildCatalog()
    {
        if (_catalog != null) return;
        _catalog = new();

        try
        {
            foreach (var s in Enum.GetNames(typeof(GameStat)).OrderBy(x => x))
                _catalog.Add(($"ItemStats[GameStat.{s}] >= 1", s));
        }
        catch (Exception e) { LogError("GameStat не прочитался: " + e.Message); }

        try
        {
            var bases = GameController.Files.BaseItemTypes.Contents.Values
                .Where(b => !string.IsNullOrEmpty(b.BaseName))
                .GroupBy(b => b.BaseName)
                .Select(g => g.First())
                .OrderBy(b => b.BaseName);

            foreach (var b in bases)
                _catalog.Add(($"BaseName == \"{b.BaseName}\"", $"{b.BaseName}   (база, {b.ClassName})"));
        }
        catch (Exception e) { LogError("Список баз не собрался: " + e.Message); }
    }

    // ---------------------------------------------------------------- настройки

    // ImGui.NET передаёт строку в TextDisabled/TextColored как printf-формат: "%" в тексте роняет игру
    private static string F(string s) => s?.Replace("%", "%%") ?? "";

    private void AppendClause(string clause)
    {
        if (_sel < 0 || _sel >= Settings.Rules.Count)
        {
            Settings.Rules.Add(new HighlightRule { Name = $"Правило {Settings.Rules.Count + 1}" });
            _sel = Settings.Rules.Count - 1;
        }

        var r = Settings.Rules[_sel];
        r.Expression = string.IsNullOrWhiteSpace(r.Expression)
            ? clause
            : r.Expression.TrimEnd() + (_useOr ? "\n|| " : "\n&& ") + clause;
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        BuildCatalog();
        _cache.Clear(); // пока открыты настройки - пересчитываем рамки сразу

        DrawRules();
        DrawEditor();
        DrawHints();
    }

    private void DrawRules()
    {
        ImGui.Separator();
        ImGui.Text("Правила (первое совпавшее сверху вниз задаёт цвет)");

        for (int i = 0; i < Settings.Rules.Count; i++)
        {
            var r = Settings.Rules[i];
            ImGui.PushID(i);

            bool en = r.Enabled;
            if (ImGui.Checkbox("##en", ref en)) r.Enabled = en;

            ImGui.SameLine();
            var col = r.Color;
            if (ImGui.ColorEdit4("##col", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                r.Color = col;

            ImGui.SameLine();
            if (ImGui.Selectable($"{r.Name}###sel", _sel == i)) _sel = i;

            ImGui.PopID();
        }

        if (ImGui.Button("+ Правило"))
        {
            Settings.Rules.Add(new HighlightRule { Name = $"Правило {Settings.Rules.Count + 1}" });
            _sel = Settings.Rules.Count - 1;
        }
    }

    private void DrawEditor()
    {
        if (_sel < 0 || _sel >= Settings.Rules.Count)
        {
            ImGui.TextDisabled("Выбери правило или нажми \"+ Правило\"");
            return;
        }

        var r = Settings.Rules[_sel];
        ImGui.Separator();

        var name = r.Name ?? "";
        if (ImGui.InputText("Название", ref name, 100)) r.Name = name;

        var ex = r.Expression ?? "";
        if (ImGui.InputTextMultiline("##expr", ref ex, 4000, new Vector2(-1, 110))) r.Expression = ex;

        UpdateTest();

        if (_testError != null)
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Ошибка: " + F(_testError));
        else if (string.IsNullOrWhiteSpace(ex))
            ImGui.TextDisabled("Пусто. Кликай по подсказкам ниже или пиши выражение.");
        else if (_hoverData == null)
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
                _hoverError != null ? "Предмет не прочитался: " + F(_hoverError)
                                    : "Синтаксис OK. Наведи курсор на предмет, чтобы проверить.");
        else if (_testResult == true)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Подходит: " + F(_hoverName));
        else
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Не подходит: " + F(_hoverName));

        if (ImGui.Button(_useOr ? "Добавлять через: ||" : "Добавлять через: &&")) _useOr = !_useOr;
        ImGui.SameLine();
        if (ImGui.Button("Вверх") && _sel > 0)
        {
            (Settings.Rules[_sel - 1], Settings.Rules[_sel]) = (Settings.Rules[_sel], Settings.Rules[_sel - 1]);
            _sel--;
        }
        ImGui.SameLine();
        if (ImGui.Button("Удалить"))
        {
            Settings.Rules.RemoveAt(_sel);
            _sel = Math.Min(_sel, Settings.Rules.Count - 1);
        }
    }

    // Одна подсказка вместо нескольких каталогов: пустой поиск = статы предмета под курсором,
    // иначе поиск по всем статам и базам. Клик вставляет условие в выбранное правило.
    private void DrawHints()
    {
        ImGui.Separator();
        ImGui.InputText("Поиск стата / базы", ref _filter, 100);

        if (_filter.Length < 2)
        {
            if (_hoverStats.Count == 0)
            {
                ImGui.TextDisabled("Наведи курсор на предмет - здесь появятся его статы.");
                ImGui.TextDisabled("Или введи 2+ символа для поиска по всем статам и базам.");
                return;
            }

            ImGui.TextDisabled("Статы предмета под курсором: " + F(_hoverName));
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
        if (shown == 0) ImGui.TextDisabled("Ничего не найдено");
    }
}
